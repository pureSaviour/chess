using Godot;
using Xiangqi.GameHost;
using Xiangqi.Presentation.GameModules;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Top-level mode gallery. It owns only presentation nodes and routes each foreground option to
/// an existing settings page. Generated images are decorative project assets: they ignore mouse
/// input and never encode room configuration or gameplay state.
/// </summary>
public partial class ModeGalleryScreen : Control
{
    internal int CardCount { get; private set; }
    internal int OptionCount { get; private set; }

    public ModeGalleryScreen(Action back, IReadOnlyList<GameModuleRegistration> modules,
        Action<GameModuleId, int> moveModule)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(moveModule);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(46, 28);
        AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 16);
        margin.AddChild(root);

        var heading = new HBoxContainer();
        root.AddChild(heading);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heading.AddChild(titles);
        titles.AddChild(UiStyle.CreateTitle("玩法画廊"));
        var subtitle = new Label { Text = "选择玩法与房间类型" };
        UiStyle.SetMuted(subtitle, 14);
        titles.AddChild(subtitle);
        var exit = UiStyle.CreateButton("返回");
        exit.CustomMinimumSize = new Vector2(112, 44);
        exit.Pressed += back;
        heading.AddChild(exit);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        root.AddChild(scroll);
        var modes = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(1970, 520)
        };
        modes.AddThemeConstantOverride("separation", 24);
        scroll.AddChild(modes);

        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];
            modes.AddChild(CreateModuleCard(module, index > 0, index + 1 < modules.Count, moveModule));
        }
    }

    private Control CreateModuleCard(GameModuleRegistration module, bool canMoveEarlier,
        bool canMoveLater, Action<GameModuleId, int> moveModule)
    {
        var manifest = module.Manifest;
        var panel = CreateCardShell(manifest.DisplayName, manifest.Summary, manifest.CardAssetPath,
            out var content, out var title);
        var order = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        var earlier = UiStyle.CreateButton("←");
        earlier.TooltipText = "向前移动玩法";
        earlier.Disabled = !canMoveEarlier;
        earlier.CustomMinimumSize = new Vector2(44, 36);
        earlier.Pressed += () => moveModule(manifest.Id, -1);
        var later = UiStyle.CreateButton("→");
        later.TooltipText = "向后移动玩法";
        later.Disabled = !canMoveLater;
        later.CustomMinimumSize = new Vector2(44, 36);
        later.Pressed += () => moveModule(manifest.Id, 1);
        order.AddChild(earlier);
        order.AddChild(later);
        title.AddChild(order);

        var collapse = new List<Action>();
        foreach (var group in module.Routes.GroupBy(route => route.Group))
        {
            var leaves = group.OrderBy(route => route.Order)
                .Select(route => (route.Label, (Action)(() => module.Activate(route.Id)))).ToArray();
            AddExpandable(content, group.Key, leaves, collapse);
        }
        OptionCount += module.Routes.Count;
        CardCount++;
        return panel;
    }

    private static void AddExpandable(VBoxContainer parent, string label,
        IReadOnlyList<(string Label, Action Action)> leaves, List<Action> collapseSiblings)
    {
        const float rowHeight = 38;
        const float separation = 6;
        var clip = new Control { ClipContents = true, CustomMinimumSize = Vector2.Zero };
        var body = new VBoxContainer { Modulate = new Color(1, 1, 1, 0) };
        body.SetAnchorsPreset(LayoutPreset.TopWide);
        body.AddThemeConstantOverride("separation", (int)separation);
        clip.AddChild(body);
        var header = CreateCardButton($"▸ {label}", false);
        parent.AddChild(header);
        foreach (var leaf in leaves)
        {
            var button = CreateLeafButton(leaf.Label); button.Pressed += leaf.Action; body.AddChild(button);
        }
        parent.AddChild(clip);
        var expanded = false;
        Tween? activeTween = null;
        void SetExpanded(bool value)
        {
            expanded = value;
            header.Text = $"{(expanded ? "▾" : "▸")} {label}";
            activeTween?.Kill();
            activeTween = header.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            var targetHeight = expanded ? leaves.Count * rowHeight + Math.Max(0, leaves.Count - 1) * separation : 0;
            activeTween.TweenProperty(clip, "custom_minimum_size", new Vector2(0, targetHeight), 0.24);
            activeTween.TweenProperty(body, "modulate", new Color(1, 1, 1, expanded ? 1 : 0), 0.18);
        }
        collapseSiblings.Add(() => SetExpanded(false));
        header.Pressed += () => { var next = !expanded; if (next) foreach (var close in collapseSiblings) close(); SetExpanded(next); };
    }

    private static Control CreateCardShell(string title, string subtitle, string texturePath,
        out VBoxContainer content, out HBoxContainer titleRow)
    {
        var panel = UiStyle.CreatePanel(new Color(0.06f, 0.045f, 0.035f, 1), 16);
        panel.ClipContents = true; panel.CustomMinimumSize = new Vector2(370, 520);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill; panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        panel.AddChild(new TextureRect { Texture = GD.Load<Texture2D>(texturePath), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore });
        panel.AddChild(new ColorRect { Color = new Color(0.025f, 0.018f, 0.014f, 0.48f), MouseFilter = MouseFilterEnum.Ignore });
        var inset = new MarginContainer(); inset.AddThemeConstantOverride("margin_left", 26); inset.AddThemeConstantOverride("margin_right", 26);
        inset.AddThemeConstantOverride("margin_top", 24); inset.AddThemeConstantOverride("margin_bottom", 24); panel.AddChild(inset);
        content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 10); inset.AddChild(content);
        titleRow = new HBoxContainer();
        content.AddChild(titleRow);
        var name = UiStyle.CreateTitle(title, 30); name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.82f)); titleRow.AddChild(name);
        var description = new Label { Text = subtitle }; description.AddThemeColorOverride("font_color", Colors.White); description.AddThemeFontSizeOverride("font_size", 15); content.AddChild(description);
        content.AddChild(new Control { CustomMinimumSize = new Vector2(0, 20) });
        return panel;
    }

    private static Button CreateLeafButton(string text)
    {
        var button = new Button
        {
            Text = $"  └  {text}", CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        button.CustomMinimumSize = new Vector2(292, 38);
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeColorOverride("font_color", new Color("#E8D7BB"));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeStyleboxOverride("normal", CardButtonBox(new Color(0.045f, 0.035f, 0.028f, 0.68f)));
        button.AddThemeStyleboxOverride("hover", CardButtonBox(new Color(0.20f, 0.15f, 0.11f, 0.92f)));
        button.AddThemeStyleboxOverride("pressed", CardButtonBox(new Color(0.30f, 0.22f, 0.15f, 0.96f)));
        return button;
    }

    private static Button CreateCardButton(string text, bool primary)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 48) };
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", primary ? new Color("#25170C") : Colors.White);
        button.AddThemeColorOverride("font_hover_color", primary ? new Color("#25170C") : Colors.White);
        button.AddThemeStyleboxOverride("normal", CardButtonBox(
            primary ? new Color(0.85f, 0.66f, 0.36f, 0.94f) : new Color(0.06f, 0.045f, 0.035f, 0.82f)));
        button.AddThemeStyleboxOverride("hover", CardButtonBox(
            primary ? new Color(0.95f, 0.78f, 0.48f, 0.98f) : new Color(0.18f, 0.14f, 0.11f, 0.94f)));
        button.AddThemeStyleboxOverride("pressed", CardButtonBox(
            primary ? new Color(0.72f, 0.48f, 0.24f, 0.98f) : new Color(0.28f, 0.21f, 0.15f, 0.96f)));
        return button;
    }

    private static StyleBoxFlat CardButtonBox(Color color) => new()
    {
        BgColor = color,
        BorderColor = new Color(0.87f, 0.68f, 0.39f, 0.48f),
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8
    };
}
