using Godot;

namespace Xiangqi.Presentation;

/// <summary>
/// Shared presentation-only timing and path for Xiangqi pieces. The authoritative move has
/// already committed before this helper is called; it never reads or changes game state.
/// </summary>
internal static class PieceMotion
{
    private const float ApproachEnd = 0.76f;

    public static float Duration(int configuredMilliseconds, float boardDistance)
    {
        if (configuredMilliseconds > 0)
            return Mathf.Clamp(configuredMilliseconds / 1000f * 0.72f, 0.14f, 0.32f);
        return Mathf.Clamp(0.13f + boardDistance * 0.016f, 0.16f, 0.29f);
    }

    public static Vector2 Position(Vector2 from, Vector2 to, float progress, float cellSize)
    {
        progress = Mathf.Clamp(progress, 0, 1);
        var above = to - Vector2.Down * cellSize * 0.18f;
        if (progress < ApproachEnd)
        {
            var t = progress / ApproachEnd;
            var eased = t * t * (3f - 2f * t);
            return from.Lerp(above, eased);
        }

        var fall = (progress - ApproachEnd) / (1f - ApproachEnd);
        return above.Lerp(to, fall * fall);
    }
}
