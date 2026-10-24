# 只装跑得动转档的最小东西：python3 + ffmpeg。
# 没有 Web 前端构建、没有运行时、没有刮削器，镜像大概 90 MB 上下。
FROM alpine:3.20

# apk 源。有些国内加速拉下来的 alpine 镜像里 /etc/apk/repositories 已经被改成自己的源
# （群晖上遇到的就是 ustc）， sed 换串会石沉大海，所以这里直接覆写整个文件。
# ALPINE_MIRROR 只是第一个试的，后面几个是备用，哪个通用哪个。
ARG ALPINE_MIRROR=https://mirrors.aliyun.com/alpine
# VERSION_ID 在小版本镜像里是 3.20.10 这种，而 apk 仓库只按分支发（v3.20），
# 所以只取前两段。候选源依次试，第一个拉到包的就用它，重复的跳过。
RUN . /etc/os-release \
    && major="${VERSION_ID%%.*}" && rest="${VERSION_ID#*.}" && v="v${major}.${rest%%.*}" \
    && ok="" && tried=" " \
    && for m in "${ALPINE_MIRROR}" https://mirrors.aliyun.com/alpine \
                  https://mirrors.tuna.tsinghua.edu.cn/alpine \
                  https://mirrors.ustc.edu.cn/alpine https://dl-cdn.alpinelinux.org/alpine; do \
         case "${tried}" in *" ${m} "*) continue;; esac; \
         tried="${tried}${m} "; \
         printf '%s/%s/main\n%s/%s/community\n' "${m}" "${v}" "${m}" "${v}" > /etc/apk/repositories; \
         echo "试 ${v} 分支：${m}"; \
         apk add --no-cache --timeout 60 python3 ffmpeg tzdata && { ok=1; break; }; \
       done; \
    [ -n "${ok}" ] || { echo "五个 apk 源都没拉到 ${v} 分支的包"; exit 1; }

WORKDIR /srv/omniplay-sidecar
COPY sidecar.py ./
COPY web ./web

RUN mkdir -p /var/lib/sidecar

ENV SIDECAR_ROOTS=/media
ENV SIDECAR_DB=/var/lib/sidecar/state.db
ENV SIDECAR_LANGS=zh
ENV SIDECAR_PORT=45733
ENV SIDECAR_INTERVAL=900

VOLUME ["/var/lib/sidecar", "/media"]
EXPOSE 45733

HEALTHCHECK --interval=60s --timeout=5s --start-period=10s \
    CMD python3 -c "import urllib.request,os;urllib.request.urlopen('http://127.0.0.1:'+os.environ.get('SIDECAR_PORT','45733')+'/health',timeout=4)"

ENTRYPOINT ["python3", "/srv/omniplay-sidecar/sidecar.py"]
