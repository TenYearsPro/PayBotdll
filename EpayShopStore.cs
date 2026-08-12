using System.Text.Json;
using System.Text.Json.Serialization;

namespace GBot.Plugins.EpayShop;

internal sealed class ShopDataCorruptException : Exception
{
    public string ShopGid { get; }
    public string Path { get; }

    public ShopDataCorruptException(string shopGid, string path, Exception? inner)
        : base($"店铺数据损坏，已拒绝读写以免清空：{path}", inner)
    {
        ShopGid = shopGid;
        Path = path;
    }
}

/// <summary>在单锁内完成库存扣减 + 订单标记发货，供并发回调/轮询/查单共用。</summary>
internal sealed class StockDeliverMutation
{
    public bool Ok { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Code { get; init; }
    public OrderInfo? Order { get; init; }
}

internal sealed class EpayShopStore
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _dataDir;
    private readonly string _shopsDir;
    private readonly string _ownersPath;
    private readonly string _globalPath;

    public GlobalConfig Global { get; private set; } = new();
    public string ConfigPath => _globalPath;

    public EpayShopStore(string dataDir, string globalConfigPath)
    {
        _dataDir = dataDir;
        _shopsDir = Path.Combine(dataDir, "shops");
        _ownersPath = Path.Combine(dataDir, "group_owners.json");
        _globalPath = globalConfigPath;
        Directory.CreateDirectory(_shopsDir);
        LoadGlobal();
    }

    public void LoadGlobal()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_globalPath))
                {
                    var json = File.ReadAllText(_globalPath);
                    Global = JsonSerializer.Deserialize<GlobalConfig>(json, JsonOpt) ?? new GlobalConfig();
                }
                else
                    Global = new GlobalConfig();
            }
            catch
            {
                Global = new GlobalConfig();
            }

            if (Global.HttpPort <= 0) Global.HttpPort = 8087;
        }
    }

    public void SaveGlobal()
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_globalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            AtomicWrite(_globalPath, JsonSerializer.Serialize(Global, JsonOpt));
        }
    }

    public Dictionary<string, string> LoadOwners()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_ownersPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(_ownersPath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpt)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void SaveOwners(Dictionary<string, string> owners)
    {
        lock (_gate)
        {
            AtomicWrite(_ownersPath, JsonSerializer.Serialize(owners, JsonOpt));
        }
    }

    public string? GetOwner(string gid)
    {
        var owners = LoadOwners();
        return owners.TryGetValue(gid, out var o) && !string.IsNullOrWhiteSpace(o) ? o : null;
    }

    public ShopData GetOrCreateShop(string gid, bool create = true)
    {
        lock (_gate)
        {
            return GetOrCreateShopUnlocked(gid, create);
        }
    }

    public void SaveShop(string gid, ShopData shop)
    {
        lock (_gate) SaveShopUnlocked(gid, shop);
    }

    /// <summary>仅追加库存（写 stock 文件，不重写整份订单）。</summary>
    public void AppendStock(string gid, string productId, IReadOnlyList<string> codes)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: true);
            if (!shop.Products.TryGetValue(productId, out var product))
                throw new InvalidOperationException("product missing");
            product.Stock.AddRange(codes);
            shop.Products[productId] = product;
            SaveStockUnlocked(gid, shop);
            SaveMetaUnlocked(gid, shop); // 商品表本身在 meta
        }
    }

    private void SaveShopUnlocked(string gid, ShopData shop)
    {
        NormalizeShop(shop);
        Directory.CreateDirectory(_shopsDir);
        SaveMetaUnlocked(gid, shop);
        SaveStockUnlocked(gid, shop);
        SaveOrdersUnlocked(gid, shop);
    }

    private void SaveMetaUnlocked(string gid, ShopData shop)
    {
        NormalizeShop(shop);
        var meta = CloneShopMeta(shop);
        AtomicWrite(ShopPath(gid), JsonSerializer.Serialize(meta, JsonOpt));
    }

    private void SaveStockUnlocked(string gid, ShopData shop)
    {
        NormalizeShop(shop);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in shop.Products)
            map[kv.Key] = kv.Value.Stock?.ToList() ?? [];
        AtomicWrite(StockPath(gid), JsonSerializer.Serialize(map, JsonOpt));
    }

    private void SaveOrdersUnlocked(string gid, ShopData shop)
    {
        NormalizeShop(shop);
        AtomicWrite(OrdersPath(gid), JsonSerializer.Serialize(shop.Orders, JsonOpt));
    }

    public IReadOnlyList<string> ListShopIds()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_shopsDir)) return [];
            return Directory.GetFiles(_shopsDir, "*.json")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n) && IsShopMetaFileName(n!))
                .Select(n => Path.GetFileNameWithoutExtension(n!)!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
    }

    private static bool IsShopMetaFileName(string fileName)
        => !fileName.EndsWith(".stock.json", StringComparison.OrdinalIgnoreCase)
           && !fileName.EndsWith(".orders.json", StringComparison.OrdinalIgnoreCase)
           && !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    public OrderInfo? GetOrder(string gid, string outTradeNo)
    {
        var shop = GetOrCreateShop(gid, create: false);
        return shop.Orders.TryGetValue(outTradeNo, out var o) ? o : null;
    }

    public (string Gid, OrderInfo Order)? FindOrderAnywhere(string outTradeNo)
    {
        foreach (var gid in ListShopIds())
        {
            ShopData shop;
            try { shop = GetOrCreateShop(gid, create: false); }
            catch (ShopDataCorruptException) { continue; }

            if (shop.Orders.TryGetValue(outTradeNo, out var o))
                return (gid, o);
            foreach (var kv in shop.Orders)
            {
                if (string.Equals(kv.Value.OutTradeNo, outTradeNo, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Value.TradeNo, outTradeNo, StringComparison.OrdinalIgnoreCase))
                    return (gid, kv.Value);
            }
        }
        return null;
    }

    public void SaveOrder(string gid, string outTradeNo, OrderInfo order)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: true);
            order.OutTradeNo = outTradeNo;
            shop.Orders[outTradeNo] = order;
            SaveOrdersUnlocked(gid, shop);
        }
    }

    public void AddPending(string gid, string outTradeNo)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: true);
            if (!shop.Pending.Contains(outTradeNo, StringComparer.OrdinalIgnoreCase))
                shop.Pending.Add(outTradeNo);
            SaveMetaUnlocked(gid, shop);
        }
    }

    public void RemovePending(string gid, string outTradeNo)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: true);
            shop.Pending.RemoveAll(x => string.Equals(x, outTradeNo, StringComparison.OrdinalIgnoreCase));
            SaveMetaUnlocked(gid, shop);
        }
    }

    /// <summary>
    /// 轮询结束合并 Pending：保留快照之后新增的单，只更新本轮处理过的项。
    /// </summary>
    public void MergePendingAfterPoll(
        string gid,
        IReadOnlyCollection<string> processedSnapshot,
        IReadOnlyCollection<string> stillWatching)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: false);
            if (shop.Orders.Count == 0 && shop.Pending.Count == 0 && processedSnapshot.Count == 0)
                return;

            var processed = new HashSet<string>(processedSnapshot, StringComparer.OrdinalIgnoreCase);
            var still = new HashSet<string>(stillWatching, StringComparer.OrdinalIgnoreCase);
            var merged = new List<string>();

            foreach (var id in shop.Pending)
            {
                if (!processed.Contains(id))
                    merged.Add(id);
                else if (still.Contains(id))
                    merged.Add(id);
            }

            foreach (var id in stillWatching)
            {
                if (!merged.Contains(id, StringComparer.OrdinalIgnoreCase))
                    merged.Add(id);
            }

            shop.Pending = merged;
            SaveMetaUnlocked(gid, shop);
        }
    }

    /// <summary>单锁内：校验订单状态 → 扣库存 → 标记已发货并移出 Pending。</summary>
    public StockDeliverMutation TryDeliverMutation(string gid, string outTradeNo, string tradeNo, string source)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: false);
            if (!shop.Orders.TryGetValue(outTradeNo, out var order))
                return new StockDeliverMutation { Ok = false, Status = "missing", Message = "order not found" };

            if (order.Status == OrderStatus.Delivered)
            {
                return new StockDeliverMutation
                {
                    Ok = true,
                    Status = OrderStatus.Delivered,
                    Message = "already delivered",
                    Code = order.DeliveredCode,
                    Order = CloneOrder(order),
                };
            }

            // 已支付成功过（缺货）再补发：不再重复计入统计
            var alreadyPaidSuccess = order.Status == OrderStatus.NoStock;

            if (order.Status == OrderStatus.Closed)
            {
                if (order.CloseReason == "timeout")
                {
                    order.Status = OrderStatus.Paid;
                    order.ReopenedFromTimeout = true;
                }
                else
                {
                    return new StockDeliverMutation
                    {
                        Ok = false,
                        Status = OrderStatus.Closed,
                        Message = "order closed",
                        Order = CloneOrder(order),
                    };
                }
            }

            if (!shop.Products.TryGetValue(order.ProductId, out var product))
            {
                order.Status = OrderStatus.NoStock;
                if (!string.IsNullOrEmpty(tradeNo)) order.TradeNo = tradeNo;
                shop.Orders[outTradeNo] = order;
                shop.Pending.RemoveAll(x => string.Equals(x, outTradeNo, StringComparison.OrdinalIgnoreCase));
                if (!alreadyPaidSuccess)
                    RecordUserPaidSuccessUnlocked(shop, order);
                SaveOrdersUnlocked(gid, shop);
                SaveMetaUnlocked(gid, shop);
                return new StockDeliverMutation
                {
                    Ok = false,
                    Status = OrderStatus.NoStock,
                    Message = "product missing",
                    Order = CloneOrder(order),
                };
            }

            if (product.Stock.Count == 0)
            {
                order.Status = OrderStatus.NoStock;
                if (!string.IsNullOrEmpty(tradeNo)) order.TradeNo = tradeNo;
                shop.Orders[outTradeNo] = order;
                shop.Products[product.Id] = product;
                shop.Pending.RemoveAll(x => string.Equals(x, outTradeNo, StringComparison.OrdinalIgnoreCase));
                if (!alreadyPaidSuccess)
                    RecordUserPaidSuccessUnlocked(shop, order);
                SaveOrdersUnlocked(gid, shop);
                SaveMetaUnlocked(gid, shop);
                return new StockDeliverMutation
                {
                    Ok = false,
                    Status = OrderStatus.NoStock,
                    Message = "no stock",
                    Order = CloneOrder(order),
                };
            }

            var code = product.Stock[0];
            product.Stock.RemoveAt(0);
            product.Sold++;
            shop.Products[product.Id] = product;

            order.Status = OrderStatus.Delivered;
            order.DeliveredCode = code;
            if (!string.IsNullOrEmpty(tradeNo)) order.TradeNo = tradeNo;
            order.DeliveredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            order.SourceDeliver = source;
            order.ShopGid = gid;
            if (string.IsNullOrEmpty(order.GroupId)) order.GroupId = gid;
            shop.Orders[outTradeNo] = order;
            shop.Pending.RemoveAll(x => string.Equals(x, outTradeNo, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPaidSuccess)
                RecordUserPaidSuccessUnlocked(shop, order);

            SaveStockUnlocked(gid, shop);
            SaveOrdersUnlocked(gid, shop);
            SaveMetaUnlocked(gid, shop); // sold + pending + user_stats

            return new StockDeliverMutation
            {
                Ok = true,
                Status = OrderStatus.Delivered,
                Message = "delivered",
                Code = code,
                Order = CloneOrder(order),
            };
        }
    }

    /// <summary>累计用户交易成功（笔数+金额）。仅在首次进入已支付态时调用。</summary>
    private static void RecordUserPaidSuccessUnlocked(ShopData shop, OrderInfo order)
    {
        var uid = order.UserId ?? "";
        if (string.IsNullOrWhiteSpace(uid)) return;
        if (!shop.UserStats.TryGetValue(uid, out var st) || st is null)
        {
            st = new UserTradeStats();
            shop.UserStats[uid] = st;
        }
        st.OrderCount++;
        if (TryParseMoney(order.Money, out var money))
            st.TotalAmount += money;
        st.LastAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrWhiteSpace(order.UserName))
            st.DisplayName = order.UserName.Trim();
    }

    /// <summary>刷新已有统计用户的展示昵称（不新建统计条目）。</summary>
    public void TouchUserDisplayName(string gid, string userId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(gid) || string.IsNullOrWhiteSpace(userId)) return;
        var name = displayName?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: false);
            if (!shop.UserStats.TryGetValue(userId, out var st) || st is null) return;
            if (string.Equals(st.DisplayName, name, StringComparison.Ordinal)) return;
            st.DisplayName = name;
            SaveMetaUnlocked(gid, shop);
        }
    }

    private static bool TryParseMoney(string? money, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(money)) return false;
        return decimal.TryParse(money.Trim(), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(money.Trim(), out value);
    }

    /// <summary>从历史已支付订单重建用户统计（老数据迁移 / 手动校正）。</summary>
    public void RebuildUserStats(string gid)
    {
        lock (_gate)
        {
            var shop = GetOrCreateShopUnlocked(gid, create: false);
            RebuildUserStatsUnlocked(shop);
            SaveMetaUnlocked(gid, shop);
        }
    }

    private static void RebuildUserStatsUnlocked(ShopData shop)
    {
        var map = new Dictionary<string, UserTradeStats>(StringComparer.Ordinal);
        var nickAt = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var order in shop.Orders.Values)
        {
            if (order.Status is not (OrderStatus.Delivered or OrderStatus.NoStock))
                continue;
            var uid = order.UserId ?? "";
            if (string.IsNullOrWhiteSpace(uid)) continue;
            if (!map.TryGetValue(uid, out var st))
            {
                st = new UserTradeStats();
                map[uid] = st;
            }
            st.OrderCount++;
            if (TryParseMoney(order.Money, out var money))
                st.TotalAmount += money;
            var at = Math.Max(order.DeliveredAt, order.CreatedAt);
            if (at > st.LastAt) st.LastAt = at;
            if (!string.IsNullOrWhiteSpace(order.UserName)
                && (!nickAt.TryGetValue(uid, out var prevNickAt) || at >= prevNickAt))
            {
                st.DisplayName = order.UserName.Trim();
                nickAt[uid] = at;
            }
        }
        shop.UserStats = map;
    }

    public (int OrderCount, decimal TotalAmount) GetUserStats(string gid, string userId)
    {
        var shop = GetOrCreateShop(gid, create: false);
        if (string.IsNullOrEmpty(userId) || !shop.UserStats.TryGetValue(userId, out var st) || st is null)
            return (0, 0);
        return (st.OrderCount, st.TotalAmount);
    }

    public (int OrderCount, decimal TotalAmount, int UserCount) GetShopStats(string gid)
    {
        var shop = GetOrCreateShop(gid, create: false);
        var count = 0;
        decimal amount = 0;
        var users = 0;
        foreach (var st in shop.UserStats.Values)
        {
            if (st is null || st.OrderCount <= 0) continue;
            users++;
            count += st.OrderCount;
            amount += st.TotalAmount;
        }
        return (count, amount, users);
    }

    public IReadOnlyList<(string UserId, UserTradeStats Stats)> GetTopUserStats(string gid, int top)
    {
        var shop = GetOrCreateShop(gid, create: false);
        return shop.UserStats
            .Where(kv => kv.Value is { OrderCount: > 0 })
            .OrderByDescending(kv => kv.Value.TotalAmount)
            .ThenByDescending(kv => kv.Value.OrderCount)
            .Take(Math.Max(1, top))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private ShopData GetOrCreateShopUnlocked(string gid, bool create = true)
    {
        var path = ShopPath(gid);
        if (File.Exists(path))
        {
            try
            {
                var shop = JsonSerializer.Deserialize<ShopData>(File.ReadAllText(path), JsonOpt);
                if (shop is null)
                    throw new InvalidOperationException("反序列化为 null");
                NormalizeShop(shop);

                // 分文件：库存 / 订单（存在则覆盖 meta 内旧字段，兼容未拆分的老数据）
                var hadStockFile = File.Exists(StockPath(gid));
                var hadOrdersFile = File.Exists(OrdersPath(gid));
                MergeStockFile(gid, shop);
                MergeOrdersFile(gid, shop);

                // 老店铺：有成功订单但尚无统计时，从订单重建一次
                var needStatsRebuild = shop.UserStats.Count == 0
                    && shop.Orders.Values.Any(o => o.Status is OrderStatus.Delivered or OrderStatus.NoStock);
                if (needStatsRebuild)
                    RebuildUserStatsUnlocked(shop);

                // 首次加载老单体：立刻拆成 meta + stock + orders，避免后续只写 meta 时丢掉内联库存
                if (!hadStockFile || !hadOrdersFile)
                {
                    SaveStockUnlocked(gid, shop);
                    SaveOrdersUnlocked(gid, shop);
                    SaveMetaUnlocked(gid, shop);
                }
                else if (needStatsRebuild)
                {
                    SaveMetaUnlocked(gid, shop);
                }

                return shop;
            }
            catch (ShopDataCorruptException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ShopDataCorruptException(gid, path, ex);
            }
        }

        if (!create) return new ShopData();
        var empty = new ShopData { Owner = GetOwner(gid) ?? "" };
        SaveShopUnlocked(gid, empty);
        return empty;
    }

    private void MergeStockFile(string gid, ShopData shop)
    {
        var path = StockPath(gid);
        if (!File.Exists(path)) return;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonOpt);
            if (map is null) return;
            foreach (var kv in map)
            {
                if (!shop.Products.TryGetValue(kv.Key, out var p))
                {
                    // 库存文件有、商品表无：保留库存待商品恢复，挂到占位不写入商品名
                    continue;
                }
                p.Stock = kv.Value ?? [];
                shop.Products[kv.Key] = p;
            }
        }
        catch (Exception ex)
        {
            throw new ShopDataCorruptException(gid, path, ex);
        }
    }

    private void MergeOrdersFile(string gid, ShopData shop)
    {
        var path = OrdersPath(gid);
        if (!File.Exists(path)) return;
        try
        {
            var orders = JsonSerializer.Deserialize<Dictionary<string, OrderInfo>>(File.ReadAllText(path), JsonOpt);
            if (orders is null) return;
            shop.Orders = new Dictionary<string, OrderInfo>(orders, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            throw new ShopDataCorruptException(gid, path, ex);
        }
    }

    /// <summary>meta 落盘：不含库存列表与订单字典，减轻主文件体积。</summary>
    private static ShopData CloneShopMeta(ShopData shop)
    {
        var products = new Dictionary<string, ProductInfo>(StringComparer.Ordinal);
        foreach (var kv in shop.Products)
        {
            var p = kv.Value;
            products[kv.Key] = new ProductInfo
            {
                Id = p.Id,
                Name = p.Name,
                Price = p.Price,
                Desc = p.Desc,
                Stock = [], // 库存在 .stock.json
                Sold = p.Sold,
                Enabled = p.Enabled,
            };
        }

        return new ShopData
        {
            Owner = shop.Owner,
            Products = products,
            Orders = new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase), // 订单在 .orders.json
            Pending = shop.Pending?.ToList() ?? [],
            ProductSeq = shop.ProductSeq,
            Config = shop.Config,
            UserStats = shop.UserStats is null
                ? new Dictionary<string, UserTradeStats>(StringComparer.Ordinal)
                : new Dictionary<string, UserTradeStats>(shop.UserStats, StringComparer.Ordinal),
        };
    }

    private string ShopPath(string gid) => Path.Combine(_shopsDir, $"{Sanitize(gid)}.json");
    private string StockPath(string gid) => Path.Combine(_shopsDir, $"{Sanitize(gid)}.stock.json");
    private string OrdersPath(string gid) => Path.Combine(_shopsDir, $"{Sanitize(gid)}.orders.json");

    private static string Sanitize(string gid)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            gid = gid.Replace(c, '_');
        return gid.Trim();
    }

    private static void NormalizeShop(ShopData shop)
    {
        shop.Config ??= new ShopConfig();
        shop.Products ??= new Dictionary<string, ProductInfo>(StringComparer.Ordinal);
        shop.Orders ??= new Dictionary<string, OrderInfo>(StringComparer.OrdinalIgnoreCase);
        shop.Pending ??= [];
        shop.UserStats ??= new Dictionary<string, UserTradeStats>(StringComparer.Ordinal);
        if (shop.ProductSeq < 1000) shop.ProductSeq = 1000;
        foreach (var p in shop.Products.Values)
            p.Stock ??= [];
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static OrderInfo CloneOrder(OrderInfo o) => new()
    {
        OutTradeNo = o.OutTradeNo,
        TradeNo = o.TradeNo,
        UserId = o.UserId,
        UserName = o.UserName,
        GroupId = o.GroupId,
        ShopGid = o.ShopGid,
        ProductId = o.ProductId,
        ProductName = o.ProductName,
        Money = o.Money,
        PayType = o.PayType,
        PayUrl = o.PayUrl,
        QrImg = o.QrImg,
        Status = o.Status,
        CreatedAt = o.CreatedAt,
        DeliveredCode = o.DeliveredCode,
        DeliveredAt = o.DeliveredAt,
        SourceDeliver = o.SourceDeliver,
        CloseReason = o.CloseReason,
        ClosedAt = o.ClosedAt,
        WatchUntil = o.WatchUntil,
        ReopenedFromTimeout = o.ReopenedFromTimeout,
    };
}
