using Godot;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Effects;

/// <summary>
/// 可复用的棋盘反馈视图。仅接收投影后的局部像素坐标与棋子尺度，不引用棋规、阵营、
/// 网络或任何特定棋盘坐标。因此矩形棋盘和未来的分片六边形棋盘可共用这一表现层。
/// 调用方负责决定哪些标记可见；旋转和闪烁由 shader 的 TIME 推进，无需逐帧重画棋盘。
/// </summary>
public partial class BoardFeedbackLayer : Control
{
    private readonly ColorRect _selection;
    private readonly ColorRect _destination;
    private readonly ColorRect _origin;
    private readonly List<ShaderMaterial> _materials = [];
    private readonly List<ThemeResourceLease> _leases = [];
    private readonly Shader _fallbackShader;
    private bool _animated = true;

    public BoardFeedbackLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _fallbackShader = GD.Load<Shader>("res://src/Presentation/Shaders/PieceFeedback.gdshader");
        _origin = CreateMarker();
        _destination = CreateMarker();
        _selection = CreateMarker();
        ApplyTheme(BoardThemeCatalog.Default.Resolve("classic"));
    }

    /// <summary>禁用动态特效时保留静态指示，避免失去选中和最近着法的基本可读性。</summary>
    public void SetAnimated(bool animated)
    {
        _animated = animated;
        foreach (var material in _materials) material.SetShaderParameter("animated", animated);
    }

    /// <summary>
    /// Applies only semantic marker colors from a resolved theme. Materials remain privately
    /// owned by this layer and are disposed on tree exit; applying a theme never resets marker
    /// visibility, animation phase or board state.
    /// </summary>
    public void ApplyTheme(BoardThemePalette palette)
    {
        SetTint(_selection, palette.SelectionFeedback);
        SetTint(_origin, palette.MoveOriginFeedback);
        SetTint(_destination, palette.MoveDestinationFeedback);
    }

    /// <summary>
    /// Applies semantic effect shaders. Each marker owns its private material/optional runtime
    /// shader; a broken custom shader is isolated to that slot and falls back to the built-in.
    /// </summary>
    public void ApplyTheme(IBoardTheme theme)
    {
        ReleaseMaterials();
        ApplyMarker(_selection, theme, ThemeEffectSlot.Selection, theme.Palette.SelectionFeedback, false);
        ApplyMarker(_origin, theme, ThemeEffectSlot.MoveFrom, theme.Palette.MoveOriginFeedback, true);
        var destinationSlot = theme.Effects.ContainsKey(ThemeEffectSlot.Landing)
            ? ThemeEffectSlot.Landing : ThemeEffectSlot.MoveTo;
        ApplyMarker(_destination, theme, destinationSlot, theme.Palette.MoveDestinationFeedback, false);
    }

    /// <summary>
    /// 同步三个独立标记的位置。null 表示隐藏；尺寸由当前投影决定，缩放和翻转后应重新调用。
    /// 此方法不读取或改变权威棋局状态，且全部控件忽略鼠标输入。
    /// </summary>
    public void SetMarkers(Vector2? selected, Vector2? from, Vector2? to, float cellSize)
    {
        Place(_selection, selected, cellSize * 1.4f);
        Place(_destination, to, cellSize * 1.4f);
        Place(_origin, from, cellSize * 0.70f);
    }

    private ColorRect CreateMarker()
    {
        var marker = new ColorRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(marker);
        return marker;
    }

    private void ApplyMarker(ColorRect marker, IBoardTheme theme, ThemeEffectSlot slot, Color tint, bool origin)
    {
        ShaderMaterial material;
        if (theme.Effects.TryGetValue(slot, out var effect) && effect.Shader is not null)
        {
            try
            {
                var lease = new ThemeResourceLease();
                material = lease.CreateMaterial(effect.Shader);
                _leases.Add(lease);
            }
            catch (Exception error)
            {
                GD.PushWarning($"主题 {theme.Id} 的 {slot} shader 加载失败：{error.Message}");
                material = new ShaderMaterial { Shader = _fallbackShader };
            }
        }
        else material = new ShaderMaterial { Shader = _fallbackShader };
        material.SetShaderParameter("origin_cursor", origin);
        material.SetShaderParameter("animated", _animated);
        material.SetShaderParameter("tint", tint);
        marker.Material = material;
        _materials.Add(material);
    }

    private static void SetTint(CanvasItem marker, Color color) =>
        ((ShaderMaterial)marker.Material!).SetShaderParameter("tint", color);

    private static void Place(Control marker, Vector2? center, float diameter)
    {
        marker.Visible = center.HasValue && diameter > 0;
        if (!marker.Visible) return;
        var size = Vector2.One * diameter;
        marker.Position = center!.Value - size * 0.5f;
        marker.Size = size;
    }

    public override void _ExitTree()
    {
        ReleaseMaterials();
    }

    private void ReleaseMaterials()
    {
        _selection.Material = null; _destination.Material = null; _origin.Material = null;
        foreach (var material in _materials) material.Dispose();
        _materials.Clear();
        foreach (var lease in _leases) lease.Dispose();
        _leases.Clear();
    }
}
