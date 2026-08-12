using System.Net;
using System.Text;
using GBot.PluginAbstractions;

namespace GBot.Plugins.EpayShop;

/// <summary>易支付异步通知：成功必须回纯文本 success。</summary>
internal sealed class EpayNotifyServer
{
    private readonly IPluginApi _api;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> _handler;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public int Port { get; private set; }

    public EpayNotifyServer(
        IPluginApi api,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> handler)
    {
        _api = api;
        _handler = handler;
    }

    public void Start(int port)
    {
        Stop();
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _api.LogWarning($"[易支付商店] 无 URLACL，仅监听 127.0.0.1:{port}");
        }

        _cts = new CancellationTokenSource();
        _loop = LoopAsync(_cts.Token);
        _api.LogInfo($"[易支付商店] 回调 HTTP :{port} 路径 /epay/notify");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* */ }
        try { _listener?.Stop(); } catch { /* */ }
        try { _listener?.Close(); } catch { /* */ }
        _listener = null;
        if (_loop is not null)
        {
            try { _loop.Wait(1500); } catch { /* */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _api.LogWarning($"[易支付商店] Accept: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleOneAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleOneAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (!path.Equals("/epay/notify", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("/epay/notify/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(ctx, 404, "not found");
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeQuery(map, ctx.Request.Url?.Query);

            if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    if (body.TrimStart().StartsWith('{'))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            foreach (var p in doc.RootElement.EnumerateObject())
                                map[p.Name] = p.Value.ToString();
                        }
                        catch { /* ignore */ }
                    }
                    else
                        MergeQuery(map, body.StartsWith('?') ? body : "?" + body);
                }
            }

            var result = await _handler(map, CancellationToken.None);
            await WriteAsync(ctx, 200, result);
        }
        catch (Exception ex)
        {
            _api.LogError($"[易支付商店] 回调处理失败: {ex.Message}");
            try { await WriteAsync(ctx, 500, "fail"); } catch { /* */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* */ }
        }
    }

    private static void MergeQuery(Dictionary<string, string> map, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0)
            {
                map[Uri.UnescapeDataString(part)] = "";
                continue;
            }
            var k = Uri.UnescapeDataString(part[..i]);
            var v = Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            map[k] = v;
        }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int code, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }
}
