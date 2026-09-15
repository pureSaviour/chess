using Godot;

namespace Xiangqi.Presentation.Boards;

/// <summary>
/// Draws a centered move number inside a stone. The font shrinks until both measured width and
/// height stay within the supplied diameter; it owns no font resource because it uses Godot's
/// fallback font and must only be called from a CanvasItem draw callback on the main thread.
/// </summary>
internal static class StoneMoveNumberPainter
{
    public static void Draw(CanvasItem canvas, Vector2 center, float radius, long moveNumber, Color color)
    {
        var font = ThemeDB.FallbackFont;
        var text = moveNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var maximumWidth = radius * 1.62f;
        var maximumHeight = radius * 1.45f;
        var fontSize = Math.Max(7, (int)Math.Floor(radius * 1.15f));
        Vector2 measured;
        do
        {
            measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
            if ((measured.X <= maximumWidth && measured.Y <= maximumHeight) || fontSize <= 7) break;
            fontSize--;
        } while (true);

        var baseline = center + new Vector2(-measured.X * .5f,
            (font.GetAscent(fontSize) - font.GetDescent(fontSize)) * .5f);
        canvas.DrawString(font, baseline, text, HorizontalAlignment.Left, -1, fontSize, color);
    }
}
