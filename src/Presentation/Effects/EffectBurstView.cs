using Godot;

namespace Xiangqi.Presentation.Effects;

/// <summary>Code-drawn burst used by cue effects; it has no texture or scene dependency.</summary>
public partial class EffectBurstView : Control
{
    private float _progress = 1;
    private Color _color = Colors.Transparent;
    private int _rayCount = 18;

    public EffectBurstView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void Configure(Color color, int rayCount)
    {
        _color = color;
        _rayCount = rayCount;
    }

    public void SetProgress(float progress)
    {
        _progress = progress;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_progress >= 1) return;
        var center = Size / 2f;
        var alpha = Mathf.Sin(_progress * Mathf.Pi) * 0.9f;
        var color = new Color(_color, alpha);
        var inner = Mathf.Lerp(35, 150, _progress);
        var outer = inner + Mathf.Lerp(85, 18, _progress);
        for (var i = 0; i < _rayCount; i++)
        {
            var angle = Mathf.Tau * i / _rayCount;
            var direction = Vector2.FromAngle(angle);
            DrawLine(center + direction * inner, center + direction * outer, color,
                Mathf.Lerp(7, 1, _progress), true);
        }
        DrawArc(center, Mathf.Lerp(45, 230, _progress), 0, Mathf.Tau, 72, color,
            Mathf.Lerp(8, 1, _progress), true);
    }
}

