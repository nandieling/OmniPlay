# 安卓 TV 1.17：遥控器手感、移除媒体源清库、筛选退场、弹层回到顶部

四件事都指向同一句话：遥控器按下去要有反馈，删掉的东西不能留在屏幕上。

## 焦点切换的掉帧从 17.4% 降到 9.5%

先测基线。48 张海报的墙上从头走到尾，`dumpsys gfxinfo com.omniplay.tv` 的原始数据：

```
Total frames rendered: 402
Janky frames: 70 (17.41%)
50th percentile: 16ms   95th percentile: 19ms
Number High input latency: 332
Number Slow UI thread: 2
Number Slow issue draw commands: 68
```

`High input latency` 里绝大部分是脚本按键的间隔，不是渲染；真正说明问题的是 17.41% 与 68 次 `Slow issue draw`。三处成因：

- 每次焦点变化都新建 `GradientDrawable`，拿焦点和丢焦点两条路径各建一次。换背景会连带重算轮廓（`Outline`），阴影跟着重建，等于把一次视觉变化放大成一串失效。
- 海报卡片有两层阴影：卡片自身一层、海报框一层，同一个圆角矩形算两遍阴影。
- 缩放动画期间整张卡片（海报位图 + 两行文字）每帧重新光栅化。

`Theme` 里的做法改成了预先画好两张 drawable，换焦点时只换引用：

```java
private void swapOnFocus(View target, GradientDrawable rest, GradientDrawable focused,
                         int restElevation, int focusedElevation,
                         FocusRepaint repaint, Runnable onFocused) {
    target.setBackground(rest);
    target.setElevation(restElevation);
    repaint.apply(false);
    target.setOnFocusChangeListener((focusedView, hasFocus) -> {
        focusedView.setBackground(hasFocus ? focused : rest);
        focusedView.setElevation(hasFocus ? focusedElevation : restElevation);
        repaint.apply(hasFocus);
        animateFocusScale(focusedView, hasFocus);
        if (hasFocus && onFocused != null) onFocused.run();
    });
}
```

`FocusRepaint` 是这次新加的一层：焦点态要改的不只背景（文字色、图标 tint 也要跟着变），用一个函数式接口承接，省得每个 `styleXxxFocus` 各自抄一遍监听器。原本想用 `android.function.BooleanConsumer`，这台 android-35 的 `android.jar` 里没有 `android.function.*`，编译报「程序包 android.function 不存在」，于是自定义了 `Theme.FocusRepaint`。

海报卡只保留一层阴影：卡片 `elevation` 0dp→8dp，海报框只换边框色、`elevation` 恒为 0。缩放动画期间给控件挂 `LAYER_TYPE_HARDWARE`，动画结束再摘：

```java
public void animateFocusScale(View view, boolean focused) {
    if (focused) {
        view.setLayerType(View.LAYER_TYPE_HARDWARE, null);
    }
    view.animate().scaleX(focused ? FOCUS_SCALE : 1f).scaleY(focused ? FOCUS_SCALE : 1f)
            .setDuration(FOCUS_ANIMATION_MS)
            .withEndAction(() -> {
                if (!view.isFocused()) {
                    view.setLayerType(View.LAYER_TYPE_NONE, null);
                }
            })
            .start();
}
```

分集缩略图原先自己写了一套焦点样式（一张阴影 + 现场画背景），现在统一走 `applyPosterCardFocus`，共用同一份缓存。

同一套走位重测两遍：

```
Total frames rendered: 412   Janky frames: 38 (9.22%)
Total frames rendered: 410   Janky frames: 39 (9.51%)
Number Slow UI thread: 0     Number Slow issue draw commands: 39
```

剩下的 9.5% 有地板：海报墙是不复用的 `GridLayout`，`Max resource usage: 99.53MB (x48)`，48 张图常驻。再往下要换 RecyclerView 复用，那是结构级改动，这轮没动。

## 移除媒体源要连首页卡片一起清

`LocalEngine.removeSource()` 原先只改写媒体源列表再落盘：

```java
List<Source> current = sources();
current.removeIf(source -> source.id == sourceId);
persistSources(current);
```

实体、文件、刮削队列、缓存记录都还留在 SQLite 里，而首页是从 `libraryItems()` 读库渲染的，于是海报墙一张不少，只有媒体源面板里少了一行。mac 版删媒体源是按 sourceId 删文件行、再清掉没有文件的实体，TV 版补上同一套：

```java
public int purgeSource(int sourceId) {
    String prefix = "local-" + sourceId + ":";      // LocalEngine.fileId 的构造规则
    ...
    db.delete("videoFile", "id = ?", new String[]{fileId});
    db.delete("offlineCache", "fileId = ?", new String[]{fileId});
    progressOverrides.remove(fileId);
    pruneOrphanEntities(db);
}
```

`pruneOrphanEntities()` 是这次抽出来的公共尾巴：删掉 `videoFile` 里没有任何文件引用的 `movie` / `tvShow`、这些剧的 `tvEpisode`，以及 `sourceFileId` 指向已消失文件的 `scrapeQueue` 行。它也接到了 `removeMissingFiles()` 后面——磁盘上删了影片再扫描，卡片同样会消失，以前这种情况也会留下点不开的空卡片。

`progressOverrides` 是内存里的播放进度覆盖表，不跟着清的话，重新添加同一个源会读到旧进度。

模拟器上的验证是端到端的：本地源 `/storage/emulated/0/Video` 索引出 48 部，媒体源面板点「移除」，面板与首页同时变成「0 部」/「媒体库为空」，`待刮削 48 个条目` 也一并清零。

## 筛选整块退场

右上角「筛选」按钮、类型与观看状态两组选项、`filteredItems()` 全删。首页名单现在只有一句话：按搜索词过滤，再按当前排序排。

```java
/** 首页名单：只按搜索词过滤再排序，不再有类型 / 观看状态筛选。 */
private List<Models.LibraryItem> displayedItems() { ... }
```

工具栏的焦点行剩下 搜索 / 排序 / 降序 / 媒体源 / 设置，返回键少一个分支（`isFilterOpen` 不再参与），计数文案用 `libraryItems.size()`。

## 弹层滚动跟中线，不跟边缘

「往下滑之后再往上滑，滑不回顶部」的现场是这样的：设置面板 19 行，往下走 18 行到底，再往上按 8 次方向键，高亮在走但页面纹丝不动，标题和状态行再也看不见。

原因不在按键，在两套滚动逻辑打架。`FocusGrid.scrollToVisible()` 当时判断的是「控件是否已经离开视口」，在视口里就不动；而系统自己的 `requestChildRectangleOnScreen` 每次都把新获焦的行往视口**边缘**贴。于是往上走时页面一直贴着上边缘走，等到真正按出可视范围才回弹，用户读到的就是「上面那几行回不去了」。而且系统只会把第一个可聚焦控件对齐上边缘，永远回不到 0，标题和状态行被永久顶出可视区。

现在 `followVertically()` 只按布局坐标算一个该落的滚动位置：控件高于视口就露出顶部，否则把控件中线对到视口中线；第一行直接归零；最后一行到底。

```java
int wanted = view.getHeight() >= viewport
        ? contentY
        : contentY + view.getHeight() / 2 - viewport / 2;
if (contentY <= firstRowTop(scroll, content) + FOCUS_MARGIN_DP * density(scroll)) {
    wanted = 0;
}
scroll.scrollTo(0, Math.max(0, Math.min(maxScrollY(scroll, content), wanted)));
```

`handleKey` 在首行 / 末行分别调 `scrollToEdge(top|bottom)`，中间的行走 `scrollToVisible`。设置面板的初始焦点从「主题第一颗色卡」改成 TMDB 卡片底部那颗「保存配置」：面板从顶部开始、焦点框一开始就看得见，也不会顺手弹起软键盘。

验证脚本 `/tmp/verify25.py`：打开面板时焦点在「保存配置」、`TMDB API Key` 标签在 y=145、`外观主题` 在 y=617；往下 4 次标题仍在；往下 18 次最后一行「退出连接」停在 y=862；往上 4 次、再 20 次之后回到同一组坐标；再无 FATAL。

## 目录浏览器的确认按钮提到焦点第一行

顺手修的一个手感问题：「选择媒体目录」浏览器的操作行（选择此目录 / 返回上级 / 取消）原先排在焦点顺序**最后**，而一个影片目录动辄几十个子目录，遥控器要按几十次才够得着确认键。改成操作行排在第一行：打开就在「选择此目录」上，往下进目录，往回一下就到按钮。进入子目录后焦点也重新落回「选择此目录」（SMB 共享根目录时按钮是禁用的，退回第一个目录行）。

## 版本

`versionCode 28 / versionName 1.17`，包名与签名不变，覆盖安装即可。八个 JVM 用例（`run-webdav.sh` / `run-smb.sh` / `run-saved.sh` / `run-phone.sh` / `run-regex.sh` / `run-bluray.sh` / `run-tmdb.sh` 与 `/tmp/lanprobe/run.sh`）全绿，它们只编 `local/` + `data/`，所以 `local/` 依旧不依赖 `ui/`。
