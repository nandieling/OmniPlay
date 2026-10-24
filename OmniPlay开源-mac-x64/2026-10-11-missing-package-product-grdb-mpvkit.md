# Missing package product 'GRDB' / 'MPVKit-GPL'

把 x64 工程复制一份到新路径（例如 `~/Downloads/OmniPlay开源-mac-x64 5/`）后在 Xcode 里编译，报：

```
OmniPlay.xcodeproj Missing package product 'GRDB'
OmniPlay.xcodeproj Missing package product 'MPVKit-GPL'
```

## 两条报错其实是一次失败

工程里两个包来源不同：

- `GRDB`：`XCLocalSwiftPackageReference`，`relativePath = "GRDB.swift-master"`，也就是必须和 `.xcodeproj` 同级放着的**本地包**（1.8 版已把 767 个文件全量入库，正常 clone 一定带着）。
- `MPVKit`：`XCRemoteSwiftPackageReference`，从 `github.com/mpvkit/MPVKit` 拉源码；而 `MPVKit-GPL` 的依赖是一串 `binaryTarget`，每个都是 `github.com/mpvkit/MPVKit/releases/download/0.41.0/*.xcframework.zip`，实测这一套落地 **1.6GB**。

SwiftPM 是在工作区级别一次性解析所有包的：只要有一个包没解析成功，Xcode 就把**每个**包产物都标成 missing——连不需要网络的本地 GRDB 也被一起点名。所以看到两条 missing 时，真正卡住的几乎总是那个远端包。

## 为什么「原来那份能编，新拷的这份不行」

DerivedData 是按工程**绝对路径**分目录的。换一个新路径打开工程 = 全新的空 `SourcePackages` = 重新 clone 仓库 + 重新下载 1.6GB 二进制。国内直连 GitHub 与 Releases CDN（`objects.githubusercontent.com`）不稳，这一步挂掉，两条 missing 就同时出现。跟 x64 / ARM 无关，两条 mac 线用的是同一个远端包，坑完全一样。

## 按成本从低到高排查

1. 先看副本全不全：`OmniPlay开源-mac-x64/GRDB.swift-master/Package.swift` 必须在。零散拷贝或从网盘同步一半，最容易漏这个目录。
2. 让网络能到 GitHub（系统代理或全局 VPN，注意命令行不一定继承浏览器的代理），然后 Xcode 菜单 File ▸ Packages ▸ Reset Package Caches，再 File ▸ Packages ▸ Resolve Package Versions；或者命令行：

   ```bash
   xcodebuild -resolvePackageDependencies -project OmniPlay.xcodeproj -scheme OmniPlay
   ```

   成功时会打印两行：`GRDB: .../GRDB.swift-master @ local`、`MPVKit: https://github.com/mpvkit/MPVKit @ 613c0cc`。
3. 网络实在不行就离线搬缓存：把能编译那台机器上 `~/Library/Developer/Xcode/DerivedData/OmniPlay-*/SourcePackages` 整个目录，拷到新路径对应的 DerivedData 目录里（目录名是工程路径的 hash，不好猜；在新路径上用 Xcode 先打开一次让它把目录建出来，再覆盖 `SourcePackages` 最省事）。
4. 更稳的做法是别再复制新路径：工程固定放在一处（建议 `~/Projects/`，`~/Downloads/` 还多一层「文稿/下载文件夹」授权），沿用已经预热好的 DerivedData。

## 工程侧顺手改的一处

x64 的 MPVKit 版本要求原本是 `upToNextMajorVersion >= 0.41.0`，这会要 GitHub 的版本标签列表；ARM 那条线一直是钉 revision。现在 x64 也改成钉死 `revision = 613c0ccc3acf70e136aaff880a9b5fe8fdfaf5b8`，`Package.resolved` 同步去掉 `version` 字段，两个工程的解析结果完全一致。好处是：离线/弱网下拿缓存就能解析，不会因为拉不到 tag 卡住，也不会哪天 0.42 放出来悄悄换掉播放器栈。

验证：`xcodebuild -resolvePackageDependencies` 两个包都解析成功；`xcodebuild build ARCHS=x86_64 ONLY_ACTIVE_ARCH=NO` 通过、零告警。
