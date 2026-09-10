using Godot;
using Xiangqi.Core.Domain.Junqi;

namespace Xiangqi.Presentation.Boards;

/// <summary>
/// Presentation-only projection for the 12x5 Junqi graph. Drawing and hit testing share the same
/// transform; the view renders only the already-redacted snapshot supplied by a controller.
/// </summary>
public partial class JunqiBoardView : Control
{
    private JunqiPlayerSnapshot? _snapshot;
    private JunqiCell? _selected;
    private IReadOnlySet<JunqiCell> _legal = new HashSet<JunqiCell>();
    private JunqiSide _bottomSide = JunqiSide.First;

    public event Action<JunqiCell>? CellPressed;

    public JunqiBoardView()
    {
        CustomMinimumSize = new Vector2(520, 700);
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public void SetPosition(JunqiPlayerSnapshot snapshot, JunqiCell? selected = null,
        IReadOnlyList<JunqiCell>? legal = null, JunqiSide bottomSide = JunqiSide.First)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _selected = selected; _legal = (legal ?? []).ToHashSet(); _bottomSide = bottomSide;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
        var (origin, spacing) = Projection();
        var best = JunqiTopology.Instance.Cells.Select(cell => (Cell: cell, Distance: Point(cell, origin, spacing).DistanceTo(click.Position)))
            .OrderBy(x => x.Distance).First();
        if (best.Distance <= spacing * 0.38f) { AcceptEvent(); CellPressed?.Invoke(best.Cell); }
    }

    public override void _Draw()
    {
        var (origin, spacing) = Projection();
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#D7B879"));
        DrawRect(new Rect2(10, 10, Size.X - 20, Size.Y - 20), new Color("#392819"), false, 3);
        var topology = JunqiTopology.Instance;
        var drawnRoads = new HashSet<(JunqiCell, JunqiCell)>();
        foreach (var from in topology.Cells)
        foreach (var to in topology.RoadNeighbors(from))
        {
            var edge = Compare(from, to) < 0 ? (from, to) : (to, from);
            if (drawnRoads.Add(edge)) DrawLine(Point(from, origin, spacing), Point(to, origin, spacing), new Color("#775D3A"), 2, true);
        }
        var drawnRails = new HashSet<(JunqiCell, JunqiCell)>();
        foreach (var from in topology.Cells)
        foreach (var to in topology.RailNeighbors(from))
        {
            var edge = Compare(from, to) < 0 ? (from, to) : (to, from);
            if (!drawnRails.Add(edge)) continue;
            var a = Point(from, origin, spacing); var b = Point(to, origin, spacing);
            var normal = (b - a).Normalized().Orthogonal() * 3;
            DrawLine(a + normal, b + normal, new Color("#443321"), 2, true);
            DrawLine(a - normal, b - normal, new Color("#443321"), 2, true);
        }
        foreach (var cell in topology.Cells) DrawCell(cell, origin, spacing);
        if (_snapshot is null) return;
        foreach (var piece in _snapshot.Pieces) DrawPiece(piece, origin, spacing);
    }

    private void DrawCell(JunqiCell cell, Vector2 origin, float spacing)
    {
        var point = Point(cell, origin, spacing); var kind = JunqiTopology.Instance.Kind(cell);
        if (_legal.Contains(cell)) DrawCircle(point, spacing * 0.30f, new Color(0.93f, 0.67f, 0.20f, 0.58f));
        if (_selected == cell) DrawCircle(point, spacing * 0.36f, new Color("#F4D27A"), false, 4, true);
        if (kind == JunqiCellKind.Camp)
        {
            var r = spacing * 0.22f;
            Vector2[] points = [point + new Vector2(0, -r), point + new Vector2(r, 0),
                point + new Vector2(0, r), point + new Vector2(-r, 0)];
            DrawColoredPolygon(points, new Color("#C9A765")); DrawPolyline([.. points, points[0]], new Color("#624A2F"), 2, true);
        }
        else
        {
            var rect = new Rect2(point - new Vector2(spacing * 0.27f, spacing * 0.18f), new Vector2(spacing * 0.54f, spacing * 0.36f));
            DrawRect(rect, kind == JunqiCellKind.Headquarters ? new Color("#B18B4E") : new Color("#CDB079"));
            DrawRect(rect, new Color("#684D2D"), false, 2);
            if (kind == JunqiCellKind.Headquarters) DrawString(ThemeDB.FallbackFont, point + new Vector2(-spacing * .16f, 5), "营", HorizontalAlignment.Center, spacing * .32f, 13, new Color("#65421F"));
        }
    }

    private void DrawPiece(JunqiPublicPlacement piece, Vector2 origin, float spacing)
    {
        var point = Point(piece.Cell, origin, spacing); var first = piece.Side == JunqiSide.First;
        var fill = first ? new Color("#A94339") : new Color("#435366");
        var rect = new Rect2(point - new Vector2(spacing * .31f, spacing * .215f), new Vector2(spacing * .62f, spacing * .43f));
        DrawStyleBox(new StyleBoxFlat { BgColor = fill, BorderColor = new Color("#F2D39A"),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7 }, rect);
        var label = piece.Kind is { } kind ? PieceName(kind) : "暗";
        DrawString(ThemeDB.FallbackFont, point + new Vector2(-spacing * .26f, 6), label,
            HorizontalAlignment.Center, spacing * .52f, 15, Colors.White);
    }

    private (Vector2 Origin, float Spacing) Projection()
    {
        var spacing = MathF.Min((Size.X - 72) / 4f, (Size.Y - 64) / 11f);
        return (new Vector2((Size.X - spacing * 4) / 2f, (Size.Y - spacing * 11) / 2f), spacing);
    }
    private Vector2 Point(JunqiCell cell, Vector2 origin, float spacing)
    {
        var row = _bottomSide == JunqiSide.First ? cell.Row : 11 - cell.Row;
        var column = _bottomSide == JunqiSide.First ? cell.Column : 4 - cell.Column;
        return origin + new Vector2(column * spacing, row * spacing);
    }
    private static int Compare(JunqiCell a, JunqiCell b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column);
    private static string PieceName(JunqiPieceKind kind) => kind switch
    {
        JunqiPieceKind.FieldMarshal => "司令", JunqiPieceKind.General => "军长", JunqiPieceKind.MajorGeneral => "师长",
        JunqiPieceKind.BrigadierGeneral => "旅长", JunqiPieceKind.Colonel => "团长", JunqiPieceKind.Major => "营长",
        JunqiPieceKind.Captain => "连长", JunqiPieceKind.Lieutenant => "排长", JunqiPieceKind.Engineer => "工兵",
        JunqiPieceKind.Landmine => "地雷", JunqiPieceKind.Bomb => "炸弹", JunqiPieceKind.Flag => "军旗", _ => "?"
    };
}
