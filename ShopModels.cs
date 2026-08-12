using System.Text.Json.Serialization;

namespace GBot.Plugins.EpayShop;

internal static class OrderStatus
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Delivered = "delivered";
    public const string NoStock = "no_stock";
    public const string Closed = "closed";
}

internal sealed class GlobalConfig
{
    [JsonPropertyName("master_id")]
    public string MasterId { get; set; } = "";

    [JsonPropertyName("notify_admin")]
    public bool NotifyAdmin { get; set; } = true;

    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 8087;

    /// <summary>公网回调基址，如 https://example.com 或 http://ip:8087，下单拼 /epay/notify。</summary>
    [JsonPropertyName("notify_base")]
    public string NotifyBase { get; set; } = "";

    /// <summary>主动消息用官方 AppID。</summary>
    [JsonPropertyName("robot_id")]
    public string RobotId { get; set; } = "";

    /// <summary>自更新：GitHub 仓库 Owner。</summary>
    [JsonPropertyName("update_owner")]
    public string UpdateOwner { get; set; } = "TenYearsPro";

    /// <summary>自更新：GitHub 仓库名。</summary>
    [JsonPropertyName("update_repo")]
    public string UpdateRepo { get; set; } = "PayBotdll";

    /// <summary>自更新：Release 附件名。</summary>
    [JsonPropertyName("update_asset")]
    public string UpdateAsset { get; set; } = "PayBot.dll";

    /// <summary>启用后自动检查更新。</summary>
    [JsonPropertyName("update_auto_check")]
    public bool UpdateAutoCheck { get; set; } = true;

    /// <summary>是否接受预发布。</summary>
    [JsonPropertyName("update_include_prerelease")]
    public bool UpdateIncludePrerelease { get; set; }

    /// <summary>GitHub Token（公开仓可空，提高限额）。</summary>
    [JsonPropertyName("github_token")]
    public string GithubToken { get; set; } = "";

    /// <summary>
    /// GitHub 加速前缀（优先于直连）。例：https://gh-proxy.com/
    /// 实际请求为：前缀 + 原 URL。
    /// </summary>
    [JsonPropertyName("update_proxies")]
    public List<string> UpdateProxies { get; set; } =
    [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    ];
}

internal sealed class ShopConfig
{
    [JsonPropertyName("epay_url")]
    public string EpayUrl { get; set; } = "https://pay.10sn.cn";

    [JsonPropertyName("epay_pid")]
    public string EpayPid { get; set; } = "";

    [JsonPropertyName("epay_key")]
    public string EpayKey { get; set; } = "";

    [JsonPropertyName("pay_type")]
    public string PayType { get; set; } = "alipay";

    [JsonPropertyName("return_url")]
    public string ReturnUrl { get; set; } = "";

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = "QQ机器人小店";

    [JsonPropertyName("client_ip")]
    public string ClientIp { get; set; } = "1.2.3.4";

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "";

    [JsonPropertyName("epay_enabled")]
    public bool EpayEnabled { get; set; }
}

internal sealed class ProductInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("stock")]
    public List<string> Stock { get; set; } = [];

    [JsonPropertyName("sold")]
    public int Sold { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

internal sealed class OrderInfo
{
    [JsonPropertyName("out_trade_no")]
    public string OutTradeNo { get; set; } = "";

    [JsonPropertyName("trade_no")]
    public string TradeNo { get; set; } = "";

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>下单时的纯文字昵称（非 QQ 号）。</summary>
    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = "";

    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = "";

    [JsonPropertyName("shop_gid")]
    public string ShopGid { get; set; } = "";

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("product_name")]
    public string ProductName { get; set; } = "";

    [JsonPropertyName("money")]
    public string Money { get; set; } = "";

    [JsonPropertyName("pay_type")]
    public string PayType { get; set; } = "";

    [JsonPropertyName("pay_url")]
    public string PayUrl { get; set; } = "";

    [JsonPropertyName("qr_img")]
    public string QrImg { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = OrderStatus.Pending;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("delivered_code")]
    public string DeliveredCode { get; set; } = "";

    [JsonPropertyName("delivered_at")]
    public long DeliveredAt { get; set; }

    [JsonPropertyName("source_deliver")]
    public string SourceDeliver { get; set; } = "";

    [JsonPropertyName("close_reason")]
    public string CloseReason { get; set; } = "";

    [JsonPropertyName("closed_at")]
    public long ClosedAt { get; set; }

    [JsonPropertyName("watch_until")]
    public long WatchUntil { get; set; }

    [JsonPropertyName("reopened_from_timeout")]
    public bool ReopenedFromTimeout { get; set; }
}

internal sealed class UserTradeStats
{
    /// <summary>交易成功笔数（已支付：已发货或已支付缺货）。</summary>
    [JsonPropertyName("order_count")]
    public int OrderCount { get; set; }

    /// <summary>交易成功累计金额。</summary>
    [JsonPropertyName("total_amount")]
    public decimal TotalAmount { get; set; }

    [JsonPropertyName("last_at")]
    public long LastAt { get; set; }

    /// <summary>最近一次记录的纯文字昵称（用于排行展示）。</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
}

internal sealed class ShopData
{
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    [JsonPropertyName("products")]
    public Dictionary<string, ProductInfo> Products { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("orders")]
    public Dictionary<string, OrderInfo> Orders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("pending")]
    public List<string> Pending { get; set; } = [];

    [JsonPropertyName("product_seq")]
    public int ProductSeq { get; set; } = 1000;

    [JsonPropertyName("config")]
    public ShopConfig Config { get; set; } = new();

    /// <summary>用户交易成功统计（按 openid）。</summary>
    [JsonPropertyName("user_stats")]
    public Dictionary<string, UserTradeStats> UserStats { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class DeliverResult
{
    public bool Ok { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Code { get; init; }
    public bool ViaButton { get; init; }
}

internal sealed class EpayCreateResult
{
    public bool Ok { get; init; }
    public string Msg { get; init; } = "";
    public string PayUrl { get; init; } = "";
    public string QrCode { get; init; } = "";
    public string UrlScheme { get; init; } = "";
    public string TradeNo { get; init; } = "";
}

internal sealed class EpayQueryResult
{
    public bool Ok { get; init; }
    public string Msg { get; init; } = "";
    public string Status { get; init; } = "";
    public string TradeStatus { get; init; } = "";
    public string TradeNo { get; init; } = "";
}
