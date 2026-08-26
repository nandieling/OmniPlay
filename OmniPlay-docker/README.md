# OmniPlay Docker

这是从 `OmniPlay-群晖-飞牛` 套件版拆出的 Docker 版，包含同一套服务端、Web UI、刮削、媒体库扫描、WebDAV、本地目录浏览、HLS 播放和 FFmpeg 转码能力。

## 快速运行

无硬解通用版，适合所有 Docker 主机：

```sh
cd OmniPlay-docker
docker compose up -d --build
```

Intel VAAPI/QSV 硬解版，适合有 `/dev/dri` 的 NAS/主机：

```sh
cd OmniPlay-docker
docker compose -f compose.hwaccel.yml up -d --build
```

默认访问地址：

```text
http://NAS_IP:45722
```

首次打开后注册管理员账号。

默认 `compose.yml` 和 `compose.hwaccel.yml` 都使用 host 网络。这样容器内访问 `127.0.0.1/localhost` 就是宿主机，适合直接使用 NAS/宿主机本机代理，例如 `http://localhost:20171`。

字幕预缓存默认关闭。需要后台预处理下一集或整个媒体库的字幕时，可在 Web UI 的“设置 -> 缓存”中选择“优化缓存方案”或“全量缓存”；关闭预缓存不影响播放时即时加载和转换字幕。

## 目录挂载

两个 compose 默认都挂载：

- `./data:/var/lib/omniplay`：数据库、设置、日志等小文件。
- `./cache:/var/cache/omniplay`：海报、缩略图、HLS、字幕和 WebDAV 缓存。
- `./media:/media:ro`：媒体目录，只读挂载。

下载源码后可以直接运行默认 compose。首次使用前，把媒体文件放到 `OmniPlay-docker/media`，或把所用 compose 文件里的左侧媒体路径改成实际目录，例如：

```yaml
volumes:
  - ./data:/var/lib/omniplay
  - /mnt/big-disk/omniplay-cache:/var/cache/omniplay
  - /mnt/media:/media/video:ro
```

然后在 OmniPlay Web UI 中添加容器内路径，例如 `/media/video`。

### 多目录或多硬盘挂载

每个宿主机目录增加一条 volume，并映射到 `/media` 下互不重复的容器路径。例如：

```yaml
volumes:
  - ./data:/var/lib/omniplay
  - ./cache:/var/cache/omniplay
  - /volume1/movies:/media/disk1/movies:ro
  - /volume1/tv:/media/disk1/tv:ro
  - /volume2/video:/media/disk2/video:ro
  - /mnt/usb4k:/media/usb/4k:ro
```

修改后重新创建容器：

```sh
docker compose up -d --build
```

使用硬解 compose 时改用：

```sh
docker compose -f compose.hwaccel.yml up -d --build
```

然后在 Web UI 的媒体源管理中分别添加 `/media/disk1/movies`、`/media/disk1/tv`、`/media/disk2/video` 和 `/media/usb/4k`。左侧必须是宿主机上的真实绝对路径，右侧是容器内路径；不同挂载不要映射到同一个右侧路径。默认的 `OMNIPLAY_LOCAL_SHARE_ROOTS=/media` 已允许浏览这些子目录，无需为每块硬盘修改环境变量。

不要把自己的 TMDB API Key、代理地址、账号密码写进 `compose.yml` 后提交。需要固定运行配置时，建议在本机创建未纳入 Git 的 `.env` 文件，或只在 Docker 管理界面里填写。

如果必须改成 bridge 网络，请删除 `network_mode: host`，恢复端口映射：

```yaml
ports:
  - "45722:45722"
environment:
  OMNIPLAY_DOCKER_HOST_NETWORK: "0"
```

bridge 网络下容器内的 `localhost` 不是宿主机。代理地址请填写宿主机 LAN IP，或确保 Docker 可通过 `host.docker.internal` / 默认网关访问宿主机代理，并且代理监听 `0.0.0.0` 或 LAN 地址。

## 构建镜像

当前目录的 `Dockerfile` 是多阶段构建，会自动完成 Web UI 构建和 .NET 服务端发布：

```sh
./scripts/build-image.sh
```

指定架构：

```sh
IMAGE=omniplay-docker:arm64 ./scripts/build-image.sh arm64
IMAGE=omniplay-docker:x64 ./scripts/build-image.sh x64
```

## 硬件转码

容器内默认安装系统 `ffmpeg`、`ffprobe`、VAAPI 基础库、Intel media driver、libvpl、Mesa VA 驱动和中文字体。默认 `compose.yml` 不挂载硬解设备，也不启用 QSV/VAAPI，保证没有 `/dev/dri` 的机器可以直接启动。

需要 Intel VAAPI/QSV 硬解时，使用硬解 compose：

```sh
docker compose -f compose.hwaccel.yml up -d --build
```

`compose.hwaccel.yml` 会挂载：

```yaml
devices:
  - /dev/dri:/dev/dri
environment:
  OMNIPLAY_VAAPI_DEVICE: /dev/dri/renderD128
  OMNIPLAY_ENABLE_QSV: "1"
```

如果设备路径不同，把 `compose.hwaccel.yml` 里的 `OMNIPLAY_VAAPI_DEVICE` 改成实际的 `renderD*` 路径。

容器默认以 root 运行，通常不需要额外配置 `group_add`。部分系统或镜像里不存在 `render` 组名，强行添加会导致容器启动时报 `Unable to find group render`。

## 常用环境变量

- `OMNIPLAY_APP_ROOT`：容器内数据根目录，默认 `/var/lib/omniplay`。
- `OMNIPLAY_CACHE_ROOT`：容器内缓存根目录，默认 `/var/cache/omniplay`。将宿主机机械硬盘目录挂载到这里即可迁移大缓存。
- `OMNIPLAY_LOCAL_SHARE_ROOTS`：本地目录浏览根，默认 `/media`。
- `OMNIPLAY_TMDB_ACCESS_TOKEN` / `OMNIPLAY_TMDB_API_KEY`：可选 TMDB 凭据。公开仓库不内置个人 TMDB Key，刮削前请在设置里填写自定义 API，或用环境变量提供。
- `OMNIPLAY_FFMPEG_PATH` / `OMNIPLAY_FFPROBE_PATH`：FFmpeg 工具路径。
- `OMNIPLAY_SCAN_PROBE_CONCURRENCY`：扫描时媒体探测并发数。
- `OMNIPLAY_ENABLE_QSV`：是否启用 Intel QSV/VAAPI。`compose.yml` 为 `0`，`compose.hwaccel.yml` 为 `1` 并挂载 `/dev/dri`。

## 和套件版的关系

Docker 版保留套件版业务逻辑，去掉 DSM/fnOS 原生安装、端口注册、套件生命周期脚本等包装层。运行期依赖通过 Docker 环境变量、卷挂载和容器内 FFmpeg 提供。
