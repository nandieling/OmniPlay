# 1.43 手机端首屏改成海报墙

## 1. 为什么手机端一进来是「添加媒体源」

`PhoneMainActivity.onCreate` 尾巴上是这么写的：

```java
if (api.hasServerUrl()) {
    checkSession();
} else {
    showSetup(null);
}
```

这套判断是从还有 Docker 服务端那阵子留下的，而独立版里 `LocalPlayApi.hasServerUrl()` 的实现就是
`!engine.sources().isEmpty()`，`authStatus()` 又固定 `requiresSetup=false`、`isAuthenticated=有没有媒体源`。
两条串起来的结果是：

- 全新安装（零媒体源）→ `showSetup(null)`，整屏表单；
- 有媒体源 → `checkSession()` 绕一次 io 线程，回调里再 `showHome()`，白闪一次「正在打开媒体库」。

电视端 1.21 就把这条路改平了（`onCreate` 只 `showHome()`，空墙自带入口），手机端一直漏着。
在 Mac Android Subsystem 里跑的时候特别扎眼——MAS 报的是手机形态，双击图标进的就是这一支。

## 2. 改法

启动直接 `showHome()`，`checkSession()` 整段删掉（没有服务端可问，auth 字段在独立版里是写死的）。
空态不再是一句死文字，而是长在墙上的入口：

```java
if (items.isEmpty()) {
    page.addView(emptyLibraryCallToAction());
}
```

`emptyLibraryCallToAction()` 分两种情况：一个媒体源都没有 → 「还没有媒体源…」+「添加媒体源」按钮开设置页；
有源但库里还没条目 → 「媒体库还是空的，可能正在扫描刮削。」+「刷新」按钮重走 `showHome()`。
之所以要分开：前者是「你还没告诉它去哪儿找片」，后者是「正在干活，等一下」，同一句文案两种意思都盖不住。

设置页随之退成二级页，配套三处：

- 页头改成标题行，右侧一个「返回」。表单有七行输入框加六个按钮，页尾那颗按钮以前够不着；
- 页尾「进入」改「进入媒体库」——它现在的语义是「从设置回墙」，不再是「首次进入」；
- `onBackPressed` 把 `SETUP` 并进 `DETAIL` 那一支回墙，不再直接退出应用。

## 3. 已知边界

手机端没有电视端那套 `Overlays` / `FocusGrid`，入口就是最朴素的一颗按钮，抽屉、局域网扫描、
按源分组都还是电视端才有。另外扫描进度只在 `screen == SETUP` 时往表单里刷：走「保存并扫描」正常路径没事
（扫描结束时人还在设置页，回调会 `showHome()` 跳墙），但扫描途中手动按「返回」，墙不会自己长出海报，
得点一下「刷新」。要让它像电视端那样边扫边上墙，得把扫描回调接一段到 `renderHome`，这条留着。

## 4. 验证

- `zsh build/tvtools/compile.sh` → `BUILD SUCCESSFUL`；
- `zsh build/tvtools/release.sh` → `BUILD SUCCESSFUL`，`aapt2 dump badging` 显示
  `versionCode='54' versionName='1.43'`，产物复制到 `dist/OmniPlay-TV-v1.43-arm64.apk`；
- `zsh build/tvtools/harness.sh` → `ALL PASS` / `ALL PASS` / `OK 字幕转档 35 项全部通过`
  （这次没动 `local/`，跑它是为了让 README 里「三套用例全绿」这句话仍然作数）。
- MAS 里实机走一遍：冷启动直接是墙（空墙带「添加媒体源」）→ 添加媒体源 → 保存并扫描 → 自动跳墙；
  设置页页头「返回」与返回键都回墙。遥控器 D-pad 那一支仍需真机。
