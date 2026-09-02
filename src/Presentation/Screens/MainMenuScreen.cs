using Godot;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class MainMenuScreen : Control
{
    public MainMenuScreen(Action play, Action settings, Action quit)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(410, 0) };
        column.AddThemeConstantOverride("separation", 15);
        center.AddChild(column);

        var emblem = new Label { Text = "楚河  ·  汉界", HorizontalAlignment = HorizontalAlignment.Center };
        UiStyle.SetMuted(emblem, 15);
        column.AddChild(emblem);
        var title = UiStyle.CreateTitle("中国象棋", 56);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);
        var subtitle = new Label
        {
            Text = "方寸棋枰，纵横千古",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 65)
        };
        UiStyle.SetMuted(subtitle, 17);
        column.AddChild(subtitle);

        var playButton = UiStyle.CreateButton("玩法选择", true);
        playButton.Pressed += play;
        column.AddChild(playButton);
        var settingsButton = UiStyle.CreateButton("设置");
        settingsButton.Pressed += settings;
        column.AddChild(settingsButton);
        var quitButton = UiStyle.CreateButton("退出游戏");
        quitButton.Pressed += quit;
        column.AddChild(quitButton);

        var version = new Label
        {
            Text = "C# · Godot 4.5",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 38),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        UiStyle.SetMuted(version, 12);
        column.AddChild(version);
    }
}

