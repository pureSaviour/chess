using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Effects;

/// <summary>
/// Maps semantic cues to Godot visuals. Pattern detectors never reference this class; alternate
/// themes can replace this player while consuming the same cue IDs.
/// </summary>
public partial class CueEffectLayer : Control
{
    private readonly ColorRect _flash = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _title = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MouseFilter = MouseFilterEnum.Ignore
    };
    private readonly EffectBurstView _burst = new();
    private Tween? _tween;
    private readonly Dictionary<ThemeEffectSlot, ShaderMaterial> _themeMaterials = [];
    private readonly Dictionary<ThemeEffectSlot, int> _themeDurations = [];
    private readonly List<ThemeResourceLease> _themeLeases = [];

    public CueEffectLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _flash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _flash.Modulate = new Color(1, 1, 1, 0);
        AddChild(_flash);
        AddChild(_burst);

        _title.SetAnchorsPreset(LayoutPreset.Center);
        _title.OffsetLeft = -300;
        _title.OffsetRight = 300;
        _title.OffsetTop = -80;
        _title.OffsetBottom = 80;
        _title.AddThemeFontSizeOverride("font_size", 64);
        _title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.92f));
        _title.AddThemeConstantOverride("shadow_offset_x", 5);
        _title.AddThemeConstantOverride("shadow_offset_y", 6);
        _title.Modulate = new Color(1, 1, 1, 0);
        AddChild(_title);
    }

    public bool EnabledBySettings { get; set; } = true;

    /// <summary>Preloads only known cue slots; failures stay local and retain built-in effects.</summary>
    public void ApplyTheme(IBoardTheme theme)
    {
        _flash.Material = null;
        foreach (var material in _themeMaterials.Values) material.Dispose();
        foreach (var lease in _themeLeases) lease.Dispose();
        _themeMaterials.Clear(); _themeDurations.Clear(); _themeLeases.Clear();
        foreach (var slot in new[] { ThemeEffectSlot.Check,
                     ThemeEffectSlot.Victory, ThemeEffectSlot.Defeat })
        {
            if (!theme.Effects.TryGetValue(slot, out var effect)) continue;
            if (effect.DurationMs > 0) _themeDurations[slot] = effect.DurationMs;
            if (effect.Shader is null) continue;
            try
            {
                var lease = new ThemeResourceLease();
                _themeMaterials[slot] = lease.CreateMaterial(effect.Shader);
                _themeLeases.Add(lease);
            }
            catch (Exception error) { GD.PushWarning($"主题 {theme.Id} 的 {slot} 终局 shader 加载失败：{error.Message}"); }
        }
    }

    public void Play(PresentationCue cue)
    {
        if (!EnabledBySettings && cue.Kind != PresentationCueKind.IllegalMove) return;
        var style = ResolveStyle(cue);
        if (style.Text is null) return;

        var slot = cue.Kind switch
        {
            PresentationCueKind.Check or PresentationCueKind.Checkmate => ThemeEffectSlot.Check,
            _ => (ThemeEffectSlot?)null
        };
        Animate(style, slot);
    }

    public void PlaySettlement(string text, bool? victory, GameEndReason reason)
    {
        if (!EnabledBySettings) return;
        var color = victory switch
        {
            true => new Color("#FFE6A1"),
            false => new Color("#D2C7BA"),
            null => new Color("#E8D8AC")
        };
        var flash = victory switch
        {
            true => new Color("#9A3C25"),
            false => new Color("#252A31"),
            null => new Color("#4E493C")
        };
        var suffix = reason == GameEndReason.Timeout
            ? victory == true ? "\n对方超时" : "\n己方超时"
            : string.Empty;
        Animate(new EffectStyle(text + suffix, color, flash, victory switch
        {
            true => 0.38f,
            false => 0.28f,
            null => 0.32f
        }, victory switch { true => 44, false => 20, null => 32 }, 2.2f),
            victory switch { true => ThemeEffectSlot.Victory, false => ThemeEffectSlot.Defeat, _ => null });
    }

    private void Animate(EffectStyle style, ThemeEffectSlot? slot)
    {
        _tween?.Kill();
        _flash.Material = slot is { } semantic && _themeMaterials.TryGetValue(semantic, out var material)
            ? material : null;
        if (_flash.Material is ShaderMaterial themed)
        {
            themed.SetShaderParameter("tint", style.Flash);
            themed.SetShaderParameter("animated", EnabledBySettings);
        }
        if (slot is { } durationSlot && _themeDurations.TryGetValue(durationSlot, out var milliseconds))
            style = style with { Duration = milliseconds / 1000f };
        _title.Text = style.Text;
        _title.AddThemeColorOverride("font_color", style.Color);
        _title.Scale = Vector2.One * 0.72f;
        _title.PivotOffset = _title.Size / 2f;
        _title.Modulate = new Color(1, 1, 1, 0);
        _flash.Color = style.Flash;
        _flash.Modulate = new Color(1, 1, 1, 0);
        _burst.Configure(style.Color, style.Rays);
        _burst.SetProgress(0);

        _tween = CreateTween();
        _tween.SetParallel(true);
        _tween.TweenProperty(_flash, "modulate:a", style.FlashAlpha, 0.10f);
        _tween.TweenProperty(_title, "modulate:a", 1.0f, 0.12f);
        _tween.TweenProperty(_title, "scale", Vector2.One, 0.24f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        _tween.TweenMethod(Callable.From<float>(_burst.SetProgress), 0.0f, 1.0f, style.Duration * 0.8f);
        _tween.Chain().TweenInterval(style.Duration * 0.58f);
        _tween.Chain().TweenProperty(_title, "modulate:a", 0.0f, 0.28f);
        _tween.TweenProperty(_flash, "modulate:a", 0.0f, 0.38f);
    }

    private static EffectStyle ResolveStyle(PresentationCue cue) => cue.Kind switch
    {
        PresentationCueKind.Capture => default,
        PresentationCueKind.Check => new("将军", new Color("#FFCF73"), new Color("#8D1F27"), 0.20f, 20, 1.05f),
        PresentationCueKind.Checkmate => new("绝杀", new Color("#FFE29A"), new Color("#A6262E"), 0.28f, 28, 1.35f),
        PresentationCueKind.Stalemate => new("困毙", new Color("#E1C591"), new Color("#3D2C25"), 0.18f, 12, 1.1f),
        PresentationCueKind.SpecialPattern => new(cue.Message ?? "妙手", new Color("#FFF0B2"),
            new Color("#8B261F"), 0.32f, 36, 1.7f),
        PresentationCueKind.Timeout => new("超时", new Color("#FFD18A"),
            new Color("#6E2525"), 0.24f, 22, 1.2f),
        PresentationCueKind.Draw => new("和棋", new Color("#E8D8AC"),
            new Color("#4E493C"), 0.22f, 24, 1.2f),
        PresentationCueKind.IllegalMove => new(cue.Message ?? "无效着法", new Color("#E4D9CA"),
            new Color("#3B3029"), 0.08f, 8, 0.65f),
        _ => default
    };

    private readonly record struct EffectStyle(
        string? Text, Color Color, Color Flash, float FlashAlpha, int Rays, float Duration);

    public override void _ExitTree()
    {
        _tween?.Kill(); _flash.Material = null;
        foreach (var material in _themeMaterials.Values) material.Dispose();
        foreach (var lease in _themeLeases) lease.Dispose();
        _themeMaterials.Clear(); _themeLeases.Clear();
    }
}
