using Godot;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Presentation.Boards;

/// <summary>Projection-only intersection board. Drawing and hit testing share the same geometry;
/// occupancy arrives as immutable snapshots and the short landing animation never mutates it.</summary>
public partial class GomokuBoardView : Control
{
    private GomokuSnapshot _snapshot = new(15, GomokuRuleSet.Freestyle, [], StoneColor.Black, 0, false, null, null, null);
    private GridPoint? _landing;
    private GridPoint? _analysisPoint;
    private bool _showMoveNumbers;
    private double _landingAge;
    public event Action<GridPoint>? PointPressed;
    public bool MoveNumbersVisible => _showMoveNumbers;
    public GomokuBoardView()
    {
        CustomMinimumSize = new(320, 320);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }
    public void SetPosition(GomokuSnapshot snapshot, bool animateLast = false)
    { _snapshot = snapshot; _analysisPoint = null; if (animateLast && snapshot.LastMove is { } move) { _landing = move.Point; _landingAge = 0; SetProcess(true); } QueueRedraw(); }
    /// <summary>Displays an engine suggestion without changing occupancy or accepting input.</summary>
    public void SetAnalysisPoint(GridPoint? point) { _analysisPoint = point; QueueRedraw(); }
    public void SetMoveNumbersVisible(bool visible) { _showMoveNumbers = visible; QueueRedraw(); }
    public override void _Process(double delta)
    { if (_landing is null) { SetProcess(false); return; } _landingAge += delta; if (_landingAge >= .22) { _landing = null; SetProcess(false); } QueueRedraw(); }
    public override void _GuiInput(InputEvent input)
    {
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse) return;
        var (origin, spacing) = Geometry(); var column = (int)Math.Round((mouse.Position.X - origin.X) / spacing); var row = (int)Math.Round((mouse.Position.Y - origin.Y) / spacing);
        var point = new GridPoint(column, row); if (column >= 0 && row >= 0 && column < _snapshot.BoardSize && row < _snapshot.BoardSize && mouse.Position.DistanceTo(origin + new Vector2(column, row) * spacing) <= spacing * .46f) PointPressed?.Invoke(point);
    }
    public override void _Draw()
    {
        var (origin, spacing) = Geometry(); var extent = spacing * (_snapshot.BoardSize - 1); var boardRect = new Rect2(origin - Vector2.One * spacing * .58f, new Vector2(extent + spacing * 1.16f, extent + spacing * 1.16f));
        DrawStyleBox(new StyleBoxFlat { BgColor = new("#D5A95F"), BorderColor = new("#6B3E1F"), BorderWidthLeft=3, BorderWidthTop=3, BorderWidthRight=3, BorderWidthBottom=3, CornerRadiusTopLeft=8, CornerRadiusTopRight=8, CornerRadiusBottomLeft=8, CornerRadiusBottomRight=8 }, boardRect);
        var line = new Color("#533318");
        for (var i=0;i<_snapshot.BoardSize;i++) { DrawLine(origin + new Vector2(i*spacing,0), origin + new Vector2(i*spacing,extent), line, 1.4f); DrawLine(origin + new Vector2(0,i*spacing), origin + new Vector2(extent,i*spacing), line, 1.4f); }
        foreach (var star in StarPoints(_snapshot.BoardSize)) DrawCircle(origin + new Vector2(star.Column, star.Row)*spacing, Math.Max(2.5f, spacing*.075f), line);
        foreach (var stone in _snapshot.Stones)
        {
            var center = origin + new Vector2(stone.Point.Column, stone.Point.Row)*spacing; var radius=spacing*.43f;
            if (_landing == stone.Point) radius *= Mathf.Lerp(.72f, 1f, (float)Math.Min(1, _landingAge/.18));
            DrawCircle(center + new Vector2(1.5f,2.2f), radius, new Color(0,0,0,.28f));
            DrawCircle(center, radius, stone.Color==StoneColor.Black ? new Color("#171717") : new Color("#F4F0E7"));
            DrawArc(center, radius, 0, Mathf.Tau, 32, stone.Color==StoneColor.Black ? new Color("#555555") : new Color("#9A948A"), 1.2f);
            var last = _snapshot.LastMove?.Point == stone.Point;
            if (_showMoveNumbers && stone.MoveNumber is { } moveNumber)
                StoneMoveNumberPainter.Draw(this, center, radius, moveNumber,
                    last ? stone.Color == StoneColor.Black ? new Color("#E4C067") : new Color("#A7382D")
                         : stone.Color == StoneColor.Black ? new Color("#F4F0E7") : new Color("#28231E"));
            else if (last)
                DrawCircle(center, Math.Max(2.2f, spacing*.075f), stone.Color==StoneColor.Black ? new Color("#E4C067") : new Color("#A7382D"));
        }
        if (_analysisPoint is { } best && best.Column >= 0 && best.Row >= 0 && best.Column < _snapshot.BoardSize && best.Row < _snapshot.BoardSize && !_snapshot.Stones.Any(stone => stone.Point == best))
        {
            var center = origin + new Vector2(best.Column, best.Row) * spacing;
            var color = new Color("#E9B949");
            DrawCircle(center, spacing * .28f, new Color(color, .22f));
            DrawArc(center, spacing * .31f, 0, Mathf.Tau, 32, color, Math.Max(2f, spacing * .065f));
            DrawLine(center - Vector2.One * spacing * .17f, center + Vector2.One * spacing * .17f, color, Math.Max(2f, spacing * .055f));
            DrawLine(center + new Vector2(-1, 1) * spacing * .17f, center + new Vector2(1, -1) * spacing * .17f, color, Math.Max(2f, spacing * .055f));
        }
    }
    private (Vector2 Origin,float Spacing) Geometry()
    { var side=Math.Min(Size.X,Size.Y); var spacing=side/(_snapshot.BoardSize+1.2f); var extent=spacing*(_snapshot.BoardSize-1); return ((Size-new Vector2(extent,extent))*.5f, spacing); }
    private static IEnumerable<GridPoint> StarPoints(int size)
    { if(size<9) yield break; var edge=size>=15?3:2; foreach(var x in new[]{edge,size/2,size-1-edge}) foreach(var y in new[]{edge,size/2,size-1-edge}) yield return new(x,y); }
}
