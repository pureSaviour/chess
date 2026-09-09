using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Presentation-only route selected by a classic mode card option; it carries no room data.</summary>
public enum ClassicGalleryEntry { Local, Lan, Steam }

/// <summary>
/// Classic-xiangqi mode router. The landing page contains only match types; each type owns a
/// separate settings page so platform-specific controls never leak into unrelated modes.
/// </summary>
public partial class GalleryScreen : Control
{
    private readonly Action _back;
    private readonly Action<TimeControl> _startLocal;
    private readonly Action<int, NetworkMatchConfiguration> _host;
    private readonly Action<string, int> _join;
    private readonly Action<NetworkMatchConfiguration> _steamInvite;
    private readonly VBoxContainer _page = new();

    public GalleryScreen(
        Action back,
        Action<TimeControl> startLocal,
        Action<int, NetworkMatchConfiguration> host,
        Action<string, int> join,
        Action<NetworkMatchConfiguration> steamInvite,
        ClassicGalleryEntry initialEntry)
    {
        _back = back;
        _startLocal = startLocal;
        _host = host;
        _join = join;
        _steamInvite = steamInvite;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34);
        AddChild(margin);
        _page.AddThemeConstantOverride("separation", 18);
        margin.AddChild(_page);
        switch (initialEntry)
        {
            case ClassicGalleryEntry.Local: ShowLocalSettings(); break;
            case ClassicGalleryEntry.Lan: ShowLanSettings(); break;
            case ClassicGalleryEntry.Steam: ShowSteamSettings(); break;
            default: throw new ArgumentOutOfRangeException(nameof(initialEntry));
        }
    }

    private void ShowLocalSettings()
    {
        ClearPage();
        AddHeader("本地对决", _back);
        var content = AddSettingsCard(600, 380);
        AddDescription(content, "两名玩家共用当前设备。可启用局时与步时。", "本地房间设置");
        var time = AddTimeControls(content);
        var start = UiStyle.CreateButton("开始本地对决", true);
        start.Pressed += () => _startLocal(time.Value);
        content.AddChild(start);
    }

    private void ShowLanSettings()
    {
        ClearPage();
        AddHeader("局域网联机", _back);
        var content = AddSettingsCard(720, 540);
        AddDescription(content, "房主配置房间并告知另一位玩家局域网 IPv4 地址；加入方只需填写地址与端口。", "ENet / UDP");
        var side = AddSideControl(content, "房主执红", "房主执黑", "随机猜先");
        var time = AddTimeControls(content);
        var form = new GridContainer { Columns = 2 };
        form.AddThemeConstantOverride("h_separation", 10);
        form.AddThemeConstantOverride("v_separation", 10);
        content.AddChild(form);
        var address = new LineEdit
        {
            Text = "127.0.0.1",
            PlaceholderText = "房主局域网 IP",
            CustomMinimumSize = new Vector2(390, 42)
        };
        var port = CreatePortControl();
        form.AddChild(address);
        form.AddChild(port);
        var hostButton = UiStyle.CreateButton("创建房间", true);
        hostButton.Pressed += () => _host((int)port.Value,
            new NetworkMatchConfiguration(time.Value, ResolveSide(side.Selected)));
        form.AddChild(hostButton);
        var joinButton = UiStyle.CreateButton("加入房间");
        joinButton.Pressed += () => _join(address.Text.Trim(), (int)port.Value);
        form.AddChild(joinButton);
        var note = new Label
        {
            Text = "Windows 防火墙可能在首次创建房间时询问 UDP 网络访问。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        UiStyle.SetMuted(note, 13);
        content.AddChild(note);
    }

    private void ShowSteamSettings()
    {
        ClearPage();
        AddHeader("邀请 Steam 好友", _back);
        var content = AddSettingsCard(620, 430);
        AddDescription(content, "创建仅好友可见的双人 Lobby，随后打开 Steam 好友邀请界面。", "Steam Lobby / P2P");
        var side = AddSideControl(content, "房主执红", "房主执黑", "随机猜先");
        var time = AddTimeControls(content);
        var start = UiStyle.CreateButton("创建房间并邀请好友", true);
        start.Pressed += () => _steamInvite(
            new NetworkMatchConfiguration(time.Value, ResolveSide(side.Selected)));
        content.AddChild(start);
    }

    private void AddHeader(string title, Action back)
    {
        var header = new HBoxContainer();
        var backButton = UiStyle.CreateButton("‹ 返回");
        backButton.CustomMinimumSize = new Vector2(120, 44);
        backButton.Pressed += back;
        header.AddChild(backButton);
        var heading = UiStyle.CreateTitle(title, 34);
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        header.AddChild(heading);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(120, 0) });
        _page.AddChild(header);
    }

    private VBoxContainer AddSettingsCard(float width, float height)
    {
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _page.AddChild(center);
        var panel = UiStyle.CreatePanel(UiStyle.PanelLight, 14);
        panel.CustomMinimumSize = new Vector2(width, height);
        center.AddChild(panel);
        return CreatePanelContent(panel, 28);
    }

    private static VBoxContainer CreatePanelContent(Container panel, int marginSize)
    {
        var margin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(key, marginSize);
        panel.AddChild(margin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 14);
        margin.AddChild(content);
        return content;
    }

    private static void AddDescription(VBoxContainer parent, string text, string badgeText)
    {
        var badge = new Label { Text = badgeText, HorizontalAlignment = HorizontalAlignment.Center };
        badge.AddThemeFontSizeOverride("font_size", 14);
        badge.AddThemeColorOverride("font_color", UiStyle.Gold);
        parent.AddChild(badge);
        var description = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        UiStyle.SetMuted(description, 15);
        parent.AddChild(description);
        parent.AddChild(new HSeparator());
    }

    private static OptionButton AddSideControl(
        VBoxContainer parent,
        string redText,
        string blackText,
        string randomText)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);
        row.AddChild(new Label { Text = "执子选择", CustomMinimumSize = new Vector2(120, 0) });
        var side = new OptionButton { CustomMinimumSize = new Vector2(220, 42) };
        side.AddItem(redText);
        side.AddItem(blackText);
        side.AddItem(randomText);
        row.AddChild(side);
        return side;
    }

    private static TimeControls AddTimeControls(VBoxContainer parent)
    {
        var row = new GridContainer { Columns = 4 };
        row.AddThemeConstantOverride("h_separation", 10);
        row.AddThemeConstantOverride("v_separation", 8);
        parent.AddChild(row);
        var enabled = new CheckButton { Text = "启用计时", ButtonPressed = true };
        row.AddChild(enabled);
        row.AddChild(new Control());
        row.AddChild(new Control());
        row.AddChild(new Control());
        row.AddChild(new Label { Text = "局时（分钟）" });
        var gameMinutes = new SpinBox { MinValue = 1, MaxValue = 180, Value = 20, Step = 1 };
        row.AddChild(gameMinutes);
        row.AddChild(new Label { Text = "步时（秒）" });
        var moveSeconds = new SpinBox { MinValue = 5, MaxValue = 300, Value = 60, Step = 5 };
        row.AddChild(moveSeconds);
        enabled.Toggled += value =>
        {
            gameMinutes.Editable = value;
            moveSeconds.Editable = value;
        };
        return new TimeControls(enabled, gameMinutes, moveSeconds);
    }

    private static SpinBox CreatePortControl() => new()
    {
        MinValue = 1024,
        MaxValue = 65535,
        Value = EnetMatchTransport.DefaultPort,
        Step = 1,
        AllowGreater = false,
        AllowLesser = false,
        CustomMinimumSize = new Vector2(200, 42)
    };

    private static PieceSide ResolveSide(int selected) => selected switch
    {
        1 => PieceSide.Black,
        2 => Random.Shared.Next(2) == 0 ? PieceSide.Red : PieceSide.Black,
        _ => PieceSide.Red
    };

    private void ClearPage()
    {
        foreach (var child in _page.GetChildren())
        {
            _page.RemoveChild(child);
            child.QueueFree();
        }
    }

    private sealed record TimeControls(CheckButton Enabled, SpinBox GameMinutes, SpinBox MoveSeconds)
    {
        public TimeControl Value => Enabled.ButtonPressed
            ? new TimeControl((int)GameMinutes.Value * 60, (int)MoveSeconds.Value)
            : TimeControl.Unlimited;
    }
}
