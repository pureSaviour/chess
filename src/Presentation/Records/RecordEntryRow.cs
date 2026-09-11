using Godot;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Records;

/// <summary>Creates the classic-record row layout for every game mode; the owning screen disposes returned textures.</summary>
public static class RecordEntryRow
{
    public static Control CreateHeadings()
    {
        var headings = new HBoxContainer();
        headings.AddChild(new Label { Text = "日期与时间", CustomMinimumSize = new Vector2(235, 0) });
        headings.AddChild(new Label { Text = "对弈双方与结果", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        headings.AddChild(new Label { Text = "对局方式", CustomMinimumSize = new Vector2(150, 0) });
        return headings;
    }

    public static Control Create(MatchRecordPresentation presentation, Action open, IList<ImageTexture> textures)
    {
        var button = UiStyle.CreateButton(string.Empty);
        button.CustomMinimumSize = new Vector2(0, 104);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.Pressed += open;
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, 18);
        row.AddThemeConstantOverride("separation", 16);
        button.AddChild(row);
        var time = new Label
        {
            Text = presentation.PlayedAt.LocalDateTime.ToString("yyyy-MM-dd\nHH-mm"),
            CustomMinimumSize = new Vector2(200, 0), VerticalAlignment = VerticalAlignment.Center
        };
        time.AddThemeFontSizeOverride("font_size", 18); row.AddChild(time);
        row.AddChild(Player(presentation.Local, true, textures, 245));
        var result = new Label
        {
            Text = presentation.Result,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(62, 46)
        };
        result.AddThemeFontSizeOverride("font_size", 22);
        result.AddThemeColorOverride("font_color", presentation.Result == "胜" ? UiStyle.Gold : new Color("#D7C3A5"));
        row.AddChild(result);
        var opponents = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        opponents.AddThemeConstantOverride("separation", 10);
        foreach (var opponent in presentation.Opponents)
            opponents.AddChild(Player(opponent, false, textures, presentation.Opponents.Count == 1 ? 245 : 155));
        row.AddChild(opponents);
        var mode = new Label
        {
            Text = presentation.Mode, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(145, 0)
        };
        UiStyle.SetMuted(mode, 15); row.AddChild(mode);
        return button;
    }

    private static Control Player(RecordPlayer player, bool local, IList<ImageTexture> textures, float width)
    {
        var group = new HBoxContainer { CustomMinimumSize = new Vector2(width, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        group.AddThemeConstantOverride("separation", 10);
        var avatarRoot = new Control { CustomMinimumSize = new Vector2(58, 58), MouseFilter = Control.MouseFilterEnum.Ignore };
        var color = new ColorRect { Color = local ? new Color("#A9473E") : new Color("#48535F"), MouseFilter = Control.MouseFilterEnum.Ignore };
        color.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); avatarRoot.AddChild(color);
        var initial = new Label { Text = string.IsNullOrEmpty(player.Name) ? "棋" : player.Name[..1], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        initial.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); avatarRoot.AddChild(initial);
        if (player.AvatarRgba is { Length: > 0 })
        {
            using var image = Image.CreateFromData(player.AvatarWidth, player.AvatarHeight, false, Image.Format.Rgba8, player.AvatarRgba);
            var texture = ImageTexture.CreateFromImage(image); textures.Add(texture);
            var view = new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore };
            view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); avatarRoot.AddChild(view);
        }
        group.AddChild(avatarRoot);
        group.AddChild(new Label { Text = local ? $"{player.Name}（我）" : player.Name, VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ClipText = true, MouseFilter = Control.MouseFilterEnum.Ignore });
        return group;
    }
}
