# 本机安卓虚拟机下线（14G）

MAS（Mac Android Subsystem，`/Applications/Mac Android Subsystem.app`）实测能直接装并跑本项目的
release APK，那套为「没有设备也能看见界面」而搭的官方模拟器就不再需要了，删掉腾出 14G。

## 1. 虚拟机本来就全在项目里

当初为了不落脏家目录，`ANDROID_AVD_HOME` 与 `sdk_root` 都指在项目内（见
`2026-10-07-android-tv-emulator-pass.md`），所以要删的东西只有三个，都在 `.gitignore` 里：

| 路径 | 大小 | 是什么 |
| --- | --- | --- |
| `.avd/` | 4.8G | `omni_tv1080` 与 `omni_tv4k` 两台 AVD 的虚拟盘（`userdata-qemu.img.qcow2` 等） |
| `.android-sdk/system-images/` | 8.1G | `system-images;android-34;android-tv;arm64-v8a` |
| `.android-sdk/emulator/` | 1.2G | 模拟器运行时（qemu + gRPC + netsimd） |

`.android-sdk` 从 13G 降到 3.2G，系统盘可用空间 26G → 40G。

## 2. 留下的是打包要用的那几样

`app/build.gradle` 写死 `compileSdk 35` + `ndkVersion "27.3.13750724"` + `externalNativeBuild.cmake`
（`app/src/main/cpp/mpv_bridge.cpp`），所以这些一个都不能动：

```text
build-tools/35.0.0    374M   aapt2 / zipalign / apksigner
ndk/                  2.4G   libmpv JNI 桥接层交叉编译
cmake/                 94M   externalNativeBuild 用
platforms/android-35  130M   android.jar，release 与 harness 都要
cmdline-tools/        158M   sdkmanager / avdmanager，恢复模拟器用
platform-tools/        37M   adb，连真机用
licenses/              28K
```

家目录只剩 32K 的 `~/.android`：`adbkey` 留着（连真机 adb 要用），
`emu-last-feature-flags.protobuf`、`emu-update-last-check.ini`、`modem-nv-ram-555{4,6}` 是模拟器写的一百来字节
指纹，留着不影响任何东西。`~/Applications/Android Apps/` 与
`~/Library/Application Support/Mac Android Subsystem`（3.2G，MAS 自己的 Android 虚拟盘）属于 MAS，不在这次删除范围内。

## 3. 删完怎么确认没伤到构建

被删的三样没有任何脚本引用：`build/tvtools/compile.sh` 与 `harness.sh` 只用
`.android-sdk/platforms/android-35/android.jar`，`release.sh` 走 Gradle + NDK + build-tools，
`local.properties` 里的 `sdk.dir` 不用改。

- `zsh build/tvtools/release.sh` → `BUILD SUCCESSFUL`，产物与 `dist/OmniPlay-TV-v1.42-arm64.apk`
  **逐字节相同**（sha256 `114ebc16…`），说明这套裁剪后的 SDK 与删之前等价。
- `zsh build/tvtools/harness.sh` → `ALL PASS` / `ALL PASS` / `OK 字幕转档 35 项全部通过`
  （字幕那套要先 `python3 build/patches/mkmkv.py` 现造 `/tmp/mkvtest` 夹具）。

## 4. MAS 代替不了真机的地方

MAS 提供的是 Android 手机形态，`LauncherActivity` 按 `uiMode & UI_MODE_TYPE_MASK` 与
`FEATURE_LEANBACK` 分发，不报电视形态就进 `PhoneMainActivity`；想验 TV 那一支得直接起
`com.omniplay.tv/.MainActivity`。另外它照样在 NAT 后面，看不见真实 NAS，4K / HDR / DV 的硬解与
遥控器手感仍然只有真机说话——这一点和当年用模拟器时一样，见 README「模拟器与真机验证」。
