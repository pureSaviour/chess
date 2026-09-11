using Godot;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Effects;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation;

/// <summary>
/// Godot rendering adapter. It draws immutable snapshots, owns only transient visual state
/// (selection pulse and move interpolation), and reports square clicks. Rule validation and
/// authoritative position changes remain outside this node.
/// </summary>
public partial class BoardView : Control
{
    private readonly IBoardThemeCatalog _themes;
    private readonly SystemFont _pieceFont = new()
    {
        FontNames = ["Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Noto Sans CJK SC"]
    };

    private readonly ColorRect _skinBackground = new()
    {
        MouseFilter = MouseFilterEnum.Ignore,
        ShowBehindParent = true,
        Visible = false
    };
    private ThemeResourceLease? _themeLease;
    private ShaderMaterial? _boardMaterial;
    private readonly BoardFeedbackLayer _feedback = new();
    private readonly ThemePieceLayer _pieceLayer;
    private ChessMove? _lastMove;
    private int _lastMovePly = -1;
    private bool _feedbackAnimated = true;
    // Read-only presentation state used by the engine-level regression harness.
    internal ChessMove? VisibleLastMove => _moveAnimation is null ? _lastMove : null;
    internal BoardThemePalette ActivePalette => _palette;
    internal bool UsingPieceAtlas => _pieceLayer.IsActive;
    internal bool HasThemedBoardMaterial => _boardMaterial is not null;
    internal bool HasRevealTransition => _moveAnimation?.RevealedPiece is not null;
    private GameSnapshot? _snapshot;
    private BoardPosition? _selected;
    private HashSet<BoardPosition> _legalDestinations = [];
    private ChessMove? _analysisMove;
    private Vector2 _origin;
    private float _cellSize;
    private PieceSide _bottomSide = PieceSide.Red;
    private MoveAnimation? _moveAnimation;
    private double _selectionTime;
    private BoardThemePalette _palette;
    private bool _showCoordinates = true;
    private IReadOnlyDictionary<int, PieceKind>? _coveredIdentityHints;
    private bool _showCoveredIdentityHints;
    private bool _coveredPieceMode;
    private int _moveDurationMs;
    private int _revealDurationMs = 280;

    public event Action<BoardPosition>? SquarePressed;
    public event Action<bool>? MoveAnimationFinished;
    public bool IsAnimating => _moveAnimation is not null;

    public PieceSide BottomSide
    {
        get => _bottomSide;
        set
        {
            _bottomSide = value;
            QueueRedraw();
        }
    }

    public BoardView(IBoardThemeCatalog? themes = null)
    {
        _themes = themes ?? BoardThemeCatalog.Default;
        _palette = _themes.Resolve(BoardSkinKind.Classic).Palette;
        CustomMinimumSize = new Vector2(620, 660);
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.All;
        _pieceLayer = new ThemePieceLayer(BuildAtlasCommands);
        AddChild(_skinBackground);
        AddChild(_pieceLayer);
        AddChild(_feedback);
    }

    public void SetFeedbackAnimated(bool enabled)
    {
        _feedbackAnimated = enabled;
        _feedback.SetAnimated(enabled);
        SetProcess(_moveAnimation is not null || (_selected is not null && _feedbackAnimated));
        QueueRedraw();
    }

    public void ApplyAppearance(BoardSkinKind skin, bool showCoordinates) =>
        ApplyAppearance(skin == BoardSkinKind.Spring ? "spring" : "classic", showCoordinates);

    /// <summary>Applies one compiled presentation theme without changing game or input state.</summary>
    public void ApplyAppearance(string? themeId, bool showCoordinates)
    {
        var theme = _themes.Resolve(themeId);
        ThemeResourceLease? replacement = null;
        ShaderMaterial? replacementMaterial = null;
        try
        {
            if (theme.Boards.TryGetValue(ThemeBoardMode.Classic, out var assets) &&
                (assets.Texture is not null || assets.Shader is not null))
            {
                replacement = new ThemeResourceLease();
                var texture = assets.Texture is null ? null : replacement.LoadTexture(assets.Texture);
                replacementMaterial = assets.Shader is not null
                    ? replacement.CreateMaterial(assets.Shader)
                    : replacement.CreateTextureMaterial(texture!);
                if (texture is not null) replacementMaterial.SetShaderParameter("theme_texture", texture);
            }
        }
        catch (Exception error)
        {
            replacement?.Dispose();
            GD.PushWarning($"主题 {theme.Id} 的经典棋盘资源加载失败，已回退 classic：{error.Message}");
            theme = _themes.Resolve("classic");
            replacement = null; replacementMaterial = null;
        }
        _skinBackground.Material = null;
        _themeLease?.Dispose();
        _themeLease = replacement;
        _boardMaterial = replacementMaterial;
        _skinBackground.Material = _boardMaterial;
        _palette = theme.Palette;
        _moveDurationMs = theme.Effects.GetValueOrDefault(ThemeEffectSlot.Move)?.DurationMs ?? 0;
        // A reveal is a landing presentation, so themes can tune it without adding a Jieqi-only
        // manifest slot. Clamp prevents a looping landing shader's long pulse from stalling turns.
        _revealDurationMs = Math.Clamp(
            theme.Effects.GetValueOrDefault(ThemeEffectSlot.Landing)?.DurationMs ?? 280, 180, 420);
        _showCoordinates = showCoordinates;
        _skinBackground.Visible = _boardMaterial is not null;
        _feedback.ApplyTheme(theme);
        _pieceLayer.ApplyTheme(theme);
        QueueRedraw();
    }

    public void SetPosition(
        GameSnapshot snapshot,
        BoardPosition? selected = null,
        IEnumerable<BoardPosition>? legalDestinations = null)
    {
        // Immediate state changes (undo, restart, resync) supersede any in-flight cosmetic
        // transition. A normal move calls AnimateMove directly and therefore is unaffected.
        // Selection/offer refreshes at the same ply retain the last committed move; a changed
        // position without a move event (undo, restart, resync) must not leave stale markers.
        if (snapshot.PlyNumber != _lastMovePly) _lastMove = null;
        _moveAnimation = null;
        _snapshot = snapshot;
        _selected = selected;
        _legalDestinations = legalDestinations?.ToHashSet() ?? [];
        SetProcess(_selected is not null && _feedbackAnimated);
        QueueRedraw();
    }

    /// <summary>
    /// Enables Jieqi's covered-piece renderer. Actual identities are accepted only for replay
    /// overlays; live callers should pass null so secret match state never enters presentation.
    /// </summary>
    public void SetCoveredIdentityHints(IReadOnlyDictionary<int, PieceKind>? hints, bool visible)
    {
        _coveredPieceMode = true;
        _coveredIdentityHints = hints;
        _showCoveredIdentityHints = visible && hints is not null;
        _pieceLayer.Visible = false;
        QueueRedraw();
    }

    /// <summary>Sets a replay-only best-move arrow; it never changes selection or game state.</summary>
    public void SetAnalysisMove(ChessMove? move)
    {
        _analysisMove = move is { From.IsInside: true, To.IsInside: true } ? move : null;
        QueueRedraw();
    }

    public void AnimateMove(GameSnapshot before, GameSnapshot after, ChessMove move, bool captured)
    {
        var moving = before.Pieces.FirstOrDefault(item => item.Position == move.From);
        if (moving.Piece.Kind.Value is null)
        {
            SetPosition(after);
            MoveAnimationFinished?.Invoke(captured);
            return;
        }

        var landed = after.Pieces.FirstOrDefault(item =>
            item.Position == move.To && item.Piece.PieceInstanceId == moving.Piece.PieceInstanceId);
        var revealedPiece = moving.Piece.Kind == BuiltInPieceKinds.Covered &&
                            landed.Piece.Kind.Value is not null &&
                            landed.Piece.Kind != BuiltInPieceKinds.Covered
            ? landed.Piece
            : (ChessPiece?)null;
        var distance = Math.Abs(move.To.File - move.From.File) + Math.Abs(move.To.Rank - move.From.Rank);
        _snapshot = after;
        _lastMove = move;
        _lastMovePly = after.PlyNumber;
        _selected = null;
        _legalDestinations = [];
        var duration = PieceMotion.Duration(_moveDurationMs, distance);
        // A covered piece translates and turns over on one shared timeline. Extending the move
        // to the themed landing duration keeps both motions concurrent instead of appending a
        // second phase after arrival.
        if (revealedPiece is not null)
            duration = Mathf.Max(duration, Math.Min(0.30f, _revealDurationMs / 1000f * 0.72f));
        _moveAnimation = new MoveAnimation(
            moving.Piece, move.From, move.To, duration, captured,
            revealedPiece);
        SetProcess(true);
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
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
        SetProcess(_moveAnimation is not null || (_selected is not null && _feedbackAnimated));
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;

        var mouse = GetLocalMousePosition();
        if (_cellSize <= 0) return;
        var screenFile = Mathf.RoundToInt((mouse.X - _origin.X) / _cellSize);
        var screenRank = Mathf.RoundToInt((mouse.Y - _origin.Y) / _cellSize);
        var position = _bottomSide == PieceSide.Red
            ? new BoardPosition(screenFile, 9 - screenRank)
            : new BoardPosition(8 - screenFile, screenRank);
        if (!position.IsInside) return;

        var point = ToScreen(position);
        if (mouse.DistanceTo(point) > _cellSize * 0.46f) return;
        SquarePressed?.Invoke(position);
        AcceptEvent();
    }

    public override void _Draw()
    {
        CalculateGeometry();
        if (_cellSize <= 0) return;

        UpdateFeedbackGeometry();
        var padding = new Vector2(_cellSize * 0.48f, _cellSize * 0.90f);
        var boardRect = new Rect2(
            _origin - padding,
            new Vector2(_cellSize * 8, _cellSize * 9) + padding * 2);
        if (_boardMaterial is null)
        {
            _skinBackground.Visible = false;
            DrawRect(boardRect, _palette.BoardFill, true);
        }
        else
        {
            _skinBackground.Visible = true;
            _skinBackground.Position = boardRect.Position;
            _skinBackground.Size = boardRect.Size;
        }
        DrawRect(boardRect, _palette.BoardEdge, false, Mathf.Max(4, _cellSize * 0.07f), true);

        DrawGrid();
        DrawSelectionAndTargets();
        DrawAnalysisArrow();
        if (_snapshot is not null)
        {
            if (_pieceLayer.IsActive && !_coveredPieceMode) { _pieceLayer.QueueRedraw(); return; }
            var animatedId = _moveAnimation?.Piece.PieceInstanceId;
            var selectedId = _selected is { } selected
                ? _snapshot.Pieces.FirstOrDefault(item => item.Position == selected).Piece.PieceInstanceId
                : -1;
            foreach (var placed in _snapshot.Pieces)
            {
                if (placed.Piece.PieceInstanceId == animatedId || placed.Piece.PieceInstanceId == selectedId)
                    continue;
                DrawPiece(placed.Piece, ToScreen(placed.Position));
            }

            if (_moveAnimation is not null) DrawAnimatedMove(_moveAnimation);
            else if (_selected is { } selectedPosition)
            {
                var selectedPiece = _snapshot.Pieces.FirstOrDefault(item => item.Position == selectedPosition);
                if (selectedPiece.Piece.Kind.Value is not null)
                    DrawPiece(selectedPiece.Piece,
                        ToScreen(selectedPosition) - new Vector2(0, SelectionLift()), 1, 1.045f);
            }
        }
    }

    private void CalculateGeometry()
    {
        // Reserve independent route-number bands beyond the outer pieces and their glow.
        var usable = Size - new Vector2(16, 16);
        _cellSize = Mathf.Max(0, Mathf.Min(usable.X / 8.96f, usable.Y / 10.8f));
        var boardSize = new Vector2(_cellSize * 8, _cellSize * 9);
        _origin = (Size - boardSize) / 2f;
    }

    private void DrawGrid()
    {
        var lineWidth = Mathf.Max(1.5f, _cellSize * 0.022f);
        for (var screenRank = 0; screenRank < 10; screenRank++)
        {
            var y = _origin.Y + screenRank * _cellSize;
            DrawLine(new Vector2(_origin.X, y), new Vector2(_origin.X + 8 * _cellSize, y),
                _palette.GridInk, lineWidth, true);
        }

        for (var file = 0; file < 9; file++)
        {
            var x = _origin.X + file * _cellSize;
            if (file is 0 or 8)
            {
                DrawLine(new Vector2(x, _origin.Y), new Vector2(x, _origin.Y + 9 * _cellSize),
                    _palette.GridInk, lineWidth, true);
            }
            else
            {
                DrawLine(new Vector2(x, _origin.Y), new Vector2(x, _origin.Y + 4 * _cellSize),
                    _palette.GridInk, lineWidth, true);
                DrawLine(new Vector2(x, _origin.Y + 5 * _cellSize),
                    new Vector2(x, _origin.Y + 9 * _cellSize), _palette.GridInk, lineWidth, true);
            }
        }

        DrawLine(ToScreen(new BoardPosition(3, 0)), ToScreen(new BoardPosition(5, 2)), _palette.GridInk, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(5, 0)), ToScreen(new BoardPosition(3, 2)), _palette.GridInk, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(3, 7)), ToScreen(new BoardPosition(5, 9)), _palette.GridInk, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(5, 7)), ToScreen(new BoardPosition(3, 9)), _palette.GridInk, lineWidth, true);

        var riverFontSize = Mathf.RoundToInt(_cellSize * 0.38f);
        var riverY = _origin.Y + 4.65f * _cellSize;
        var leftRiver = _bottomSide == PieceSide.Red ? "楚 河" : "汉 界";
        var rightRiver = _bottomSide == PieceSide.Red ? "汉 界" : "楚 河";
        DrawString(_pieceFont, new Vector2(_origin.X + 0.55f * _cellSize, riverY), leftRiver,
            HorizontalAlignment.Center, 2.4f * _cellSize, riverFontSize, _palette.GridInk);
        DrawString(_pieceFont, new Vector2(_origin.X + 5.05f * _cellSize, riverY), rightRiver,
            HorizontalAlignment.Center, 2.4f * _cellSize, riverFontSize, _palette.GridInk);
        if (_showCoordinates) DrawCoordinates();
    }

    private void DrawCoordinates()
    {
        var fontSize = Mathf.Max(12, Mathf.RoundToInt(_cellSize * 0.22f));
        var topSide = _bottomSide.Opponent();
        var topY = _origin.Y - _cellSize * 0.55f;
        var bottomY = _origin.Y + _cellSize * 9.75f;
        var color = new Color(_palette.GridInk, 0.72f);
        for (var screenFile = 0; screenFile < BoardState.FileCount; screenFile++)
        {
            var x = _origin.X + screenFile * _cellSize - _cellSize * 0.25f;
            DrawString(_pieceFont, new Vector2(x, topY), RouteLabel(topSide, screenFile + 1),
                HorizontalAlignment.Center, _cellSize * 0.5f, fontSize, color);
            DrawString(_pieceFont, new Vector2(x, bottomY), RouteLabel(_bottomSide, 9 - screenFile),
                HorizontalAlignment.Center, _cellSize * 0.5f, fontSize, color);
        }
    }

    private static string RouteLabel(PieceSide side, int route)
    {
        if (side == PieceSide.Black) return route.ToString();
        string[] redRoutes = ["一", "二", "三", "四", "五", "六", "七", "八", "九"];
        return redRoutes[route - 1];
    }

    private void DrawSelectionAndTargets()
    {
        foreach (var target in _legalDestinations)
        {
            var hasPiece = _snapshot?.Pieces.Any(p => p.Position == target) == true;
            if (hasPiece)
                DrawArc(ToScreen(target), _cellSize * 0.43f, 0, Mathf.Tau, 40, _palette.LegalMove,
                    Mathf.Max(3, _cellSize * 0.07f), true);
            else
                DrawCircle(ToScreen(target), Mathf.Max(5, _cellSize * 0.10f), _palette.LegalMove);
        }
    }

    private void DrawAnimatedMove(MoveAnimation animation)
    {
        var raw = Mathf.Clamp(animation.Elapsed / animation.Duration, 0, 1);
        var from = ToScreen(animation.From);
        var to = ToScreen(animation.To);
        var center = PieceMotion.Position(from, to, raw, _cellSize);
        if (animation.RevealedPiece is { } revealed)
        {
            var horizontalScale = Mathf.Max(0.035f, Mathf.Abs(Mathf.Cos(raw * Mathf.Pi)));
            DrawPiece(raw < 0.5f ? animation.Piece : revealed,
                center, 1, 1.055f, horizontalScale);
        }
        else DrawPiece(animation.Piece, center, 1, 1.055f);
    }

    private void DrawPiece(
        ChessPiece piece, Vector2 center, float alpha = 1, float scale = 1, float horizontalScale = 1)
    {
        var transformed = !Mathf.IsEqualApprox(horizontalScale, 1);
        if (transformed)
        {
            DrawSetTransform(center, 0, new Vector2(horizontalScale, 1));
            center = Vector2.Zero;
        }
        var radius = _cellSize * 0.39f * scale;
        var covered = piece.Kind == BuiltInPieceKinds.Covered;
        var sideColor = new Color(covered
            ? _palette.GridInk
            : _palette.ClassicPieceColors[piece.Side == PieceSide.Red ? 0 : 1], alpha);
        DrawCircle(center + new Vector2(2, 3 + (scale - 1) * 30), radius,
            new Color(0, 0, 0, 0.28f * alpha));
        DrawCircle(center, radius, new Color(_palette.PieceFill, alpha));
        // These circles are redrawn each animation frame. Thirty-two segments remain visually
        // smooth at the supported board size while substantially reducing transient draw work.
        DrawArc(center, radius, 0, Mathf.Tau, 32, sideColor, Mathf.Max(2, _cellSize * 0.045f), true);
        DrawArc(center, radius * 0.82f, 0, Mathf.Tau, 32, sideColor, Mathf.Max(1, _cellSize * 0.018f), true);

        if (covered)
        {
            DrawArc(center, radius * 0.68f, 0, Mathf.Tau, 32,
                new Color(sideColor, 0.76f), Mathf.Max(1, _cellSize * 0.012f), true);
            for (var index = 0; index < 8; index++)
            {
                var angle = index * Mathf.Tau / 8f;
                DrawCircle(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * 0.75f,
                    Mathf.Max(1.2f, radius * 0.035f), new Color(sideColor, 0.82f));
            }
        }

        var label = PieceLabel(piece);
        var fontSize = Mathf.RoundToInt(_cellSize * 0.48f);
        if (label.Length > 0)
            DrawString(_pieceFont, center + new Vector2(-radius, fontSize * 0.35f), label,
                HorizontalAlignment.Center, radius * 2, fontSize, sideColor);
        if (piece.Kind == BuiltInPieceKinds.Covered && _showCoveredIdentityHints &&
            _coveredIdentityHints?.TryGetValue(piece.PieceInstanceId, out var actual) == true)
        {
            var hintPiece = piece with { Kind = actual };
            var hintSize = Mathf.RoundToInt(_cellSize * 0.34f);
            var hintColor = new Color(_palette.ClassicPieceColors[piece.Side == PieceSide.Red ? 0 : 1], 0.38f * alpha);
            DrawString(_pieceFont, center + new Vector2(-radius, hintSize * 0.35f), PieceLabel(hintPiece),
                HorizontalAlignment.Center, radius * 2, hintSize, hintColor);
        }
        if (transformed) DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private float SelectionLift() => _feedbackAnimated
        ? _cellSize * (0.075f + 0.012f * Mathf.Sin((float)_selectionTime * 4.2f))
        : 0;

    private void UpdateFeedbackGeometry()
    {
        Vector2? selected = _selected is { } square
            ? ToScreen(square) - new Vector2(0, SelectionLift()) : null;
        var move = VisibleLastMove;
        _feedback.SetMarkers(selected,
            move is { } last ? ToScreen(last.From) : null,
            move is { } landed ? ToScreen(landed.To) : null,
            _cellSize);
    }

    private Vector2 ToScreen(BoardPosition position) =>
        _bottomSide == PieceSide.Red
            ? _origin + new Vector2(position.File * _cellSize, (9 - position.Rank) * _cellSize)
            : _origin + new Vector2((8 - position.File) * _cellSize, position.Rank * _cellSize);

    internal static string PieceLabel(ChessPiece piece) => piece.Kind.Value switch
    {
        "general" => piece.Side == PieceSide.Red ? "帅" : "将",
        "advisor" => piece.Side == PieceSide.Red ? "仕" : "士",
        "elephant" => piece.Side == PieceSide.Red ? "相" : "象",
        "horse" => "马",
        "chariot" => "车",
        "cannon" => piece.Side == PieceSide.Red ? "炮" : "砲",
        "soldier" => piece.Side == PieceSide.Red ? "兵" : "卒",
        "covered" => string.Empty,
        _ => "?"
    };

    public override void _ExitTree()
    {
        _skinBackground.Material = null;
        _themeLease?.Dispose();
        _pieceFont.Dispose();
    }

    private void DrawAnalysisArrow()
    {
        if (_analysisMove is not { } move) return;
        var from = ToScreen(move.From); var to = ToScreen(move.To); var vector = to - from;
        if (vector.LengthSquared() < 1) return;
        var direction = vector.Normalized(); var end = to - direction * _cellSize * .28f;
        var color = new Color(0.38f, 0.82f, 0.30f, 0.88f); var width = Mathf.Max(5, _cellSize * .10f);
        DrawLine(from + direction * _cellSize * .22f, end, color, width, true);
        var normal = new Vector2(-direction.Y, direction.X); var head = Mathf.Max(13, _cellSize * .24f);
        DrawColoredPolygon([to, end - direction * head + normal * head * .55f, end - direction * head - normal * head * .55f], color);
    }

    private IReadOnlyList<ThemePieceDrawCommand> BuildAtlasCommands()
    {
        if (_snapshot is null || _cellSize <= 0 || _coveredPieceMode)
            return Array.Empty<ThemePieceDrawCommand>();
        List<ThemePieceDrawCommand> commands = [];
        var animatedId = _moveAnimation?.Piece.PieceInstanceId;
        var selectedId = _selected is { } selected
            ? _snapshot.Pieces.FirstOrDefault(item => item.Position == selected).Piece.PieceInstanceId : -1;
        foreach (var placed in _snapshot.Pieces)
            if (placed.Piece.PieceInstanceId != animatedId && placed.Piece.PieceInstanceId != selectedId)
                commands.Add(AtlasCommand(placed.Piece, ToScreen(placed.Position)));
        if (_moveAnimation is { } animation)
        {
            var raw = Mathf.Clamp(animation.Elapsed / animation.Duration, 0, 1);
            var center = PieceMotion.Position(ToScreen(animation.From), ToScreen(animation.To), raw, _cellSize);
            commands.Add(AtlasCommand(animation.Piece, center, 1, 1.055f));
        }
        else if (_selected is { } selectedPosition)
        {
            var selectedPiece = _snapshot.Pieces.FirstOrDefault(item => item.Position == selectedPosition);
            if (selectedPiece.Piece.Kind.Value is not null)
                commands.Add(AtlasCommand(selectedPiece.Piece,
                    ToScreen(selectedPosition) - Vector2.Down * SelectionLift(), 1, 1.045f));
        }
        return commands;
    }

    private ThemePieceDrawCommand AtlasCommand(ChessPiece piece, Vector2 center, float alpha = 1, float scale = 1) =>
        new(PieceColumn(piece.Kind.Value), piece.Side == PieceSide.Red ? 0 : 1, center,
            _cellSize * 0.82f * scale, new Color(1, 1, 1, alpha));

    internal static int PieceColumn(string? kind) => kind switch
    {
        "general" => 0, "advisor" => 1, "elephant" => 2, "horse" => 3,
        "chariot" => 4, "cannon" => 5, "soldier" => 6, _ => -1
    };

    private sealed class MoveAnimation(
        ChessPiece piece,
        BoardPosition from,
        BoardPosition to,
        float duration,
        bool captured,
        ChessPiece? revealedPiece)
    {
        public ChessPiece Piece { get; } = piece;
        public BoardPosition From { get; } = from;
        public BoardPosition To { get; } = to;
        public float Duration { get; } = duration;
        public bool Captured { get; } = captured;
        public ChessPiece? RevealedPiece { get; } = revealedPiece;
        public float Elapsed { get; set; }
    }
}
