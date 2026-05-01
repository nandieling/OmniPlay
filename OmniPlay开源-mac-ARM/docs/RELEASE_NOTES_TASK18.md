# 任务18：发布说明与冻结记录

日期：2026-04-07  
版本：`1.0 (1)`

## 一、发布内容

### 1. WebDAV 连接与诊断

- 新增 WebDAV 连接预检（保存前测试连通性与认证）。
- 预检失败与扫描失败统一进入“复制诊断”通道。
- 诊断文本结构化输出并默认脱敏（不暴露 URL 凭据）。

### 2. 扫描可观测性

- 同步流程支持按源进度提示与失败分类（auth/network/server/config/unknown）。
- 同步结束可弹出失败摘要。

### 3. WebDAV 播放稳定性

- 修复 WebDAV 播放链路认证未完整传递导致的“加载后无进度”问题。
- mpv 日志打印改为脱敏 URL，避免凭据泄露。

### 4. 添加源体验（任务17.1）

- WebDAV 地址输入提示明确“应填写 NAS 里的具体媒体文件夹地址”。
- 增加目录级校验，避免仅填服务根目录（如 `/dav`）。
- 媒体源列表显示路径解码（中文目录可读，不显示 `%xx` 编码串）。

## 二、自动化冻结结果

执行命令：

```bash
xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx test
```

结果：

- `TEST SUCCEEDED`
- `OmniPlayTests`：通过
- `OmniPlayUITests`：通过（含 `testWebDAVPreflightFailureShowsCopyDiagnosticsButton`）

## 三、已知限制

- 暂不支持 SMB 直连播放/扫描（继续建议通过 WebDAV 接入）。
- 远程源离线缓存仍禁用（按既定策略）。

## 四、回滚点

若上线后出现 P0/P1：

1. 立即停止分发当前构建。
2. 回滚至任务16前稳定包。
3. 收集“复制诊断”文本 + 控制台日志。
4. 修复后重新执行任务16/17/18清单再发布。
