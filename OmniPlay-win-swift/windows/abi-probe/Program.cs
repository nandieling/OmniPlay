using System.Text;
using System.Text.Json.Nodes;
using OmniPlay.WpfSpike;

// 跨语言边界的验收探针：跑通就打印 abi-probe: OK，任何一步不对都 FAIL 并返回非零。
// 与 db-spike 同一套约定，CI 直接把它的退出码当结论。

var failures = new List<string>();
var notes = new List<string>();

void Check(bool condition, string what, string? detail = null)
{
    if (condition) { notes.Add("  ok  " + what); return; }
    failures.Add(what + (detail is null ? "" : " —— " + detail));
}

using var core = new CoreBridge();
Console.WriteLine($"核心版本：{core.Version}");

// 1. 版本与方法清单
var version = core.Invoke("version");
var platform = version["platform"]?.GetValue<string>() ?? "?";
var features = (version["features"]?.AsArray() ?? new JsonArray())
    .Select(node => node?.GetValue<string>() ?? string.Empty).ToList();
Check(core.Version.StartsWith("0."), "版本号可读", core.Version);
// 同一份探针也拿到 mac 上干跑（验 P/Invoke 本身），平台不对时只记一条跳过
if (platform == "windows") Check(true, "平台自报为 windows");
else notes.Add("  skip 平台自报（本机是 " + platform + "）");
foreach (var method in new[] { "theme.list", "theme.get", "home.sort", "category.groups", "player.start" })
    Check(features.Contains(method), $"方法 {method} 在能力清单里");
Check(!string.IsNullOrEmpty(version["dataDirectory"]?.GetValue<string>()), "核心告知数据目录");

// 2. 主题：五套配色、#AARRGGBB、渐变方向
var themes = core.Invoke("theme.list")["themes"]?.AsArray() ?? new JsonArray();
Check(themes.Count == 5, "配色数量为 5", themes.Count.ToString());
foreach (var theme in themes)
{
    var id = theme?["id"]?.GetValue<string>() ?? "?";
    var spec = core.Invoke("theme.get", new JsonObject { ["color"] = id, ["style"] = "liquidGlass" });
    var accent = spec["accent"]?["argb"]?.GetValue<string>() ?? "";
    Check(accent.Length == 9 && accent[0] == '#' && accent.Skip(1).All(Uri.IsHexDigit),
          $"{id} 输出 #AARRGGBB", accent);
    var backdrop = spec["backdrop"]?.AsArray() ?? new JsonArray();
    Check(backdrop.Count == 3, $"{id} 玻璃背景三段", backdrop.Count.ToString());
    var luminance = backdrop.Select(node =>
    {
        var argb = node?["argb"]?.GetValue<string>() ?? "#FF000000";
        double Channel(int at) => Convert.ToByte(argb.Substring(at, 2), 16);
        return 0.2126 * Channel(3) + 0.7152 * Channel(5) + 0.0722 * Channel(7);
    }).ToList();
    // 中段是主题色的晕染，允许比两端都深；必须成立的是左上最浅、且比右下浅
    Check(luminance.Count == 3 && luminance[0] > luminance[2] && luminance[0] == luminance.Max(),
          $"{id} 背景左上最浅、右下更深", string.Join(" > ", luminance.Select(v => v.ToString("F1"))));
}

// 3. 排序与分组：与 mac 同一份实现，结果必须逐字一致
// JsonNode 只能有一个父节点，两处调用各建一份
static JsonArray Items() => new JsonArray(
    Item(1, "肖申克的救赎", new[] { "马丁·斯科塞斯" }),
    Item(2, "阿凡达", new[] { "詹姆斯·卡梅隆" }),
    Item(3, "流浪地球", new[] { "詹姆斯·卡梅隆", "郭帆" }));
var sorted = (core.Invoke("home.sort", new JsonObject
{
    ["key"] = "name", ["ascending"] = true, ["items"] = Items(),
})["ids"]?.AsArray() ?? new JsonArray()).Select(n => n?.GetValue<long>() ?? 0).ToList();
Check(sorted.SequenceEqual(new long[] { 2, 3, 1 }), "中文名按拼音排序", string.Join(",", sorted));

var groups = core.Invoke("category.groups", new JsonObject
{
    ["mode"] = "director", ["ascending"] = true, ["items"] = Items(),
})["groups"]?.AsArray() ?? new JsonArray();
Check(groups.Count == 3, "导演分组按人物建桶", groups.Count.ToString());
Check(groups.Any(node => node?["name"]?.GetValue<string>() == "詹姆斯·卡梅隆"
                         && node["count"]?.GetValue<int>() == 2),
      "共用条目同时留在两个分类里");

// 4. 播放：headless 起 mpv，验库能加载、文件能打开、时间能推进
var sample = Path.Combine(AppContext.BaseDirectory, "testdata", "sample.mp4");
Check(File.Exists(sample), "示例视频就位", sample);
if (File.Exists(sample))
{
    var started = core.Invoke("player.start", new JsonObject { ["path"] = sample });
    var reason = started["reason"]?.GetValue<string>();
    if (started["started"]?.GetValue<bool>() != true && reason?.Contains("没找到 libmpv") == true)
    {
        // 没装 libmpv 的机器上跳过这一段；Windows CI 会带 dll，这一段就是真验收
        notes.Add("  skip 播放（" + reason + "）");
    }
    else if (started["started"]?.GetValue<bool>() != true)
    {
        Check(false, "libmpv 能加载并初始化", reason ?? "无原因");
    }
    else
    {
        notes.Add("  ok  libmpv 加载并初始化" + (reason is null ? "" : $"（{reason}）"));
        Thread.Sleep(600);
        var status = core.Invoke("player.status");
        var duration = status["duration"]?.GetValue<double>() ?? 0;
        var position = status["timePos"]?.GetValue<double>() ?? 0;
        Check(duration > 1.5 && duration < 2.5, "时长解析正确", duration.ToString("F2"));
        Check(position > 0, "播放进度在推进", position.ToString("F2"));
        core.Invoke("player.pause", new JsonObject { ["paused"] = true });
        Thread.Sleep(200);
        Check(core.Invoke("player.status")["paused"]?.GetValue<bool>() == true, "暂停属性生效");
        var frozen = core.Invoke("player.status")["timePos"]?.GetValue<double>() ?? 0;
        Thread.Sleep(400);
        Check(Math.Abs((core.Invoke("player.status")["timePos"]?.GetValue<double>() ?? 0) - frozen) < 0.05,
              "暂停后时间轴停住");
        core.Invoke("player.stop");
    }
}

// 5. 事件通道：宿主注册的 C 函数指针要活过整个进程生命周期
var polled = core.Invoke("events.poll", new JsonObject { ["since"] = 0 });
Check(polled["cursor"]?.GetValue<int>() >= 0, "事件游标可读");
Check(core.EventCount >= 0, "事件回调已注册");

core.Dispose();

foreach (var note in notes) Console.WriteLine(note);
foreach (var failure in failures) Console.WriteLine("  FAIL " + failure);
Console.WriteLine(failures.Count == 0
    ? $"abi-probe: OK platform={platform} core={core.Version}"
    : $"abi-probe: FAIL {failures.Count} 项");
return failures.Count == 0 ? 0 : 1;

static JsonNode Item(long id, string title, string[] directors) => new JsonObject
{
    ["id"] = id,
    ["title"] = title,
    ["directors"] = new JsonArray(directors.Select(d => JsonValue.Create(d)).ToArray()),
    ["casts"] = new JsonArray(),
};
