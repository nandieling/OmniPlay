# 2026-10-30 · 1.46：打开时进哪套界面，改成可以自己钉

## 一、问题

同一份 APK 里装着两套界面（`MainActivity` 遥控器版与 `PhoneMainActivity` 触摸版），桌面只有一个入口 `LauncherActivity`，它按设备身份现场判：

```java
int uiModeType = getResources().getConfiguration().uiMode & Configuration.UI_MODE_TYPE_MASK;
return uiModeType == Configuration.UI_MODE_TYPE_TELEVISION
        || getPackageManager().hasSystemFeature(PackageManager.FEATURE_LEANBACK);
```

真电视上这条没问题（Android TV 的 `uiMode` 就是 `television`）。问题是盒子与虚拟机经常两边都报：既有触屏又有遥控器输入，`leanback` 还带着——判错一次就再也进不去另一套，只能卸重装。用户报的「Mac Android Subsystem 打开软件是 TV 版」就是这一类。

排查时先把设备身份抄了一份下来，别猜：

```sh
adb -s 127.0.0.1:58526 shell 'pm list features | grep -iE "leanback|touchscreen"'
adb -s 127.0.0.1:58526 shell 'dumpsys window | grep -m1 mGlobalConfiguration'
adb -s 127.0.0.1:58526 shell 'am force-stop com.omniplay.tv; monkey -p com.omniplay.tv -c android.intent.category.LAUNCHER 1; sleep 4; dumpsys activity activities | grep topResumedActivity'
```

MAS 这台（LineageOS 24 / `virtio_arm64only`）报的是触屏 + 无 `leanback` + `uiMode` 正常，桌面入口记的也是 `.LauncherActivity`（`~/Library/Application Support/Mac Android Subsystem/apps.json` 里能看到），所以它当场判的是手机版；看到电视版是上一次停在电视界面后被恢复回来的结果——判定只在冷启动那一刻做一次，恢复上次界面不会再判第二遍。

## 二、改法

判定加一层手动指定，记在 `omniplay_tv_ui` 的 `ui_shell_mode`（`auto` / `tv` / `phone`，与播放模式同一个偏好文件）：

```java
private boolean preferTelevision() {
    String shell = shellMode(this);
    if (SHELL_TV.equals(shell)) return true;
    if (SHELL_PHONE.equals(shell)) return false;
    return looksLikeTelevision();
}

private boolean looksLikeTelevision() {
    if (uiModeType == Configuration.UI_MODE_TYPE_TELEVISION) return true;
    // 只报 leanback 却带触屏的，是盒子与虚拟机一类，别强推 D-pad 界面；
    // 真电视的 uiMode 会是 television，上面那条已经接住了。
    return hasSystemFeature(FEATURE_LEANBACK) && !hasSystemFeature(FEATURE_TOUCHSCREEN);
}
```

`setShellMode()` 用 `commit()` 不用 `apply()`：这是决定「下一次打开进哪套」的一条配置，刚选完就被杀进程丢掉的话，下次打开还是老界面，用户会以为没生效。

入口两端都给：

- 手机端 `PhoneSettingsSheet` 在「外观」段里加一行「打开时进的界面」，选一档后先 toast 记住，再弹一层问「现在就去 / 下次再说」，现在就去就用 `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK` 回到 `LauncherActivity` 重新分发。
- 电视端 `openSettingsOverlay()` 在 TMDB 与外观主题之间插一段「打开时进的界面」，分段控件选 自动 / 电视版 / 手机版，另配一颗「现在就换过去」；这颗按钮先 `releasePlaybackState()`（记位置、停轮询、停本地代理、毁播放器）再回入口，免得换个界面把还在出声的实例丢在后台。

## 三、验证

- `zsh build/tvtools/compile.sh`、`release.sh` 通过。

装的是 `dist/OmniPlay-TV-v1.46-arm64.apk`（`aapt2 dump badging` 读到 `versionCode='57' versionName='1.46'`），MAS 这台 `virtio_arm64only` 上实测：

- 设备身份：`pm list features` 有 `android.hardware.touchscreen`（连 multitouch 全家）、`android.hardware.type.pc`，**没有** `android.software.leanback`；`dumpsys uimode` 是 `mCurUiMode=0x21`，类型位 `0x1` 即 `UI_MODE_TYPE_NORMAL`。两条自动判定都不成立，所以自动档进手机版。
- 手机端：设置 → 「打开时进的界面」→ 弹层里 ✓ 停在「自动（按设备身份判）」，点「电视版（遥控器）」→ 再弹一层「现在就去 / 下次再说」→ 现在就去，`mCurrentFocus` 变成 `com.omniplay.tv/com.omniplay.tv.MainActivity`。
- 电视端：设置浮层里「打开时进的界面」卡给出 自动 / 电视版 / 手机版 三档与「现在就换过去」，选手机版点那颗，`mCurrentFocus` 回到 `com.omniplay.tv.PhoneMainActivity`。
- 手动指定确实跨冷启动：`ui_shell_mode=tv` 下 `am force-stop` 再起入口，两次都落 `MainActivity`；改回 `auto` 后同样的冷启动落 `PhoneMainActivity`。

顺手把「为什么每次打开都是同一套」钉死了：把 `ui_shell_mode` 设成 `tv` 但选「下次再说」，任务还活在手机版上，这时用桌面图标那条意图（`monkey -p com.omniplay.tv -c android.intent.category.LAUNCHER 1`）重新打开，`mCurrentFocus` 仍是 `PhoneMainActivity`——Android 把还活着的任务直接调到前台，压根不会再进 `LauncherActivity`；只有 `force-stop` 之后才会重新分发。所以「下次打开生效」这句话得配一句「先把软件彻底退出」，README 的常见问题里已经补上。

一个操作提醒：MAS 的 smart 模式几分钟不给它活儿就把虚拟机停掉，adb 跟着掉线，验证脚本里要先 `adb connect 127.0.0.1:58526` 重试；起虚拟机点 System 页那颗 Start。

## 四、留给下次的判断口径

`ui_shell_mode` 只在 `LauncherActivity` 读，两套界面内部都不再改它；要加第三套入口（比如车机）也从这个键分。判不准的设备优先让用户钉死，别在 `isTelevision()` 里继续叠启发式——叠到第三条就会开始误伤真机。
