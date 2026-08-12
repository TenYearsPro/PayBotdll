using System.Net;
using System.Text;

namespace GBot.Plugins.EpayShop;

/// <summary>Markdown 排版 + QQ 官方镶嵌指令 qqbot-cmd-input。</summary>
internal static class MdFmt
{
    public static string Cmd(string text, string? show = null, bool reference = false)
    {
        var t = EscapeAttr(text);
        var s = EscapeAttr(string.IsNullOrWhiteSpace(show) ? text : show!);
        var r = reference ? "true" : "false";
        return $"<qqbot-cmd-input text=\"{t}\" show=\"{s}\" reference=\"{r}\" />";
    }

    public static string Line(params string[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static string Title(string emoji, string text) => $"# {emoji} {text}";

    public static string Hr() => "━━━━━━━━━━━━";

    public static string Join(params string?[] lines)
        => string.Join("\n", lines.Where(l => l is not null));

    public static string Nav(string prefix, params (string Show, string Text)[] items)
    {
        var p = prefix ?? "";
        return string.Join("  ", items.Select(i => Cmd(p + i.Text, i.Show)));
    }

    public static object Keyboard(params IEnumerable<object>[] rows)
    {
        return new Dictionary<string, object?>
        {
            ["content"] = new Dictionary<string, object?>
            {
                ["rows"] = rows.Select(r => new Dictionary<string, object?>
                {
                    ["buttons"] = r.ToList(),
                }).ToList(),
            },
        };
    }

    /// <param name="enter">true=点击直接发送指令（普通按钮）；false=只填入输入框（如卡密）</param>
    public static object InputButton(
        string label, string fill, int style = 1, string? id = null,
        IEnumerable<string>? onlyUsers = null, bool enter = true)
    {
        label = Trunc(label, 40);
        fill = Trunc(fill, 128);
        id ??= "b" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(fill)))[..10].ToLowerInvariant();
        var permission = onlyUsers is null
            ? new Dictionary<string, object?> { ["type"] = 2 }
            : new Dictionary<string, object?>
            {
                ["type"] = 0,
                ["specify_user_ids"] = onlyUsers.ToList(),
            };
        return new Dictionary<string, object?>
        {
            ["id"] = Trunc(id, 40),
            ["render_data"] = new Dictionary<string, object?>
            {
                ["label"] = label,
                ["visited_label"] = label,
                ["style"] = style is 0 or 1 ? style : 1,
            },
            ["action"] = new Dictionary<string, object?>
            {
                ["type"] = 2,
                ["permission"] = permission,
                ["data"] = fill,
                ["enter"] = enter,
                ["unsupport_tips"] = "当前客户端暂不支持",
            },
        };
    }

    /// <summary>把按钮按每行最多 maxPerRow 个拆成 Keyboard 行。</summary>
    public static object KeyboardRows(IEnumerable<object> buttons, int maxPerRow = 3, params IEnumerable<object>[] extraRows)
    {
        var list = buttons.ToList();
        var rows = new List<IEnumerable<object>>();
        for (var i = 0; i < list.Count; i += maxPerRow)
            rows.Add(list.Skip(i).Take(maxPerRow));
        foreach (var r in extraRows)
            rows.Add(r);
        return Keyboard(rows.ToArray());
    }

    private static string EscapeAttr(string s)
        => WebUtility.HtmlEncode(s ?? "").Replace("\"", "&quot;");

    private static string Trunc(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
