using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GBot.PluginAbstractions;

namespace GBot.Plugins.EpayShop;

/// <summary>命令路由 / 下单发货 / 轮询。消息优先 Markdown + qqbot-cmd-input 镶嵌指令。</summary>
internal sealed class EpayShopEngine
{
    public const int OrderPayTimeoutSec = 2 * 60;
    public const int LatePayWatchSec = 5 * 60;
    private const int MaxPollHttp = 8;
    private const int MaxStockFileBytes = 2 * 1024 * 1024;
    /// <summary>单次文本/TXT 上架最多张数（去重后）。</summary>
    private const int MaxStockAddPerBatch = 2000;

    private static readonly HttpClient StockFileHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, // 禁止自动跳转到内网
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    /// <summary>文件名：群ID#商品ID.txt</summary>
    private static readonly Regex StockFileNameRegex = new(
        @"^(?<gid>[^#]+)#(?<pid>\d+)\.txt$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly EpayShopStore _store;
    private readonly IPluginApi _api;

    static EpayShopEngine()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EpayShopEngine(EpayShopStore store, IPluginApi api)
    {
        _store = store;
        _api = api;
    }

    public string BuildNotifyUrl()
    {
        var bas = (_store.Global.NotifyBase ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(bas))
            return $"http://127.0.0.1:{_store.Global.HttpPort}/epay/notify";
        return bas + "/epay/notify";
    }

    public async Task<int> HandleMessageAsync(EventContext ctx, CancellationToken ct = default)
    {
        var sender = ctx.FromOpenId;
        var isGroup = ctx.Scene == MessageScene.Group;
        var reply = new Replier(ctx, _api, _store);

        try
        {
            // 私聊 TXT：文件名 群ID#商品ID.txt
            if (!isGroup && await TryHandlePrivateStockFileAsync(reply, sender, ctx, ct))
                return 1;

            var rawText = ctx.Text;
            if (string.IsNullOrWhiteSpace(rawText))
                rawText = ctx.MessageContent;

            // 私聊一律保留换行，避免「上架卡密」多行卡密被压成一条；群聊仍折叠空白
            var keepNewlines = !isGroup || LooksLikeStockCommand(rawText);
            var content = Normalize(rawText, keepNewlines);
            if (string.IsNullOrEmpty(content))
                return 0;

            // 卡密填入输入框的提示，不当命令
            if (content.StartsWith("请不要点击发送", StringComparison.Ordinal))
                return 1;

            var shopGid = isGroup ? ctx.SourceId : "";
            var prefix = "";
            if (isGroup)
            {
                var shop = _store.GetOrCreateShop(shopGid, create: false);
                prefix = shop.Config.Prefix ?? "";
                if (!string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, StringComparison.Ordinal))
                    content = keepNewlines
                        ? content[prefix.Length..].Trim('\r', '\n', ' ', '\t')
                        : content[prefix.Length..].Trim();
            }

            var cmd = keepNewlines ? FirstLine(content) : content;

            if (!isGroup)
            {
                await HandlePrivateAsync(reply, sender, cmd, content, ct);
                return 1;
            }

            await HandleGroupAsync(reply, sender, shopGid, prefix, cmd, content, ct);
            return reply.Handled ? 1 : 0;
        }
        catch (ShopDataCorruptException ex)
        {
            _api.LogError($"[易支付商店] {ex.Message}");
            await reply.TextAsync("❌ 店铺数据文件损坏，已拒绝写入以免清空库存。请检查 shops 目录备份。", ct);
            return 1;
        }
        catch (Exception ex)
        {
            _api.LogWarning($"[易支付商店] 处理异常: {ex.GetType().Name}: {ex.Message}");
            await reply.TextAsync($"❌ 处理失败：{ex.Message}", ct);
            return 1;
        }
    }

    private async Task HandlePrivateAsync(Replier reply, string sender, string cmd, string content, CancellationToken ct)
    {
        if (cmd.StartsWith("存主人"))
        {
            await CmdSaveOwnerAsync(reply, sender, content, null, ct);
            return;
        }
        if (cmd.StartsWith("查主人") || cmd.StartsWith("群主人"))
        {
            await CmdQueryOwnerAsync(reply, content, ct);
            return;
        }
        if (cmd.StartsWith("删主人") || cmd.StartsWith("删除主人"))
        {
            if (!IsGlobalMaster(sender))
            {
                await reply.TextAsync("❌ 仅最高权限可删主人", ct);
                return;
            }
            await CmdDeleteOwnerAsync(reply, content, ct);
            return;
        }
        if (cmd is "检查更新" or "更新插件")
        {
            if (!IsGlobalMaster(sender))
            {
                await reply.TextAsync("❌ 仅最高权限可检查更新", ct);
                return;
            }
            await CmdCheckUpdateAsync(reply, ct);
            return;
        }
        if (cmd.StartsWith("店铺配置") || cmd.StartsWith("查看配置"))
        {
            var name = cmd.StartsWith("店铺配置") ? "店铺配置" : "查看配置";
            var (gid, _) = await RequirePrivateShopAsync(reply, sender, name, content, ct);
            if (!string.IsNullOrEmpty(gid))
                await CmdShowConfigAsync(reply, gid, ct);
            return;
        }
        if (cmd.StartsWith("设置易支付"))
        {
            await CmdSetEpayAsync(reply, sender, content, ct);
            return;
        }
        if (cmd.StartsWith("设置网关"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置网关", content, ct);
            if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(rest))
            {
                PatchCfg(gid, c => c.EpayUrl = rest.TrimEnd('/'));
                await reply.MdAsync($"✅ 已更新网关：`{rest.TrimEnd('/')}`", ct: ct);
            }
            else if (!string.IsNullOrEmpty(gid))
                await reply.TextAsync("格式：设置网关 <群ID> <网址>", ct);
            return;
        }
        if (cmd.StartsWith("设置商户"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置商户", content, ct);
            if (!string.IsNullOrEmpty(gid))
                await CmdSetMerchantAsync(reply, gid, "设置商户 " + rest, ct);
            return;
        }
        if (cmd.StartsWith("设置支付方式"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置支付方式", content, ct);
            if (!string.IsNullOrEmpty(gid))
                await CmdSetPayTypeAsync(reply, gid, "设置支付方式 " + rest, ct);
            return;
        }
        if (cmd.StartsWith("设置跳转"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置跳转", content, ct);
            if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(rest))
            {
                PatchCfg(gid, c => c.ReturnUrl = rest);
                await reply.MdAsync($"✅ 已更新跳转：{rest}", ct: ct);
            }
            else if (!string.IsNullOrEmpty(gid))
                await reply.TextAsync("格式：设置跳转 <群ID> <网址>", ct);
            return;
        }
        if (cmd.StartsWith("设置站点"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置站点", content, ct);
            if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(rest))
            {
                PatchCfg(gid, c => c.SiteName = rest);
                await reply.MdAsync($"✅ 已更新站点：{rest}", ct: ct);
            }
            else if (!string.IsNullOrEmpty(gid))
                await reply.TextAsync("格式：设置站点 <群ID> <名称>", ct);
            return;
        }
        if (cmd.StartsWith("设置IP", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("设置ip"))
        {
            var name = cmd.StartsWith("设置IP") ? "设置IP" : "设置ip";
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, name, content, ct);
            if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(rest))
            {
                PatchCfg(gid, c => c.ClientIp = rest);
                await reply.MdAsync($"✅ 已更新 IP：`{rest}`", ct: ct);
            }
            else if (!string.IsNullOrEmpty(gid))
                await reply.TextAsync($"格式：{name} <群ID> <IP>", ct);
            return;
        }
        if (cmd.StartsWith("设置前缀"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "设置前缀", content, ct);
            if (!string.IsNullOrEmpty(gid))
            {
                if (rest is "空" or "无" or "清除" or "清空" or "-") rest = "";
                PatchCfg(gid, c => c.Prefix = rest);
                await reply.MdAsync(string.IsNullOrEmpty(rest) ? "✅ 已清除命令前缀" : $"✅ 命令前缀：`{rest}`", ct: ct);
            }
            return;
        }
        if (cmd.StartsWith("上架卡密"))
        {
            var (gid, rest) = await RequirePrivateShopAsync(reply, sender, "上架卡密", content, ct);
            if (!string.IsNullOrEmpty(gid))
                await CmdAddStockAsync(reply, gid, "上架卡密 " + rest, ct);
            return;
        }
        if (cmd is "帮助" or "菜单" or "help")
        {
            await ShowMainMenuAsync(reply, "", "", ct);
            return;
        }
        if (IsGroupOnlyCommand(cmd))
        {
            await reply.MdAsync(
                MdFmt.Join(
                    "❌ 请到对应**群内**发送该命令",
                    "",
                    "私聊用于：",
                    MdFmt.Cmd("存主人", "存主人"),
                    MdFmt.Cmd("设置易支付", "设置易支付"),
                    MdFmt.Cmd("上架卡密", "上架卡密"),
                    MdFmt.Cmd("店铺配置", "店铺配置")),
                ct: ct);
            return;
        }
        reply.Handled = false;
    }

    private async Task HandleGroupAsync(
        Replier reply, string sender, string shopGid, string prefix, string cmd, string content, CancellationToken ct)
    {
        if (cmd.StartsWith("存主人") || cmd.StartsWith("查主人") || cmd.StartsWith("群主人")
            || cmd.StartsWith("删主人") || cmd.StartsWith("删除主人")
            || cmd.StartsWith("设置易支付") || cmd.StartsWith("上架卡密")
            || cmd.StartsWith("店铺配置") || cmd.StartsWith("查看配置"))
        {
            await reply.MdAsync("❌ 该命令请**私聊**机器人发送（不要在群里发）", ct: ct);
            return;
        }

        // 未开店也可查群ID，便于「存主人 / 设置易支付」
        if (cmd is "群信息" or "本群信息" or "群ID" or "群id")
        {
            await CmdGroupInfoAsync(reply, sender, shopGid, ct);
            return;
        }

        if (cmd.StartsWith("开启易支付") || cmd == "开易支付")
        {
            if (!IsMaster(sender, shopGid))
            {
                await reply.TextAsync("❌ 仅本群店主可操作", ct);
                return;
            }
            PatchCfg(shopGid, c => c.EpayEnabled = true);
            await reply.MdAsync("✅ 本群易支付已**开启**", ct: ct);
            return;
        }
        if (cmd is "检查更新" or "更新插件")
        {
            if (!IsMaster(sender, shopGid))
            {
                await reply.TextAsync("❌ 仅本群店主或最高权限可检查更新", ct);
                return;
            }
            await CmdCheckUpdateAsync(reply, ct);
            return;
        }
        if (cmd.StartsWith("关闭易支付") || cmd == "关易支付")
        {
            if (!IsMaster(sender, shopGid))
            {
                await reply.TextAsync("❌ 仅本群店主可操作", ct);
                return;
            }
            PatchCfg(shopGid, c => c.EpayEnabled = false);
            await reply.MdAsync("✅ 本群易支付已**关闭**", ct: ct);
            return;
        }

        var shop = _store.GetOrCreateShop(shopGid);
        if (!shop.Config.EpayEnabled)
        {
            reply.Handled = false;
            return;
        }

        // 已有成交用户发消息时刷新昵称，便于排行识别
        var nick = PickSenderDisplayName(reply.Ctx);
        if (!string.IsNullOrEmpty(nick))
            _store.TouchUserDisplayName(shopGid, sender, nick);

        if (cmd.StartsWith("shop_"))
        {
            await HandleShopButtonAsync(reply, sender, cmd, shopGid, prefix, ct);
            return;
        }
        if (cmd is "帮助" or "菜单" or "help")
        {
            await ShowMainMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (cmd is "商品" or "商店" or "商品列表")
        {
            await ShowProductsMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (cmd.StartsWith("购买") || cmd.StartsWith("买"))
        {
            await CmdBuyAsync(reply, sender, content, shopGid, prefix, ct);
            return;
        }
        if (cmd is "我的订单" or "订单")
        {
            await ShowOrdersMenuAsync(reply, sender, prefix, shopGid, ct);
            return;
        }
        if (cmd is "我的统计" or "下单统计")
        {
            await CmdMyStatsAsync(reply, sender, shopGid, prefix, ct);
            return;
        }
        if (cmd.StartsWith("查单"))
        {
            await CmdQueryAsync(reply, sender, content, shopGid, ct);
            return;
        }

        var groupAdmin = cmd is "库存" or "库存查询" or "统计" or "销售统计"
                         || cmd.StartsWith("添加商品")
                         || cmd.StartsWith("删除商品")
                         || cmd.StartsWith("补单");
        if (!groupAdmin)
        {
            reply.Handled = false;
            return;
        }
        if (!IsMaster(sender, shopGid))
        {
            await reply.TextAsync("❌ 仅本群店主可操作", ct);
            return;
        }
        if (cmd.StartsWith("添加商品"))
        {
            await CmdAddProductAsync(reply, shopGid, content, ct);
            return;
        }
        if (cmd.StartsWith("删除商品"))
        {
            await CmdDelProductAsync(reply, shopGid, content, ct);
            return;
        }
        if (cmd.StartsWith("补单"))
        {
            await CmdForceDeliverAsync(reply, shopGid, content, ct);
            return;
        }
        if (cmd is "库存" or "库存查询")
        {
            await reply.MdAsync(StockText(shopGid), ct: ct);
            return;
        }
        if (cmd is "统计" or "销售统计")
        {
            await CmdShopStatsAsync(reply, shopGid, prefix, ct);
            return;
        }
    }

    private static bool IsGroupOnlyCommand(string cmd)
        => cmd is "商品" or "商店" or "商品列表" or "我的订单" or "订单" or "库存" or "库存查询"
           or "群信息" or "本群信息" or "群ID" or "群id"
           or "我的统计" or "下单统计" or "统计" or "销售统计"
           or "开启易支付" or "开易支付" or "关闭易支付" or "关易支付"
           or "检查更新" or "更新插件"
           || cmd.StartsWith("购买") || cmd.StartsWith("买")
           || cmd.StartsWith("添加商品") || cmd.StartsWith("删除商品")
           || cmd.StartsWith("补单") || cmd.StartsWith("查单");

    private async Task CmdGroupInfoAsync(Replier reply, string sender, string shopGid, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(shopGid))
        {
            await reply.TextAsync("❌ 请在群内发送「群信息」", ct);
            return;
        }

        ShopData shop;
        try { shop = _store.GetOrCreateShop(shopGid, create: false); }
        catch (ShopDataCorruptException)
        {
            shop = new ShopData();
        }

        var owner = _store.GetOwner(shopGid) ?? shop.Owner;
        var ownerTip = string.IsNullOrWhiteSpace(owner) ? "（未绑定）" : $"`{owner}`";
        var prefix = shop.Config.Prefix ?? "";
        var payOn = shop.Config.EpayEnabled;

        await reply.MdAsync(
            MdFmt.Join(
                MdFmt.Title("📋", "群信息"),
                MdFmt.Hr(),
                $"**群ID**：`{shopGid}`",
                $"**你的openid**：`{sender}`",
                $"**店主**：{ownerTip}",
                $"**易支付**：{(payOn ? "已开启" : "未开启")}",
                string.IsNullOrEmpty(prefix) ? "**命令前缀**：（无）" : $"**命令前缀**：`{prefix}`",
                "",
                "开店可私聊机器人：",
                MdFmt.Cmd($"存主人{shopGid}", "存主人"),
                MdFmt.Cmd($"设置易支付 {shopGid}#", "设置易支付")),
            ct: ct);
    }

    // ── 菜单（MD + 镶嵌指令）────────────────────────────────

    private async Task ShowMainMenuAsync(Replier reply, string prefix, string shopGid, CancellationToken ct)
    {
        var body = HelpText(prefix ?? "", shopGid);
        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("商品", "shop_products", id: "m_goods"),
                MdFmt.InputButton("购买", "shop_products", id: "m_buy"),
                MdFmt.InputButton("我的订单", "shop_orders", id: "m_orders"),
            },
            new[]
            {
                MdFmt.InputButton("我的统计", "我的统计", id: "m_mystats"),
                MdFmt.InputButton("查单", "查单 ", id: "m_query", enter: false),
                MdFmt.InputButton("群信息", "群信息", id: "m_ginfo"),
            },
            new[]
            {
                MdFmt.InputButton("添加商品", "添加商品 ", id: "m_add", enter: false),
                MdFmt.InputButton("统计", "统计", id: "m_stats"),
                MdFmt.InputButton("菜单", "shop_menu", id: "m_menu"),
            });
        await reply.MdAsync(body, keyboard, ct);
    }

    private async Task ShowProductsMenuAsync(Replier reply, string prefix, string shopGid, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(shopGid))
        {
            await reply.TextAsync("❌ 请在群内查看商品", ct);
            return;
        }
        var shop = _store.GetOrCreateShop(shopGid);
        var text = ListProductsText(shopGid);
        var buyable = shop.Products.Values
            .Where(x => x.Enabled && x.Stock.Count > 0)
            .OrderBy(x => int.TryParse(x.Id, out var n) ? n : 0)
            .ToList();

        if (shop.Products.Count == 0)
            text += "\n\n本群暂无商品";
        else if (buyable.Count == 0)
            text += "\n\n当前没有可购买库存";
        if (!shop.Config.EpayEnabled)
            text += "\n\n⚠️ 本群支付已关闭";

        // QQ 键盘最多 5 行×5 钮：商品按钮最多 8 个（4 行×2）+ 1 行导航
        var buyBtns = buyable.Take(8).Select(item =>
        {
            var label = Trunc($"买{item.Id}:{item.Name}", 12);
            return MdFmt.InputButton(label, $"shop_buy_{item.Id}", id: $"buy_{item.Id}");
        }).ToList();

        var nav = new object[]
        {
            MdFmt.InputButton("购买", "shop_products", id: "p_buy"),
            MdFmt.InputButton("我的订单", "shop_orders", id: "p_orders"),
            MdFmt.InputButton("返回菜单", "shop_menu", id: "p_back"),
        };
        var keyboard = buyBtns.Count > 0
            ? MdFmt.KeyboardRows(buyBtns, maxPerRow: 2, nav)
            : MdFmt.Keyboard(nav);
        await reply.MdAsync(text, keyboard, ct);
    }

    private async Task ShowOrdersMenuAsync(Replier reply, string sender, string prefix, string shopGid, CancellationToken ct)
    {
        var text = MyOrdersText(sender, shopGid);
        var latest = LatestUserPending(sender, shopGid);
        var queryData = latest is not null ? $"shop_query_{latest.OutTradeNo}" : "查单 ";
        var queryEnter = latest is not null;
        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("查单", queryData, id: "o_query", enter: queryEnter),
                MdFmt.InputButton("我的统计", "我的统计", id: "o_stats"),
            },
            new[]
            {
                MdFmt.InputButton("商品", "shop_products", id: "o_goods"),
                MdFmt.InputButton("返回菜单", "shop_menu", id: "o_back"),
            });
        await reply.MdAsync(text, keyboard, ct);
    }

    private async Task CmdMyStatsAsync(Replier reply, string sender, string shopGid, string prefix, CancellationToken ct)
    {
        var (count, amount) = _store.GetUserStats(shopGid, sender);
        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("我的订单", "shop_orders", id: "ms_orders"),
                MdFmt.InputButton("商品", "shop_products", id: "ms_goods"),
                MdFmt.InputButton("返回菜单", "shop_menu", id: "ms_back"),
            });
        await reply.MdAsync(
            MdFmt.Join(
                MdFmt.Title("📊", "我的下单统计"),
                MdFmt.Hr(),
                "仅统计**交易成功**（已支付发货 / 已支付缺货）",
                $"**成功笔数**：{count}",
                $"**成功金额**：¥{amount:0.00}"),
            keyboard, ct);
    }

    private async Task CmdShopStatsAsync(Replier reply, string shopGid, string prefix, CancellationToken ct)
    {
        var (count, amount, users) = _store.GetShopStats(shopGid);
        var top = _store.GetTopUserStats(shopGid, 8);
        var lines = new List<string>
        {
            MdFmt.Title("📈", "本群销售统计"),
            MdFmt.Hr(),
            "仅统计**交易成功**（已支付发货 / 已支付缺货）",
            $"**成功笔数**：{count}",
            $"**成功金额**：¥{amount:0.00}",
            $"**成交用户**：{users}",
        };
        if (top.Count > 0)
        {
            lines.Add("");
            lines.Add("**用户排行（按金额）**");
            var i = 1;
            foreach (var (uid, st) in top)
            {
                lines.Add($"{i}. {FormatRankUser(uid, st.DisplayName)}  {st.OrderCount}笔  ¥{st.TotalAmount:0.00}");
                i++;
            }
        }
        else
        {
            lines.Add("");
            lines.Add("暂无成交记录");
        }

        lines.Add("");
        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("库存", "库存", id: "st_stock"),
                MdFmt.InputButton("返回菜单", "shop_menu", id: "st_back"),
            });
        await reply.MdAsync(MdFmt.Join(lines.ToArray()), keyboard, ct);
    }

    private async Task ShowPayMethodMenuAsync(Replier reply, string shopGid, string productId, string prefix, CancellationToken ct)
    {
        var p = prefix ?? "";
        var shop = _store.GetOrCreateShop(shopGid);
        if (!shop.Products.TryGetValue(productId, out var product))
        {
            await reply.TextAsync("❌ 商品不存在", ct);
            await ShowProductsMenuAsync(reply, p, shopGid, ct);
            return;
        }

        var md = MdFmt.Join(
            MdFmt.Title("🛒", "确认购买"),
            MdFmt.Hr(),
            $"**商品**：`[{productId}]` {product.Name}",
            $"**价格**：¥{product.Price:0.00}",
            $"**库存**：{product.Stock.Count}",
            MdFmt.Hr(),
            "请选择支付方式：");
        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("支付宝", $"shop_buy_{productId}_alipay", id: $"pay_ali_{productId}"),
                MdFmt.InputButton("微信", $"shop_buy_{productId}_wxpay", id: $"pay_wx_{productId}"),
                MdFmt.InputButton("QQ钱包", $"shop_buy_{productId}_qqpay", id: $"pay_qq_{productId}"),
            },
            new[]
            {
                MdFmt.InputButton("返回商品", "shop_products", id: $"pay_back_g_{productId}"),
                MdFmt.InputButton("我的订单", "shop_orders", id: $"pay_ord_{productId}"),
                MdFmt.InputButton("返回菜单", "shop_menu", id: $"pay_menu_{productId}"),
            });
        await reply.MdAsync(md, keyboard, ct);
    }

    private async Task HandleShopButtonAsync(
        Replier reply, string sender, string cmd, string shopGid, string prefix, CancellationToken ct)
    {
        if (cmd is "shop_menu" or "shop_help")
        {
            await ShowMainMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (cmd == "shop_products")
        {
            await ShowProductsMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (cmd == "shop_orders")
        {
            await ShowOrdersMenuAsync(reply, sender, prefix, shopGid, ct);
            return;
        }
        var m = Regex.Match(cmd, @"^shop_buy_(\d+)(?:_(alipay|wxpay|qqpay))?$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var pid = m.Groups[1].Value;
            var pay = m.Groups[2].Value.ToLowerInvariant();
            var hint = pay switch { "alipay" => "支付宝", "wxpay" => "微信", "qqpay" => "QQ", _ => "" };
            var body = "购买" + pid + (string.IsNullOrEmpty(hint) ? "" : " " + hint);
            await CmdBuyAsync(reply, sender, body, shopGid, prefix, ct);
            return;
        }
        if (cmd.StartsWith("shop_query_"))
        {
            var no = cmd["shop_query_".Length..].Trim();
            if (!string.IsNullOrEmpty(no))
                await CmdQueryAsync(reply, sender, "查单 " + no, shopGid, ct);
            else
                await reply.TextAsync("订单号无效", ct);
            return;
        }
        await ShowMainMenuAsync(reply, prefix, shopGid, ct);
    }

    // ── 购买 / 查单 / 发货 ──────────────────────────────────

    private async Task CmdBuyAsync(
        Replier reply, string sender, string content, string shopGid, string prefix, CancellationToken ct)
    {
        var m = Regex.Match(content, @"^(?:购买|买)\s*(\d+)\s*(.*)$");
        if (!m.Success)
        {
            await ShowProductsMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        var productId = m.Groups[1].Value;
        var payHint = m.Groups[2].Value.Trim();
        var shop = _store.GetOrCreateShop(shopGid);
        if (!shop.Products.TryGetValue(productId, out var product))
        {
            await reply.TextAsync("❌ 商品不存在，请从本群商品列表选择", ct);
            await ShowProductsMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (product.Stock.Count == 0)
        {
            await reply.TextAsync("❌ 该商品暂时缺货", ct);
            await ShowProductsMenuAsync(reply, prefix, shopGid, ct);
            return;
        }
        if (string.IsNullOrEmpty(payHint))
        {
            await ShowPayMethodMenuAsync(reply, shopGid, productId, prefix, ct);
            return;
        }
        if (!EpayClient.IsKnownPayHint(payHint))
        {
            await reply.TextAsync("请选择支付方式：支付宝 / 微信 / QQ", ct);
            await ShowPayMethodMenuAsync(reply, shopGid, productId, prefix, ct);
            return;
        }
        if (!shop.Config.EpayEnabled)
        {
            await reply.TextAsync("❌ 本群商店已关闭支付", ct);
            return;
        }
        if (!EpayClient.IsReady(shop.Config))
        {
            await reply.MdAsync(
                "❌ 本群店主未配置易支付。\n请店主私聊：" + MdFmt.Cmd($"设置易支付 {shopGid}#", "设置易支付"),
                ct: ct);
            return;
        }

        var payType = EpayClient.ResolvePayType(shop.Config, payHint);
        var money = $"{product.Price:0.00}";
        var outTradeNo = EpayClient.GenOrderNo();
        var name = Trunc(product.Name, 50);
        var notifyUrl = BuildNotifyUrl();

        var create = await EpayClient.CreateOrderAsync(shop.Config, outTradeNo, name, money, payType, notifyUrl, ct);
        if (!create.Ok)
        {
            await reply.TextAsync($"❌ 下单失败：{create.Msg}", ct);
            return;
        }

        var payUrl = EpayClient.NormalizePayUrl(shop.Config, create.PayUrl is { Length: > 0 } u ? u : (create.QrCode is { Length: > 0 } q ? q : create.UrlScheme));
        var qrImg = EpayClient.PickPayImageUrl(shop.Config, create.QrCode, create.PayUrl);
        if (string.IsNullOrEmpty(payUrl) && string.IsNullOrEmpty(qrImg))
        {
            await reply.TextAsync("❌ 下单成功但未返回支付链接", ct);
            return;
        }

        var order = new OrderInfo
        {
            OutTradeNo = outTradeNo,
            TradeNo = create.TradeNo,
            UserId = sender,
            UserName = PickSenderDisplayName(reply.Ctx),
            GroupId = shopGid,
            ShopGid = shopGid,
            ProductId = productId,
            ProductName = name,
            Money = money,
            PayType = payType,
            PayUrl = string.IsNullOrEmpty(payUrl) ? (qrImg ?? "") : payUrl,
            QrImg = qrImg ?? "",
            Status = OrderStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        _store.SaveOrder(shopGid, outTradeNo, order);
        _store.AddPending(shopGid, outTradeNo);

        string? payTip;
        string? qrMd = null;
        if (!string.IsNullOrEmpty(qrImg))
        {
            qrMd = $"![图片 #1024px #1024px]({qrImg})";
            payTip = "请扫上方二维码完成支付"
                     + (!string.IsNullOrEmpty(payUrl) && payUrl != qrImg ? $"\n备用链接：{payUrl}" : "");
        }
        else
            payTip = $"请打开链接完成支付：\n{payUrl}";

        var md = MdFmt.Join(
            MdFmt.Title("🧾", "订单已创建"),
            qrMd,
            $"**订单号**：`{outTradeNo}`",
            $"**商品**：{name}",
            $"**金额**：¥{money}",
            $"**方式**：{EpayClient.PayTypeName(payType)}",
            MdFmt.Hr(),
            payTip,
            MdFmt.Hr(),
            "请在 **2 分钟内**完成支付，超时将自动关闭，请勿再付款。",
            "",
            MdFmt.Cmd($"{prefix}查单 {outTradeNo}", "我已支付"),
            MdFmt.Nav(prefix, ("我的订单", "我的订单"), ("返回菜单", "菜单")));
        await reply.MdAsync(md, ct: ct);
    }

    private async Task CmdQueryAsync(Replier reply, string sender, string content, string? shopGid, CancellationToken ct)
    {
        var parts = content.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var queryNo = parts.Length > 1 ? parts[1].Trim() : "";
        if (string.IsNullOrEmpty(queryNo))
        {
            var latest = LatestUserPending(sender, shopGid);
            if (latest is null)
            {
                await reply.MdAsync("用法：" + MdFmt.Cmd("查单 ", "查单") + "\n或先下单后再发「查单」", ct: ct);
                return;
            }
            queryNo = latest.OutTradeNo;
        }

        var found = ResolveLocalOrder(queryNo, shopGid);
        if (found is null)
        {
            await reply.TextAsync("❌ 本地无此订单。请用下单时机器人回复的订单号查单", ct);
            return;
        }
        var (orderGid, outTradeNo, order) = found.Value;
        if (order.UserId != sender && !IsMaster(sender, orderGid))
        {
            await reply.TextAsync("❌ 只能查询自己的订单", ct);
            return;
        }

        if (order.Status == OrderStatus.Delivered)
        {
            var code = order.DeliveredCode ?? "";
            var isBuyer = order.UserId == sender;
            if (!isBuyer)
            {
                await reply.MdAsync(
                    MdFmt.Join(
                        "✅ 该订单已发货",
                        $"订单号：`{outTradeNo}`",
                        $"商品：{order.ProductName}",
                        "卡密仅购买者本人可查看，请让买家发送：",
                        MdFmt.Cmd($"查单 {outTradeNo}", "查单")),
                    ct: ct);
                return;
            }
            if (!string.IsNullOrEmpty(code) && await SendDeliveryCardAsync(reply, order, code, outTradeNo, useReply: true, ct))
                return;
            await reply.MdAsync(
                MdFmt.Join(
                    "✅ 该订单已发货",
                    $"订单号：`{outTradeNo}`",
                    $"卡密：`{code}`",
                    "⚠️ 请自行保存，切勿转发"),
                ct: ct);
            return;
        }

        if (order.Status == OrderStatus.Pending)
        {
            var created = order.CreatedAt;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (created > 0 && now - created >= OrderPayTimeoutSec)
            {
                var shop = _store.GetOrCreateShop(orderGid);
                var infoPre = await EpayClient.QueryOrderAsync(shop.Config, outTradeNo, order.TradeNo, ct);
                if (!EpayClient.IsPaid(infoPre))
                {
                    await CloseOrderTimeoutAsync(orderGid, outTradeNo, order, notify: true, ct);
                    await reply.MdAsync(
                        MdFmt.Join(
                            "⏰ 订单已超时关闭",
                            $"订单号：`{outTradeNo}`",
                            "请勿再付款！需要请重新下单。",
                            "",
                            MdFmt.Nav(_store.GetOrCreateShop(orderGid).Config.Prefix, ("商品", "商品"), ("菜单", "菜单"))),
                        ct: ct);
                    return;
                }
                order = _store.GetOrder(orderGid, outTradeNo) ?? order;
            }
        }

        {
            var shop = _store.GetOrCreateShop(orderGid);
            var info = await EpayClient.QueryOrderAsync(shop.Config, outTradeNo, order.TradeNo, ct);
            if (!info.Ok)
            {
                var tip = string.IsNullOrWhiteSpace(info.Msg) ? "未知错误" : info.Msg;
                var lines = new List<string> { $"查询失败：{tip}" };
                if (tip.Contains("有效订单") || order.UserId == sender || IsMaster(sender, orderGid))
                {
                    lines.Add("");
                    lines.Add("可让店主发送：");
                    lines.Add(MdFmt.Cmd($"补单 {outTradeNo}", "补单"));
                }
                await reply.MdAsync(MdFmt.Join(lines.ToArray()), ct: ct);
                return;
            }
            if (!string.IsNullOrEmpty(info.TradeNo) && info.TradeNo != order.TradeNo)
            {
                order.TradeNo = info.TradeNo;
                _store.SaveOrder(orderGid, outTradeNo, order);
            }
            if (!EpayClient.IsPaid(info))
            {
                if (order.Status == OrderStatus.Closed && order.CloseReason == "timeout")
                {
                    await reply.MdAsync($"⏰ 订单已超时关闭\n订单号：`{outTradeNo}`\n请勿再付款！", ct: ct);
                    return;
                }
                await reply.MdAsync(
                    MdFmt.Join(
                        "⏳ 订单未支付或处理中",
                        $"订单号：`{outTradeNo}`",
                        $"状态：{(string.IsNullOrEmpty(info.Status) ? "未支付" : info.Status)}",
                        "",
                        MdFmt.Cmd($"查单 {outTradeNo}", "再查一次")),
                    ct: ct);
                return;
            }

            var r = await TryDeliverAsync(outTradeNo, info.TradeNo, "manual", orderGid, reply, ct);
            if (r.Status == OrderStatus.Delivered)
            {
                if (r.ViaButton) return;
                var isBuyer = order.UserId == sender;
                if (!isBuyer)
                {
                    await reply.MdAsync(
                        $"✅ 支付成功，已发货\n订单号：`{outTradeNo}`\n卡密仅购买者可查看：" + MdFmt.Cmd($"查单 {outTradeNo}", "查单"),
                        ct: ct);
                    return;
                }
                await reply.MdAsync(
                    MdFmt.Join(
                        "✅ 支付成功，已发货",
                        $"订单号：`{outTradeNo}`",
                        $"商品：{order.ProductName}",
                        $"卡密：`{r.Code}`",
                        "⚠️ 请自行保存，切勿转发"),
                    ct: ct);
            }
            else if (r.Status == OrderStatus.NoStock)
                await reply.MdAsync(
                    MdFmt.Join(
                        "⚠️ 已支付但库存不足，已通知店主",
                        "请稍等补货后再次查单；或让店主发送：",
                        MdFmt.Cmd($"补单 {outTradeNo}", "补单")),
                    ct: ct);
            else
                await reply.TextAsync($"处理结果：{r.Message}", ct);
        }
    }

    public async Task<DeliverResult> TryDeliverAsync(
        string outTradeNo, string tradeNo, string source, string shopGid, Replier? reply, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(shopGid))
        {
            var found = _store.FindOrderAnywhere(outTradeNo);
            if (found is null)
                return new DeliverResult { Ok = false, Status = "missing", Message = "order not found" };
            shopGid = found.Value.Gid;
        }

        StockDeliverMutation mut;
        try
        {
            mut = _store.TryDeliverMutation(shopGid, outTradeNo, tradeNo, source);
        }
        catch (ShopDataCorruptException ex)
        {
            _api.LogError($"[易支付商店] {ex.Message}");
            return new DeliverResult { Ok = false, Status = "corrupt", Message = "shop data corrupt" };
        }

        var order = mut.Order;
        if (mut.Status == OrderStatus.NoStock && order is not null)
        {
            if (mut.Message == "product missing")
                await NotifyMastersAsync($"⚠️ 订单 {outTradeNo} 已支付，但商品已删除，无法发货", shopGid, ct);
            else
                await NotifyMastersAsync(
                    $"⚠️ 订单已支付但缺货\n订单：{outTradeNo}\n商品：{order.ProductName}\n用户：{order.UserId}\n请补货后让用户「查单」",
                    shopGid, ct);
        }

        if (!mut.Ok || mut.Status != OrderStatus.Delivered || order is null || string.IsNullOrEmpty(mut.Code))
        {
            return new DeliverResult
            {
                Ok = mut.Ok,
                Status = mut.Status,
                Message = mut.Message,
                Code = mut.Code,
            };
        }

        // 已发过货：仅返回，避免重复通知
        if (mut.Message == "already delivered")
        {
            return new DeliverResult
            {
                Ok = true,
                Status = OrderStatus.Delivered,
                Message = mut.Message,
                Code = mut.Code,
            };
        }

        var code = mut.Code!;
        var useReply = source == "manual" && reply is not null;
        var viaButton = await SendDeliveryCardAsync(reply, order, code, outTradeNo, useReply, ct);

        if (!viaButton)
        {
            var tip = MdFmt.Join(
                "✅ 支付成功，已发货",
                $"订单号：`{outTradeNo}`",
                $"商品：{order.ProductName}",
                "请购买者本人发送：",
                MdFmt.Cmd($"查单 {outTradeNo}", "查单"),
                "（卡密仅购买者可查看）");
            if (useReply)
            {
                await reply!.MdAsync(
                    MdFmt.Join(
                        "✅ 支付成功，已发货",
                        $"订单号：`{outTradeNo}`",
                        $"商品：{order.ProductName}",
                        $"卡密：`{code}`",
                        "⚠️ 请自行保存，切勿转发"),
                    ct: ct);
            }
            else
            {
                var robot = _store.Global.RobotId;
                if (!string.IsNullOrEmpty(robot) && !string.IsNullOrEmpty(order.GroupId))
                    await ApiCompat.SendGroupMarkdownAsync(_api, robot, order.GroupId, tip, ct: ct);
            }
        }

        await NotifyMastersAsync(
            $"💸 发货成功\n订单：{outTradeNo}\n商品：{order.ProductName}\n金额：¥{order.Money}\n用户：{order.UserId}\n来源：{source}",
            shopGid, ct);

        return new DeliverResult
        {
            Ok = true,
            Status = OrderStatus.Delivered,
            Message = "delivered",
            Code = code,
            ViaButton = viaButton,
        };
    }

    private async Task<bool> SendDeliveryCardAsync(
        Replier? reply, OrderInfo order, string code, string outTradeNo, bool useReply, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.UserId)) return false;
        var prefix = "";
        var gid = order.GroupId;
        if (!string.IsNullOrEmpty(gid))
            prefix = _store.GetOrCreateShop(gid).Config.Prefix ?? "";

        var card = MdFmt.Join(
            MdFmt.Title("🔑", "卡密"),
            $"订单 `{outTradeNo}` 支付成功",
            $"商品：{order.ProductName}",
            "请点击下方「卡密」按钮查看（仅购买者）",
            "⚠️ **请不要点击发送！** 复制后自行保存");

        var keyboard = MdFmt.Keyboard(
            new[]
            {
                MdFmt.InputButton("卡密", $"请不要点击发送，卡密：{code}", style: 1, id: "card_key",
                    onlyUsers: new[] { order.UserId }, enter: false),
            },
            new[]
            {
                MdFmt.InputButton("我的订单", "shop_orders", id: "deliv_orders"),
                MdFmt.InputButton("商品", "shop_products", id: "deliv_goods"),
                MdFmt.InputButton("返回菜单", "shop_menu", id: "deliv_back"),
            });

        try
        {
            if (useReply && reply is not null)
            {
                await reply.MdAsync(card, keyboard, ct);
                return true;
            }
            var robot = _store.Global.RobotId;
            if (!string.IsNullOrEmpty(robot) && !string.IsNullOrEmpty(gid))
            {
                await ApiCompat.SendGroupMarkdownAsync(_api, robot, gid, card, keyboard: keyboard, ct: ct);
                return true;
            }
        }
        catch (Exception ex)
        {
            _api.LogWarning($"[易支付商店] 发货卡片失败: {ex.Message}");
        }
        return false;
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        var gids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _store.ListShopIds()) gids.Add(id);
        foreach (var kv in _store.LoadOwners()) gids.Add(kv.Key);
        if (gids.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var httpN = 0;
        var delivered = 0;
        var timedOut = 0;

        foreach (var shopGid in gids)
        {
            ShopData shop;
            try { shop = _store.GetOrCreateShop(shopGid, create: false); }
            catch (ShopDataCorruptException ex)
            {
                _api.LogError($"[易支付商店] {ex.Message}");
                continue;
            }
            var pending = shop.Pending.ToList();
            if (pending.Count == 0) continue;
            var still = new List<string>();

            foreach (var outTradeNo in pending)
            {
                if (!shop.Orders.TryGetValue(outTradeNo, out var order))
                    continue;
                if (order.Status is OrderStatus.Delivered or OrderStatus.NoStock)
                    continue;
                if (order.Status == OrderStatus.Closed && order.CloseReason != "timeout")
                    continue;
                if (order.Status == OrderStatus.Closed && order.CloseReason == "timeout"
                    && order.WatchUntil > 0 && now > order.WatchUntil)
                    continue;

                if (order.Status == OrderStatus.Pending
                    && order.CreatedAt > 0
                    && now - order.CreatedAt >= OrderPayTimeoutSec)
                {
                    // 先查是否已付，再关
                }

                if (httpN >= MaxPollHttp)
                {
                    still.Add(outTradeNo);
                    continue;
                }

                httpN++;
                var info = await EpayClient.QueryOrderAsync(shop.Config, outTradeNo, order.TradeNo, ct);
                var paid = EpayClient.IsPaid(info);
                if (info.Ok && !string.IsNullOrEmpty(info.TradeNo) && info.TradeNo != order.TradeNo)
                {
                    order.TradeNo = info.TradeNo;
                    _store.SaveOrder(shopGid, outTradeNo, order);
                }

                if (paid)
                {
                    var r = await TryDeliverAsync(outTradeNo, info.TradeNo, "poll", shopGid, null, ct);
                    if (r.Status == OrderStatus.Delivered) delivered++;
                    else still.Add(outTradeNo);
                    continue;
                }

                if (order.Status == OrderStatus.Pending
                    && order.CreatedAt > 0
                    && now - order.CreatedAt >= OrderPayTimeoutSec)
                {
                    await CloseOrderTimeoutAsync(shopGid, outTradeNo, order, notify: true, ct);
                    timedOut++;
                    still.Add(outTradeNo); // 继续 watch
                    continue;
                }

                still.Add(outTradeNo);
            }

            // 合并写回：保留轮询期间新加入的 Pending，避免整表覆盖丢单
            try
            {
                _store.MergePendingAfterPoll(shopGid, pending, still);
            }
            catch (ShopDataCorruptException ex)
            {
                _api.LogError($"[易支付商店] {ex.Message}");
            }
        }

        if (delivered > 0 || timedOut > 0)
            _api.LogInfo($"[易支付商店] 轮询完成 deliver={delivered} timeout={timedOut} http={httpN}");
    }

    public async Task<string> HandleNotifyAsync(IReadOnlyDictionary<string, string> payload, CancellationToken ct)
    {
        if (!payload.TryGetValue("out_trade_no", out var outTradeNo) || string.IsNullOrWhiteSpace(outTradeNo))
            return "fail";
        var tradeStatus = payload.TryGetValue("trade_status", out var ts) ? ts : "";
        if (!string.IsNullOrEmpty(tradeStatus) && tradeStatus != "TRADE_SUCCESS")
            return "fail";

        var found = _store.FindOrderAnywhere(outTradeNo);
        if (found is null)
        {
            _api.LogWarning($"[易支付商店] webhook 无订单: {outTradeNo}");
            return "fail";
        }

        var (shopGid, _) = found.Value;
        ShopData shop;
        try { shop = _store.GetOrCreateShop(shopGid); }
        catch (ShopDataCorruptException ex)
        {
            _api.LogError($"[易支付商店] {ex.Message}");
            return "fail";
        }

        var map = payload.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);
        // 强制验签：无 sign / 签名错误一律拒绝，防止伪造回调领卡
        if (!EpayClient.VerifySign(shop.Config, map))
        {
            _api.LogWarning($"[易支付商店] 回调验签失败或缺少 sign: {outTradeNo}");
            return "fail";
        }

        var tradeNo = payload.TryGetValue("trade_no", out var tn) ? tn : "";
        await TryDeliverAsync(outTradeNo, tradeNo ?? "", "webhook", shopGid, null, ct);
        return "success";
    }

    private async Task CloseOrderTimeoutAsync(
        string shopGid, string outTradeNo, OrderInfo order, bool notify, CancellationToken ct)
    {
        if (order.Status != OrderStatus.Pending) return;
        order.Status = OrderStatus.Closed;
        order.CloseReason = "timeout";
        order.ClosedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        order.WatchUntil = order.ClosedAt + LatePayWatchSec;
        _store.SaveOrder(shopGid, outTradeNo, order);

        if (!notify) return;
        var prefix = _store.GetOrCreateShop(shopGid).Config.Prefix ?? "";
        var text = MdFmt.Join(
            "⏰ 订单已超时",
            $"订单号：`{outTradeNo}`",
            $"商品：{order.ProductName}",
            $"金额：¥{order.Money}",
            MdFmt.Hr(),
            "超过2分钟未支付，订单已自动关闭。",
            "请勿再付款！如需购买请重新下单。",
            "",
            MdFmt.Nav(prefix, ("重新购买", "商品"), ("我的订单", "我的订单"), ("返回菜单", "菜单")));

        var robot = _store.Global.RobotId;
        try
        {
            if (!string.IsNullOrEmpty(robot) && !string.IsNullOrEmpty(order.GroupId))
                await ApiCompat.SendGroupMarkdownAsync(_api, robot, order.GroupId, text, ct: ct);
            if (!string.IsNullOrEmpty(robot) && !string.IsNullOrEmpty(order.UserId))
                await ApiCompat.SendPrivateMarkdownAsync(_api, robot, order.UserId, text, ct: ct);
        }
        catch (Exception ex)
        {
            _api.LogWarning($"[易支付商店] 超时提醒失败: {ex.Message}");
        }
    }

    // ── 店主命令 ────────────────────────────────────────────

    private async Task CmdAddProductAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var body = content["添加商品".Length..].Trim();
        var parts = body.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2)
        {
            await reply.MdAsync(
                "格式：添加商品 名称|价格|描述\n例：" + MdFmt.Cmd("添加商品 月卡|9.9|一个月会员", "添加商品示例"),
                ct: ct);
            return;
        }
        if (!double.TryParse(parts[1], out var price) || price <= 0)
        {
            await reply.TextAsync("❌ 价格必须是大于0的数字", ct);
            return;
        }
        var name = parts[0];
        var desc = parts.Length > 2 ? parts[2] : "";
        var shop = _store.GetOrCreateShop(shopGid);
        shop.ProductSeq = Math.Max(shop.ProductSeq, 1000) + 1;
        var pid = shop.ProductSeq.ToString();
        shop.Products[pid] = new ProductInfo
        {
            Id = pid,
            Name = name,
            Price = price,
            Desc = desc,
            Stock = [],
            Enabled = true,
        };
        _store.SaveShop(shopGid, shop);
        await reply.MdAsync(
            MdFmt.Join(
                "✅ 已添加到本群商店",
                $"**ID**：`{pid}`",
                $"**名称**：{name}",
                $"**价格**：¥{price:0.00}",
                "请私聊上架卡密（须带群ID）：",
                MdFmt.Cmd($"上架卡密 {shopGid} {pid}", "上架卡密"),
                "然后换行贴卡密；或发 TXT：",
                $"`{shopGid}#{pid}.txt`（一行一个）"),
            ct: ct);
    }

    private async Task CmdDelProductAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var m = Regex.Match(content, @"(\d+)");
        if (!m.Success)
        {
            await reply.TextAsync("格式：删除商品 <商品ID>", ct);
            return;
        }
        var pid = m.Groups[1].Value;
        var shop = _store.GetOrCreateShop(shopGid);
        if (!shop.Products.Remove(pid))
        {
            await reply.TextAsync("❌ 本店无此商品", ct);
            return;
        }
        _store.SaveShop(shopGid, shop);
        await reply.MdAsync($"✅ 已删除本店商品 `{pid}`", ct: ct);
    }

    private async Task CmdAddStockAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var m = Regex.Match(content, @"^上架卡密\s*(\d+)\s*(.*)$", RegexOptions.Singleline);
        if (!m.Success)
        {
            await reply.TextAsync(
                "格式（私聊，一行一个）：\n上架卡密 <群ID> 1001\n卡密1\n卡密2\n\n或发 TXT，文件名：群ID#1001.txt",
                ct);
            return;
        }
        var pid = m.Groups[1].Value;
        var raw = m.Groups[2].Value.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            await reply.TextAsync("请在商品ID后跟上卡密，一行一个；也可私聊发送 TXT（文件名：群ID#商品ID.txt）", ct);
            return;
        }
        var codes = ParseStockCodes(raw);
        await CommitStockAsync(reply, shopGid, pid, codes, null, ct);
    }

    /// <summary>私聊 TXT 上架：文件名须为 群ID#商品ID.txt。</summary>
    private async Task<bool> TryHandlePrivateStockFileAsync(
        Replier reply, string sender, EventContext ctx, CancellationToken ct)
    {
        var file = FindStockTxtFile(ctx);
        if (file is null) return false;

        var (fileName, url) = file.Value;
        var m = StockFileNameRegex.Match(fileName);
        if (!m.Success)
        {
            await reply.MdAsync(
                MdFmt.Join(
                    "❌ 卡密 TXT 文件名不正确",
                    "请命名为：`群ID#商品ID.txt`",
                    "例如：`670EA1E621C22A8BBE75F8BA0BD9C969#1001.txt`"),
                ct: ct);
            return true;
        }

        var gid = m.Groups["gid"].Value.Trim();
        var pid = m.Groups["pid"].Value;
        if (string.IsNullOrEmpty(gid))
        {
            await reply.TextAsync("❌ 文件名中未解析到群ID", ct);
            return true;
        }
        if (!IsMaster(sender, gid))
        {
            await reply.TextAsync("❌ 你不是该群店主", ct);
            return true;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            await reply.TextAsync("❌ 未能获取文件下载地址，请重新发送该 TXT", ct);
            return true;
        }
        if (!TryValidateStockDownloadUrl(url, out var urlErr))
        {
            await reply.TextAsync($"❌ 不安全的文件地址：{urlErr}", ct);
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = await DownloadStockFileAsync(url, ct);
        }
        catch (InvalidOperationException ex)
        {
            await reply.TextAsync($"❌ {ex.Message}", ct);
            return true;
        }
        catch (Exception ex)
        {
            await reply.TextAsync($"❌ 下载 TXT 失败：{ex.Message}", ct);
            return true;
        }

        if (bytes.Length == 0)
        {
            await reply.TextAsync("❌ TXT 文件为空", ct);
            return true;
        }

        var text = DecodeStockFileText(bytes);
        var codes = ParseStockCodes(text);
        await CommitStockAsync(reply, gid, pid, codes, fileName, ct);
        return true;
    }

    private static async Task<byte[]> DownloadStockFileAsync(string url, CancellationToken ct)
    {
        using var resp = await StockFileHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("下载被重定向，已拒绝（防 SSRF）");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载 TXT 失败：HTTP {(int)resp.StatusCode}");

        if (resp.Content.Headers.ContentLength is long len && len > MaxStockFileBytes)
            throw new InvalidOperationException($"TXT 过大（上限 {MaxStockFileBytes / 1024 / 1024}MB）");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream(capacity: 64 * 1024);
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n <= 0) break;
            total += n;
            if (total > MaxStockFileBytes)
                throw new InvalidOperationException($"TXT 过大（上限 {MaxStockFileBytes / 1024 / 1024}MB）");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static bool TryValidateStockDownloadUrl(string url, out string error)
    {
        error = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "不是合法 URL";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "仅允许 http/https";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "禁止带认证信息的 URL";
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "主机名为空";
            return false;
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "禁止本机/内网主机名";
            return false;
        }

        try
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (IsPrivateOrReservedIp(ip))
                {
                    error = "禁止内网/保留 IP";
                    return false;
                }
            }
            else
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0)
                {
                    error = "无法解析主机";
                    return false;
                }
                foreach (var a in addrs)
                {
                    if (IsPrivateOrReservedIp(a))
                    {
                        error = "主机解析到内网/保留 IP";
                        return false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = $"主机校验失败：{ex.Message}";
            return false;
        }

        return true;
    }

    private static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        // 0.0.0.0/8, 10/8, 127/8, 169.254/16, 172.16/12, 192.168/16, 100.64/10(CGNAT), 224+/multicast
        if (b[0] == 0 || b[0] == 10 || b[0] == 127) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        if (b[0] >= 224) return true;
        return false;
    }

    private static (string FileName, string Url)? FindStockTxtFile(EventContext ctx)
    {
        // 1) 消息段（不限 Type，只要带 filename）
        if (ctx.Segments is { Count: > 0 })
        {
            foreach (var seg in ctx.Segments)
            {
                var name = SegStr(seg, "filename")
                    ?? SegStr(seg, "file_name")
                    ?? SegStr(seg, "fileName")
                    ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var fileName = Path.GetFileName(name.Trim());
                if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.IndexOf('#') < 0) continue;
                var url = SegStr(seg, "url") ?? SegStr(seg, "file_url") ?? "";
                return (fileName, url.Trim());
            }
        }

        // 2) RawJson.attachments 兜底
        foreach (var (fileName, url) in EnumerateRawJsonTxtFiles(ctx))
        {
            if (fileName.IndexOf('#') >= 0)
                return (fileName, url);
        }

        // 3) 文本摘要里出现 [文件] xxx#yyy.txt
        foreach (var tip in new[] { ctx.Text, ctx.MessageContent, ctx.DisplaySummary })
        {
            if (string.IsNullOrWhiteSpace(tip)) continue;
            var m = Regex.Match(tip, @"([^\s\\/]+#[0-9]+\.txt)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var fileName = Path.GetFileName(m.Groups[1].Value.Trim());
            // 无 url 时仍返回，后续给出明确错误，避免“完全没反应”
            return (fileName, "");
        }

        return null;
    }

    private static IEnumerable<(string FileName, string Url)> EnumerateRawJsonTxtFiles(EventContext ctx)
    {
        if (!TryGetRawJsonRoot(ctx, out var root))
            yield break;

        foreach (var arr in FindAttachmentsArrays(root))
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = JsonStr(a, "filename") ?? JsonStr(a, "file_name") ?? JsonStr(a, "fileName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                var fileName = Path.GetFileName(name.Trim());
                var url = JsonStr(a, "url") ?? JsonStr(a, "file_url") ?? "";
                yield return (fileName, url);
            }
        }
    }

    private static IEnumerable<JsonElement> FindAttachmentsArrays(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (root.TryGetProperty("attachments", out var a1) && a1.ValueKind == JsonValueKind.Array)
            yield return a1;
        if (root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("attachments", out var a2) && a2.ValueKind == JsonValueKind.Array)
                yield return a2;
            if (d.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("attachments", out var a3) && a3.ValueKind == JsonValueKind.Array)
                yield return a3;
        }
    }

    private static bool TryGetRawJsonRoot(EventContext ctx, out JsonElement root)
    {
        root = default;
        try
        {
            var raw = ctx.RawJson;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var text = raw.Trim();
            if (text[0] is not ('{' or '[')) return false;
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? JsonStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static string? SegStr(MessageSegment seg, string key)
    {
        try
        {
            var v = seg[key];
            if (v is null) return null;
            var s = v as string ?? v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ParseStockCodes(string raw)
    {
        var codes = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return codes;
        foreach (var line in Regex.Split(raw, @"[\n\r]+"))
        {
            var l = line.Trim();
            if (string.IsNullOrEmpty(l)) continue;
            if (l.Contains(',') || l.Contains('，'))
            {
                foreach (var part in Regex.Split(l, "[,，]+"))
                {
                    var c = part.Trim();
                    if (!string.IsNullOrEmpty(c)) codes.Add(c);
                }
            }
            else codes.Add(l);
        }
        return codes;
    }

    private async Task CommitStockAsync(
        Replier reply, string shopGid, string pid, List<string> codes, string? sourceFile, CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            await reply.TextAsync("❌ 未解析到卡密（请一行一个，或逗号分隔）", ct);
            return;
        }

        // 批次内去重（保序）
        var batchUnique = new List<string>();
        var seenBatch = new HashSet<string>(StringComparer.Ordinal);
        var dupInBatch = 0;
        foreach (var c in codes)
        {
            if (!seenBatch.Add(c))
            {
                dupInBatch++;
                continue;
            }
            batchUnique.Add(c);
        }

        var shop = _store.GetOrCreateShop(shopGid);
        if (!shop.Products.TryGetValue(pid, out var product))
        {
            var tip = string.Join("、", shop.Products.Keys);
            await reply.TextAsync($"❌ 本店无此商品 ID：{pid}\n当前商品：{(string.IsNullOrEmpty(tip) ? "（无）" : tip)}", ct);
            return;
        }

        // 与已有库存去重
        var existing = new HashSet<string>(product.Stock, StringComparer.Ordinal);
        var toAdd = new List<string>();
        var dupExisting = 0;
        foreach (var c in batchUnique)
        {
            if (existing.Contains(c))
            {
                dupExisting++;
                continue;
            }
            toAdd.Add(c);
        }

        if (toAdd.Count == 0)
        {
            await reply.MdAsync(
                MdFmt.Join(
                    "❌ 没有可上架的新卡密",
                    $"批次重复：{dupInBatch}",
                    $"已在库中：{dupExisting}",
                    $"当前库存：{product.Stock.Count}"),
                ct: ct);
            return;
        }

        if (toAdd.Count > MaxStockAddPerBatch)
        {
            await reply.TextAsync(
                $"❌ 单次最多上架 {MaxStockAddPerBatch} 张（去重后 {toAdd.Count} 张），请拆分后再发",
                ct);
            return;
        }

        _store.AppendStock(shopGid, pid, toAdd);
        shop = _store.GetOrCreateShop(shopGid, create: false);
        product = shop.Products[pid];

        var lines = new List<string>
        {
            $"✅ 已上架 **{toAdd.Count}** 张卡密",
            $"群ID：`{shopGid}`",
            $"商品：{product.Name}(`{pid}`)",
            $"当前库存：{product.Stock.Count}",
        };
        if (dupInBatch > 0 || dupExisting > 0)
            lines.Add($"已跳过重复：批次内 {dupInBatch}，已在库 {dupExisting}");
        if (!string.IsNullOrEmpty(sourceFile))
            lines.Add($"来源文件：`{sourceFile}`");
        await reply.MdAsync(MdFmt.Join(lines.ToArray()), ct: ct);
    }

    private static string DecodeStockFileText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private async Task CmdForceDeliverAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var parts = content.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await reply.TextAsync("格式：补单 <订单号>", ct);
            return;
        }
        var queryNo = parts[1].Trim();
        var found = ResolveLocalOrder(queryNo, shopGid);
        if (found is null)
        {
            await reply.TextAsync("❌ 本店无此订单", ct);
            return;
        }
        var (foundGid, outTradeNo, order) = found.Value;
        if (!string.Equals(foundGid, shopGid, StringComparison.OrdinalIgnoreCase))
        {
            await reply.TextAsync("❌ 该订单不属于本群店铺", ct);
            return;
        }
        if (order.Status == OrderStatus.Delivered)
        {
            var code = order.DeliveredCode ?? "";
            if (!string.IsNullOrEmpty(code) && await SendDeliveryCardAsync(reply, order, code, outTradeNo, useReply: false, ct))
            {
                await reply.MdAsync($"✅ 该订单已发货\n订单号：`{outTradeNo}`\n已向群内推送「仅购买者可点」的卡密按钮", ct: ct);
                return;
            }
            await reply.MdAsync($"✅ 该订单已发货\n请让买家发送：" + MdFmt.Cmd($"查单 {outTradeNo}", "查单"), ct: ct);
            return;
        }
        var r = await TryDeliverAsync(outTradeNo, order.TradeNo, "force", shopGid, reply, ct);
        if (r.Status == OrderStatus.Delivered)
            await reply.MdAsync($"✅ 补单发货成功\n订单：`{outTradeNo}`\n卡密已推送给购买者（仅其可查看）", ct: ct);
        else if (r.Status == OrderStatus.NoStock)
            await reply.TextAsync("❌ 库存不足，请先私聊上架卡密再补单", ct);
        else
            await reply.TextAsync($"补单失败：{r.Message}", ct);
    }

    /// <summary>格式：设置易支付 群ID#网关#pid#密钥（兼容旧空格/| 写法）。</summary>
    private async Task CmdSetEpayAsync(Replier reply, string sender, string content, CancellationToken ct)
    {
        var body = (content ?? "").Trim();
        if (body.StartsWith("设置易支付", StringComparison.Ordinal))
            body = body["设置易支付".Length..].Trim();

        if (!TryParseSetEpayBody(body, out var shopGid, out var epayUrl, out var epayPid, out var epayKey))
        {
            await reply.MdAsync(
                MdFmt.Join(
                    "格式（私聊）：",
                    "`设置易支付 群ID#网关#pid#密钥`",
                    "例：",
                    "`设置易支付 670EA1...#https://pay.xxx.cn#1#密钥`"),
                ct: ct);
            return;
        }

        if (!IsMaster(sender, shopGid))
        {
            await reply.TextAsync("❌ 你不是该群店主", ct);
            return;
        }

        PatchCfg(shopGid, c =>
        {
            c.EpayUrl = epayUrl;
            c.EpayPid = epayPid;
            c.EpayKey = epayKey;
        });
        await reply.MdAsync(
            MdFmt.Join(
                "✅ 该群易支付已配置",
                $"群：`{shopGid}`",
                $"网关：`{epayUrl}`",
                $"商户ID：`{epayPid}`",
                "",
                "群内店主再发：" + MdFmt.Cmd("开启易支付", "开启易支付")),
            ct: ct);
    }

    private static bool TryParseSetEpayBody(
        string body, out string gid, out string url, out string pid, out string key)
    {
        gid = url = pid = key = "";
        body = (body ?? "").Trim();
        if (string.IsNullOrEmpty(body)) return false;

        if (body.Contains('#'))
        {
            var parts = body.Split('#').Select(p => p.Trim()).ToArray();
            // 标准：群ID#网关#pid#密钥
            if (parts.Length >= 4
                && !parts[0].Contains(' ')
                && !string.IsNullOrEmpty(parts[0])
                && !string.IsNullOrEmpty(parts[1])
                && !string.IsNullOrEmpty(parts[2])
                && !string.IsNullOrEmpty(parts[3]))
            {
                gid = parts[0];
                url = parts[1].TrimEnd('/');
                pid = parts[2];
                key = string.Join("#", parts.Skip(3)).Trim(); // 密钥内若含 # 仍拼回
                return !string.IsNullOrEmpty(key);
            }
            // 兼容：群ID 网关#pid#密钥
            if (parts.Length >= 3)
            {
                var head = parts[0].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (head.Length == 2
                    && !string.IsNullOrEmpty(parts[1])
                    && !string.IsNullOrEmpty(parts[2]))
                {
                    gid = head[0].Trim();
                    url = head[1].Trim().TrimEnd('/');
                    pid = parts[1];
                    key = string.Join("#", parts.Skip(2)).Trim();
                    return !string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(key);
                }
            }
            return false;
        }

        // 兼容：群ID 网关|pid|密钥 或 群ID|网关|pid|密钥
        if (body.Contains('|'))
        {
            var parts = body.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length >= 4
                && parts.Take(4).All(p => !string.IsNullOrEmpty(p))
                && !parts[0].Contains(' '))
            {
                gid = parts[0];
                url = parts[1].TrimEnd('/');
                pid = parts[2];
                key = parts[3];
                return true;
            }
            if (parts.Length >= 3)
            {
                var head = parts[0].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (head.Length == 2 && !string.IsNullOrEmpty(parts[1]) && !string.IsNullOrEmpty(parts[2]))
                {
                    gid = head[0].Trim();
                    url = head[1].Trim().TrimEnd('/');
                    pid = parts[1];
                    key = parts[2];
                    return true;
                }
            }
        }

        return false;
    }

    private async Task CmdSetMerchantAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var body = content["设置商户".Length..].Trim();
        string[] parts = body.Contains('|')
            ? body.Split('|').Select(p => p.Trim()).ToArray()
            : body.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await reply.TextAsync("格式：设置商户 pid|密钥", ct);
            return;
        }
        PatchCfg(shopGid, c =>
        {
            c.EpayPid = parts[0];
            c.EpayKey = parts[1];
        });
        await reply.MdAsync($"✅ 本群商户已更新\n商户ID：`{parts[0]}`", ct: ct);
    }

    private async Task CmdSetPayTypeAsync(Replier reply, string shopGid, string content, CancellationToken ct)
    {
        var body = content["设置支付方式".Length..].Trim();
        if (string.IsNullOrEmpty(body) || !EpayClient.IsKnownPayHint(body))
        {
            await reply.TextAsync("格式：设置支付方式 支付宝/微信/QQ", ct);
            return;
        }
        var payType = EpayClient.ResolvePayType(_store.GetOrCreateShop(shopGid).Config, body);
        PatchCfg(shopGid, c => c.PayType = payType);
        await reply.MdAsync($"✅ 本群默认支付方式：**{EpayClient.PayTypeName(payType)}**", ct: ct);
    }

    private async Task CmdShowConfigAsync(Replier reply, string shopGid, CancellationToken ct)
    {
        var shop = _store.GetOrCreateShop(shopGid);
        var cfg = shop.Config;
        var key = cfg.EpayKey ?? "";
        var keyShow = key.Length > 8 ? key[..4] + "****" + key[^4..] : (string.IsNullOrEmpty(key) ? "（未设置）" : "****");
        var owner = _store.GetOwner(shopGid) ?? shop.Owner;
        if (string.IsNullOrEmpty(owner)) owner = "（未绑定）";
        await reply.MdAsync(
            MdFmt.Join(
                MdFmt.Title("⚙️", "本群店铺配置"),
                MdFmt.Hr(),
                $"群ID：`{shopGid}`",
                $"店主：`{owner}`",
                $"易支付开关：{(cfg.EpayEnabled ? "开" : "关")}",
                $"网关：{(string.IsNullOrEmpty(cfg.EpayUrl) ? "（未设置）" : cfg.EpayUrl)}",
                $"商户ID：{(string.IsNullOrEmpty(cfg.EpayPid) ? "（未设置）" : cfg.EpayPid)}",
                $"密钥：{keyShow}",
                $"默认支付：{EpayClient.PayTypeName(cfg.PayType)}",
                $"跳转URL：{(string.IsNullOrEmpty(cfg.ReturnUrl) ? "（未设置）" : cfg.ReturnUrl)}",
                $"站点名：{(string.IsNullOrEmpty(cfg.SiteName) ? "（未设置）" : cfg.SiteName)}",
                $"客户端IP：{(string.IsNullOrEmpty(cfg.ClientIp) ? "（未设置）" : cfg.ClientIp)}",
                $"发货通知：{(_store.Global.NotifyAdmin ? "开" : "关")}",
                $"命令前缀：{(string.IsNullOrEmpty(cfg.Prefix) ? "（无）" : cfg.Prefix)}",
                $"异步通知：`{BuildNotifyUrl()}`",
                MdFmt.Hr(),
                "群内：" + MdFmt.Cmd("开启易支付", "开启易支付") + " / " + MdFmt.Cmd("关闭易支付", "关闭易支付")),
            ct: ct);
    }

    private async Task CmdSaveOwnerAsync(Replier reply, string sender, string content, string? currentGroupId, CancellationToken ct)
    {
        var body = content["存主人".Length..].Trim();
        var gid = Regex.Replace(string.IsNullOrEmpty(body) ? (currentGroupId ?? "") : body, @"\s+", "");
        if (string.IsNullOrEmpty(gid))
        {
            await reply.TextAsync("格式（私聊）：存主人<群ID>", ct);
            return;
        }
        var owners = _store.LoadOwners();
        if (owners.TryGetValue(gid, out var old) && !string.IsNullOrEmpty(old))
        {
            if (old == sender)
                await reply.MdAsync($"该群已绑定店主（就是你）\n群ID：`{gid}`", ct: ct);
            else
                await reply.MdAsync($"该群已有店主，无法覆盖\n群ID：`{gid}`\n如需更换请最高权限删主人后再存", ct: ct);
            return;
        }
        owners[gid] = sender;
        _store.SaveOwners(owners);
        var shop = _store.GetOrCreateShop(gid);
        shop.Owner = sender;
        _store.SaveShop(gid, shop);
        await reply.MdAsync(
            MdFmt.Join(
                "✅ 已绑定为本群店主",
                $"群ID：`{gid}`",
                "",
                "接下来私聊：" + MdFmt.Cmd($"设置易支付 {gid}#", "设置易支付"),
                "群内：" + MdFmt.Cmd("开启易支付", "开启易支付")),
            ct: ct);
    }

    private async Task CmdQueryOwnerAsync(Replier reply, string content, CancellationToken ct)
    {
        var body = content;
        foreach (var p in new[] { "查主人", "群主人" })
            if (body.StartsWith(p)) { body = body[p.Length..].Trim(); break; }
        var gid = Regex.Replace(body, @"\s+", "");
        if (string.IsNullOrEmpty(gid))
        {
            await reply.TextAsync("格式：查主人 <群ID>", ct);
            return;
        }
        var owner = _store.GetOwner(gid) ?? "（未绑定）";
        await reply.MdAsync($"群 `{gid}` 店主：`{owner}`", ct: ct);
    }

    private async Task CmdDeleteOwnerAsync(Replier reply, string content, CancellationToken ct)
    {
        var body = content;
        foreach (var p in new[] { "删除主人", "删主人" })
            if (body.StartsWith(p)) { body = body[p.Length..].Trim(); break; }
        var gid = Regex.Replace(body, @"\s+", "");
        if (string.IsNullOrEmpty(gid))
        {
            await reply.TextAsync("格式：删主人 <群ID>", ct);
            return;
        }
        var owners = _store.LoadOwners();
        owners.Remove(gid);
        _store.SaveOwners(owners);
        var shop = _store.GetOrCreateShop(gid, create: false);
        shop.Owner = "";
        _store.SaveShop(gid, shop);
        await reply.MdAsync($"✅ 已清除群 `{gid}` 店主，可重新存主人", ct: ct);
    }

    private async Task CmdCheckUpdateAsync(Replier reply, CancellationToken ct)
    {
        await reply.TextAsync("正在检查 GitHub 更新…", ct);
        var r = await PluginSelfUpdater.CheckAndUpdateAsync(
            _store.Global,
            downloadIfNewer: true,
            localVersion: PluginSelfUpdater.LocalVersion,
            log: m => _api.LogInfo("[易支付商店] " + m),
            ct: ct);

        var lines = new List<string>
        {
            MdFmt.Title("🔄", "插件更新"),
            MdFmt.Hr(),
            $"本地版本：`{PluginSelfUpdater.LocalVersion}`",
        };
        if (!string.IsNullOrEmpty(r.RemoteTag))
            lines.Add($"远程版本：`{r.RemoteTag}`");
        lines.Add("");
        lines.Add(r.Message);
        if (r.Downloaded)
        {
            lines.Add("");
            lines.Add("替换路径：`" + PluginSelfUpdater.GetInstalledDllPath() + "`");
        }

        await reply.MdAsync(MdFmt.Join(lines.ToArray()), ct: ct);
    }

    // ── 文案 / 权限 / 工具 ──────────────────────────────────

    private static string HelpText(string prefix, string shopGid)
    {
        var p = prefix ?? "";
        var shopTip = string.IsNullOrEmpty(shopGid)
            ? "各群商店互相独立、互不关联"
            : $"本群商店：`{shopGid}`";
        return MdFmt.Join(
            MdFmt.Title("🛒", "虚拟商品小店"),
            MdFmt.Hr(),
            shopTip,
            $"- {p}群信息 - 查看本群ID（开店用）",
            $"- {p}商品 - 查看本群商品",
            $"- {p}购买&lt;ID&gt; - 下单，如：购买1001",
            $"- {p}我的订单 / {p}查单 / {p}我的统计",
            MdFmt.Hr(),
            "**群店主**",
            $"- {p}开启易支付 / {p}关闭易支付（默认关）",
            $"- {p}添加商品 名称|价格|描述",
            $"- {p}删除商品 / 补单 / 库存 / 统计",
            $"- {p}检查更新 / {p}更新插件（下载后重启 GBot）",
            MdFmt.Hr(),
            "**私聊（须带群ID）**",
            "- 存主人群ID",
            "- 设置易支付 群ID#网关#pid#密钥",
            "- 上架卡密 &lt;群ID&gt; &lt;商品ID&gt;（换行贴卡密）",
            "- 或发 TXT：群ID#商品ID.txt",
            "- 店铺配置 &lt;群ID&gt;",
            "- 检查更新（最高权限）");
    }

    private string ListProductsText(string shopGid)
    {
        var shop = _store.GetOrCreateShop(shopGid);
        var items = shop.Products.Values.Where(p => p.Enabled)
            .OrderBy(p => int.TryParse(p.Id, out var n) ? n : 0).ToList();
        if (items.Count == 0)
            return "本群暂无在售商品，请店主先添加商品并上架卡密";
        var lines = new List<string> { MdFmt.Title("🛍️", "本群在售商品"), MdFmt.Hr() };
        foreach (var p in items)
        {
            var stockTip = p.Stock.Count > 0 ? $"库存{p.Stock.Count}" : "缺货";
            lines.Add($"[`{p.Id}`] **{p.Name}**  ¥{p.Price:0.00}  ({stockTip})");
            if (!string.IsNullOrEmpty(p.Desc))
                lines.Add($"    {p.Desc}");
        }
        lines.Add(MdFmt.Hr());
        lines.Add("发送：购买商品ID   例如：购买1001");
        return string.Join("\n", lines);
    }

    private string StockText(string shopGid)
    {
        var shop = _store.GetOrCreateShop(shopGid);
        if (shop.Products.Count == 0) return "暂无商品";
        var lines = new List<string> { MdFmt.Title("📦", "库存"), MdFmt.Hr() };
        foreach (var p in shop.Products.Values.OrderBy(x => int.TryParse(x.Id, out var n) ? n : 0))
            lines.Add($"[`{p.Id}`] {p.Name} — 库存 {p.Stock.Count} / 已售 {p.Sold}");
        return string.Join("\n", lines);
    }

    private string MyOrdersText(string senderId, string shopGid)
    {
        var mine = new List<OrderInfo>();
        if (!string.IsNullOrEmpty(shopGid))
        {
            var shop = _store.GetOrCreateShop(shopGid);
            mine.AddRange(shop.Orders.Values.Where(o => o.UserId == senderId));
        }
        mine = mine.OrderByDescending(o => o.CreatedAt).Take(8).ToList();
        if (mine.Count == 0) return "暂无订单，在群内发「商品」看看吧";
        var lines = new List<string> { MdFmt.Title("📋", "我的订单（本群最近8笔）") };
        foreach (var o in mine)
        {
            var st = o.Status switch
            {
                OrderStatus.Pending => "待支付",
                OrderStatus.Paid => "已支付",
                OrderStatus.Delivered => "已发货",
                OrderStatus.NoStock => "缺货待发",
                OrderStatus.Closed when o.CloseReason == "timeout" => "已超时",
                OrderStatus.Closed => "已关闭",
                _ => o.Status,
            };
            lines.Add($"· `{o.OutTradeNo}` | {o.ProductName} | ¥{o.Money} | {st}");
        }
        return string.Join("\n", lines);
    }

    private OrderInfo? LatestUserPending(string senderId, string? shopGid)
    {
        if (string.IsNullOrEmpty(shopGid)) return null;
        var shop = _store.GetOrCreateShop(shopGid);
        return shop.Orders.Values
            .Where(o => o.UserId == senderId && o.Status == OrderStatus.Pending)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefault();
    }

    private (string Gid, string OutTradeNo, OrderInfo Order)? ResolveLocalOrder(string queryNo, string? preferGid)
    {
        if (!string.IsNullOrEmpty(preferGid))
        {
            var shop = _store.GetOrCreateShop(preferGid, create: false);
            if (shop.Orders.TryGetValue(queryNo, out var o))
                return (preferGid, queryNo, o);
            foreach (var kv in shop.Orders)
            {
                if (string.Equals(kv.Value.OutTradeNo, queryNo, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Value.TradeNo, queryNo, StringComparison.OrdinalIgnoreCase))
                    return (preferGid, kv.Key, kv.Value);
            }
        }
        var any = _store.FindOrderAnywhere(queryNo);
        if (any is null) return null;
        return (any.Value.Gid, any.Value.Order.OutTradeNo is { Length: > 0 } n ? n : queryNo, any.Value.Order);
    }

    private async Task<(string Gid, string RestArgs)> RequirePrivateShopAsync(
        Replier reply, string sender, string cmdName, string content, CancellationToken ct)
    {
        var body = content.StartsWith(cmdName) ? content[cmdName.Length..].Trim() : content.Trim();
        var (gid, rest) = ExtractGidRest(body);
        if (string.IsNullOrEmpty(gid))
        {
            await reply.MdAsync($"格式（私聊）：{cmdName} &lt;群ID&gt; ...", ct: ct);
            return ("", "");
        }
        if (!IsMaster(sender, gid))
        {
            await reply.TextAsync("❌ 你不是该群店主", ct);
            return ("", "");
        }
        return (gid, rest);
    }

    private static (string Gid, string RestArgs) ExtractGidRest(string body)
    {
        body = (body ?? "").Trim();
        if (string.IsNullOrEmpty(body)) return ("", "");
        var m = Regex.Match(body, @"^(\S+)(?:\s+|$)(.*)$", RegexOptions.Singleline);
        return !m.Success ? ("", "") : (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
    }

    private void PatchCfg(string gid, Action<ShopConfig> patch)
    {
        var shop = _store.GetOrCreateShop(gid);
        patch(shop.Config);
        _store.SaveShop(gid, shop);
    }

    private bool IsGlobalMaster(string openId)
    {
        var raw = _store.Global.MasterId ?? "";
        return raw.Split(',', ';', ' ', '\n', '\t')
            .Select(s => s.Trim())
            .Any(s => !string.IsNullOrEmpty(s) && s == openId);
    }

    private bool IsMaster(string openId, string gid)
    {
        if (IsGlobalMaster(openId)) return true;
        var owner = _store.GetOwner(gid);
        if (!string.IsNullOrEmpty(owner) && owner == openId) return true;
        var shop = _store.GetOrCreateShop(gid, create: false);
        return !string.IsNullOrEmpty(shop.Owner) && shop.Owner == openId;
    }

    private async Task NotifyMastersAsync(string text, string? groupId, CancellationToken ct)
    {
        if (!_store.Global.NotifyAdmin) return;
        var robot = _store.Global.RobotId;
        if (string.IsNullOrEmpty(robot)) return;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in (_store.Global.MasterId ?? "").Split(',', ';', ' ', '\n', '\t'))
            if (!string.IsNullOrWhiteSpace(id)) set.Add(id.Trim());
        if (!string.IsNullOrEmpty(groupId))
        {
            var o = _store.GetOwner(groupId);
            if (!string.IsNullOrEmpty(o)) set.Add(o);
        }
        foreach (var mid in set)
        {
            try { await _api.SendPrivateMessageAsync(robot, mid, text, ct: ct); }
            catch (Exception ex) { _api.LogWarning($"[易支付商店] 通知失败 {mid}: {ex.Message}"); }
        }
    }

    private static readonly Regex AtTagRegex = new(@"<@!?[A-Za-z0-9]+>", RegexOptions.Compiled);

    private static bool LooksLikeStockCommand(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // 保留换行检测首行（私聊已默认 keepNewlines，此处主要给群聊兜底）
        var t = AtTagRegex.Replace(s.Replace('\u00a0', ' '), " ");
        t = t.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
        return FirstLine(t).TrimStart().StartsWith("上架卡密", StringComparison.Ordinal);
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i].Trim();
    }

    private static string Normalize(string? s, bool keepNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var cleaned = AtTagRegex.Replace(s.Replace('\u00a0', ' '), " ");
        if (!keepNewlines)
            return Regex.Replace(cleaned, @"\s+", " ").Trim();

        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = cleaned.Split('\n')
            .Select(l => Regex.Replace(l, @"[^\S\n]+", " ").TrimEnd())
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static string Trunc(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    /// <summary>从消息上下文取纯文字昵称（非 QQ 号）。</summary>
    private static string PickSenderDisplayName(EventContext ctx)
    {
        foreach (var candidate in new[] { ctx.SenderDisplay, ctx.Nickname, ctx.SenderName })
        {
            var name = NormalizeUserName(candidate);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "";
    }

    private static string NormalizeUserName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = AtTagRegex.Replace(raw.Trim(), "");
        s = Regex.Replace(s, @"[\r\n\t]+", " ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return Trunc(s, 32);
    }

    private static string FormatRankUser(string userId, string? displayName)
    {
        var name = NormalizeUserName(displayName);
        if (!string.IsNullOrEmpty(name))
        {
            // 纯文字展示，弱化 markdown 特殊字符
            return name
                .Replace("`", "'", StringComparison.Ordinal)
                .Replace("*", "＊", StringComparison.Ordinal)
                .Replace("_", "＿", StringComparison.Ordinal)
                .Replace("[", "［", StringComparison.Ordinal)
                .Replace("]", "］", StringComparison.Ordinal);
        }

        var uid = userId ?? "";
        var shortId = uid.Length <= 10 ? uid : uid[..6] + "…" + uid[^4..];
        return $"`{shortId}`";
    }
}

/// <summary>被动回复封装；Handled=true 表示已消费消息。</summary>
internal sealed class Replier
{
    public EventContext Ctx { get; }
    public bool Handled { get; set; } = true;
    private readonly IPluginApi _api;
    private readonly EpayShopStore _store;

    public Replier(EventContext ctx, IPluginApi api, EpayShopStore store)
    {
        Ctx = ctx;
        _api = api;
        _store = store;
    }

    public Task TextAsync(string text, CancellationToken ct) => Ctx.ReplyAsync(text, ct);

    public Task MdAsync(string md, object? keyboard = null, CancellationToken ct = default)
        => ApiCompat.ReplyMarkdownAsync(Ctx, md, keyboard, ct);
}
