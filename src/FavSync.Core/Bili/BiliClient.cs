using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FavSync.Core.Config;

namespace FavSync.Core.Bili;

/// <summary>探测四档（DESIGN.md §4.4）。</summary>
public static class ProbeVerdict
{
    public const string Exact = "exact";     // 稿件存活 + 同 quality + 同 codecid → 允许下载替代
    public const string Lower = "lower";     // 稿件存活但同档不可用 → 默认禁止，用户可显式覆盖
    public const string Gone = "gone";       // -404/-403 / 版权 / 地区 / 会员 → 禁止，只能手机拷贝
    public const string Charge = "charge";   // 充电专属视频：稿件活着，但账号没充电 → 只能手机拷贝
    public const string Unknown = "unknown"; // 探测失败 → 保守等同 gone
}

public sealed record BiliAccount(bool LoggedIn, long Mid, string? Uname);

/// <summary>
/// 一条探测结论。
/// <paramref name="Throttled"/>=true 表示"这次没探出东西，而且原因是撞风控"——
/// 调用方**不要**把它写库当结论，应该交给 <see cref="ProbePacer"/> 熔断后重探。
/// </summary>
public sealed record ProbeOutcome(string Verdict, string? Note, int? MaxQuality = null, int? MaxCodecid = null,
    bool Throttled = false);

public sealed record QrStartInfo(string QrKey, string QrUrl);

public sealed record QrPollInfo(int Code, string? CookieUrl);

/// <summary>
/// B 站 Web API 客户端。单例；Cookie 从 <see cref="FavSyncConfig"/> 读（扫码登录后落 config），
/// 所以登录/登出后立即生效，无需重建。
///
/// wbi：keys 每天轮换，进程内缓存 1 小时；签名 = md5(排序后 query + mixinKey)。
/// </summary>
public sealed class BiliClient : IDisposable
{
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // wbi 混淆表（B 站公开约定，全量 64 位取前 32）
    private static readonly int[] MixinTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    };

    private readonly FavSyncConfig _cfg;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private CookieContainer _cookies = new();
    private (string Mixin, DateTime At)? _wbiCache;
    private bool _buvidReady;
    private bool _loginLoaded;

    public BiliClient(FavSyncConfig cfg)
    {
        _cfg = cfg;
        _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
            // 直连 B 站 CDN（默认）。走代理实测只有直连的 1/5 速度，见 FavSyncConfig.BiliUseProxy
            UseProxy = cfg.BiliUseProxy,
        };
        _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
    }

    public bool HasCookies => !string.IsNullOrEmpty(_cfg.BiliSessdata);

    /// <summary>
    /// 探测限速器（单例，跨作业保留状态）。故意挂在 BiliClient 上而不是作业里——
    /// 熔断状态必须活过"这次作业结束、下次作业又点开始"这个 gap，否则每次重跑都从 1.6 QPS 猛冲一次墙。
    /// </summary>
    public ProbePacer Pacer { get; } = new();

    public void Dispose() => _http.Dispose();

    // ---------------- 登录 ----------------

    /// <summary>把 config 里的登录 cookie 灌进 CookieContainer（服务启动后首次 API 调用时自动执行）。</summary>
    private void EnsureLoginCookies()
    {
        if (_loginLoaded || !HasCookies) return;
        _loginLoaded = true;
        AddCookie("SESSDATA", _cfg.BiliSessdata!);
        if (!string.IsNullOrEmpty(_cfg.BiliJct)) AddCookie("bili_jct", _cfg.BiliJct!);
        if (!string.IsNullOrEmpty(_cfg.BiliDedeUserId)) AddCookie("DedeUserID", _cfg.BiliDedeUserId!);
    }

    private void AddCookie(string name, string value) =>
        _cookies.Add(new Cookie(name, value, "/", ".bilibili.com"));

    /// <summary>
    /// B 站 Web API 现在要求浏览器指纹 cookie（buvid3 等），没有它 playurl 直接 -400 / 412。
    /// 首次调用前先访问一次首页拿匿名 cookie，行为对齐真实浏览器。
    /// </summary>
    private async Task EnsureBuvidAsync(CancellationToken ct)
    {
        if (_buvidReady) return;
        _buvidReady = true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // 首页访问失败不阻塞后续 API（可能已被风控，探测会按 unknown 走重试）
        }
    }

    /// <summary>扫码登录第一步：拿 qrcode_key 和二维码内容（内容是带 web 端口的确认 URL）。</summary>
    public async Task<QrStartInfo> QrGenerateAsync(CancellationToken ct = default)
    {
        using var doc = await GetApiAsync(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/generate",
            new Dictionary<string, string>(), signed: false, ct).ConfigureAwait(false);
        var d = doc.RootElement.GetProperty("data");
        return new QrStartInfo(
            d.GetProperty("qrcode_key").GetString()!,
            d.GetProperty("url").GetString()!);
    }

    /// <summary>
    /// 轮询扫码状态。data.code：86101=待扫码，86090=已扫未确认，86038=已过期，0=已确认
    /// （此时 <see cref="QrPollInfo.CookieUrl"/> 携带 cookie 参数）。
    /// </summary>
    public async Task<QrPollInfo> QrPollAsync(string qrKey, CancellationToken ct = default)
    {
        using var doc = await GetApiAsync(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/poll",
            new Dictionary<string, string> { ["qrcode_key"] = qrKey },
            signed: false, ct).ConfigureAwait(false);
        var d = doc.RootElement.GetProperty("data");
        var code = d.GetProperty("code").GetInt32();
        var url = d.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        return new QrPollInfo(code, url);
    }

    /// <summary>
    /// 新版扫码确认流程（2024+）：poll 返回的 url 只带 <c>ticket</c> 参数，
    /// 需要再 GET 一次该 crossDomain 地址，由服务端 302 + Set-Cookie 下发登录态
    /// （旧版直接把 SESSDATA 放在 url 参数里）。
    /// </summary>
    public async Task<(string? Sessdata, string? Jct, string? DedeUserId)> ExchangeTicketAsync(
        string url, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://passport.bilibili.com/");
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            await res.Content.CopyToAsync(Stream.Null, ct).ConfigureAwait(false); // 读掉响应体，Set-Cookie 已入容器
        }
        catch
        {
            // 网络异常不提前返回：容器里可能已有部分 cookie
        }

        var table = _cookies.GetCookies(new Uri("https://www.bilibili.com/"));
        string? Get(string n) => table[n]?.Value;
        return (Get("SESSDATA"), Get("bili_jct"), Get("DedeUserID"));
    }

    /// <summary>从确认回跳 URL 里解析 cookie（**保留原始 URL 编码**，塞 header 时要的就是它）。</summary>
    public static (string? Sessdata, string? Jct, string? DedeUserId) ParseCookieUrl(string url)
    {
        string? q = null;
        if (url.Contains('?')) q = url[(url.IndexOf('?') + 1)..];
        if (string.IsNullOrEmpty(q)) return (null, null, null);
        string? sessdata = null, jct = null, dede = null;
        foreach (var pair in q.Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i <= 0) continue;
            var k = pair[..i];
            var v = pair[(i + 1)..];
            // B 站返回的键大小写不统一（SESSDATA 大写、bili_jct 小写、DedeUserID 驼峰），按不敏感比较
            if (k.Equals("SESSDATA", StringComparison.OrdinalIgnoreCase)) sessdata = v;
            else if (k.Equals("bili_jct", StringComparison.OrdinalIgnoreCase)) jct = v;
            else if (k.Equals("DedeUserID", StringComparison.OrdinalIgnoreCase)) dede = v;
        }
        return (sessdata, jct, dede);
    }

    /// <summary>登录态校验（nav）。缓存 5 分钟，避免 UI 每次刷新都打 B 站。</summary>
    public async Task<BiliAccount> WhoAmIAsync(CancellationToken ct = default)
    {
        if (_whoAmI is { } c && (DateTime.UtcNow - c.At).TotalMinutes < 5) return c.Account;
        var account = await WhoAmIInnerAsync(ct).ConfigureAwait(false);
        _whoAmI = (account, DateTime.UtcNow);
        return account;
    }

    private (BiliAccount Account, DateTime At)? _whoAmI;

    public void InvalidateCaches()
    {
        _wbiCache = null;
        _whoAmI = null;
    }

    /// <summary>登录成功后调用：灌 cookie + 清缓存。登出时先清 config 再调用，同时清空容器。</summary>
    public void SyncFromConfig()
    {
        if (!HasCookies)
        {
            // 登出：重建空容器
            _cookies = new CookieContainer();
            _handler.CookieContainer = _cookies;
            _loginLoaded = false;
        }
        else
        {
            _loginLoaded = false;
            EnsureLoginCookies();
        }
        _wbiCache = null;
        _whoAmI = null;
    }

    private async Task<BiliAccount> WhoAmIInnerAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetApiAsync(
                "https://api.bilibili.com/x/web-interface/nav",
                new Dictionary<string, string>(), signed: false, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0) return new BiliAccount(false, 0, null);
            var d = root.GetProperty("data");
            var logged = d.TryGetProperty("isLogin", out var il) && il.ValueKind == JsonValueKind.True;
            return new BiliAccount(
                logged,
                d.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0,
                d.TryGetProperty("uname", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new BiliAccount(HasCookies, 0, null); // 网络失败时按"有 cookie 即视为登录"，探测时自然会重试
        }
    }

    // ---------------- 探测 ----------------

    /// <summary>
    /// 一次探测 = **一次 playurl**（不再打 view）。
    ///
    /// 为什么可以去掉 view：view 只用来判"稿件不可见"，但 playurl 单独对同一批稿件直接回 -404
    /// （实测 62002 样本 3 条 + 62012 样本 3 条，全部 -404），gone 白名单两边一致 —— view 只是
    /// 把同一件事问两遍。请求数从 2 降到 1，同样的风控配额能探两倍的条目。
    ///
    /// 风控降级（code=0 但 dash/durl 全空）**不在这里退避**：实测风控窗口约 4.5 分钟，
    /// 1.5s/3s 的退避根本等不到窗口关闭，只会把一堆假 unknown 写进库。
    /// 改成返回 <c>Throttled=true</c>，由调用方交给 <see cref="ProbePacer"/> 熔断。
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(long avid, long cid, int? quality, int? codecid,
        long? phoneVideoSize = null, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (pcode, data) = await PlayUrlAsync(avid, cid, quality ?? 80, ct).ConfigureAwait(false);
                if (pcode == 0)
                {
                    // 风控静默降级：code 是 0，但连 dash/durl 都不给
                    var hasDurl = data.TryGetProperty("durl", out var durlEl) &&
                                  durlEl.ValueKind == JsonValueKind.Array && durlEl.GetArrayLength() > 0;
                    if (data.ValueKind == JsonValueKind.Undefined || (!data.TryGetProperty("dash", out _) && !hasDurl))
                        return new ProbeOutcome(ProbeVerdict.Unknown, "playurl 降级（无 dash/durl，疑似风控）", Throttled: true);

                    var hasTier = false;
                    var maxQ = 0;
                    var maxCodec = 0;      // 最高档位上的一个 codecid
                    var maxCodecAlt = 0;   // 同档位但另一个 codecid（"同画质不同编码"就是它）
                    string? tierUrl = null;
                    IReadOnlyList<string> tierAlts = Array.Empty<string>();
                    if (data.TryGetProperty("dash", out var dash) &&
                        dash.ValueKind == JsonValueKind.Object &&
                        dash.TryGetProperty("video", out var videos) &&
                        videos.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in videos.EnumerateArray())
                        {
                            var id = v.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 0;
                            var cc = v.TryGetProperty("codecid", out var ic) && ic.ValueKind == JsonValueKind.Number ? ic.GetInt32() : 0;
                            if (id > maxQ) { maxQ = id; maxCodec = cc; maxCodecAlt = 0; }
                            else if (id == maxQ && cc != maxCodec && maxCodecAlt == 0) maxCodecAlt = cc;
                            if (quality is { } q && codecid is { } c && id == q && cc == c)
                            {
                                hasTier = true;
                                tierUrl = GetStr(v, "baseUrl") ?? GetStr(v, "base_url");
                                tierAlts = AltUrlList(v);
                            }
                        }
                    }

                    // "仅低档"时把 B 站能给到的最高档位连同编码一起记下来，UI 要并排标出
                    // 手机里那份的档位（q120/HEVC）到底差在哪：画质低了，还是同画质但编码不同。
                    int? maxCodecOut = null;
                    if (maxQ > 0)
                        maxCodecOut = (codecid is { } pc && maxCodecAlt != 0 && maxCodecAlt != pc) ? maxCodecAlt
                                     : (maxCodec != 0 ? maxCodec : (int?)null);

                    if (hasTier)
                    {
                        // 同档同编码 ≠ 同一份字节：web 端的 mux 与手机缓存常常差几百到几千字节
                        // （实测同一条流：217,701,037 vs 217,705,396），md5 必然不符。
                        // 探一次真实长度就能提前判"不可下载相同编码"，省掉整条 200 MB 白跑。
                        if (phoneVideoSize is > 0 && tierUrl is not null)
                        {
                            var webLen = await MeasureAsync(tierUrl, tierAlts, ct).ConfigureAwait(false);
                            if (webLen > 0 && webLen != phoneVideoSize.Value)
                                return new ProbeOutcome(ProbeVerdict.Lower,
                                    $"同档同编码但字节不同：web {webLen:N0} B ≠ 手机 {phoneVideoSize.Value:N0} B" +
                                    $"（差 {webLen - phoneVideoSize.Value:+#,0;-#,0;0} B）", quality, codecid);
                        }
                        return new ProbeOutcome(ProbeVerdict.Exact, null);
                    }
                    if (maxQ > 0) return new ProbeOutcome(ProbeVerdict.Lower, $"同档不可用，最高 q{maxQ}", maxQ, maxCodecOut);
                    return new ProbeOutcome(ProbeVerdict.Lower, "无 dash 流（durl/老格式）"); // 走到这说明有 durl
                }

                // 充电专属视频：稿件本身活着（view 正常返回），但 playurl 直接回 87008。
                // 与 gone 的区别是"稿件还在、只是账号没权限"，所以要单列一档 —— 归到 gone 会让人误以为
                // 视频没了；归到 unknown 又会让同步页把它当"待探测"反复重试。
                // 实测样本 <avid>_c<cid>「示例标题」。
                if (pcode == 87008) return new ProbeOutcome(ProbeVerdict.Charge, "充电视频：未充电或已过期，B站不给流");

                // gone 白名单：-404 不存在 / -403 权限 / 62002 稿件不可见 / 62004 审核中 / 62012 不可见
                // （62012：view 端点当年只回码不给 message，但 playurl 对这些稿件直接回 -404，已实测）
                if (pcode is -404 or -403 or 62002 or 62004 or 62012) return new ProbeOutcome(ProbeVerdict.Gone, $"稿件不可见（{pcode}）");
                return new ProbeOutcome(ProbeVerdict.Unknown, $"playurl code {pcode}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // 纯网络抖动给一次机会（800ms），再失败就是 unknown；这不属于风控，不触发熔断
                if (attempt >= 2) return new ProbeOutcome(ProbeVerdict.Unknown, $"网络失败：{ex.Message}");
                await Task.Delay(800, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 只发头部请求量一次长度（不比节点、不读 body）——探测里每条多花 0.1s，
    /// 但能提前判掉「同档同编码却字节不同」的条目，省掉整条几百 MB 的白下载。
    /// </summary>
    private async Task<long> MeasureAsync(string url, IReadOnlyList<string> alts, CancellationToken ct)
    {
        var n = await HeadLengthAsync(url, ct).ConfigureAwait(false);
        if (n > 0) return n;
        foreach (var a in alts)
        {
            n = await HeadLengthAsync(a, ct).ConfigureAwait(false);
            if (n > 0) return n;
        }
        return 0;
    }

    private async Task<(int Code, JsonElement Data)> PlayUrlAsync(long avid, long cid, int qn, CancellationToken ct)
    {
        // 注意：wbi 端点只认 avid，用 aid 会 -400（实测，旧端点两者皆可）
        using var doc = await GetApiAsync(
            "https://api.bilibili.com/x/player/wbi/playurl",
            new Dictionary<string, string>
            {
                ["avid"] = avid.ToString(),
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                // fnval 是位标志：16=dash，64=HDR，128=4K，256/512=杜比，1024=8K，2048=AV1。
                // 只给 16 的话大会员也拿不到 q120/q116 等高档（实测），网页端用的就是 4048。
                ["fnval"] = "4048",
                ["fourk"] = "1",
            },
            signed: true, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return (
            root.GetProperty("code").GetInt32(),
            root.TryGetProperty("data", out var d) ? d.Clone() : default);
    }

    // ---------------- 下载替代（S5） ----------------

    /// <summary>
    /// 一条 dash 流。<see cref="AltUrls"/> 是 playurl 给的 backupUrl 备用地址（每档通常 2 条），
    /// 与 baseUrl 指向同一字节，只是接入节点不同——换节点全靠它和域名池。
    /// </summary>
    public sealed record DashStream(int Id, int Codecid, long Size, string Url,
        IReadOnlyList<string>? AltUrls = null, string? Codecs = null, string? MimeType = null,
        int Width = 0, int Height = 0, long Bandwidth = 0);

    /// <summary>
    /// 在线预览用：取 dash 流清单并**缓存 10 分钟**。
    /// 必须缓存：MSE 会按块反复 Range 拉取同一条流，每块都打一次 playurl 必撞风控；
    /// 而 playurl 给的 URL 本身就是有时效的短链，缓存 10 分钟足够一次预览用完。
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime Expire, IReadOnlyList<DashStream> Videos, IReadOnlyList<DashStream> Audios)> _preview = new();

    public async Task<(IReadOnlyList<DashStream> Videos, IReadOnlyList<DashStream> Audios)> PreviewAsync(
        long avid, long cid, CancellationToken ct = default)
    {
        var key = $"{avid}_{cid}";
        if (_preview.TryGetValue(key, out var hit) && hit.Expire > DateTime.UtcNow)
            return (hit.Videos, hit.Audios);

        await Pacer.WaitTurnAsync(ct).ConfigureAwait(false);   // 与探测共用限速器，别把风控打出来
        var (videos, audios) = await DashAsync(avid, cid, 80, 7, ct).ConfigureAwait(false);
        _preview[key] = (DateTime.UtcNow.AddMinutes(10), videos, audios);
        return (videos, audios);
    }

    /// <summary>
    /// 代理拉一段字节（原样转发 Range，用于前端 MSE 分块喂 SourceBuffer）。
    /// 走同一个 HttpClient：UA 与登录 cookie 都带着，B 站 CDN 认。
    /// </summary>
    public async Task<HttpResponseMessage> FetchRangeAsync(string url, string? range, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // B 站 CDN 校验 Referer：不带就是 403（一个 552 字节的 html 错误页）。
        // 浏览器直连也一样被挡，这正是预览必须走服务端代理的原因。
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(range)) req.Headers.TryAddWithoutValidation("Range", range!);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取 dash 流清单（下载替代用）。**video 返回全部档位**：调用方先找同档同编码（exact，可 md5 严格比对），
    /// 找不到时按同步策略升档（最高档 ≥ 手机档位 → 下载最高档，声明大小校验；只有更低档 → 拒绝，须拷贝）。
    /// qn 只影响期望档位，dash 恒返回账号可用的全部档位。
    /// audio 返回全部档位——手机缓存用的是哪一档无从得知，靠 size 预筛 + md5 终验锁定。
    /// </summary>
    public async Task<(IReadOnlyList<DashStream> Videos, IReadOnlyList<DashStream> Audios)> DashAsync(
        long avid, long cid, int quality, int codecid, CancellationToken ct = default)
    {
        await EnsureBuvidAsync(ct).ConfigureAwait(false);
        var (pcode, data) = await PlayUrlAsync(avid, cid, quality, ct).ConfigureAwait(false);
        if (pcode != 0) throw new InvalidOperationException($"playurl 失败（{pcode}）");
        if (data.ValueKind == JsonValueKind.Undefined || !data.TryGetProperty("dash", out var dash))
        {
            // 充电专属（有试看）就是这一种：code=0、没有 dash、只给一条 durl 整段 mp4。
            // 报"风控"会误导排查方向，这里分开说清楚。
            var hasDurl = data.ValueKind == JsonValueKind.Object &&
                          data.TryGetProperty("durl", out var du) &&
                          du.ValueKind == JsonValueKind.Array && du.GetArrayLength() > 0;
            throw new InvalidOperationException(hasDurl
                ? "B站只给了 durl 整段 mp4（无 dash 分片流，多为充电/付费视频的试看）"
                : "playurl 未返回 dash（可能被风控，稍后重试）");
        }

        var videos = new List<DashStream>();
        var audios = new List<DashStream>();
        foreach (var v in dash.GetProperty("video").EnumerateArray())
        {
            var url = GetStr(v, "baseUrl") ?? GetStr(v, "base_url");
            if (url is null) continue;
            videos.Add(new DashStream(
                GetInt(v, "id") ?? 0, GetInt(v, "codecid") ?? 0, GetInt(v, "size") ?? 0, url,
                AltUrlList(v),
                // codecs/mimeType 只有 MSE 需要（建 SourceBuffer 要精确的 codec 串），下载路径不用
                GetStr(v, "codecs"), GetStr(v, "mimeType") ?? GetStr(v, "mime_type"),
                GetInt(v, "width") ?? 0, GetInt(v, "height") ?? 0, GetInt(v, "bandwidth") ?? 0));
        }
        if (videos.Count == 0)
            throw new InvalidOperationException("dash 中没有任何视频流（疑似风控降级，重试可恢复）");

        if (dash.TryGetProperty("audio", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var url = GetStr(a, "baseUrl") ?? GetStr(a, "base_url");
                if (url is null) continue;
                audios.Add(new DashStream(
                    GetInt(a, "id") ?? 0, 0, GetInt(a, "size") ?? 0, url, AltUrlList(a),
                    GetStr(a, "codecs"), GetStr(a, "mimeType") ?? GetStr(a, "mime_type")));
            }
        if (audios.Count == 0)
            throw new InvalidOperationException("dash 中没有音频流");
        return (videos, audios);
    }

    /// <summary>playurl 的 backupUrl 列表（字段名有 backupUrl / backup_url 两种写法）。</summary>
    private static IReadOnlyList<string> AltUrlList(JsonElement e)
    {
        var list = new List<string>();
        foreach (var name in new[] { "backupUrl", "backup_url" })
        {
            if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var x in arr.EnumerateArray())
                if (x.GetString() is { Length: > 0 } s && !list.Contains(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// 流式下载到文件，边下边算 md5。返回 (md5, bytes)。
    /// B 站 CDN **按接入节点限速**（同一条视频不同节点实测差 5 倍：1.3 到 9.4 MB/s 单连接），
    /// 所以下载前先给候选节点测速排序，段内再按 4MB 一块随时换节点；大文件 4 段并行 Range，
    /// 不支持 Range 或小文件回退单连接。
    /// 大文件带断点续传：同目录 <c>&lt;dest&gt;.resume.json</c> 记录各段已写字节，
    /// 进程被杀后重跑同路径可从断点继续（progress 只报新增字节）。
    /// </summary>
    public Task<(string Md5, long Bytes)> DownloadStreamAsync(
        string url, string destPath, IProgress<long>? progress, CancellationToken ct = default)
        => DownloadStreamAsync(url, Array.Empty<string>(), 0, destPath, progress, ct);

    /// <summary>按 dash 流下载：baseUrl 与 backupUrl 一起参与选节点。</summary>
    public Task<(string Md5, long Bytes)> DownloadStreamAsync(
        DashStream stream, string destPath, IProgress<long>? progress, CancellationToken ct = default)
        => DownloadStreamAsync(stream.Url, stream.AltUrls ?? Array.Empty<string>(), stream.Size,
            destPath, progress, ct);

    /// <summary>已知总长时少发一次 HEAD：调用方（同步作业）先用 <see cref="StreamLengthAsync"/> 问过，这里直接复用。</summary>
    public Task<(string Md5, long Bytes)> DownloadStreamAsync(
        DashStream stream, string destPath, IProgress<long>? progress, long knownLength,
        CancellationToken ct = default)
        => DownloadStreamAsync(stream.Url, stream.AltUrls ?? Array.Empty<string>(),
            knownLength > 0 ? knownLength : stream.Size, destPath, progress, ct);

    /// <summary>
    /// 取这条流的真实字节数（0 = 拿不到）。dash 的 size 恒为 0（实测），
    /// 所以要么让调用方先问一次（进度分母、并行段数都靠它），要么由下载内部再问。
    /// </summary>
    public async Task<long> StreamLengthAsync(DashStream stream, CancellationToken ct = default)
    {
        if (stream.Size > 0) return stream.Size;
        var hosts = await RankHostUrlsAsync(stream.Url, stream.AltUrls ?? Array.Empty<string>(), ct)
            .ConfigureAwait(false);
        foreach (var h in hosts)
        {
            var n = await HeadLengthAsync(h, ct).ConfigureAwait(false);
            if (n > 0) return n;
        }
        return 0;
    }

    private async Task<(string Md5, long Bytes)> DownloadStreamAsync(
        string url, IReadOnlyList<string> altUrls, long knownLength, string destPath,
        IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var hosts = await RankHostUrlsAsync(url, altUrls, ct).ConfigureAwait(false);
        long total = knownLength;
        if (total <= 0)
            foreach (var h in hosts)
            {
                total = await HeadLengthAsync(h, ct).ConfigureAwait(false);
                if (total > 0) break;
            }
        var manifestPath = destPath + ".resume.json";

        if (SegCountFor(total) > 1)
        {
            await DownloadParallelAsync(hosts, destPath, total, manifestPath, progress, ct).ConfigureAwait(false);
        }
        else
        {
            // 单连接：内部自己管文件（换节点按 Range 续传，不再清零重下）
            await DownloadSingleAsync(hosts, destPath, total, progress, ct).ConfigureAwait(false);
        }

        try { File.Delete(manifestPath); } catch { }
        var md5 = await ComputeMd5Async(destPath, ct).ConfigureAwait(false);
        return (md5, new FileInfo(destPath).Length);
    }

    /// <summary>
    /// 段数按体积自适应（直连实测：同一文件单连接 13.3 MB/s、4 段 24.1、8 段 35.6，
    /// B 站按连接限速所以并行几乎线性叠加）。比 4 MB 还小的文件单连接更划算（建连/TCP 慢启动占比太高）。
    /// </summary>
    private static int SegCountFor(long total) => total switch
    {
        >= 48L << 20 => 8,
        >= 12L << 20 => 4,
        >= 4L << 20 => 3,
        _ => 1,
    };

    /// <summary>段内换节点的粒度：每块 4 MB 整块先读进内存，读完才落盘（换节点不会写脏）。</summary>
    private const int BlockSize = 4 << 20;

    /// <summary>低于此速率（且已读够一半）就判定为烂节点，换下一个。实测好节点 8~9 MB/s、烂节点 1~2 MB/s。</summary>
    private const double SlowBytesPerSec = 1.5 * 1024 * 1024;

    /// <summary>测速用的样本大小。</summary>
    private const int ProbeBytes = 256 << 10;

    /// <summary>节点测速结果有效期。</summary>
    private const long HostTtlMs = 180_000;

    /// <summary>
    /// 同族接入域名池。playurl 只返回 1 条 baseUrl + 2 条 backupUrl，实测同一条文件换到这些域名
    /// 也能 206（签名 upsig 不绑 host），而同一时刻不同域名能差 5 倍，所以一并纳入候选。
    /// 注意：只对 <c>*.bilivideo.com</c> 生效——akamaized.net 那类换域名会 403。
    /// </summary>
    private static readonly string[] ExtraHosts =
    {
        "upos-sz-estgcos.bilivideo.com",
        "upos-sz-mirrorcos.bilivideo.com",
        "upos-sz-mirrorcosb.bilivideo.com",
        "upos-sz-mirrorali.bilivideo.com",
    };

    private sealed class HostStat
    {
        public double Bps;
        public long At;
        public long PenaltyUntil;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HostStat> _hostStats = new();

    private readonly object _manifestLock = new();

    // Version 0：旧的 4/8 个固定大段，各段记录前缀已写字节。
    // Version 2：4 MB 动态块，每块记录前缀已写字节；一个块同一时刻只归一个 worker。
    private sealed record ResumeManifest(long Total, long[] Done, int Version = 0);

    /// <summary>
    /// 探测内容总长（0 = 未知/不支持）。
    /// 只认 206 + Content-Range 会漏判：某些节点对 Range: bytes=0-0 直接回 200 整文件、
    /// 或 206 但不带 Content-Range → 返回 0 → 落到单连接路径（1 段），240 MB 的流按 1 MB/s 爬。
    /// 现在 206 无 Content-Range 用 Content-Length，200 也用 Content-Length（立刻 dispose，不读 body）。
    /// </summary>
    private async Task<long> HeadLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                var range = res.Content.Headers.ContentRange;
                if (range?.Length is { } len && len > 0) return len;
                return res.Content.Headers.ContentLength ?? 0;
            }
            return res.IsSuccessStatusCode ? res.Content.Headers.ContentLength ?? 0 : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 给候选节点测速并排序。同 host 结果缓存 <see cref="HostTtlMs"/>，避免每条都测。
    /// <para>
    /// 候选一律是**完整 URL**（baseUrl 原样 + 每个 backupUrl 原样），绝不拆开重组。
    /// mcdn 节点的地址形如 <c>https://xxx.mcdn.bilivideo.cn:8082/v1/resource/upgcxcode/...</c>：
    /// 既带非默认端口，path 前缀也和 upos 的 <c>/upgcxcode/...</c> 不是一回事。
    /// 实测（竖屏 4K 的 audio 常走 mcdn）：原样请求 206；去掉 :8082 连不上；
    /// 把 mcdn 的 tail 挂到 upos 域名上 403。这正是「部分视频 audio 稳定 403、永远下不下来」的根因——
    /// 旧实现的 <c>"https://" + uri.Host + tail</c> 把这两点全踩了。
    /// </para>
    /// </summary>
    private async Task<List<string>> RankHostUrlsAsync(
        string templateUrl, IReadOnlyList<string> altUrls, CancellationToken ct)
    {
        var cands = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (!cands.Contains(u, StringComparer.Ordinal)) cands.Add(u);
        }

        Add(templateUrl);
        foreach (var u in altUrls) Add(u);

        // 同族域名池：只有 path 约定一致才谈得上换 host，所以仅对 *.bilivideo.com 的 baseUrl 生效
        // （mcdn.bilivideo.cn 那类硬换必 403，不在此列）。
        var uri = new Uri(templateUrl);
        if (uri.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase))
        {
            var slash = templateUrl.IndexOf('/', 8);
            var tail = slash > 0 ? templateUrl[slash..] : "/";
            foreach (var h in ExtraHosts) Add("https://" + h + tail);
        }

        var now = Environment.TickCount64;
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in cands) samples.TryAdd(KeyOf(u), u);

        var stale = samples.Keys
            .Where(h => !_hostStats.TryGetValue(h, out var s) || now - s.At > HostTtlMs)
            .ToList();
        if (stale.Count > 0)
        {
            await Task.WhenAll(stale.Select(async h =>
            {
                var bps = await ProbeHostAsync(samples[h], ct).ConfigureAwait(false);
                if (bps < 0) bps = 0;   // 探不通：排到能用者之后
                _hostStats.AddOrUpdate(h,
                    _ => new HostStat { Bps = bps, At = Environment.TickCount64 },
                    (_, old) => { old.Bps = bps; old.At = Environment.TickCount64; return old; });
            })).ConfigureAwait(false);
        }

        return cands
            .OrderByDescending(u =>
            {
                if (!_hostStats.TryGetValue(KeyOf(u), out var s)) return -1.0;
                return Environment.TickCount64 < s.PenaltyUntil ? -1.0 : s.Bps;
            })
            .ToList();
    }

    /// <summary>节点记账的键：带非默认端口（mcdn 的 :8082 必须与同域默认端口分开算）。</summary>
    private static string KeyOf(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
        }
        catch { return url; }
    }

    /// <summary>测一个节点的吞吐（256 KB），失败返回 -1。</summary>
    private async Task<double> ProbeHostAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(3500));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ProbeBytes - 1);
            var sw = Stopwatch.StartNew();
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return -1;
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[64 << 10];
            var n = 0;
            while (n < ProbeBytes)
            {
                var k = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                if (k == 0) break;
                n += k;
            }
            var sec = sw.Elapsed.TotalSeconds;
            return sec <= 0.001 ? 0 : n / sec;
        }
        catch { return -1; }
    }

    /// <summary>节点被判为慢：2 分钟内排到最后（下次选节点时不再首选）。</summary>
    private void PenalizeHost(string url)
    {
        try
        {
            var h = KeyOf(url);
            var st = _hostStats.GetOrAdd(h, _ => new HostStat());
            lock (st)
            {
                st.PenaltyUntil = Environment.TickCount64 + 120_000;
                st.Bps = Math.Min(st.Bps, 1 * 1024 * 1024);
            }
        }
        catch { }
    }

    /// <summary>
    /// 单连接下载（小文件，或拿不到总长时兜底）。按测速排好的节点顺序，失败就换下一个。
    /// <para>
    /// 一遇异常就 <c>fs.SetLength(0)</c> 从 0 重下，会把已下的字节丢掉而 progress 继续累加 ——
    /// 一条 240 MB 的流永远停在几十 MB，UI 却因为重复计数早早顶到 100%，速度掉到 0.x MB/s
    /// （用户报障「最后一点特别慢、还显示 228 MB / 216 MB」）。现在换节点按 Range 从断点续，
    /// 节点不认 Range 才不得已清零，并且**报回负增量**把多算的字节退掉。
    /// 另外加了慢节点判定：连了 8 秒还不达标就换节点续传，而不是硬等十分钟。
    /// </para>
    /// </summary>
    private async Task DownloadSingleAsync(IReadOnlyList<string> hosts, string destPath, long total,
        IProgress<long>? progress, CancellationToken ct)
    {
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        Exception? last = null;
        long written = 0;
        var buf = new byte[1 << 16];

        foreach (var url in hosts)
        {
            ct.ThrowIfCancellationRequested();
            var host = new Uri(url).Authority;   // 带端口：mcdn 的 :8082 与同域默认端口是两个节点
            var completed = false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://www.bilibili.com/");
                if (written > 0)
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(written, null);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (written > 0 && res.StatusCode == HttpStatusCode.OK)
                {
                    // 节点不认 Range：只能从头来，把已经算进进度的字节退回去
                    fs.SetLength(0);
                    fs.Position = 0;
                    progress?.Report(-written);
                    written = 0;
                }
                else if (written > 0 && res.StatusCode == HttpStatusCode.PartialContent)
                {
                    var from = res.Content.Headers.ContentRange?.From;
                    if (from is { } f && f != written)
                    {
                        last = new InvalidOperationException($"{host} 续传起点不符（要 {written}，回 {f}）");
                        continue;
                    }
                }
                else res.EnsureSuccessStatusCode();

                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var sw = Stopwatch.StartNew();
                long sessionStart = written;
                while (true)
                {
                    var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0) { completed = true; break; }
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    written += n;
                    progress?.Report(n);

                    // 慢节点：连了 8 秒还低于阈值就换（还有别的节点才换，最后一个只能硬读）
                    var sec = sw.Elapsed.TotalSeconds;
                    if (hosts.Count > 1 && sec > 8 && (written - sessionStart) / sec < SlowBytesPerSec)
                    {
                        PenalizeHost(url);
                        last = new InvalidOperationException(
                            $"{host} 速率过低（{(written - sessionStart) / sec / 1048576:F2} MB/s），换节点续传");
                        break;
                    }
                }
                if (completed) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                PenalizeHost(url);
            }
        }

        // 总长已知时必须补齐，否则交给上层的 md5/size 闸门就变成"内容不符"的假失败
        if (total > 0 && written < total)
            throw new InvalidOperationException(
                $"单连接只下到 {written}/{total} B：{last?.Message ?? "节点未读完"}");
        if (written == 0)
            throw last ?? new InvalidOperationException("所有候选节点均不可用");
    }

    /// <summary>
    /// 读一个块进 <paramref name="buf"/>。返回实际字节数与是否因过慢被中止。
    /// <paramref name="allowSlow"/> = false 时，读到一半仍低于 <see cref="SlowBytesPerSec"/> 就直接放弃
    /// （已读部分只在内存里，没落盘，换节点重读本块是干净的）。
    /// </summary>
    private async Task<(int Bytes, bool Aborted)> ReadBlockAsync(
        string url, long offset, int take, long total, byte[] buf, bool allowSlow, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + take - 1);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (res.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidOperationException($"返回 {res.StatusCode}（非 206）");

        // 有的 CDN 节点会把 Range 请求当整文件回（起点被忽略）或回的区间不是要的那段。
        // 以前不校验：字节数照样凑够 total，文件长度对、内容全错 —— md5 必然不符，
        // 表现成"替代下载随机失败"（实测 5 条里 4 条中招）。
        var cr = res.Content.Headers.ContentRange;
        if (cr?.From is { } from && from != offset)
            throw new InvalidOperationException($"{new Uri(url).Host} 忽略 Range 起点（要 {offset}，回 {from}）");
        if (cr?.Length is { } len && total > 0 && len != total)
            throw new InvalidOperationException($"{new Uri(url).Host} 总长不符（要 {total}，回 {len}）");
        await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var half = Math.Max(1, take / 2);
        var n = 0;
        while (n < take)
        {
            var read = await src.ReadAsync(buf.AsMemory(n, take - n), ct).ConfigureAwait(false);
            if (read == 0) break;
            n += read;
            if (!allowSlow && n >= half)
            {
                var sec = sw.Elapsed.TotalSeconds;
                if (sec > 0.05 && n / sec < SlowBytesPerSec) return (n, true);
            }
        }
        return (n, false);
    }

    /// <summary>
    /// 并行 Range 下载，按 <see cref="BlockSize"/> 切成 4 MB 动态块。
    /// worker 空闲就领下一块，不再把文件固定分成 4/8 个大段——旧做法到最后常只剩一个慢连接
    /// 跑几十 MB 的尾巴，整体速度会从几十 MB/s 塌到 0.x MB/s；现在单连接最多拖 4 MB。
    /// 每块独立选节点、读完才落盘，所以「慢节点换掉重读本块」是幂等的。
    /// 断点清单 v2 记录各块前缀；旧 v0 固定大段清单会按已完成区间换算过来，不重新下载。
    /// </summary>
    private async Task DownloadParallelAsync(IReadOnlyList<string> hosts, string destPath, long total,
        string manifestPath, IProgress<long>? progress, CancellationToken ct)
    {
        var workerCount = SegCountFor(total);
        var chunkCount = checked((int)((total + BlockSize - 1) / BlockSize));
        var done = new long[chunkCount];
        long ChunkLength(int i) => Math.Min(BlockSize, total - (long)i * BlockSize);

        // ---- 续传：文件总长必须吻合。v2 直接读；v0 把固定大段的已完成前缀映射到 4 MB 块 ----
        var canResume = new FileInfo(destPath) is { Exists: true } file && file.Length == total;
        var manifestValid = false;
        if (canResume && File.Exists(manifestPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<ResumeManifest>(File.ReadAllText(manifestPath));
                if (m is not null && m.Total == total)
                {
                    if (m.Version == 2 && m.Done.Length == chunkCount)
                    {
                        for (var i = 0; i < chunkCount; i++)
                            done[i] = Math.Clamp(m.Done[i], 0, ChunkLength(i));
                        manifestValid = true;
                    }
                    else if (m.Version == 0 && m.Done.Length > 1)
                    {
                        var oldCount = m.Done.Length;
                        var oldSegSize = total / oldCount;
                        for (var old = 0; old < oldCount; old++)
                        {
                            // 这一段一个字节都没写就不能拿 oldStart 当"已完成边界"，
                            // 否则会把前面所有段之间的区间都误标成已完成（实测踩坑：
                            // 直接跳过整片未下载区间，文件里全是 0，md5 必然不符）
                            if (m.Done[old] <= 0) continue;
                            var oldStart = old * oldSegSize;
                            var oldLength = old == oldCount - 1 ? total - oldStart : oldSegSize;
                            var completedEnd = oldStart + Math.Clamp(m.Done[old], 0, oldLength);
                            for (var i = 0; i < chunkCount; i++)
                            {
                                var chunkStart = (long)i * BlockSize;
                                if (completedEnd <= chunkStart) continue;
                                done[i] = Math.Max(done[i], Math.Clamp(completedEnd - chunkStart, 0, ChunkLength(i)));
                            }
                        }
                        manifestValid = true;
                    }
                }
            }
            catch { }
        }

        if (!manifestValid)
        {
            Array.Clear(done);
            await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite, 1, FileOptions.RandomAccess))
            {
                fs.SetLength(total);
            }
        }
        SaveManifest(manifestPath, total, done);

        var failures = new ConcurrentQueue<string>();
        var next = 0;
        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(async worker =>
        {
            try
            {
                // 同文件多句柄：动态块区间不重叠，一个块只归一个 worker。
                await using var part = new FileStream(destPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, 1, FileOptions.RandomAccess);
                var buf = new byte[BlockSize];
                var lastSave = Environment.TickCount64;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var i = Interlocked.Increment(ref next) - 1;
                    if (i >= chunkCount) break;
                    var chunkLength = ChunkLength(i);
                    if (done[i] >= chunkLength) continue;

                    var chunkStart = (long)i * BlockSize;
                    // 块内循环：一次没读满（短读 / 慢节点只回一部分）必须继续补这块，
                    // 不能领下一块——否则这块会永远缺尾巴，文件长度对、md5 必不符
                    while (done[i] < chunkLength)
                    {
                        var offset = chunkStart + done[i];
                        var take = (int)(chunkLength - done[i]);
                        var got = 0;
                        var allSlow = true;
                        var lastErr = "无候选节点";

                        foreach (var url in hosts)
                        {
                            try
                            {
                                var (n, aborted) = await ReadBlockAsync(url, offset, take, total, buf, false, ct)
                                    .ConfigureAwait(false);
                                if (aborted)
                                {
                                    PenalizeHost(url);
                                    lastErr = $"{new Uri(url).Host} 速率过低";
                                    continue;   // buf 未落盘，换节点重读本块
                                }
                                got = n;
                                allSlow = false;
                                if (n == take) break;
                                lastErr = $"{new Uri(url).Host} 只回到 {n}/{take}";
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex) { lastErr = ex.Message; }
                        }

                        // 全部节点都慢：用排最前的那个硬读完这一块，避免在节点之间无限打转。
                        if (allSlow)
                        {
                            var (n, _) = await ReadBlockAsync(hosts[0], offset, take, total, buf, true, ct)
                                .ConfigureAwait(false);
                            if (n == 0) throw new InvalidOperationException($"块 {offset} 全节点失败：{lastErr}");
                            got = n;
                        }
                        if (got <= 0) throw new InvalidOperationException($"块 {offset} 失败：{lastErr}");

                        part.Seek(offset, SeekOrigin.Begin);
                        await part.WriteAsync(buf.AsMemory(0, got), ct).ConfigureAwait(false);
                        done[i] += got;
                        progress?.Report(got);

                        if (Environment.TickCount64 - lastSave > 2000)
                        {
                            SaveManifest(manifestPath, total, done);
                            lastSave = Environment.TickCount64;
                        }
                    }
                    SaveManifest(manifestPath, total, done);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Enqueue($"worker {worker}：{ex.Message}"); }
        })).ConfigureAwait(false);

        if (failures.Count > 0)
            throw new InvalidOperationException($"并行分段下载失败（{failures.Count} 个 worker）：{string.Join("; ", failures)}");
    }

    private void SaveManifest(string manifestPath, long total, long[] done)
    {
        try
        {
            lock (_manifestLock)
            {
                var tmp = manifestPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new ResumeManifest(total, done, 2)));
                File.Move(tmp, manifestPath, overwrite: true);
            }
        }
        catch { }
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = await md5.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---------------- wbi ----------------

    private async Task<string> WbiMixinAsync(CancellationToken ct)
    {
        if (_wbiCache is { } c && (DateTime.UtcNow - c.At).TotalHours < 1) return c.Mixin;

        using var doc = await GetApiAsync(
            "https://api.bilibili.com/x/web-interface/nav",
            new Dictionary<string, string>(), signed: false, ct).ConfigureAwait(false);
        var wbi = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
        var img = KeyFromUrl(wbi.GetProperty("img_url").GetString());
        var sub = KeyFromUrl(wbi.GetProperty("sub_url").GetString());
        if (string.IsNullOrEmpty(img) || string.IsNullOrEmpty(sub))
            throw new InvalidOperationException("无法获取 wbi key（nav 响应异常）");

        var mixin = MixinKey(img, sub);
        _wbiCache = (mixin, DateTime.UtcNow);
        return mixin;
    }

    private static string KeyFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var q = url.Contains('?') ? url[..url.IndexOf('?')] : url;
        var name = q[(q.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string MixinKey(string imgKey, string subKey)
    {
        var s = imgKey + subKey;
        var sb = new StringBuilder(32);
        for (var i = 0; i < 32; i++) sb.Append(s[MixinTab[i]]);
        return sb.ToString();
    }

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>wbi 签名：加 wts → 过滤 !'()* → 按 key 排序 → urlencode → md5(query + mixinKey)。</summary>
    public static (string Query, string Rid) SignWbi(Dictionary<string, string> ps, string mixinKey)
    {
        ps["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var query = string.Join("&", ps
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(Clean(kv.Value))}"));
        var rid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey))).ToLowerInvariant();
        return (query, rid);
    }

    private static string Clean(string v) => new(v.Where(c => !"!'()*".Contains(c)).ToArray());

    // ---------------- 收藏夹直下（S6） ----------------

    public sealed record FavFolder(long Id, string Title, int MediaCount);

    /// <summary>Invalid=true 表示非视频或已失效条目（type≠2 / 无 bvid），列表展示但不可下载。</summary>
    public sealed record FavItem(long Avid, string? Bvid, long? Cid, string Title,
        string? Owner, long DurationSec, bool Invalid, string? Cover);

    public sealed record ViewInfo(long Avid, string Bvid, string Title, string? Owner, long? OwnerMid, string? Cover,
        long DurationMs, IReadOnlyList<ViewPage> Pages,
        // 充电专属且**本账号没有播放权**（is_upower_exclusive=true 且 is_upower_play=false）。
        // 这是判断充电视频最可靠的信号：view 一次就能拿到，不必等 playurl 报错 ——
        // 实测充电视频的 playurl 有两种表现：无试看回 87008，有试看则回 code=0 + 只有 durl（无 dash），
        // 后者永远撞不到 87008，靠下载失败打标会漏（实测 8 条全中）。
        bool UpowerExclusive = false);

    /// <summary>单个分P。DurationSec 来自 view.pages[].duration（秒），P 选择框展示用。</summary>
    public sealed record ViewPage(long Cid, string Part, int Page, int? Width, int? Height, long DurationSec = 0);

    /// <summary>登录用户自建收藏夹列表（created/list-all 一次拿全；旧的 owned/list 已废弃返回 HTML 错误页）。</summary>
    public async Task<IReadOnlyList<FavFolder>> FavFoldersAsync(CancellationToken ct = default)
    {
        var me = await WhoAmIAsync(ct).ConfigureAwait(false);
        if (!me.LoggedIn || me.Mid == 0)
            throw new InvalidOperationException("B 站未登录，无法读取收藏夹");

        using var doc = await GetApiAsync(
            "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
            new Dictionary<string, string> { ["up_mid"] = me.Mid.ToString() },
            signed: false, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0) throw new InvalidOperationException($"收藏夹列表失败：{FavCodeMsg(code)}");
        var d = root.GetProperty("data");
        var folders = new List<FavFolder>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("list", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                folders.Add(new FavFolder(
                    f.GetProperty("id").GetInt64(),
                    f.GetProperty("title").GetString() ?? "(未命名)",
                    f.TryGetProperty("media_count", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0));
            }
        }
        return folders;
    }

    /// <summary>
    /// 收藏夹接口的返回码翻译。重点是把 -412 说成人话：它不是"接口坏了"，
    /// 而是 B 站风控把这次请求拦了（翻页/连续扫描大夹最容易撞），一般 5~10 分钟自愈。
    /// </summary>
    private static string FavCodeMsg(int code) => code switch
    {
        -412 => "触发风控（B站限流，一般 5~10 分钟后自愈，别连点）",
        -400 => "请求被拒（缺少浏览器指纹 cookie，重新登录后再试）",
        -101 => "未登录（先扫码登录）",
        -403 => "没有权限查看这个收藏夹",
        -404 => "收藏夹不存在（可能已删除）",
        62011 or 62012 => "收藏夹不可见（私密或未公开）",
        _ => $"code {code}",
    };

    /// <summary>收藏夹内容一页。type=2 才是视频；失效条目通常无 bvid，标记 Invalid。</summary>
    public async Task<(int Total, IReadOnlyList<FavItem> Items)> FavItemsAsync(
        long mediaId, int pn, int ps, CancellationToken ct = default)
    {
        using var doc = await GetApiAsync(
            "https://api.bilibili.com/x/v3/fav/resource/list",
            new Dictionary<string, string>
            {
                ["media_id"] = mediaId.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = ps.ToString(),
                ["keyword"] = "",
                ["order"] = "mtime",
                ["type"] = "0",
                ["tid"] = "0",
                ["platform"] = "web",
            },
            signed: true, ct).ConfigureAwait(false);   // 同上，wbi 签名
        var root = doc.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0) throw new InvalidOperationException($"收藏夹内容失败：{FavCodeMsg(code)}");
        var d = root.GetProperty("data");

        var total = d.TryGetProperty("info", out var info) &&
                    info.TryGetProperty("media_count", out var mc) && mc.ValueKind == JsonValueKind.Number
            ? mc.GetInt32() : 0;

        var items = new List<FavItem>();
        if (d.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in medias.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var mi) && mi.ValueKind == JsonValueKind.Number ? mi.GetInt64() : 0;
                var type = m.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : 0;
                var bvid = m.TryGetProperty("bvid", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString() : null;
                var title = m.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                var cid = m.TryGetProperty("cid", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt64() : 0;
                var dur = m.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetInt64() : 0;
                string? owner = m.TryGetProperty("upper", out var up) &&
                                up.ValueKind == JsonValueKind.Object &&
                                up.TryGetProperty("name", out var un) ? un.GetString() : null;
                var invalid = id <= 0 || type != 2 || string.IsNullOrEmpty(bvid)
                              // B 站对已删除的收藏条目仍回 type=2 + bvid，只把标题改成"已失效视频"
                              // —— 按结构判不出来，只能认标题。认出来才能归进失效桶、灰掉不可勾选
                              // （否则会落在"未同步"里，点了下载必然失败）
                              || string.Equals(title?.Trim(), "已失效视频", StringComparison.Ordinal);
                var cover = m.TryGetProperty("cover", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
                items.Add(new FavItem(id, bvid, cid > 0 ? cid : null,
                    string.IsNullOrEmpty(title) ? "(无标题)" : title!, owner, dur, invalid, cover));
            }
        }
        return (total, items);
    }

    /// <summary>
    /// 稿件详情（view，无 wbi）。code≠0（-404 失效 / -403 权限等）返回 null，由调用方记日志跳过。
    /// </summary>
    public async Task<ViewInfo?> ViewAsync(long avid, CancellationToken ct = default)
    {
        using var doc = await GetApiAsync(
            "https://api.bilibili.com/x/web-interface/view",
            new Dictionary<string, string> { ["aid"] = avid.ToString() },
            signed: false, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetInt32() != 0) return null;
        var d = root.GetProperty("data");

        var pages = new List<ViewPage>();
        if (d.TryGetProperty("pages", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                pages.Add(new ViewPage(
                    p.GetProperty("cid").GetInt64(),
                    p.TryGetProperty("part", out var pt) ? pt.GetString() ?? "" : "",
                    p.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : pages.Count + 1,
                    p.TryGetProperty("dimension", out var dim) && dim.ValueKind == JsonValueKind.Object
                        ? (dim.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null)
                        : null,
                    p.TryGetProperty("dimension", out var dim2) && dim2.ValueKind == JsonValueKind.Object
                        ? (dim2.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null)
                        : null,
                    p.TryGetProperty("duration", out var pd) && pd.ValueKind == JsonValueKind.Number ? pd.GetInt64() : 0));
            }
        }
        if (pages.Count == 0) return null;

        return new ViewInfo(
            d.TryGetProperty("aid", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64() : avid,
            d.TryGetProperty("bvid", out var bvid) ? bvid.GetString() ?? "" : "",
            d.TryGetProperty("title", out var t) ? t.GetString() ?? "(无标题)" : "(无标题)",
            d.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.Object &&
                ow.TryGetProperty("name", out var on) ? on.GetString() : null,
            // UP 主的 mid（数字 ID，永不随改名变动）。写进 entry.json 的 owner_id —— 外部播放器的 dual 源
            // 正是按它把同一 UP 主的作品归成一个合集，缺了会全塌进「未识别 UP 主」。
            d.TryGetProperty("owner", out var ow2) && ow2.ValueKind == JsonValueKind.Object &&
                ow2.TryGetProperty("mid", out var om) && om.ValueKind == JsonValueKind.Number ? om.GetInt64() : (long?)null,
            d.TryGetProperty("pic", out var pic) ? pic.GetString() : null,
            d.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt64() * 1000 : 0,
            pages,
            // 充电专属 = 稿件被 UP 设为充电可见，且当前账号 is_upower_play=false（没充/已过期）。
            IsTrue(d, "is_upower_exclusive") && !IsTrue(d, "is_upower_play"));
    }

    /// <summary>JSON 里某个布尔字段严格等于 true（缺失/类型不对都算 false）。</summary>
    private static bool IsTrue(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    // ---------------- HTTP 底座 ----------------

    private async Task<JsonDocument> GetApiAsync(
        string url, Dictionary<string, string> ps, bool signed, CancellationToken ct)
    {
        EnsureLoginCookies();
        await EnsureBuvidAsync(ct).ConfigureAwait(false);

        string query;
        if (signed)
        {
            var mixin = await WbiMixinAsync(ct).ConfigureAwait(false);
            var (q, rid) = SignWbi(ps, mixin);
            query = $"{q}&w_rid={rid}";
        }
        else
        {
            query = string.Join("&", ps.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}?{query}");
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }
}
