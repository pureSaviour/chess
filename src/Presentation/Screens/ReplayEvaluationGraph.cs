using Godot;
using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Screens;

/// <summary>Presentation-only score chart. Values are centipawns from Red's perspective.</summary>
public partial class ReplayEvaluationGraph : Control
{
    private readonly Dictionary<int, int> _scores = [];
    private int _positionCount = 1;
    private int _current;
    public ReplayEvaluationGraph() { CustomMinimumSize = new(330, 260); MouseFilter = MouseFilterEnum.Ignore; }
    public void Configure(int positionCount) { _positionCount = Math.Max(1, positionCount); QueueRedraw(); }
    public void SetPoint(ReplayAnalysisPoint point) { _scores[point.PositionIndex] = point.RedCentipawns; QueueRedraw(); }
    public void SetCurrent(int index) { _current = Math.Clamp(index, 0, _positionCount - 1); QueueRedraw(); }
    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
    public override void _Draw()
    {
        var rect = new Rect2(new Vector2(28, 18), Size - new Vector2(42, 42));
        DrawRect(rect, new Color(0.10f, 0.075f, 0.055f, .9f), true);
        var middle = rect.Position.Y + rect.Size.Y / 2; DrawLine(new(rect.Position.X, middle), new(rect.End.X, middle), new Color(.75f, .67f, .55f, .45f), 1);
        for (var i = -2; i <= 2; i++) { var y = middle - i * rect.Size.Y / 5f; DrawLine(new(rect.Position.X, y), new(rect.End.X, y), new Color(.5f, .44f, .36f, .16f), 1); }
        Vector2 Point(int index, int score) => new(rect.Position.X + rect.Size.X * index / Math.Max(1, _positionCount - 1), middle - Mathf.Clamp(score / 1000f, -1, 1) * rect.Size.Y * .44f);
        var ordered = _scores.OrderBy(pair => pair.Key).ToArray();
        for (var i = 1; i < ordered.Length; i++) if (ordered[i].Key == ordered[i - 1].Key + 1)
            DrawLine(Point(ordered[i - 1].Key, ordered[i - 1].Value), Point(ordered[i].Key, ordered[i].Value), new Color("#E77B72"), 3, true);
        foreach (var pair in ordered) DrawCircle(Point(pair.Key, pair.Value), pair.Key == _current ? 6 : 3, pair.Value >= 0 ? new Color("#F08078") : new Color("#7FA6D8"));
        var cursorX = rect.Position.X + rect.Size.X * _current / Math.Max(1, _positionCount - 1);
        DrawLine(new(cursorX, rect.Position.Y), new(cursorX, rect.End.Y), new Color("#E5B84B"), 2, true);
        var textColor = new Color(.8f, .72f, .62f);
        DrawString(ThemeDB.FallbackFont, new(2, rect.Position.Y + 14), "红优", HorizontalAlignment.Left, 26, 12, textColor);
        DrawString(ThemeDB.FallbackFont, new(8, middle + 5), "0", HorizontalAlignment.Left, 20, 12, textColor);
        DrawString(ThemeDB.FallbackFont, new(2, rect.End.Y), "黑优", HorizontalAlignment.Left, 26, 12, textColor);
    }
}
