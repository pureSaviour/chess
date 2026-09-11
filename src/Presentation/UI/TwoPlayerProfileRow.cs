using System.Globalization;
using Godot;
using Xiangqi.Presentation.Profiles;

namespace Xiangqi.Presentation.UI;

/// <summary>
/// Reusable classic-style player strip for two-player rooms. It renders only match-scoped profile
/// pixels and caller-supplied status text; it does not resolve platform identity, seats or clocks.
/// The row owns its ImageTexture and releases it when replaced or when leaving the tree.
/// </summary>
public partial class TwoPlayerProfileRow : HBoxContainer
{
    private readonly TextureRect _avatar = new();
    private readonly Label _initial = new();
    private readonly Label _name = new();
    private readonly Label _identity = new();
    private readonly Label _trailing = new();
    private ImageTexture? _texture;

    public TwoPlayerProfileRow(bool local)
    {
        CustomMinimumSize = new Vector2(0, 50); AddThemeConstantOverride("separation", 10);
        var avatarRoot = new Control { CustomMinimumSize = new Vector2(46, 46) };
        var background = new ColorRect { Color = local ? new Color("#8E3B35") : new Color("#34373D") };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); avatarRoot.AddChild(background);
        _avatar.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _avatar.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        _avatar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); avatarRoot.AddChild(_avatar);
        _initial.HorizontalAlignment = HorizontalAlignment.Center; _initial.VerticalAlignment = VerticalAlignment.Center;
        _initial.AddThemeFontSizeOverride("font_size", 22); _initial.AddThemeColorOverride("font_color", Colors.White);
        _initial.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); avatarRoot.AddChild(_initial); AddChild(avatarRoot);

        var labels = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        labels.AddThemeConstantOverride("separation", 0); _name.AddThemeFontSizeOverride("font_size", 16);
        _name.AddThemeColorOverride("font_color", UiStyle.Text); labels.AddChild(_name);
        UiStyle.SetMuted(_identity, 12); labels.AddChild(_identity); AddChild(labels);
        _trailing.SizeFlagsHorizontal = SizeFlags.ExpandFill; _trailing.HorizontalAlignment = HorizontalAlignment.Right;
        _trailing.VerticalAlignment = VerticalAlignment.Center; _trailing.AddThemeFontSizeOverride("font_size", 17); AddChild(_trailing);
    }

    public void Apply(PlayerProfileSnapshot? profile, string fallbackName, string detail, string trailing)
    {
        ReleaseTexture();
        var name = profile?.DisplayName ?? fallbackName;
        _name.Text = name; _identity.Text = profile is null ? detail :
            $"{(profile.Platform == "Steam" ? "Steam 昵称" : profile.Platform)} · {detail}";
        _trailing.Text = trailing; _initial.Text = FirstTextElement(name); _initial.Visible = true;
        if (profile?.Avatar is null) return;
        using var image = Image.CreateFromData(profile.Avatar.Width, profile.Avatar.Height, false,
            Image.Format.Rgba8, profile.Avatar.CopyRgba());
        _texture = ImageTexture.CreateFromImage(image); _avatar.Texture = _texture; _initial.Visible = false;
    }

    public void SetTrailing(string text) => _trailing.Text = text;

    private static string FirstTextElement(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var enumerator = StringInfo.GetTextElementEnumerator(value.Trim());
        return enumerator.MoveNext() ? enumerator.GetTextElement() : "—";
    }

    private void ReleaseTexture()
    {
        _avatar.Texture = null; _texture?.Dispose(); _texture = null;
    }

    public override void _ExitTree() => ReleaseTexture();
}
