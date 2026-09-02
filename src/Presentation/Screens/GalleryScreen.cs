using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class GalleryScreen : Control
{
    private readonly LineEdit _address = new() { Text = "127.0.0.1", PlaceholderText = "房主局域网 IP" };
    private readonly SpinBox _port = new()
    {
        MinValue = 1024,
        MaxValue = 65535,
        Value = EnetMatchTransport.DefaultPort,
        Step = 1,
        AllowGreater = false,
        AllowLesser = false
    };
    private readonly OptionButton _hostSide = new();
    private readonly CheckButton _timed = new() { Text = "启用计时", ButtonPressed = true };
    private readonly SpinBox _gameMinutes = new() { MinValue = 1, MaxValue = 180, Value = 20, Step = 1 };
    private readonly SpinBox _moveSeconds = new() { MinValue = 5, MaxValue = 300, Value = 60, Step = 5 };

    public GalleryScreen(
        Action back,
        Action startLocal,
        Action<int, NetworkMatchConfiguration> host,
        Action<string, int> join,
        Action<NetworkMatchConfiguration> steamInvite)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34);
        AddChild(margin);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 20);
        margin.AddChild(page);

        var header = new HBoxContainer();
        var backButton = UiStyle.CreateButton("‹ 返回");
        backButton.CustomMinimumSize = new Vector2(120, 44);
        backButton.Pressed += back;
        header.AddChild(backButton);
        var heading = UiStyle.CreateTitle("玩法 Gallery", 34);
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        header.AddChild(heading);
        var spacer = new Control { CustomMinimumSize = new Vector2(120, 0) };
        header.AddChild(spacer);
        page.AddChild(header);

        var intro = new Label
        {
            Text = "选择棋规与对局方式。新的变体玩法将在这里以独立卡片加入。",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        UiStyle.SetMuted(intro);
        page.AddChild(intro);

        var galleryCenter = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddChild(galleryCenter);
        var card = UiStyle.CreatePanel(UiStyle.PanelLight, 14);
        card.CustomMinimumSize = new Vector2(720, 510);
        galleryCenter.AddChild(card);
        var cardMargin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            cardMargin.AddThemeConstantOverride(key, 30);
        card.AddChild(cardMargin);
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 14);
        cardMargin.AddChild(content);

        var badge = new Label { Text = "标准玩法", HorizontalAlignment = HorizontalAlignment.Center };
        badge.AddThemeFontSizeOverride("font_size", 14);
        badge.AddThemeColorOverride("font_color", UiStyle.Gold);
        content.AddChild(badge);
        var name = UiStyle.CreateTitle("标准中国象棋", 31);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(name);
        var description = new Label
        {
            Text = "经典九路十行棋盘。包含将军、将死、困毙和完整基础走子规则。",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        UiStyle.SetMuted(description, 15);
        content.AddChild(description);
        content.AddChild(new HSeparator());

        var localButton = UiStyle.CreateButton("本地双人", true);
        localButton.Pressed += startLocal;
        content.AddChild(localButton);

        var networkLabel = new Label { Text = "局域网对弈（ENet / UDP）" };
        networkLabel.AddThemeColorOverride("font_color", UiStyle.Text);
        networkLabel.AddThemeFontSizeOverride("font_size", 16);
        content.AddChild(networkLabel);
        var roomSettings = new GridContainer { Columns = 4 };
        roomSettings.AddThemeConstantOverride("h_separation", 10);
        roomSettings.AddThemeConstantOverride("v_separation", 8);
        content.AddChild(roomSettings);
        roomSettings.AddChild(new Label { Text = "房主先后手" });
        _hostSide.AddItem("房主执红", 0);
        _hostSide.AddItem("房主执黑", 1);
        _hostSide.AddItem("随机猜先", 2);
        _hostSide.CustomMinimumSize = new Vector2(155, 38);
        roomSettings.AddChild(_hostSide);
        roomSettings.AddChild(_timed);
        roomSettings.AddChild(new Control());
        roomSettings.AddChild(new Label { Text = "局时（分钟）" });
        roomSettings.AddChild(_gameMinutes);
        roomSettings.AddChild(new Label { Text = "步时（秒）" });
        roomSettings.AddChild(_moveSeconds);
        _timed.Toggled += enabled =>
        {
            _gameMinutes.Editable = enabled;
            _moveSeconds.Editable = enabled;
        };

        var form = new GridContainer { Columns = 2 };
        form.AddThemeConstantOverride("h_separation", 10);
        form.AddThemeConstantOverride("v_separation", 10);
        content.AddChild(form);
        _address.CustomMinimumSize = new Vector2(390, 42);
        _address.AddThemeFontSizeOverride("font_size", 15);
        form.AddChild(_address);
        _port.CustomMinimumSize = new Vector2(200, 42);
        form.AddChild(_port);
        var hostButton = UiStyle.CreateButton("创建房间（红方）");
        hostButton.Text = "创建房间";
        hostButton.Pressed += () => host((int)_port.Value, CreateHostConfiguration());
        form.AddChild(hostButton);
        var joinButton = UiStyle.CreateButton("加入房间（黑方）");
        joinButton.Pressed += () => join(_address.Text.Trim(), (int)_port.Value);
        form.AddChild(joinButton);

        var note = new Label
        {
            Text = "房主需将本机局域网 IPv4 地址告知另一位玩家；Windows 防火墙可能首次询问 UDP 网络访问。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        UiStyle.SetMuted(note, 13);
        content.AddChild(note);
        var steamButton = UiStyle.CreateButton("创建 Steam 房间并邀请好友");
        steamButton.Pressed += () => steamInvite(CreateHostConfiguration());
        content.AddChild(steamButton);
    }

    private NetworkMatchConfiguration CreateHostConfiguration()
    {
        var side = _hostSide.Selected switch
        {
            1 => PieceSide.Black,
            2 => Random.Shared.Next(2) == 0 ? PieceSide.Red : PieceSide.Black,
            _ => PieceSide.Red
        };
        var time = _timed.ButtonPressed
            ? new TimeControl((int)_gameMinutes.Value * 60, (int)_moveSeconds.Value)
            : TimeControl.Unlimited;
        return new NetworkMatchConfiguration(time, side);
    }
}
