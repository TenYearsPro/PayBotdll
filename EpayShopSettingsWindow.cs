using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GBot.Plugins.EpayShop;

/// <summary>与框架 AccountEditDialog / Backend 设置窗视觉对齐。</summary>
internal sealed class EpayShopSettingsWindow : Window
{
    private readonly EpayShopStore _store;
    private readonly Action? _onSaved;
    private readonly bool _dark;

    private readonly TextBox _robotBox;
    private readonly TextBox _masterBox;
    private readonly TextBox _portBox;
    private readonly TextBox _notifyBaseBox;
    private readonly CheckBox _notifyAdminCheck;
    private readonly TextBlock _errorText;
    private readonly TextBlock _pathText;

    private static readonly Color LightCard = Color.Parse("#FDF7FE");
    private static readonly Color LightCard2 = Color.Parse("#FFF0F8");
    private static readonly Color LightBorder = Color.Parse("#F3C4DE");
    private static readonly Color LightPrimary = Color.Parse("#E84D9C");
    private static readonly Color LightDanger = Color.Parse("#FF5D72");
    private static readonly Color LightText = Color.Parse("#3B2434");
    private static readonly Color LightText2 = Color.Parse("#8D6E82");

    private static readonly Color DarkCard = Color.Parse("#201722");
    private static readonly Color DarkCard2 = Color.Parse("#241926");
    private static readonly Color DarkBorder = Color.Parse("#553648");
    private static readonly Color DarkPrimary = Color.Parse("#F472B6");
    private static readonly Color DarkDanger = Color.Parse("#FB7185");
    private static readonly Color DarkText = Color.Parse("#FFF1F8");
    private static readonly Color DarkText2 = Color.Parse("#D8B8CA");

    public EpayShopSettingsWindow(EpayShopStore store, Action? onSaved = null)
    {
        _store = store;
        _onSaved = onSaved;
        _dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        Width = 480;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;

        var cardBg = Solid(_dark ? DarkCard : LightCard);
        var card2 = Solid(_dark ? DarkCard2 : LightCard2);
        var border = Solid(_dark ? DarkBorder : LightBorder);
        var primary = Solid(_dark ? DarkPrimary : LightPrimary);
        var danger = Solid(_dark ? DarkDanger : LightDanger);
        var text = Solid(_dark ? DarkText : LightText);
        var text2 = Solid(_dark ? DarkText2 : LightText2);

        var g = store.Global;
        _robotBox = MakeTextBox(g.RobotId, "官方 AppID，如 102766364", card2, text, border);
        _masterBox = MakeTextBox(g.MasterId, "最高权限 openid，逗号分隔", card2, text, border);
        _portBox = MakeTextBox(g.HttpPort.ToString(), "8087", card2, text, border);
        _notifyBaseBox = MakeTextBox(
            g.NotifyBase,
            "公网基址 https://域名 或 http://ip:8087",
            card2, text, border);
        _notifyAdminCheck = new CheckBox
        {
            Content = "支付发货成功后私聊通知管理员/店主",
            IsChecked = g.NotifyAdmin,
            Foreground = text,
            Margin = new Thickness(0, 2, 0, 0),
        };

        _errorText = new TextBlock
        {
            Foreground = danger,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            FontSize = 12,
        };

        _pathText = new TextBlock
        {
            Text = store.ConfigPath,
            Foreground = text2,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };

        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "易支付虚拟商品",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                },
                FieldLabel("发送机器人 AppID", text2),
                _robotBox,
                FieldLabel("最高权限 openid", text2),
                _masterBox,
                FieldLabel("回调 HTTP 端口", text2),
                _portBox,
                FieldLabel("公网回调基址（notify_url = 基址/epay/notify）", text2),
                _notifyBaseBox,
                _notifyAdminCheck,
                new TextBlock
                {
                    Text = "外网访问需反代到本机端口；无管理员权限时仅监听 127.0.0.1，可 netsh http add urlacl。",
                    Foreground = text2,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.9,
                },
                _errorText,
                _pathText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children =
                    {
                        MakePrimaryButton("保存", primary, OnSave),
                        MakeSecondaryButton("取消", card2, text, () => Close()),
                    },
                },
            },
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = cardBg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 24 70 0 #2DE84D9C"),
            Padding = new Thickness(28),
            Margin = new Thickness(12),
            Child = new ScrollViewer
            {
                MaxHeight = 640,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = form,
            },
        };
    }

    private void OnSave()
    {
        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            _errorText.Text = "请输入有效端口（1-65535）";
            _errorText.IsVisible = true;
            return;
        }

        _store.Global.RobotId = _robotBox.Text?.Trim() ?? "";
        _store.Global.MasterId = _masterBox.Text?.Trim() ?? "";
        _store.Global.NotifyBase = (_notifyBaseBox.Text ?? "").Trim().TrimEnd('/');
        _store.Global.NotifyAdmin = _notifyAdminCheck.IsChecked == true;
        _store.Global.HttpPort = port;
        _store.SaveGlobal();
        _onSaved?.Invoke();
        Close();
    }

    private static TextBlock FieldLabel(string text, IBrush foreground) => new()
    {
        Text = text,
        Foreground = foreground,
        FontSize = 12,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static TextBox MakeTextBox(string text, string watermark, IBrush bg, IBrush fg, IBrush border) => new()
    {
        Text = text,
        Watermark = watermark,
        CornerRadius = new CornerRadius(12),
        MinHeight = 36,
        Background = bg,
        Foreground = fg,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8),
    };

    private static Button MakePrimaryButton(string text, IBrush primary, Action click)
    {
        var btn = new Button
        {
            Content = text,
            Width = 110,
            Padding = new Thickness(0, 10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(16),
            Background = primary,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    private static Button MakeSecondaryButton(string text, IBrush bg, IBrush fg, Action click)
    {
        var btn = new Button
        {
            Content = text,
            Width = 110,
            Padding = new Thickness(0, 10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(14),
            Background = bg,
            Foreground = fg,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    private static SolidColorBrush Solid(Color c) => new(c);
}
