package com.omniplay.tv.phone;

import android.app.Activity;
import android.os.Handler;

import com.omniplay.tv.data.LocalPlayApi;
import com.omniplay.tv.ui.ImageLoader;

import java.util.concurrent.ExecutorService;

/**
 * 手机端弹层（媒体源、设置、手动匹配、自定义编辑）向宿主活动要的那几样东西。
 * 弹层自己不认识界面栈，只知道「做完事请宿主刷新」。
 */
public interface PhoneHost {
    Activity activity();

    LocalPlayApi api();

    ImageLoader images();

    ExecutorService io();

    Handler main();

    /** 媒体源或资料变了：首页与当前详情页都要重新拉一遍。 */
    com.omniplay.tv.local.SavedConnections savedConnections();

    void refreshLibrary();

    void toast(String message);
}
