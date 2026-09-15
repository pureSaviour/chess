using System.Globalization;
using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Presentation.Profiles;

namespace Xiangqi.Presentation.UI;

/// <summary>
/// Shared player strip for match screens. It renders caller-supplied identity and clock projections,
/// but never resolves seats, advances clocks or decides whose turn it is. Avatar pixels are copied
/// into a texture owned and released by this row.
/// </summary>
public partial class TwoPlayerProfileRow : HBoxContainer
{
    private readonly AvatarClockView _avatar = new();
    private readonly Label _name = new();
    private readonly Label _identity = new();
    private readonly Label _trailing = new();

    public TwoPlayerProfileRow(bool local)
    {
        CustomMinimumSize = new Vector2(0, 54);
        AddThemeConstantOverride("separation", 10);
        AddChild(_avatar);
        var labels = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        labels.AddThemeConstantOverride("separation", 0);
        _name.AddThemeFontSizeOverride("font_size", 16);
        _name.AddThemeColorOverride("font_color", UiStyle.Text);
        labels.AddChild(_name);
        UiStyle.SetMuted(_identity, 12);
        labels.AddChild(_identity);
        AddChild(labels);
        _trailing.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _trailing.HorizontalAlignment = HorizontalAlignment.Right;
        _trailing.VerticalAlignment = VerticalAlignment.Center;
        _trailing.AddThemeFontSizeOverride("font_size", 17);
        AddChild(_trailing);
    }

    public void Apply(PlayerProfileSnapshot? profile, string fallbackName, string detail, string trailing)
    {
        var name = profile?.DisplayName ?? fallbackName;
        _name.Text = name;
        _identity.Text = profile is null ? detail : $"{(profile.Platform == "Steam" ? "Steam 昵称" : profile.Platform)} · {detail}";
        _trailing.Text = trailing;
        _avatar.Apply(name, profile?.Avatar?.Width ?? 0, profile?.Avatar?.Height ?? 0, profile?.Avatar?.CopyRgba());
    }

    public void ApplyCommand(PlayerProfileCommand? profile, string fallbackName, string detail, string trailing)
    {
        var name = profile?.DisplayName ?? fallbackName;
        _name.Text = name;
        _identity.Text = profile is null ? detail : $"联机资料 · {detail}";
        _trailing.Text = trailing;
        _avatar.Apply(name, profile?.AvatarWidth ?? 0, profile?.AvatarHeight ?? 0, profile?.AvatarRgba.ToArray());
    }

    public void SetTrailing(string text) => _trailing.Text = text;
    public void SetMoveClock(MoveClockIndicator indicator) => _avatar.SetClock(indicator);
    public string DisplayName => _name.Text;
    internal bool HasColoredAvatarBackground => false;
    internal bool HasRoundedAvatar => true;
    internal MoveClockUrgency ClockUrgency => _avatar.Indicator.Urgency;

    private sealed partial class AvatarClockView : Control
    {
        private const float BorderWidth = 2.2f;
        private readonly TextureRect _image = new();
        private readonly Label _initial = new();
        private readonly ShaderMaterial _clipMaterial;
        private ImageTexture? _texture;
        public MoveClockIndicator Indicator { get; private set; }

        public AvatarClockView()
        {
            CustomMinimumSize = new Vector2(52, 52);
            MouseFilter = MouseFilterEnum.Ignore;
            _clipMaterial = new ShaderMaterial
            {
                Shader = new Shader { Code = "shader_type canvas_item; uniform float radius = 0.19; void fragment(){ vec2 q=abs(UV-vec2(0.5))-vec2(0.5-radius); float d=length(max(q,vec2(0.0)))+min(max(q.x,q.y),0.0)-radius; vec4 c=texture(TEXTURE,UV); COLOR=vec4(c.rgb,c.a*smoothstep(0.012,-0.012,d)); }" }
            };
            _image.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            _image.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            _image.Material = _clipMaterial;
            _image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _image.OffsetLeft = 4; _image.OffsetTop = 4; _image.OffsetRight = -4; _image.OffsetBottom = -4;
            AddChild(_image);
            _initial.HorizontalAlignment = HorizontalAlignment.Center;
            _initial.VerticalAlignment = VerticalAlignment.Center;
            _initial.AddThemeFontSizeOverride("font_size", 21);
            _initial.AddThemeColorOverride("font_color", UiStyle.Text);
            _initial.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _initial.OffsetLeft = 4; _initial.OffsetTop = 4; _initial.OffsetRight = -4; _initial.OffsetBottom = -4;
            AddChild(_initial);
        }

        public void Apply(string name, int width, int height, byte[]? rgba)
        {
            ReleaseTexture();
            _initial.Text = FirstTextElement(name);
            _initial.Visible = true;
            if (rgba is null || width <= 0 || height <= 0) return;
            using var source = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
            _texture = ImageTexture.CreateFromImage(source);
            _image.Texture = _texture;
            _initial.Visible = false;
        }

        public void SetClock(MoveClockIndicator indicator) { Indicator = indicator; QueueRedraw(); }

        public override void _Draw()
        {
            var rect = new Rect2(new Vector2(2, 2), Size - new Vector2(4, 4));
            var path = RoundedPath(rect, 10, 5);
            DrawPolyline(path, new Color(1, 1, 1, .17f), BorderWidth, true);
            if (!Indicator.IsVisible || Indicator.RemainingRatio <= 0) return;
            var color = Indicator.Urgency == MoveClockUrgency.Critical ? new Color("#E5524A") : new Color("#36C76C");
            DrawPolyline(PartialPath(path, (float)Indicator.RemainingRatio), color, BorderWidth, true);
        }

        private static Vector2[] RoundedPath(Rect2 rect, float radius, int arcSteps)
        {
            var points = new List<Vector2> { new(rect.GetCenter().X, rect.Position.Y) };
            AddLine(points, new(rect.End.X - radius, rect.Position.Y));
            AddArc(points, new(rect.End.X - radius, rect.Position.Y + radius), radius, -90, 0, arcSteps);
            AddLine(points, new(rect.End.X, rect.End.Y - radius));
            AddArc(points, new(rect.End.X - radius, rect.End.Y - radius), radius, 0, 90, arcSteps);
            AddLine(points, new(rect.Position.X + radius, rect.End.Y));
            AddArc(points, new(rect.Position.X + radius, rect.End.Y - radius), radius, 90, 180, arcSteps);
            AddLine(points, new(rect.Position.X, rect.Position.Y + radius));
            AddArc(points, new(rect.Position.X + radius, rect.Position.Y + radius), radius, 180, 270, arcSteps);
            AddLine(points, points[0]);
            return points.ToArray();
        }

        private static Vector2[] PartialPath(Vector2[] path, float ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            var lengths = new float[path.Length - 1];
            var total = 0f;
            for (var i = 0; i < lengths.Length; i++) { lengths[i] = path[i].DistanceTo(path[i + 1]); total += lengths[i]; }
            var remaining = total * ratio;
            var result = new List<Vector2> { path[0] };
            for (var i = 0; i < lengths.Length && remaining > 0; i++)
            {
                if (remaining >= lengths[i]) { result.Add(path[i + 1]); remaining -= lengths[i]; }
                else { result.Add(path[i].Lerp(path[i + 1], remaining / lengths[i])); remaining = 0; }
            }
            return result.ToArray();
        }

        private static void AddLine(List<Vector2> points, Vector2 point) => points.Add(point);
        private static void AddArc(List<Vector2> points, Vector2 center, float radius, float from, float to, int steps)
        {
            for (var i = 1; i <= steps; i++)
            {
                var radians = Mathf.DegToRad(Mathf.Lerp(from, to, i / (float)steps));
                points.Add(center + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * radius);
            }
        }

        private static string FirstTextElement(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "—";
            var enumerator = StringInfo.GetTextElementEnumerator(value.Trim());
            return enumerator.MoveNext() ? enumerator.GetTextElement() : "—";
        }

        private void ReleaseTexture() { _image.Texture = null; _texture?.Dispose(); _texture = null; }
        public override void _ExitTree() { ReleaseTexture(); _clipMaterial.Shader?.Dispose(); _clipMaterial.Dispose(); }
    }
}
