using Godot;

namespace Xiangqi.Presentation.UI;

public static class UiStyle
{
    public static readonly Color Background = new("#15120F");
    public static readonly Color Panel = new("#28211B");
    public static readonly Color PanelLight = new("#342A21");
    public static readonly Color Gold = new("#D8AA62");
    public static readonly Color Text = new("#F0E7DA");
    public static readonly Color Muted = new("#B7A894");
    public static readonly Color Red = new("#C84449");

    public static ColorRect CreateBackground()
    {
        var node = new ColorRect { Color = Background, MouseFilter = Control.MouseFilterEnum.Ignore };
        node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return node;
    }

    public static PanelContainer CreatePanel(Color? color = null, int radius = 10)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = color ?? Panel,
            BorderColor = new Color("#5C4836"),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius
        });
        return panel;
    }

    public static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(190, 48) };
        button.AddThemeFontSizeOverride("font_size", 17);
        if (primary)
        {
            button.AddThemeColorOverride("font_color", new Color("#24170D"));
            button.AddThemeStyleboxOverride("normal", ButtonBox(Gold));
            button.AddThemeStyleboxOverride("hover", ButtonBox(new Color("#ECC57D")));
            button.AddThemeStyleboxOverride("pressed", ButtonBox(new Color("#BE8843")));
        }
        return button;
    }

    public static Label CreateTitle(string text, int size = 34)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Gold);
        return label;
    }

    public static MarginContainer CreatePageMargin(int horizontal = 36, int vertical = 28)
    {
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        return margin;
    }

    public static void SetMuted(Label label, int size = 15)
    {
        label.AddThemeColorOverride("font_color", Muted);
        label.AddThemeFontSizeOverride("font_size", size);
    }

    private static StyleBoxFlat ButtonBox(Color color) => new()
    {
        BgColor = color,
        CornerRadiusTopLeft = 7,
        CornerRadiusTopRight = 7,
        CornerRadiusBottomLeft = 7,
        CornerRadiusBottomRight = 7,
        ContentMarginLeft = 18,
        ContentMarginRight = 18,
        ContentMarginTop = 10,
        ContentMarginBottom = 10
    };
}

