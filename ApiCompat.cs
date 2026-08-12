using System.Collections.Concurrent;
using System.Reflection;
using GBot.PluginAbstractions;

namespace GBot.Plugins.EpayShop;

/// <summary>
/// GBot ≥0.1.25 起 Markdown API 增加了 bool 参数（官方插件普遍传 false）。
/// 旧签名调用会 MissingMethodException，这里按运行时实际重载适配。
/// </summary>
internal static class ApiCompat
{
    private static readonly ConcurrentDictionary<string, MethodInfo?> Cache = new(StringComparer.Ordinal);

    public static Task ReplyMarkdownAsync(
        EventContext ctx, string markdown, object? keyboard = null, CancellationToken ct = default)
        => InvokeMarkdown(
            key: "EventContext.ReplyMarkdownAsync",
            target: ctx,
            type: ctx.GetType(),
            name: nameof(EventContext.ReplyMarkdownAsync),
            markdown: markdown,
            keyboard: keyboard,
            robotId: null,
            targetId: null,
            msgId: null,
            ct: ct);

    public static Task SendGroupMarkdownAsync(
        IPluginApi api, string robotId, string groupId, string markdown,
        string? msgId = null, object? keyboard = null, CancellationToken ct = default)
        => InvokeMarkdown(
            key: "IPluginApi.SendGroupMarkdownAsync",
            target: api,
            type: api.GetType(),
            name: "SendGroupMarkdownAsync",
            markdown: markdown,
            keyboard: keyboard,
            robotId: robotId,
            targetId: groupId,
            msgId: msgId,
            ct: ct);

    public static Task SendPrivateMarkdownAsync(
        IPluginApi api, string robotId, string userId, string markdown,
        string? msgId = null, object? keyboard = null, CancellationToken ct = default)
        => InvokeMarkdown(
            key: "IPluginApi.SendPrivateMarkdownAsync",
            target: api,
            type: api.GetType(),
            name: "SendPrivateMarkdownAsync",
            markdown: markdown,
            keyboard: keyboard,
            robotId: robotId,
            targetId: userId,
            msgId: msgId,
            ct: ct);

    private static async Task InvokeMarkdown(
        string key,
        object target,
        Type type,
        string name,
        string markdown,
        object? keyboard,
        string? robotId,
        string? targetId,
        string? msgId,
        CancellationToken ct)
    {
        var mi = Cache.GetOrAdd(key + "@" + type.AssemblyQualifiedName, _ => Resolve(type, name, robotId is not null));
        if (mi is null)
            throw new MissingMethodException(type.FullName, name);

        object?[] args;
        var ps = mi.GetParameters();
        if (robotId is null)
        {
            // EventContext.ReplyMarkdownAsync(markdown, keyboard[, bool], ct)
            args = ps.Length >= 4 && ps[2].ParameterType == typeof(bool)
                ? [markdown, keyboard!, false, ct]
                : [markdown, keyboard!, ct];
        }
        else
        {
            // IPluginApi.Send*MarkdownAsync(robot, id, markdown, msgId, keyboard[, bool], ct)
            args = ps.Length >= 7 && ps[5].ParameterType == typeof(bool)
                ? [robotId, targetId!, markdown, msgId!, keyboard!, false, ct]
                : [robotId, targetId!, markdown, msgId!, keyboard!, ct];
        }

        var result = mi.Invoke(target, args);
        if (result is Task task)
            await task.ConfigureAwait(false);
    }

    private static MethodInfo? Resolve(Type type, string name, bool isSend)
    {
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == name)
            .ToList();

        // Prefer host 新签名（带 bool）
        if (isSend)
        {
            return methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 7 && p[5].ParameterType == typeof(bool);
            }) ?? methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 6 && p[^1].ParameterType == typeof(CancellationToken);
            });
        }

        return methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length >= 4 && p[0].ParameterType == typeof(string) && p[2].ParameterType == typeof(bool);
        }) ?? methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length >= 3 && p[0].ParameterType == typeof(string);
        });
    }
}
