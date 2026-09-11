using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Presentation.Networking;

namespace Xiangqi.Presentation.UI;

/// <summary>
/// Shared two-player room chat. It intentionally has no recipient selector: in a two-seat room
/// the only recipient is the opponent. The panel owns its Godot controls and event subscription,
/// while the caller owns and disposes the injected channel after this node leaves the tree.
/// </summary>
public partial class TwoPlayerChatPanel : PanelContainer
{
    private readonly RawTwoPlayerRoomChat _channel;
    private readonly Func<int?> _localSeat;
    private readonly RichTextLabel _log = new();
    private readonly LineEdit _input = new();
    private readonly Button _send;

    public TwoPlayerChatPanel(RawTwoPlayerRoomChat channel, Func<int?> localSeat)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _localSeat = localSeat ?? throw new ArgumentNullException(nameof(localSeat));
        CustomMinimumSize = new Vector2(300, 0);
        SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UiStyle.PanelLight,
            BorderColor = new Color("#5C4836"),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10
        });

        var margin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(key, 12);
        AddChild(margin);
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 8); margin.AddChild(column);
        var title = UiStyle.CreateTitle("对局消息", 20); title.HorizontalAlignment = HorizontalAlignment.Center; column.AddChild(title);
        _log.SizeFlagsVertical = SizeFlags.ExpandFill; _log.CustomMinimumSize = new Vector2(0, 300);
        _log.BbcodeEnabled = true; _log.ScrollFollowing = true; _log.SelectionEnabled = true;
        _log.AddThemeFontSizeOverride("normal_font_size", 15); column.AddChild(_log);
        _input.PlaceholderText = "输入发给对方的消息（最多 120 字）"; _input.MaxLength = 120;
        _input.CustomMinimumSize = new Vector2(0, 40); _input.TextSubmitted += _ => Send(); column.AddChild(_input);
        _send = UiStyle.CreateButton("发送给对方", true); _send.CustomMinimumSize = new Vector2(0, 40); _send.Pressed += Send; column.AddChild(_send);
        _channel.Messages.MessageAdded += OnMessage;
        Append("系统", "连接建立后可直接给对方发送消息。", true);
    }

    private async void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _send.Disabled = true;
        var error = await _channel.SendToOpponentAsync(text);
        _send.Disabled = false;
        if (error is not null) { Append("系统", error, true); return; }
        _input.Clear(); _input.GrabFocus();
    }

    private void OnMessage(RoomChatEntry entry)
    {
        if (_localSeat() is not { } local || !RoomChatManager.IsVisibleTo(entry, local)) return;
        Append(entry.SenderSeat == local ? "我" : "对方", entry.Text);
    }

    private void Append(string sender, string text, bool system = false)
    {
        var color = system ? "#9A8170" : sender == "我" ? "#8E3B35" : "#34373D";
        _log.AppendText($"[color={color}][b]{sender}[/b][/color]  {Escape(text)}\n");
    }

    private static string Escape(string value) => value.Replace("[", "[lb]");

    public override void _ExitTree()
    {
        _channel.Messages.MessageAdded -= OnMessage;
    }
}
