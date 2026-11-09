package com.omniplay.tv.ui;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * 「使用指南」与「关于 / 开源」的正文。mac 版是两块独立页面（UsageGuideView、
 * OpenSourceInfoView），安卓这边只有弹层，就把正文收在这儿，手机与电视共用一份，
 * 免得两边各写一套说明越改越不一样。
 */
public final class GuideContent {
    public static final class Section {
        public final String title;
        public final String body;

        Section(String title, String body) {
            this.title = title;
            this.body = body;
        }
    }

    private GuideContent() {
    }

    public static List<Section> guide() {
        return Arrays.asList(
                new Section("一 · 添加媒体源",
                        "首页右上角「媒体源」里添加本地目录、SMB 共享或 WebDAV。局域网里自动扫出的"
                                + "SMB 与 WebDAV，点一下先登录、再选要挂载的文件夹；挂载完就在后台扫描"
                                + "入库，海报一张张补到墙上，不用等全部刮完才看得见面。"),
                new Section("二 · 扫描与刮削",
                        "右上角「扫描刮削」随时重扫目录并补刮缺的元数据，左上角那一行是进度：扫到多少"
                                + "文件、入库多少部、正在刮第几部。刮错的片在详情页右上角用「手动匹配」"
                                + "改名重绑，没填 TMDB Key 也能只用豆瓣刮削；「自定义编辑」能手改片名、"
                                + "年份和海报。"),
                new Section("三 · 播放模式与字幕",
                        "播放页右下角在「兼容」和「高性能直出」之间切。1080p 和大部分片源用兼容模式，"
                                + "内封字幕（含 ASS）都能显示；4K 蓝光和 4K MKV 建议直出，整条流直接进"
                                + "硬件解码器不卡。直出模式下内封的 PGS 位图字幕不上屏——在 NAS 上跑"
                                + "「OmniPlay 字幕转档」把内封字幕转成视频旁边的 .srt / .ass / .sup，"
                                + "字幕菜单会自动列出这些外挂文件，蓝光盘则按分卷各存一份。"),
                new Section("四 · 遥控器与触屏",
                        "长按左右方向键快进快退，按住越久步长越大。字幕、音轨、播放模式都在播放页底部"
                                + "的菜单里，选中一行直接按确认。输入框里用左右方向键移动光标，删除键"
                                + "才跟手。"),
                new Section("五 · 用手机代替遥控器打字",
                        "电视上任何输入框都有一条「手机输入」：手机连同一个局域网，扫屏幕上那个地址打开"
                                + "页面，在手机上打长链接和密码，点确定就回填到电视。改 TMDB Key、绑定"
                                + "豆瓣链接这类长文本都用它，比遥控器一个字母一个字母敲快得多。"),
                new Section("六 · 主题",
                        "设置里的主题与 mac 版是同一套配色（含深色），海报墙、详情页、播放器一起跟着换。"
                                + "电视在暗环境里看建议用午夜蓝或纯黑。"));
    }

    /** 关于 / 开源：{@code versionLine} 由调用方填，各端自己的版本号写法不一样。 */
    public static List<Section> about(String versionLine) {
        return Collections.singletonList(
                new Section("元数据来源",
                        "标题、海报、简介与分集来自 TMDB（可整站改道到你自己的反代网关）；豆瓣用于兜底"
                                + "与手动匹配，绑过豆瓣条目的会在评分里并排显示豆瓣分。\n"
                                + "内置的开源组件：libmpv（GPL-3.0）播放内核，兼容模式与高性能直出都跑在"
                                + "它上面；jcifs-ng（LGPL-2.1）SMB / CIFS 客户端；ZXing core（Apache-2.0）"
                                + "手机输入页的二维码；媒体库为本机 SQLite。\n"
                                + versionLine));
    }
}
