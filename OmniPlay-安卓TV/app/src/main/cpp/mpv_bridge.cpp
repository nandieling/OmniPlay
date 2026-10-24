#include <android/log.h>
#include <dlfcn.h>
#include <jni.h>

#include <cerrno>
#include <cctype>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>
#include <thread>

#define LOG_TAG "OmniPlayMpv"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

struct mpv_handle;

static constexpr int MPV_FORMAT_STRING = 1;
static constexpr int MPV_FORMAT_INT64 = 4;
static constexpr int MPV_FORMAT_DOUBLE = 5;
static constexpr int MPV_EVENT_NONE = 0;
static constexpr int MPV_EVENT_SHUTDOWN = 1;
static constexpr int MPV_EVENT_LOG_MESSAGE = 2;
static constexpr int MPV_EVENT_START_FILE = 6;
static constexpr int MPV_EVENT_END_FILE = 7;
static constexpr int MPV_EVENT_FILE_LOADED = 8;
static constexpr int MPV_EVENT_IDLE = 11;
static constexpr int MPV_EVENT_VIDEO_RECONFIG = 17;
static constexpr int MPV_EVENT_AUDIO_RECONFIG = 18;
static constexpr int MPV_EVENT_PLAYBACK_RESTART = 21;

struct mpv_event {
    int event_id;
    int error;
    uint64_t reply_userdata;
    void *data;
};

struct mpv_event_log_message {
    const char *prefix;
    const char *level;
    const char *text;
    int log_level;
};

struct mpv_event_end_file {
    int reason;
    int error;
    int playlist_entry_id;
    int playlist_insert_id;
    int playlist_insert_num_entries;
};

using mpv_create_fn = mpv_handle *(*)();
using mpv_initialize_fn = int (*)(mpv_handle *);
using mpv_set_option_fn = int (*)(mpv_handle *, const char *, int, void *);
using mpv_set_option_string_fn = int (*)(mpv_handle *, const char *, const char *);
using mpv_set_property_fn = int (*)(mpv_handle *, const char *, int, void *);
using mpv_set_property_string_fn = int (*)(mpv_handle *, const char *, const char *);
using mpv_get_property_fn = int (*)(mpv_handle *, const char *, int, void *);
using mpv_command_fn = int (*)(mpv_handle *, const char **);
using mpv_error_string_fn = const char *(*)(int);
using mpv_free_fn = void (*)(void *);
using mpv_request_log_messages_fn = int (*)(mpv_handle *, const char *);
using mpv_wait_event_fn = mpv_event *(*)(mpv_handle *, double);
using mpv_wakeup_fn = void (*)(mpv_handle *);
using mpv_terminate_destroy_fn = void (*)(mpv_handle *);
using av_jni_set_java_vm_fn = int (*)(void *, void *);

static JavaVM *g_java_vm = nullptr;

struct Bridge {
    void *lib = nullptr;
    mpv_handle *mpv = nullptr;
    jobject surface = nullptr;
    jobject event_listener = nullptr;
    jmethodID event_method = nullptr;
    int surface_width = 0;
    int surface_height = 0;
    bool initialized = false;
    bool direct_playback_mode = false;
    std::mutex event_listener_mutex;

    mpv_create_fn mpv_create = nullptr;
    mpv_initialize_fn mpv_initialize = nullptr;
    mpv_set_option_fn mpv_set_option = nullptr;
    mpv_set_option_string_fn mpv_set_option_string = nullptr;
    mpv_set_property_fn mpv_set_property = nullptr;
    mpv_set_property_string_fn mpv_set_property_string = nullptr;
    mpv_get_property_fn mpv_get_property = nullptr;
    mpv_command_fn mpv_command = nullptr;
    mpv_error_string_fn mpv_error_string = nullptr;
    mpv_free_fn mpv_free = nullptr;
    mpv_request_log_messages_fn mpv_request_log_messages = nullptr;
    mpv_wait_event_fn mpv_wait_event = nullptr;
    mpv_wakeup_fn mpv_wakeup = nullptr;
    mpv_terminate_destroy_fn mpv_terminate_destroy = nullptr;
    std::atomic_bool event_thread_running{false};
    std::thread event_thread;
    std::string last_error;
};

static std::string to_string(JNIEnv *env, jstring value) {
    if (value == nullptr) {
        return {};
    }

    const char *chars = env->GetStringUTFChars(value, nullptr);
    std::string result = chars == nullptr ? "" : chars;
    if (chars != nullptr) {
        env->ReleaseStringUTFChars(value, chars);
    }
    return result;
}

static void *symbol(Bridge *bridge, const char *name) {
    void *result = dlsym(bridge->lib, name);
    if (result == nullptr) {
        LOGE("Missing libmpv symbol: %s", name);
    }
    return result;
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *) {
    g_java_vm = vm;

    void *avcodec = dlopen("libavcodec.so", RTLD_NOW | RTLD_GLOBAL);
    auto av_jni_set_java_vm = reinterpret_cast<av_jni_set_java_vm_fn>(
            dlsym(avcodec == nullptr ? RTLD_DEFAULT : avcodec, "av_jni_set_java_vm"));
    if (av_jni_set_java_vm == nullptr) {
        LOGE("Missing FFmpeg symbol: av_jni_set_java_vm");
    } else {
        int result = av_jni_set_java_vm(vm, nullptr);
        if (result < 0) {
            LOGE("av_jni_set_java_vm failed: %d", result);
        } else {
            LOGI("Registered JavaVM for FFmpeg/libmpv");
        }
    }

    return JNI_VERSION_1_6;
}

static void set_last_error(Bridge *bridge, const std::string &message) {
    if (bridge != nullptr) {
        bridge->last_error = message;
    }
}

static std::string mpv_error_detail(Bridge *bridge, int result) {
    if (bridge != nullptr && bridge->mpv_error_string != nullptr) {
        const char *message = bridge->mpv_error_string(result);
        if (message != nullptr && message[0] != '\0') {
            return std::string(message) + " (" + std::to_string(result) + ")";
        }
    }

    return std::to_string(result);
}

static bool parse_double_value(const char *text, double *value) {
    if (text == nullptr || value == nullptr) {
        return false;
    }

    errno = 0;
    char *end = nullptr;
    double parsed = std::strtod(text, &end);
    if (end == text || errno == ERANGE) {
        return false;
    }

    while (end != nullptr && *end != '\0' && std::isspace(static_cast<unsigned char>(*end)) != 0) {
        end++;
    }
    if (end != nullptr && *end != '\0') {
        return false;
    }

    *value = parsed;
    return true;
}

static const char *event_name(int event_id) {
    switch (event_id) {
        case MPV_EVENT_SHUTDOWN:
            return "shutdown";
        case MPV_EVENT_START_FILE:
            return "start-file";
        case MPV_EVENT_END_FILE:
            return "end-file";
        case MPV_EVENT_FILE_LOADED:
            return "file-loaded";
        case MPV_EVENT_IDLE:
            return "idle";
        case MPV_EVENT_VIDEO_RECONFIG:
            return "video-reconfig";
        case MPV_EVENT_AUDIO_RECONFIG:
            return "audio-reconfig";
        case MPV_EVENT_PLAYBACK_RESTART:
            return "playback-restart";
        default:
            return "event";
    }
}

static const char *end_file_reason(int reason) {
    switch (reason) {
        case 0:
            return "eof";
        case 2:
            return "stop";
        case 3:
            return "quit";
        case 4:
            return "error";
        case 5:
            return "redirect";
        default:
            return "unknown";
    }
}

static void send_event(Bridge *bridge, const char *event_name, const char *detail);

static void log_mpv_event(Bridge *bridge, mpv_event *event) {
    if (event == nullptr || event->event_id == MPV_EVENT_NONE) {
        return;
    }

    if (event->event_id == MPV_EVENT_LOG_MESSAGE) {
        auto *message = reinterpret_cast<mpv_event_log_message *>(event->data);
        if (message != nullptr && message->text != nullptr) {
            const char *prefix = message->prefix == nullptr ? "mpv" : message->prefix;
            const char *level = message->level == nullptr ? "info" : message->level;
            LOGI("[%s/%s] %s", prefix, level, message->text);
        }
        return;
    }

    if (event->event_id == MPV_EVENT_END_FILE) {
        auto *endFile = reinterpret_cast<mpv_event_end_file *>(event->data);
        if (endFile != nullptr) {
            if (endFile->error < 0) {
                std::string detail = "播放失败：" + mpv_error_detail(bridge, endFile->error);
                set_last_error(bridge, detail);
                send_event(bridge, "error", detail.c_str());
                LOGE("mpv event end-file reason=%s error=%s",
                     end_file_reason(endFile->reason),
                     mpv_error_detail(bridge, endFile->error).c_str());
            } else {
                LOGI("mpv event end-file reason=%s", end_file_reason(endFile->reason));
            }
        } else {
            LOGI("mpv event end-file");
        }
        return;
    }

    if (event->event_id == MPV_EVENT_PLAYBACK_RESTART) {
        send_event(bridge, "playback-restart", "");
    }

    if (event->error < 0) {
        LOGE("mpv event %s error=%s", event_name(event->event_id), mpv_error_detail(bridge, event->error).c_str());
    } else {
        LOGI("mpv event %s", event_name(event->event_id));
    }
}

static void start_event_thread(Bridge *bridge) {
    if (bridge == nullptr ||
        bridge->mpv == nullptr ||
        bridge->mpv_wait_event == nullptr ||
        bridge->event_thread_running.load()) {
        return;
    }

    bridge->event_thread_running = true;
    bridge->event_thread = std::thread([bridge]() {
        LOGI("mpv event thread started");
        while (bridge->event_thread_running.load()) {
            mpv_event *event = bridge->mpv_wait_event(bridge->mpv, 0.25);
            log_mpv_event(bridge, event);
        }
        LOGI("mpv event thread stopped");
    });
}

static void stop_event_thread(Bridge *bridge) {
    if (bridge == nullptr || !bridge->event_thread_running.load()) {
        return;
    }

    bridge->event_thread_running = false;
    if (bridge->mpv != nullptr && bridge->mpv_wakeup != nullptr) {
        bridge->mpv_wakeup(bridge->mpv);
    }
    if (bridge->event_thread.joinable()) {
        bridge->event_thread.join();
    }
}

static void send_event(Bridge *bridge, const char *event_name, const char *detail) {
    if (bridge == nullptr || event_name == nullptr || g_java_vm == nullptr) {
        return;
    }

    JNIEnv *env = nullptr;
    bool detach = false;
    if (g_java_vm->GetEnv(reinterpret_cast<void **>(&env), JNI_VERSION_1_6) != JNI_OK) {
        if (g_java_vm->AttachCurrentThread(&env, nullptr) != JNI_OK) {
            return;
        }
        detach = true;
    }

    jobject listener = nullptr;
    jmethodID method = nullptr;
    {
        std::lock_guard<std::mutex> lock(bridge->event_listener_mutex);
        listener = bridge->event_listener == nullptr ? nullptr : env->NewLocalRef(bridge->event_listener);
        method = bridge->event_method;
    }

    if (listener != nullptr && method != nullptr) {
        jstring name = env->NewStringUTF(event_name);
        jstring message = env->NewStringUTF(detail == nullptr ? "" : detail);
        env->CallVoidMethod(listener, method, name, message);
        env->DeleteLocalRef(name);
        env->DeleteLocalRef(message);
        if (env->ExceptionCheck()) {
            env->ExceptionClear();
        }
    }
    if (listener != nullptr) {
        env->DeleteLocalRef(listener);
    }

    if (detach) {
        g_java_vm->DetachCurrentThread();
    }
}

static bool load_libmpv(Bridge *bridge) {
    if (bridge->lib != nullptr) {
        return true;
    }

    bridge->lib = dlopen("libmpv.so", RTLD_NOW | RTLD_GLOBAL);
    if (bridge->lib == nullptr) {
        LOGE("dlopen libmpv.so failed: %s", dlerror());
        return false;
    }

    bridge->mpv_create = reinterpret_cast<mpv_create_fn>(symbol(bridge, "mpv_create"));
    bridge->mpv_initialize = reinterpret_cast<mpv_initialize_fn>(symbol(bridge, "mpv_initialize"));
    bridge->mpv_set_option = reinterpret_cast<mpv_set_option_fn>(symbol(bridge, "mpv_set_option"));
    bridge->mpv_set_option_string = reinterpret_cast<mpv_set_option_string_fn>(symbol(bridge, "mpv_set_option_string"));
    bridge->mpv_set_property = reinterpret_cast<mpv_set_property_fn>(symbol(bridge, "mpv_set_property"));
    bridge->mpv_set_property_string = reinterpret_cast<mpv_set_property_string_fn>(dlsym(bridge->lib, "mpv_set_property_string"));
    bridge->mpv_get_property = reinterpret_cast<mpv_get_property_fn>(symbol(bridge, "mpv_get_property"));
    bridge->mpv_command = reinterpret_cast<mpv_command_fn>(symbol(bridge, "mpv_command"));
    bridge->mpv_error_string = reinterpret_cast<mpv_error_string_fn>(dlsym(bridge->lib, "mpv_error_string"));
    bridge->mpv_free = reinterpret_cast<mpv_free_fn>(dlsym(bridge->lib, "mpv_free"));
    bridge->mpv_request_log_messages = reinterpret_cast<mpv_request_log_messages_fn>(dlsym(bridge->lib, "mpv_request_log_messages"));
    bridge->mpv_wait_event = reinterpret_cast<mpv_wait_event_fn>(dlsym(bridge->lib, "mpv_wait_event"));
    bridge->mpv_wakeup = reinterpret_cast<mpv_wakeup_fn>(dlsym(bridge->lib, "mpv_wakeup"));
    bridge->mpv_terminate_destroy = reinterpret_cast<mpv_terminate_destroy_fn>(symbol(bridge, "mpv_terminate_destroy"));

    return bridge->mpv_create != nullptr &&
           bridge->mpv_initialize != nullptr &&
           bridge->mpv_set_option != nullptr &&
           bridge->mpv_set_option_string != nullptr &&
           bridge->mpv_set_property != nullptr &&
           bridge->mpv_get_property != nullptr &&
           bridge->mpv_command != nullptr &&
           bridge->mpv_terminate_destroy != nullptr;
}

static void set_option(Bridge *bridge, const char *name, const char *value) {
    if (bridge->mpv != nullptr && bridge->mpv_set_option_string != nullptr && value != nullptr) {
        bridge->mpv_set_option_string(bridge->mpv, name, value);
    }
}

static void set_string(Bridge *bridge, const char *name, const char *value) {
    if (bridge->mpv == nullptr || value == nullptr) {
        return;
    }

    int result = bridge->initialized && bridge->mpv_set_property_string != nullptr
            ? bridge->mpv_set_property_string(bridge->mpv, name, value)
            : -1;
    if (result < 0 && bridge->mpv_set_option_string != nullptr) {
        result = bridge->mpv_set_option_string(bridge->mpv, name, value);
    }
    if (result < 0) {
        LOGE("mpv set %s failed: %d", name, result);
    }
}

static void set_wid(Bridge *bridge, jobject surface) {
    if (bridge->mpv == nullptr || bridge->mpv_set_option == nullptr || bridge->mpv_set_property == nullptr) {
        return;
    }

    int64_t wid = reinterpret_cast<intptr_t>(surface);
    int result = 0;
    if (bridge->initialized) {
        result = bridge->mpv_set_property(bridge->mpv, "wid", MPV_FORMAT_INT64, &wid);
    } else {
        result = bridge->mpv_set_option(bridge->mpv, "wid", MPV_FORMAT_INT64, &wid);
    }

    if (result < 0) {
        LOGE("mpv set wid failed: %s", mpv_error_detail(bridge, result).c_str());
    } else {
        LOGI("Set mpv wid=%s before_initialize=%s", surface == nullptr ? "null" : "surface", bridge->initialized ? "no" : "yes");
    }
}

static void set_surface_size(Bridge *bridge, int width, int height) {
    if (bridge == nullptr || width <= 0 || height <= 0) {
        return;
    }

    bridge->surface_width = width;
    bridge->surface_height = height;
    if (bridge->mpv == nullptr) {
        return;
    }

    char value[32];
    std::snprintf(value, sizeof(value), "%dx%d", width, height);
    set_string(bridge, "android-surface-size", value);
    LOGI("Set android-surface-size=%s", value);
}

static bool command(Bridge *bridge, const char **args) {
    if (bridge->mpv == nullptr || bridge->mpv_command == nullptr) {
        return false;
    }

    int result = bridge->mpv_command(bridge->mpv, args);
    if (result < 0) {
        std::string name = args != nullptr && args[0] != nullptr ? args[0] : "<null>";
        std::string detail = "mpv command " + name + " failed: " + mpv_error_detail(bridge, result);
        set_last_error(bridge, detail);
        LOGE("%s", detail.c_str());
        return false;
    }
    return true;
}

extern "C" JNIEXPORT jlong JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeCreate(JNIEnv *, jclass) {
    return reinterpret_cast<jlong>(new Bridge());
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeInitialize(JNIEnv *, jclass, jlong handle) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr) {
        return JNI_FALSE;
    }

    if (bridge->initialized) {
        return JNI_TRUE;
    }

    if (!load_libmpv(bridge)) {
        set_last_error(bridge, "无法加载 APK 内的 libmpv.so。");
        return JNI_FALSE;
    }

    bridge->mpv = bridge->mpv_create();
    if (bridge->mpv == nullptr) {
        LOGE("mpv_create failed");
        set_last_error(bridge, "mpv_create failed");
        return JNI_FALSE;
    }

    set_option(bridge, "terminal", "no");
    set_option(bridge, "msg-level", "all=warn");
    set_option(bridge, "cache", "yes");
    set_option(bridge, "demuxer-max-bytes", "128MiB");
    set_option(bridge, "demuxer-max-back-bytes", "32MiB");
    set_option(bridge, "demuxer-readahead-secs", "20");
    if (bridge->direct_playback_mode) {
        set_option(bridge, "vo", "mediacodec_embed");
        set_option(bridge, "hwdec", "mediacodec");
        set_option(bridge, "vd-lavc-dr", "yes");
        set_option(bridge, "hwdec-codecs", "all");
    } else {
        set_option(bridge, "vo", "gpu");
        set_option(bridge, "gpu-context", "android");
        set_option(bridge, "opengl-es", "yes");
        set_option(bridge, "hwdec", "mediacodec-copy");
        set_option(bridge, "vd-lavc-dr", "no");
        set_option(bridge, "hwdec-codecs", "all");
        set_option(bridge, "scale", "bilinear");
        set_option(bridge, "cscale", "bilinear");
        set_option(bridge, "dscale", "bilinear");
        set_option(bridge, "correct-downscaling", "no");
        set_option(bridge, "sigmoid-upscaling", "no");
        set_option(bridge, "deband", "no");
        set_option(bridge, "target-prim", "bt.709");
        set_option(bridge, "target-trc", "bt.1886");
        set_option(bridge, "tone-mapping", "bt.2446a");
        // 4K HDR 在电视 GPU 上最贵的是逐帧峰值探测与高位宽 FBO。文件自带 MaxCLL /
        // mastering display 元数据时 auto 会直接用元数据，省掉逐帧回读；FBO 退到
        // rgb10_a2、抖动按 8bit 算，字幕层也不再混进视频帧里多 blend 一次。
        set_option(bridge, "hdr-compute-peak", "auto");
        set_option(bridge, "fbo-format", "rgb10_a2");
        set_option(bridge, "dither-depth", "8");
        set_option(bridge, "blend-subtitles", "no");
        set_option(bridge, "sub-gauss", "0");
    }
    set_option(bridge, "ao", "audiotrack");
    set_option(bridge, "osc", "no");
    set_option(bridge, "input-default-bindings", "no");
    set_option(bridge, "audio-display", "no");
    set_option(bridge, "keep-open", "no");
    set_option(bridge, "sub-visibility", "yes");
    set_option(bridge, "sub-auto", "all");
    set_option(bridge, "sid", "auto");
    set_option(bridge, "slang", "zh,zh-CN,zh-Hans,zh-Hant,chi,chs,cht,cn,en");
    set_option(bridge, "subs-with-matching-audio", "yes");
    set_option(bridge, "sub-forced-only", "no");
    set_option(bridge, "sub-font-size", "42");
    set_option(bridge, "sub-use-margins", "yes");
    set_option(bridge, "demuxer-mkv-subtitle-preroll", "yes");

    if (bridge->surface != nullptr) {
        set_wid(bridge, bridge->surface);
    }
    if (bridge->surface_width > 0 && bridge->surface_height > 0) {
        set_surface_size(bridge, bridge->surface_width, bridge->surface_height);
    }

    int initializeResult = bridge->mpv_initialize(bridge->mpv);
    if (initializeResult < 0) {
        std::string detail = "mpv_initialize failed: " + mpv_error_detail(bridge, initializeResult);
        LOGE("%s", detail.c_str());
        set_last_error(bridge, detail);
        bridge->mpv_terminate_destroy(bridge->mpv);
        bridge->mpv = nullptr;
        return JNI_FALSE;
    }

    bridge->initialized = true;
    if (bridge->mpv_request_log_messages != nullptr) {
        bridge->mpv_request_log_messages(bridge->mpv, "info");
    }
    start_event_thread(bridge);
    if (bridge->surface != nullptr) {
        set_wid(bridge, bridge->surface);
    }
    if (bridge->surface_width > 0 && bridge->surface_height > 0) {
        set_surface_size(bridge, bridge->surface_width, bridge->surface_height);
    }
    return JNI_TRUE;
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSetPlaybackMode(JNIEnv *, jclass, jlong handle, jboolean directMode) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->initialized) {
        return;
    }

    bridge->direct_playback_mode = directMode == JNI_TRUE;
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSetEventListener(
        JNIEnv *env,
        jclass,
        jlong handle,
        jobject listener) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr) {
        return;
    }

    jobject nextListener = listener == nullptr ? nullptr : env->NewGlobalRef(listener);
    jmethodID nextMethod = nullptr;
    if (nextListener != nullptr) {
        jclass listenerClass = env->GetObjectClass(listener);
        nextMethod = env->GetMethodID(listenerClass, "onMpvEvent", "(Ljava/lang/String;Ljava/lang/String;)V");
        env->DeleteLocalRef(listenerClass);
        if (nextMethod == nullptr) {
            env->DeleteGlobalRef(nextListener);
            return;
        }
    }

    jobject previousListener = nullptr;
    {
        std::lock_guard<std::mutex> lock(bridge->event_listener_mutex);
        previousListener = bridge->event_listener;
        bridge->event_listener = nextListener;
        bridge->event_method = nextMethod;
    }
    if (previousListener != nullptr) {
        env->DeleteGlobalRef(previousListener);
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeAttachSurface(
        JNIEnv *env,
        jclass,
        jlong handle,
        jobject surface,
        jint width,
        jint height) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr) {
        return;
    }

    set_surface_size(bridge, width, height);

    jobject nextSurface = surface == nullptr ? nullptr : env->NewGlobalRef(surface);
    if (bridge->surface != nullptr) {
        set_wid(bridge, nullptr);
        env->DeleteGlobalRef(bridge->surface);
    }

    bridge->surface = nextSurface;
    if (bridge->surface != nullptr) {
        set_wid(bridge, bridge->surface);
        set_surface_size(bridge, width, height);
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeDetachSurface(JNIEnv *env, jclass, jlong handle) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->surface == nullptr) {
        return;
    }

    set_wid(bridge, nullptr);
    env->DeleteGlobalRef(bridge->surface);
    bridge->surface = nullptr;
    bridge->surface_width = 0;
    bridge->surface_height = 0;
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeLoad(
        JNIEnv *env,
        jclass,
        jlong handle,
        jstring url,
        jstring cookieHeader,
        jstring userAgent,
        jdouble startSeconds,
        jboolean isoPlayback) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->mpv == nullptr) {
        return JNI_FALSE;
    }

    std::string nativeUrl = to_string(env, url);
    std::string nativeCookie = to_string(env, cookieHeader);
    std::string nativeUserAgent = to_string(env, userAgent);
    if (nativeUrl.empty()) {
        set_last_error(bridge, "播放地址为空。");
        return JNI_FALSE;
    }

    bridge->last_error.clear();
    if (!nativeUserAgent.empty()) {
        set_string(bridge, "user-agent", nativeUserAgent.c_str());
    }
    if (!nativeCookie.empty()) {
        std::string header = "Cookie: " + nativeCookie;
        set_string(bridge, "http-header-fields", header.c_str());
    }
    char startValue[64];
    std::snprintf(
            startValue,
            sizeof(startValue),
            "%.3f",
            startSeconds > 0.5 ? static_cast<double>(startSeconds) : 0.0);
    set_string(bridge, "start", startValue);

    if (isoPlayback == JNI_TRUE) {
        set_string(bridge, "bluray-device", nativeUrl.c_str());
        const char *bdLongestArgs[] = {"loadfile", "bd://longest", "replace", nullptr};
        if (command(bridge, bdLongestArgs)) {
            return JNI_TRUE;
        }

        const char *bdArgs[] = {"loadfile", "bd://", "replace", nullptr};
        if (command(bridge, bdArgs)) {
            return JNI_TRUE;
        }

        set_string(bridge, "dvd-device", nativeUrl.c_str());
        const char *dvdArgs[] = {"loadfile", "dvd://", "replace", nullptr};
        if (command(bridge, dvdArgs)) {
            return JNI_TRUE;
        }
    }

    const char *args[] = {"loadfile", nativeUrl.c_str(), "replace", nullptr};
    return command(bridge, args) ? JNI_TRUE : JNI_FALSE;
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeAddSubtitle(
        JNIEnv *env,
        jclass,
        jlong handle,
        jstring url,
        jstring cookieHeader) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->mpv == nullptr) {
        return JNI_FALSE;
    }

    std::string nativeUrl = to_string(env, url);
    std::string nativeCookie = to_string(env, cookieHeader);
    if (nativeUrl.empty()) {
        set_last_error(bridge, "字幕地址为空。");
        return JNI_FALSE;
    }
    if (!nativeCookie.empty()) {
        std::string header = "Cookie: " + nativeCookie;
        set_string(bridge, "http-header-fields", header.c_str());
    }

    const char *args[] = {"sub-add", nativeUrl.c_str(), "select", nullptr};
    return command(bridge, args) ? JNI_TRUE : JNI_FALSE;
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeLastError(JNIEnv *env, jclass, jlong handle) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->last_error.empty()) {
        return env->NewStringUTF("");
    }

    return env->NewStringUTF(bridge->last_error.c_str());
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSetPaused(JNIEnv *, jclass, jlong handle, jboolean paused) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    const char *args[] = {"set", "pause", paused ? "yes" : "no", nullptr};
    command(bridge, args);
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSeek(JNIEnv *, jclass, jlong handle, jdouble seconds) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    char value[32];
    std::snprintf(value, sizeof(value), "%.3f", static_cast<double>(seconds));
    const char *args[] = {"seek", value, "relative+exact", nullptr};
    command(bridge, args);
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSeekAbsolute(JNIEnv *, jclass, jlong handle, jdouble seconds) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    char value[32];
    std::snprintf(value, sizeof(value), "%.3f", static_cast<double>(seconds));
    const char *args[] = {"seek", value, "absolute+exact", nullptr};
    command(bridge, args);
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeSetString(
        JNIEnv *env,
        jclass,
        jlong handle,
        jstring property,
        jstring value) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    std::string nativeProperty = to_string(env, property);
    std::string nativeValue = to_string(env, value);
    if (nativeProperty.empty()) {
        return;
    }

    const char *args[] = {"set", nativeProperty.c_str(), nativeValue.c_str(), nullptr};
    command(bridge, args);
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeGetString(
        JNIEnv *env,
        jclass,
        jlong handle,
        jstring property,
        jstring fallback) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    std::string fallbackValue = to_string(env, fallback);
    if (bridge == nullptr || bridge->mpv == nullptr || bridge->mpv_get_property == nullptr) {
        return env->NewStringUTF(fallbackValue.c_str());
    }

    std::string nativeProperty = to_string(env, property);
    if (nativeProperty.empty()) {
        return env->NewStringUTF(fallbackValue.c_str());
    }

    char *value = nullptr;
    int result = bridge->mpv_get_property(bridge->mpv, nativeProperty.c_str(), MPV_FORMAT_STRING, &value);
    if (result < 0 || value == nullptr) {
        return env->NewStringUTF(fallbackValue.c_str());
    }

    jstring output = env->NewStringUTF(value);
    if (bridge->mpv_free != nullptr) {
        bridge->mpv_free(value);
    }
    return output;
}

extern "C" JNIEXPORT jdouble JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeGetDouble(
        JNIEnv *env,
        jclass,
        jlong handle,
        jstring property,
        jdouble fallback) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr || bridge->mpv == nullptr || bridge->mpv_get_property == nullptr) {
        return fallback;
    }

    std::string nativeProperty = to_string(env, property);
    if (nativeProperty.empty()) {
        return fallback;
    }

    double value = fallback;
    if (bridge->mpv_get_property(bridge->mpv, nativeProperty.c_str(), MPV_FORMAT_DOUBLE, &value) >= 0) {
        return value;
    }

    int64_t intValue = 0;
    if (bridge->mpv_get_property(bridge->mpv, nativeProperty.c_str(), MPV_FORMAT_INT64, &intValue) >= 0) {
        return static_cast<double>(intValue);
    }

    if (bridge->mpv_free != nullptr) {
        char *stringValue = nullptr;
        if (bridge->mpv_get_property(bridge->mpv, nativeProperty.c_str(), MPV_FORMAT_STRING, &stringValue) >= 0) {
            bool parsed = parse_double_value(stringValue, &value);
            bridge->mpv_free(stringValue);
            if (parsed) {
                return value;
            }
        }
    }

    return fallback;
}

extern "C" JNIEXPORT void JNICALL
Java_com_omniplay_tv_player_MpvBridge_nativeDestroy(JNIEnv *env, jclass, jlong handle) {
    auto *bridge = reinterpret_cast<Bridge *>(handle);
    if (bridge == nullptr) {
        return;
    }

    // 先把声音闸掉再拆。直出模式下 terminate_destroy 要等解码线程收工，万一它卡住，
    // 至少这一刻之后已经不会有声音了，不会出现「退出软件还在放声音」。
    if (bridge->mpv != nullptr && bridge->mpv_set_property_string != nullptr) {
        bridge->mpv_set_property_string(bridge->mpv, "pause", "yes");
    }
    if (bridge->surface != nullptr) {
        if (bridge->mpv != nullptr) {
            set_wid(bridge, nullptr);
        }
        env->DeleteGlobalRef(bridge->surface);
        bridge->surface = nullptr;
    }
    if (bridge->mpv != nullptr && bridge->mpv_terminate_destroy != nullptr) {
        stop_event_thread(bridge);
        bridge->mpv_terminate_destroy(bridge->mpv);
        bridge->mpv = nullptr;
    }
    jobject previousListener = nullptr;
    {
        std::lock_guard<std::mutex> lock(bridge->event_listener_mutex);
        previousListener = bridge->event_listener;
        bridge->event_listener = nullptr;
        bridge->event_method = nullptr;
    }
    if (previousListener != nullptr) {
        env->DeleteGlobalRef(previousListener);
    }
    // 不 dlclose：libmpv 卸载时要收尾它自己起的线程，紧挨着 terminate_destroy 卸最容易
    // 出事（切模式时表现为卡死），而且下一次换模式还得把整个库重新定位一遍，白等。
    // 库跟着进程活到结束，代价只是一次映射。

    delete bridge;
}
