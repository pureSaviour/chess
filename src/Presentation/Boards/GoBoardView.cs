using Godot;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Presentation.Boards;

/// <summary>
/// Projection-only 19-line Go board. Drawing, edge labels and hit testing use the same geometry;
/// occupancy/dead groups arrive in immutable snapshots. Coordinate labels use A–T without I and
/// 1–19 from White's lower edge. The view owns no rule state and emits only logical intersections.
/// </summary>
public partial class GoBoardView : Control
{
    private GoSnapshot _snapshot = new GoGame().Snapshot;
    private GoScoreEstimate? _scorePreview;
    private bool _showCoordinates = true;
    private bool _showMoveNumbers;
    public bool CoordinatesVisible => _showCoordinates;
    public bool MoveNumbersVisible => _showMoveNumbers;
    public bool ScorePreviewVisible => _scorePreview is not null;
    public bool HasLastMoveHighlight => _snapshot.LastAction is { Kind: GoActionKind.Place, Point: { } point } &&
        _snapshot.Stones.Any(stone => stone.Point == point);
    public event Action<GridPoint>? PointPressed;
    public GoBoardView()
    {
        CustomMinimumSize = new(320, 320);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }
    public void SetPosition(GoSnapshot snapshot) { _snapshot = snapshot; QueueRedraw(); }
    public void SetScorePreview(GoScoreEstimate? estimate) { _scorePreview = estimate; QueueRedraw(); }
    public void SetCoordinatesVisible(bool visible) { _showCoordinates = visible; QueueRedraw(); }
    public void SetMoveNumbersVisible(bool visible) { _showMoveNumbers = visible; QueueRedraw(); }

    public override void _GuiInput(InputEvent input)
    {
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse) return;
        var (origin, spacing) = Geometry();
        var column = (int)Math.Round((mouse.Position.X - origin.X) / spacing);
        var row = (int)Math.Round((mouse.Position.Y - origin.Y) / spacing);
        var point = new GridPoint(column, row);
        if (column >= 0 && row >= 0 && column < GoGame.StandardBoardSize && row < GoGame.StandardBoardSize &&
            mouse.Position.DistanceTo(origin + new Vector2(column, row) * spacing) <= spacing * .46f)
            PointPressed?.Invoke(point);
    }

    public override void _Draw()
    {
        var (origin, spacing) = Geometry();
        var extent = spacing * (GoGame.StandardBoardSize - 1);
        var padding = spacing * (_showCoordinates ? 1.02f : .58f);
        var boardRect = new Rect2(origin - Vector2.One * padding,
            new Vector2(extent + padding * 2, extent + padding * 2));
        var style = new StyleBoxFlat
        {
            BgColor = new("#D8AE64"), BorderColor = new("#71451F"),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7
        };
        DrawStyleBox(style, boardRect);
        var line = new Color("#4C2F17");
        for (var i = 0; i < GoGame.StandardBoardSize; i++)
        {
            DrawLine(origin + new Vector2(i * spacing, 0), origin + new Vector2(i * spacing, extent), line, 1.35f);
            DrawLine(origin + new Vector2(0, i * spacing), origin + new Vector2(extent, i * spacing), line, 1.35f);
        }
        foreach (var x in new[] { 3, 9, 15 })
        foreach (var y in new[] { 3, 9, 15 })
            DrawCircle(origin + new Vector2(x, y) * spacing, Math.Max(2.5f, spacing * .075f), line);
        if (_showCoordinates) DrawCoordinates(origin, spacing, extent);

        if (_scorePreview is { } preview)
        {
            foreach (var point in preview.BlackTerritory)
                DrawTerritoryMarker(origin + new Vector2(point.Column, point.Row) * spacing, spacing, GoStone.Black);
            foreach (var point in preview.WhiteTerritory)
                DrawTerritoryMarker(origin + new Vector2(point.Column, point.Row) * spacing, spacing, GoStone.White);
        }

        foreach (var stone in _snapshot.Stones)
        {
            var center = origin + new Vector2(stone.Point.Column, stone.Point.Row) * spacing;
            var radius = spacing * .44f;
            var previewDead = _scorePreview?.MarkedDeadStones.Contains(stone.Point) == true;
            var dead = _snapshot.DeadStones.Contains(stone.Point) || previewDead;
            DrawCircle(center + new Vector2(1.5f, 2.1f), radius, new Color(0, 0, 0, .28f));
            var fill = stone.Color == GoStone.Black ? new Color("#171717") : new Color("#F5F1E8");
            if (dead) fill = new Color(fill, .42f);
            DrawCircle(center, radius, fill);
            DrawArc(center, radius, 0, Mathf.Tau, 32,
                stone.Color == GoStone.Black ? new Color("#585858") : new Color("#99938A"), 1.15f);
            if (dead)
            {
                var mark = previewDead ? new Color("#F0A83A") : new Color("#B3312D");
                DrawLine(center - Vector2.One * radius * .55f, center + Vector2.One * radius * .55f, mark, 2.5f);
                DrawLine(center + new Vector2(-1, 1) * radius * .55f, center + new Vector2(1, -1) * radius * .55f, mark, 2.5f);
            }
            var last = _snapshot.LastAction is { Kind: GoActionKind.Place, Point: { } lastPoint } && lastPoint == stone.Point;
            if (last)
            {
                var highlight = stone.Color == GoStone.Black ? new Color("#F4CF5D") : new Color("#B72F27");
                DrawArc(center, radius * .67f, 0, Mathf.Tau, 40, highlight, Math.Max(2.2f, spacing * .085f), true);
            }
            if (_showMoveNumbers)
                StoneMoveNumberPainter.Draw(this, center, radius, stone.MoveNumber,
                    last ? stone.Color == GoStone.Black ? new Color("#E4C067") : new Color("#A7382D")
                         : stone.Color == GoStone.Black ? new Color("#F5F1E8") : new Color("#28231E"));
        }
    }

    private void DrawTerritoryMarker(Vector2 center, float spacing, GoStone owner)
    {
        var half = spacing * .15f;
        var fill = owner == GoStone.Black ? new Color(0.07f, 0.07f, 0.07f, .72f) : new Color(1, 1, 1, .82f);
        DrawRect(new Rect2(center - Vector2.One * half, Vector2.One * half * 2), fill, true);
        DrawRect(new Rect2(center - Vector2.One * half, Vector2.One * half * 2), new Color(0.2f, 0.13f, 0.07f, .78f), false,
            Math.Max(1f, spacing * .025f));
    }

    private void DrawCoordinates(Vector2 origin, float spacing, float extent)
    {
        const string columns = "ABCDEFGHJKLMNOPQRST";
        var color = new Color("#50351F");
        var font = ThemeDB.FallbackFont;
        var size = Math.Clamp((int)(spacing * .36f), 11, 16);
        for (var i = 0; i < GoGame.StandardBoardSize; i++)
        {
            var x = origin.X + i * spacing;
            var label = columns[i].ToString();
            DrawString(font, new Vector2(x - spacing * .3f, origin.Y - spacing * .55f), label,
                HorizontalAlignment.Center, spacing * .6f, size, color);
            DrawString(font, new Vector2(x - spacing * .3f, origin.Y + extent + spacing * .72f), label,
                HorizontalAlignment.Center, spacing * .6f, size, color);
            var y = origin.Y + i * spacing + size * .35f;
            var number = (GoGame.StandardBoardSize - i).ToString();
            DrawString(font, new Vector2(origin.X - spacing * .95f, y), number,
                HorizontalAlignment.Right, spacing * .58f, size, color);
            DrawString(font, new Vector2(origin.X + extent + spacing * .38f, y), number,
                HorizontalAlignment.Left, spacing * .58f, size, color);
        }
    }
    private (Vector2 Origin, float Spacing) Geometry()
    {
        var side = Math.Min(Size.X, Size.Y);
        var marginUnits = _showCoordinates ? 2.65f : 1.2f;
        var spacing = side / (GoGame.StandardBoardSize - 1 + marginUnits);
        var extent = spacing * (GoGame.StandardBoardSize - 1);
        return ((Size - new Vector2(extent, extent)) * .5f, spacing);
    }
}
