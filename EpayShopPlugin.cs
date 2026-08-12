using Avalonia.Controls;
using GBot.PluginAbstractions;

namespace GBot.Plugins.EpayShop;

/// <summary>易支付虚拟商品：按群开店、下单查单发货；消息用 Markdown + qqbot-cmd-input 镶嵌指令。</summary>
public sealed class EpayShopPlugin : BotPluginBase
{
    private EpayShopStore? _store;
    private EpayShopEngine? _engine;
    private EpayNotifyServer? _server;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _updateCts;
    private EpayShopSettingsWindow? _settingsWindow;

    public override PluginInfo GetPluginInfo() => new()
    {
        Id = "epay_shop",
        Name = "易支付虚拟商品",
        Version = "1.0.7",
        Author = "GBot",
        Description = "按群隔离虚拟商品店；易支付发货；GitHub 自更新",
    };

    public override bool OnLoad()
    {
        _store = new EpayShopStore(GetDataDir(), GetConfigPath());
        _engine = new EpayShopEngine(_store, Api);
        return base.OnLoad();
    }

    public override bool OnEnable()
    {
        if (_store is null || _engine is null)
        {
            _store = new EpayShopStore(GetDataDir(), GetConfigPath());
            _engine = new EpayShopEngine(_store, Api);
        }
        _store.LoadGlobal();
        StartHttp();
        StartPoll();
        StartAutoUpdateCheck();
        return base.OnEnable();
    }

    public override bool OnDisable()
    {
        StopPoll();
        StopAutoUpdateCheck();
        _server?.Stop();
        _server = null;
        return base.OnDisable();
    }

    public override bool OnUnload()
    {
        StopPoll();
        StopAutoUpdateCheck();
        _server?.Stop();
        _server = null;
        return base.OnUnload();
    }

    public override bool OnSettings(object? parent = null)
    {
        if (_store is null) return false;
        try
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return true;
            }
            _store.LoadGlobal();
            _settingsWindow = new EpayShopSettingsWindow(_store, () =>
            {
                if (!IsEnabled) return;
                StartHttp();
            });
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            if (parent is Window owner)
                _ = _settingsWindow.ShowDialog(owner);
            else
                _settingsWindow.Show();
            return true;
        }
        catch (Exception ex)
        {
            Api.LogError($"[易支付商店] 打开设置失败: {ex.Message}");
            return false;
        }
    }

    public override int OnGroupMessage(EventContext context)
    {
        _ = HandleAsync(context);
        return 0;
    }

    public override int OnGroupAtMessage(EventContext context)
    {
        _ = HandleAsync(context);
        return 0;
    }

    public override int OnC2CMessage(EventContext context)
    {
        _ = HandleAsync(context);
        return 0;
    }

    public override int OnInteraction(EventContext context)
    {
        _ = HandleAsync(context);
        return 0;
    }

    private async Task HandleAsync(EventContext context)
    {
        if (_engine is null) return;
        try
        {
            await _engine.HandleMessageAsync(context);
        }
        catch (Exception ex)
        {
            Api.LogWarning($"[易支付商店] {ex.Message}");
        }
    }

    private void StartHttp()
    {
        if (_store is null || _engine is null) return;
        _server?.Stop();
        _server = new EpayNotifyServer(Api, (payload, ct) => _engine.HandleNotifyAsync(payload, ct));
        try
        {
            _server.Start(_store.Global.HttpPort);
        }
        catch (Exception ex)
        {
            Api.LogError($"[易支付商店] HTTP 启动失败: {ex.Message}");
        }
    }

    private void StartPoll()
    {
        StopPoll();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            // 启动后稍等再跑，避免和 Enable 抢资源
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_engine is not null)
                        await _engine.PollOnceAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Api.LogWarning($"[易支付商店] 轮询异常: {ex.Message}");
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
        Api.LogInfo("[易支付商店] 查单轮询已启动（每分钟）");
    }

    private void StopPoll()
    {
        try { _pollCts?.Cancel(); } catch { /* */ }
        _pollCts = null;
    }

    private void StartAutoUpdateCheck()
    {
        StopAutoUpdateCheck();
        PluginSelfUpdater.TryResumePendingApply(m => Api.LogInfo("[易支付商店] " + m));

        if (_store is null || !_store.Global.UpdateAutoCheck) return;
        _updateCts = new CancellationTokenSource();
        var ct = _updateCts.Token;
        var store = _store;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { return; }
            try
            {
                var r = await PluginSelfUpdater.CheckAndUpdateAsync(
                    store.Global,
                    downloadIfNewer: true,
                    localVersion: PluginSelfUpdater.LocalVersion,
                    log: m => Api.LogInfo("[易支付商店] " + m),
                    ct: ct);
                if (!r.Ok)
                    Api.LogWarning("[易支付商店] " + r.Message);
                else if (r.Downloaded)
                    Api.LogInfo("[易支付商店] " + r.Message);
                else
                    Api.LogInfo("[易支付商店] " + r.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Api.LogWarning($"[易支付商店] 自动更新异常: {ex.Message}");
            }
        }, ct);
    }

    private void StopAutoUpdateCheck()
    {
        try { _updateCts?.Cancel(); } catch { /* */ }
        _updateCts = null;
    }
}
