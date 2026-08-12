using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GBot.Plugins.EpayShop;

internal static class EpayClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static string Sign(IDictionary<string, string?> parameters, string key)
    {
        var items = parameters
            .Where(kv => kv.Key is not ("sign" or "sign_type"))
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
        var plain = string.Join("&", items) + key;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifySign(ShopConfig cfg, IDictionary<string, string?> payload)
    {
        var key = (cfg.EpayKey ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return false;
        if (!payload.TryGetValue("sign", out var recv) || string.IsNullOrEmpty(recv)) return false;
        var calc = Sign(payload, key);
        return string.Equals(recv, calc, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReady(ShopConfig cfg)
        => cfg.EpayEnabled
           && !string.IsNullOrWhiteSpace(cfg.EpayUrl)
           && !string.IsNullOrWhiteSpace(cfg.EpayPid)
           && !string.IsNullOrWhiteSpace(cfg.EpayKey);

    public static string BaseUrl(ShopConfig cfg) => (cfg.EpayUrl ?? "").Trim().TrimEnd('/');

    public static async Task<EpayCreateResult> CreateOrderAsync(
        ShopConfig cfg, string outTradeNo, string name, string money, string payType, string notifyUrl, CancellationToken ct)
    {
        var pid = cfg.EpayPid.Trim();
        var key = cfg.EpayKey.Trim();
        var returnUrl = string.IsNullOrWhiteSpace(cfg.ReturnUrl) ? notifyUrl : cfg.ReturnUrl.Trim();
        var clientIp = string.IsNullOrWhiteSpace(cfg.ClientIp) ? "1.2.3.4" : cfg.ClientIp.Trim();
        var siteName = string.IsNullOrWhiteSpace(cfg.SiteName) ? "QQ机器人小店" : cfg.SiteName.Trim();

        var parameters = new Dictionary<string, string?>
        {
            ["pid"] = pid,
            ["type"] = payType,
            ["out_trade_no"] = outTradeNo,
            ["notify_url"] = notifyUrl,
            ["return_url"] = returnUrl,
            ["name"] = name,
            ["money"] = money,
            ["clientip"] = clientIp,
            ["device"] = "jump",
            ["sitename"] = siteName,
        };
        parameters["sign"] = Sign(parameters, key);
        parameters["sign_type"] = "MD5";

        var form = new FormUrlEncodedContent(parameters
            .Where(kv => kv.Value is not null)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!)));

        try
        {
            using var resp = await Http.PostAsync($"{BaseUrl(cfg)}/mapi.php", form, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!TryParseJson(text, out var data) || data is null)
                return new EpayCreateResult { Ok = false, Msg = $"返回非JSON: {Truncate(text, 200)}" };

            var code = data.Value.TryGetProperty("code", out var c) ? c.ToString() : "";
            if (code != "1")
            {
                var msg = data.Value.TryGetProperty("msg", out var m) ? m.GetString() : $"code={code}";
                return new EpayCreateResult { Ok = false, Msg = msg ?? "下单失败" };
            }

            return new EpayCreateResult
            {
                Ok = true,
                PayUrl = GetStr(data.Value, "payurl"),
                QrCode = GetStr(data.Value, "qrcode"),
                UrlScheme = GetStr(data.Value, "urlscheme"),
                TradeNo = GetStr(data.Value, "trade_no"),
                Msg = GetStr(data.Value, "msg") is { Length: > 0 } s ? s : "ok",
            };
        }
        catch (Exception ex)
        {
            return new EpayCreateResult { Ok = false, Msg = ex.Message };
        }
    }

    public static async Task<EpayQueryResult> QueryOrderAsync(
        ShopConfig cfg, string outTradeNo, string tradeNo, CancellationToken ct)
    {
        var pid = cfg.EpayPid.Trim();
        var key = cfg.EpayKey.Trim();
        var baseUrl = BaseUrl(cfg);
        string? err = null;

        foreach (var (orderNo, typ) in OrderNoCandidates(outTradeNo, tradeNo))
        {
            var findParams = new Dictionary<string, string?>
            {
                ["order_no"] = orderNo,
                ["type"] = typ,
                ["pid"] = pid,
            };
            findParams["sign"] = Sign(findParams, key);
            findParams["sign_type"] = "MD5";

            try
            {
                var form = new FormUrlEncodedContent(findParams
                    .Where(kv => kv.Value is not null)
                    .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!)));
                using var resp = await Http.PostAsync($"{baseUrl}/api/findorder", form, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!TryParseJson(text, out var body) || body is null)
                {
                    err = Truncate(text, 120);
                    continue;
                }

                var code = body.Value.TryGetProperty("code", out var c) ? c.ToString() : "";
                if (code is "200" or "1")
                {
                    var info = body.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                        ? d
                        : body.Value;
                    return new EpayQueryResult
                    {
                        Ok = true,
                        Status = GetStr(info, "status"),
                        TradeStatus = GetStr(info, "trade_status"),
                        TradeNo = GetStr(info, "trade_no"),
                        Msg = GetStr(body.Value, "msg") is { Length: > 0 } m ? m : "ok",
                    };
                }

                err = GetStr(body.Value, "msg");
                if (err.Contains("验签") || err.Contains("密钥"))
                    break;
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
        }

        foreach (var (orderNo, typ) in OrderNoCandidates(outTradeNo, tradeNo))
        {
            if (typ != "1") continue;
            try
            {
                var url =
                    $"{baseUrl}/api.php?act=order&pid={Uri.EscapeDataString(pid)}&key={Uri.EscapeDataString(key)}&out_trade_no={Uri.EscapeDataString(orderNo)}";
                using var resp = await Http.GetAsync(url, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!TryParseJson(text, out var body2) || body2 is null)
                {
                    err ??= Truncate(text, 120);
                    continue;
                }

                if (body2.Value.TryGetProperty("code", out var c2))
                {
                    var code = c2.ToString();
                    if (code is not ("1" or "0" or "200")
                        && body2.Value.TryGetProperty("msg", out _)
                        && !body2.Value.TryGetProperty("status", out _))
                    {
                        err = GetStr(body2.Value, "msg");
                        continue;
                    }
                }

                return new EpayQueryResult
                {
                    Ok = true,
                    Status = GetStr(body2.Value, "status"),
                    TradeStatus = GetStr(body2.Value, "trade_status"),
                    TradeNo = GetStr(body2.Value, "trade_no"),
                    Msg = GetStr(body2.Value, "msg") is { Length: > 0 } m ? m : "ok",
                };
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
        }

        return new EpayQueryResult { Ok = false, Msg = err ?? "查询失败" };
    }

    public static bool IsPaid(EpayQueryResult info)
    {
        if (!info.Ok) return false;
        if (string.Equals(info.TradeStatus, "TRADE_SUCCESS", StringComparison.OrdinalIgnoreCase))
            return true;
        var status = (info.Status ?? "").Trim().ToLowerInvariant();
        return status is "1" or "2" or "paid" or "success" or "已支付" or "支付成功";
    }

    public static IEnumerable<(string No, string Type)> OrderNoCandidates(string outTradeNo, string tradeNo)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        void Push(string? no, string typ)
        {
            no = (no ?? "").Trim();
            if (string.IsNullOrEmpty(no)) return;
            var key = no + "|" + typ;
            if (!seen.Add(key)) return;
            list.Add((no, typ));
        }

        var q = (outTradeNo ?? "").Trim();
        var tn = (tradeNo ?? "").Trim();
        if (q.StartsWith("Y", StringComparison.OrdinalIgnoreCase))
            Push(q, "2");
        Push(q, "1");
        if (q.All(char.IsDigit) && q.Length >= 16)
        {
            Push(q.Length >= 18 ? q[..18] : q, "1");
            Push(q.Length >= 16 ? q[..16] : q, "1");
            if (q.Length > 1) Push(q[..^1], "1");
            if (q.Length > 18) Push(q[..15], "1");
        }
        if (!string.IsNullOrEmpty(tn))
        {
            Push(tn, "2");
            Push(tn, "1");
        }
        return list;
    }

    public static string ResolvePayType(ShopConfig cfg, string hint)
    {
        var def = string.IsNullOrWhiteSpace(cfg.PayType) ? "alipay" : cfg.PayType.Trim();
        if (string.IsNullOrWhiteSpace(hint)) return def;
        var h = hint.ToLowerInvariant();
        if (hint.Contains("微信") || h.Contains("wx") || h.Contains("wechat")) return "wxpay";
        if (h.Contains("qq") || hint.Contains("QQ")) return "qqpay";
        if (hint.Contains("支付宝") || h.Contains("ali")) return "alipay";
        if (hint is "alipay" or "wxpay" or "qqpay") return hint;
        return def;
    }

    public static bool IsKnownPayHint(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return false;
        var h = hint.ToLowerInvariant();
        return hint.Contains("支付宝") || hint.Contains("微信") || hint.Contains("QQ")
               || h.Contains("qq") || h.Contains("ali") || h.Contains("wx")
               || hint is "alipay" or "wxpay" or "qqpay";
    }

    public static string PayTypeName(string payType) => payType switch
    {
        "wxpay" => "微信",
        "qqpay" => "QQ钱包",
        _ => "支付宝",
    };

    public static string NormalizePayUrl(ShopConfig cfg, string raw)
    {
        raw = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return raw;
        if (raw.StartsWith("//")) return "https:" + raw;
        if (raw.StartsWith("/"))
        {
            var b = BaseUrl(cfg);
            return string.IsNullOrEmpty(b) ? raw : b + raw;
        }
        return raw;
    }

    public static string? PickPayImageUrl(ShopConfig cfg, string qrcode, string payurl)
    {
        foreach (var cand in new[] { qrcode, payurl })
        {
            var u = NormalizePayUrl(cfg, cand);
            if (IsImageUrl(u)) return u;
        }
        return null;
    }

    public static bool IsImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        var path = url.Split('?', 2)[0].ToLowerInvariant();
        return path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg")
               || path.EndsWith(".gif") || path.EndsWith(".webp")
               || path.Contains("/qr") || path.Contains("qrcode");
    }

    public static string GenOrderNo()
    {
        var stamp = DateTime.Now.ToString("yyMMddHHmmss");
        const string alphabet = "0123456789ABCDEF";
        Span<char> suffix = stackalloc char[4];
        Random.Shared.GetItems(alphabet.AsSpan(), suffix);
        return "E" + stamp + new string(suffix);
    }

    private static bool TryParseJson(string text, out JsonElement? el)
    {
        el = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            el = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? "",
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.ToString(),
            }
            : "";

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
