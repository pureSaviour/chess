using Godot;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;
using Xiangqi.Presentation.Effects;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Boards;

/// <summary>
/// 三国棋盘仅绘制不可变局面并发出交点点击。逻辑/点击/网格共享投影缓存；Shader 负责光圈
/// 动画，静止棋盘不逐帧重画。控制色来自 Owner 而非 BirthRegion，障碍使用灰色与叉标。
/// </summary>
public partial class ThreeBoardView : Control
{
    public static readonly Color[] KingdomColors = [new("#367CC6"), new("#B88B12"), new("#358450")];
    private readonly IBoardThemeCatalog _themes;
    private readonly ThreeBoardProjection _projection = new();
    private readonly BoardFeedbackLayer _feedback = new();
    private readonly ThemePieceLayer _pieceLayer;
    private readonly SystemFont _font = new() { FontNames = ["Microsoft YaHei", "SimHei", "Noto Sans CJK SC"] };
    private readonly Polygon2D _spring = new() { ShowBehindParent = true, Visible = false };
    private ThemeResourceLease? _themeLease;
    private ShaderMaterial? _boardMaterial;
    private BoardThemePalette _palette;
    private KingdomSnapshot? _snapshot;
    private BoardCellId? _selected;
    private IReadOnlyList<BoardCellId> _legal = Array.Empty<BoardCellId>();
    private bool _coordinates = true;
    private bool _animated = true;
    private double _selectionTime;
    private MoveAnimation? _moveAnimation;
    private int _moveDurationMs;
    public event Action<BoardCellId>? CellPressed;
    public event Action<bool>? MoveAnimationFinished;
    internal ThreeBoardProjection Projection => _projection;
    internal KingdomMove? VisibleLastMove => _moveAnimation is null ? _snapshot?.LastMove : null;
    internal BoardThemePalette ActivePalette => _palette;
    internal bool HasThemedBoardMaterial => _boardMaterial is not null;
    public bool IsAnimating => _moveAnimation is not null;

    public ThreeBoardView(IBoardThemeCatalog? themes = null)
    {
        _themes = themes ?? BoardThemeCatalog.Default;
        _palette = _themes.Resolve(BoardSkinKind.Classic).Palette;
        CustomMinimumSize = new(720, 690);
        SizeFlagsHorizontal = SizeFlags.ExpandFill; SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        _pieceLayer = new ThemePieceLayer(BuildAtlasCommands);
        AddChild(_spring); AddChild(_pieceLayer); AddChild(_feedback);
        SetProcess(false);
    }
    public void ApplyAppearance(AppSettings settings)
    {
        var theme = _themes.Resolve(settings.ThemeId);
        ThemeResourceLease? replacement = null;
        ShaderMaterial? replacementMaterial = null;
        Texture2D? replacementTexture = null;
        try
        {
            if (theme.Boards.TryGetValue(ThemeBoardMode.ThreeKingdoms, out var assets) &&
                (assets.Texture is not null || assets.Shader is not null))
            {
                replacement = new ThemeResourceLease();
                replacementTexture = assets.Texture is null ? null : replacement.LoadTexture(assets.Texture);
                replacementMaterial = assets.Shader is not null
                    ? replacement.CreateMaterial(assets.Shader)
                    : replacement.CreateTextureMaterial(replacementTexture!);
                if (replacementTexture is not null)
                    replacementMaterial.SetShaderParameter("theme_texture", replacementTexture);
            }
        }
        catch (Exception error)
        {
            replacement?.Dispose();
            GD.PushWarning($"主题 {theme.Id} 的三国棋盘资源加载失败，已回退 classic：{error.Message}");
            theme = _themes.Resolve("classic");
            replacement = null; replacementMaterial = null; replacementTexture = null;
        }
        _spring.Material = null; _spring.Texture = null;
        _themeLease?.Dispose();
        _themeLease = replacement; _boardMaterial = replacementMaterial;
        _spring.Material = replacementMaterial; _spring.Texture = replacementTexture;
        _palette = theme.Palette;
        _moveDurationMs = theme.Effects.GetValueOrDefault(ThemeEffectSlot.Move)?.DurationMs ?? 0;
        _coordinates = settings.ShowBoardCoordinates;
        _animated = settings.EffectsEnabled;
        _feedback.SetAnimated(settings.EffectsEnabled);
        _feedback.ApplyTheme(theme);
        _pieceLayer.ApplyTheme(theme);
        _spring.Visible = _boardMaterial is not null;
        SetProcess(_moveAnimation is not null || (_selected is not null && _animated));
        QueueRedraw();
    }
    /// <summary>Rotates only projection and hit-testing so the requested country faces the viewer.</summary>
    public void SetBottomKingdom(Kingdom kingdom)
    {
        _projection.SetBottomKingdom(kingdom);
        QueueRedraw();
    }
    public void SetPosition(KingdomSnapshot state, BoardCellId? selected = null, IReadOnlyList<BoardCellId>? legal = null)
    {
        var previous = _snapshot;
        _snapshot = state;
        _selected = selected;
        _legal = legal ?? Array.Empty<BoardCellId>();
        if (previous is not null && state.Ply == previous.Ply + 1 && state.LastMove is { } move)
            BeginMoveAnimation(previous, state, move);
        else if (previous is null || state.Ply != previous.Ply)
            _moveAnimation = null;
        SetProcess(_moveAnimation is not null || (_selected is not null && _animated));
        QueueRedraw();
    }
    public override void _Notification(int what)
    { if (what == NotificationResized) { _projection.Resize(Size); QueueRedraw(); } }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } &&
            _projection.Hit(GetLocalMousePosition()) is { } cell)
        { CellPressed?.Invoke(cell); AcceptEvent(); }
    }
    public override void _Process(double delta)
    {
        _selectionTime += delta;
        if (_moveAnimation is not null)
        {
            _moveAnimation.Elapsed += (float)delta;
            if (_moveAnimation.Elapsed >= _moveAnimation.Duration)
            {
                var captured = _moveAnimation.Captured;
                _moveAnimation = null;
                MoveAnimationFinished?.Invoke(captured);
            }
        }
        SetProcess(_moveAnimation is not null || (_selected is not null && _animated));
        QueueRedraw();
    }
    public override void _Draw()
    {
        _projection.Resize(Size);
        var cell = _projection.CellSize;
        var outline = _projection.Outline;
        _spring.Polygon = outline;
        _spring.UV = outline.Select(v => (v - _projection.Center) / (_projection.Radius * 2) + Vector2.One * 0.5f).ToArray();
        if (!_spring.Visible) DrawColoredPolygon(outline, _palette.BoardFill);
        DrawGrandDecoration();
        // Region washes remain quiet and use shared outline/center vertices; no seams can open.
        for (var k = 0; k < 3; k++)
        {
            var a = _projection.Vertex(2 * k); var b = _projection.Vertex(2 * k + 1);
            var l = (_projection.Vertex(2 * k - 1) + a) / 2;
            var r = (b + _projection.Vertex(2 * k + 2)) / 2;
            DrawColoredPolygon([a, b, r, _projection.Center, l], new Color(_palette.KingdomPieceColors[k], 0.095f));
            if (_coordinates)
                for (var f = 0; f < 9; f++)
                {
                    var p = Point(k, f, 0);
                    var outward = ((a + b) / 2 - _projection.Center).Normalized();
                    Text("一二三四五六七八九"[f].ToString(), p + outward * cell * 0.82f, (int)(cell * 0.33f), _palette.KingdomPieceColors[k]);
                }
            Text(ThreeMatchSession.Name((Kingdom)k), Point(k, 4, 2),
                Math.Max(28, (int)(cell * 1.2f)), new Color(_palette.KingdomPieceColors[k], 0.13f));
            // Bilinear projection is not affine: splitting each diagonal at the actual logical
            // center guarantees all four palace strokes meet at exactly one point.
            foreach (var segment in PalaceSegments(k))
                DrawLine(segment.From, segment.To, _palette.GridInk, 1.3f, true);
        }
        foreach (var edge in ThreeKingdomsTopology.Instance.Edges.Where(edge => !IsTerritoryBoundaryEdge(edge)))
            DrawLine(_projection.Point(edge.A), _projection.Point(edge.B), _palette.GridInk, 1.2f, true);
        DrawBoardFrame(outline);
        DrawTerritoryBoundary(Kingdom.Wei, Kingdom.Shu, "秦岭");
        DrawTerritoryBoundary(Kingdom.Wei, Kingdom.Wu, "江淮");
        DrawTerritoryBoundary(Kingdom.Shu, Kingdom.Wu, "三峡");
        foreach (var c in _legal) DrawCircle(_projection.Point(c), cell * 0.09f, _palette.LegalMove, true, -1, true);
        if (_snapshot is null) return;
        if (_pieceLayer.IsActive)
        {
            foreach (var obstacle in _snapshot.Pieces.Where(p => p.Piece.Owner is null)) Piece(obstacle, false);
            _pieceLayer.QueueRedraw();
        }
        else
        {
            var animatedId = _moveAnimation?.Moving.Piece.Id;
            foreach (var p in _snapshot.Pieces.Where(p => p.Cell != _selected && p.Piece.Id != animatedId)) Piece(p, false);
            if (_moveAnimation is not null) DrawAnimatedMove(_moveAnimation);
            var chosen = _snapshot.Pieces.FirstOrDefault(p => p.Cell == _selected);
            if (chosen is not null && _moveAnimation is null) Piece(chosen, true);
        }
        var visibleMove = VisibleLastMove;
        _feedback.SetMarkers(_selected is { } s && _moveAnimation is null
                ? _projection.Point(s) - Vector2.Down * SelectionLift(cell) : null,
            visibleMove is { } m ? _projection.Point(m.From) : null,
            visibleMove is { } n ? _projection.Point(n.To) : null, cell * 0.84f);
    }

    private IReadOnlyList<ThemePieceDrawCommand> BuildAtlasCommands()
    {
        if (_snapshot is null) return Array.Empty<ThemePieceDrawCommand>();
        List<ThemePieceDrawCommand> commands = [];
        var animatedId = _moveAnimation?.Moving.Piece.Id;
        foreach (var placement in _snapshot.Pieces.Where(p => p.Piece.Owner is not null &&
                     p.Cell != _selected && p.Piece.Id != animatedId))
            commands.Add(AtlasCommand(placement, _projection.Point(placement.Cell)));
        if (_moveAnimation is { } animation)
        {
            var raw = Mathf.Clamp(animation.Elapsed / animation.Duration, 0, 1);
            var center = PieceMotion.Position(_projection.Point(animation.From),
                _projection.Point(animation.To), raw, _projection.CellSize);
            commands.Add(AtlasCommand(animation.Moving, center, 1, 1.055f));
        }
        else if (_snapshot.Pieces.FirstOrDefault(p => p.Cell == _selected) is { Piece.Owner: not null } chosen)
            commands.Add(AtlasCommand(chosen,
                _projection.Point(chosen.Cell) - Vector2.Down * SelectionLift(_projection.CellSize), 1, 1.045f));
        return commands;
    }

    private ThemePieceDrawCommand AtlasCommand(KingdomPlacement placement, Vector2 center,
        float alpha = 1, float scale = 1) => new(
        BoardView.PieceColumn(placement.Piece.Kind.Value), 2 + (int)placement.Piece.Owner!.Value,
        center, _projection.CellSize * 0.84f * scale, new Color(1, 1, 1, alpha));

    private void DrawGrandDecoration()
    {
        var center = _projection.Center;
        var cell = _projection.CellSize;
        DrawCircle(center, cell * 0.78f, new Color(_palette.BoardEdge, 0.055f), true, -1, true);
        DrawArc(center, cell * 0.78f, 0, Mathf.Tau, 72, new Color(_palette.BoardEdge, 0.28f), 2.1f, true);
        DrawArc(center, cell * 0.61f, 0, Mathf.Tau, 72, new Color(_palette.BoardEdge, 0.17f), 1.1f, true);
        for (var i = 0; i < 6; i++)
        {
            var vertex = _projection.Vertex(i);
            var inward = (center - vertex).Normalized();
            var tangent = new Vector2(-inward.Y, inward.X);
            var a = vertex + inward * cell * 0.32f;
            DrawLine(a - tangent * cell * 0.24f, a + tangent * cell * 0.24f,
                new Color(_palette.BoardEdge, 0.42f), 2.2f, true);
            DrawLine(a - tangent * cell * 0.24f, a + inward * cell * 0.18f,
                new Color(_palette.BoardEdge, 0.32f), 1.4f, true);
            DrawLine(a + tangent * cell * 0.24f, a + inward * cell * 0.18f,
                new Color(_palette.BoardEdge, 0.32f), 1.4f, true);
        }
    }

    private void DrawBoardFrame(Vector2[] outline)
    {
        var closed = outline.Append(outline[0]).ToArray();
        DrawPolyline(closed, _palette.BoardEdge.Darkened(0.34f), 8.5f, true);
        DrawPolyline(closed, _palette.BoardEdge.Lightened(0.2f), 4.2f, true);
        var inner = outline.Select(point => point.Lerp(_projection.Center, 0.018f)).ToArray();
        DrawPolyline(inner.Append(inner[0]).ToArray(), new Color(_palette.BoardEdge, 0.72f), 1.5f, true);
    }

    internal IReadOnlyList<(Vector2 From, Vector2 To)> PalaceSegments(int kingdom)
    {
        var center = Point(kingdom, 4, 1);
        return
        [
            (Point(kingdom, 3, 0), center), (center, Point(kingdom, 5, 2)),
            (Point(kingdom, 5, 0), center), (center, Point(kingdom, 3, 2))
        ];
    }

    internal static bool IsTerritoryBoundaryEdge(BoardEdge edge) =>
        KingdomSquare.FromCell(edge.A).Region != KingdomSquare.FromCell(edge.B).Region;

    internal static string? TerritoryBoundaryName(Kingdom first, Kingdom second) =>
        (Math.Min((int)first, (int)second), Math.Max((int)first, (int)second)) switch
        {
            (0, 1) => "江淮",
            (0, 2) => "秦岭",
            (1, 2) => "三峡",
            _ => null
        };

    private void DrawTerritoryBoundary(Kingdom first, Kingdom second, string name)
    {
        var bridges = ThreeKingdomsTopology.Instance.Edges
            .Where(IsTerritoryBoundaryEdge)
            .Where(edge =>
            {
                var a = KingdomSquare.FromCell(edge.A).Region;
                var b = KingdomSquare.FromCell(edge.B).Region;
                return TerritoryBoundaryName(a, b) == name;
            })
            .Select(edge => (_projection.Point(edge.A) + _projection.Point(edge.B)) * 0.5f)
            .ToArray();
        if (bridges.Length == 0 || TerritoryBoundaryName(first, second) != name) return;

        var center = bridges.Aggregate(Vector2.Zero, (sum, point) => sum + point) / bridges.Length;
        var fontSize = Math.Max(15, (int)(_projection.CellSize * 0.43f));
        var ink = _palette.BoardEdge.Darkened(0.2f);
        Text(name, center + new Vector2(-1, 0), fontSize, ink);
        Text(name, center + new Vector2(1, 0), fontSize, ink);
        Text(name, center + new Vector2(0, -1), fontSize, ink);
        Text(name, center + new Vector2(0, 1), fontSize, ink);
        Text(name, center, fontSize, ink);
    }

    private void BeginMoveAnimation(KingdomSnapshot before, KingdomSnapshot after, KingdomMove move)
    {
        var moving = before.Pieces.FirstOrDefault(piece => piece.Cell == move.From);
        var landed = after.Pieces.FirstOrDefault(piece => piece.Cell == move.To);
        if (moving is null || landed is null || moving.Piece.Id != landed.Piece.Id)
        {
            _moveAnimation = null;
            return;
        }
        var captured = before.Pieces.FirstOrDefault(piece => piece.Cell == move.To);
        var distance = _projection.Point(move.From).DistanceTo(_projection.Point(move.To)) /
            Math.Max(1, _projection.CellSize);
        _selected = null;
        _legal = Array.Empty<BoardCellId>();
        var duration = PieceMotion.Duration(_moveDurationMs, distance);
        _moveAnimation = new(landed, captured is not null, move.From, move.To, duration);
    }

    private void DrawAnimatedMove(MoveAnimation animation)
    {
        var raw = Mathf.Clamp(animation.Elapsed / animation.Duration, 0, 1);
        var from = _projection.Point(animation.From);
        var to = _projection.Point(animation.To);
        var center = PieceMotion.Position(from, to, raw, _projection.CellSize);
        Piece(animation.Moving, false, center, 1, 1.055f);
    }
    private Vector2 Point(int k, int f, int r) => _projection.Point(new(45 * k + 9 * r + f));
    private void Piece(KingdomPlacement placement, bool selected, Vector2? center = null, float alpha = 1, float scale = 1)
    {
        var cell = _projection.CellSize; var p = center ?? _projection.Point(placement.Cell);
        var radius = cell * 0.405f * scale;
        DrawCircle(p + Vector2.Down * cell * 0.07f, radius, new Color(0, 0, 0, (selected ? 0.29f : 0.16f) * alpha), true, -1, true);
        if (selected) p -= Vector2.Down * SelectionLift(cell);
        var owner = placement.Piece.Owner;
        var baseInk = owner is { } k ? _palette.KingdomPieceColors[(int)k] : _palette.NeutralInk;
        var ink = new Color(baseInk, alpha);
        var fill = owner.HasValue ? _palette.PieceFill : _palette.NeutralPieceFill;
        DrawCircle(p, radius, new Color(fill, alpha), true, -1, true);
        DrawArc(p, radius, 0, Mathf.Tau, 40, ink, 2f, true);
        DrawArc(p, radius * 0.82f, 0, Mathf.Tau, 40, new Color(baseInk, 0.35f * alpha), 1, true);
        var glyph = placement.Piece.Kind.Value switch
        { "general" => "将", "advisor" => "士", "elephant" => "象", "horse" => "马", "chariot" => "车", "cannon" => "炮", "soldier" => "兵", _ => "?" };
        Text(glyph, p, Math.Max(12, (int)(cell * 0.59f)), ink);
        if (!owner.HasValue) DrawLine(p + new Vector2(-radius, radius), p + new Vector2(radius, -radius), new Color(ink, 0.7f), 1, true);
    }
    private float SelectionLift(float cell) => _animated
        ? cell * (0.075f + 0.012f * Mathf.Sin((float)_selectionTime * 4.2f))
        : 0;
    private void Text(string text, Vector2 center, int size, Color color)
    {
        var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        var y = (_font.GetAscent(size) - _font.GetDescent(size)) * 0.5f;
        DrawString(_font, center + new Vector2(-width / 2, y), text, HorizontalAlignment.Left, -1, size, color);
    }
    public override void _ExitTree()
    {
        _spring.Material = null; _spring.Texture = null;
        _themeLease?.Dispose(); _font.Dispose();
    }

    private sealed class MoveAnimation(
        KingdomPlacement moving,
        bool captured,
        BoardCellId from,
        BoardCellId to,
        float duration)
    {
        public KingdomPlacement Moving { get; } = moving;
        public bool Captured { get; } = captured;
        public BoardCellId From { get; } = from;
        public BoardCellId To { get; } = to;
        public float Duration { get; } = duration;
        public float Elapsed { get; set; }
    }
}
