using Godot;
using Xiangqi.Core.Domain;

namespace Xiangqi.Presentation;

/// <summary>
/// Godot rendering adapter. It draws immutable snapshots, owns only transient visual state
/// (selection pulse and move interpolation), and reports square clicks. Rule validation and
/// authoritative position changes remain outside this node.
/// </summary>
public partial class BoardView : Control
{
    private static readonly Color BoardColor = new("#D6A45F");
    private static readonly Color BoardEdgeColor = new("#4D2D1B");
    private static readonly Color InkColor = new("#3A2115");
    private static readonly Color RedColor = new("#B6262D");
    private static readonly Color BlackColor = new("#202327");
    private static readonly Color HighlightColor = new("#F2D46B");
    private static readonly Color LegalColor = new("#2F7D62");
    private static readonly Color PieceFillColor = new("#F5D99A");

    private readonly SystemFont _pieceFont = new()
    {
        FontNames = ["Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Noto Sans CJK SC"]
    };

    private GameSnapshot? _snapshot;
    private BoardPosition? _selected;
    private HashSet<BoardPosition> _legalDestinations = [];
    private Vector2 _origin;
    private float _cellSize;
    private PieceSide _bottomSide = PieceSide.Red;
    private MoveAnimation? _moveAnimation;
    private double _selectionTime;

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

    public BoardView()
    {
        CustomMinimumSize = new Vector2(620, 660);
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.All;
    }

    public void SetPosition(
        GameSnapshot snapshot,
        BoardPosition? selected = null,
        IEnumerable<BoardPosition>? legalDestinations = null)
    {
        // Immediate state changes (undo, restart, resync) supersede any in-flight cosmetic
        // transition. A normal move calls AnimateMove directly and therefore is unaffected.
        _moveAnimation = null;
        _snapshot = snapshot;
        _selected = selected;
        _legalDestinations = legalDestinations?.ToHashSet() ?? [];
        SetProcess(_moveAnimation is not null || _selected is not null);
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

        var capturedPiece = captured
            ? before.Pieces.FirstOrDefault(item => item.Position == move.To)
            : (PlacedPiece?)null;
        var distance = Math.Abs(move.To.File - move.From.File) + Math.Abs(move.To.Rank - move.From.Rank);
        _snapshot = after;
        _selected = null;
        _legalDestinations = [];
        _moveAnimation = new MoveAnimation(
            moving.Piece, capturedPiece, move.From, move.To,
            Mathf.Clamp(0.18f + distance * 0.025f, 0.22f, 0.42f), captured);
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
        SetProcess(_moveAnimation is not null || _selected is not null);
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

        var padding = _cellSize * 0.48f;
        var boardRect = new Rect2(
            _origin - new Vector2(padding, padding),
            new Vector2(_cellSize * 8 + padding * 2, _cellSize * 9 + padding * 2));
        DrawRect(boardRect, BoardEdgeColor, true);
        DrawRect(boardRect.Grow(-Mathf.Max(4, _cellSize * 0.07f)), BoardColor, true);

        DrawGrid();
        DrawSelectionAndTargets();
        if (_snapshot is not null)
        {
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
        var usable = Size - new Vector2(48, 48);
        _cellSize = Mathf.Max(0, Mathf.Min(usable.X / 8f, usable.Y / 9f));
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
                InkColor, lineWidth, true);
        }

        for (var file = 0; file < 9; file++)
        {
            var x = _origin.X + file * _cellSize;
            if (file is 0 or 8)
            {
                DrawLine(new Vector2(x, _origin.Y), new Vector2(x, _origin.Y + 9 * _cellSize),
                    InkColor, lineWidth, true);
            }
            else
            {
                DrawLine(new Vector2(x, _origin.Y), new Vector2(x, _origin.Y + 4 * _cellSize),
                    InkColor, lineWidth, true);
                DrawLine(new Vector2(x, _origin.Y + 5 * _cellSize),
                    new Vector2(x, _origin.Y + 9 * _cellSize), InkColor, lineWidth, true);
            }
        }

        DrawLine(ToScreen(new BoardPosition(3, 0)), ToScreen(new BoardPosition(5, 2)), InkColor, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(5, 0)), ToScreen(new BoardPosition(3, 2)), InkColor, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(3, 7)), ToScreen(new BoardPosition(5, 9)), InkColor, lineWidth, true);
        DrawLine(ToScreen(new BoardPosition(5, 7)), ToScreen(new BoardPosition(3, 9)), InkColor, lineWidth, true);

        var riverFontSize = Mathf.RoundToInt(_cellSize * 0.38f);
        var riverY = _origin.Y + 4.65f * _cellSize;
        var leftRiver = _bottomSide == PieceSide.Red ? "楚 河" : "汉 界";
        var rightRiver = _bottomSide == PieceSide.Red ? "汉 界" : "楚 河";
        DrawString(_pieceFont, new Vector2(_origin.X + 0.55f * _cellSize, riverY), leftRiver,
            HorizontalAlignment.Center, 2.4f * _cellSize, riverFontSize, InkColor);
        DrawString(_pieceFont, new Vector2(_origin.X + 5.05f * _cellSize, riverY), rightRiver,
            HorizontalAlignment.Center, 2.4f * _cellSize, riverFontSize, InkColor);
    }

    private void DrawSelectionAndTargets()
    {
        if (_selected is { } selected)
        {
            var center = ToScreen(selected) - new Vector2(0, SelectionLift());
            var pulse = 1f + 0.035f * Mathf.Sin((float)_selectionTime * 5.2f);
            DrawCircle(center, _cellSize * 0.46f * pulse, new Color(HighlightColor, 0.48f));
            DrawArc(center, _cellSize * 0.47f * pulse, 0, Mathf.Tau, 48, RedColor,
                Mathf.Max(2, _cellSize * 0.045f), true);
        }

        foreach (var target in _legalDestinations)
        {
            var hasPiece = _snapshot?.Pieces.Any(p => p.Position == target) == true;
            if (hasPiece)
                DrawArc(ToScreen(target), _cellSize * 0.43f, 0, Mathf.Tau, 40, LegalColor,
                    Mathf.Max(3, _cellSize * 0.07f), true);
            else
                DrawCircle(ToScreen(target), Mathf.Max(5, _cellSize * 0.10f), LegalColor);
        }
    }

    private void DrawAnimatedMove(MoveAnimation animation)
    {
        var raw = Mathf.Clamp(animation.Elapsed / animation.Duration, 0, 1);
        var eased = raw * raw * (3f - 2f * raw);
        var from = ToScreen(animation.From);
        var to = ToScreen(animation.To);
        var center = from.Lerp(to, eased) - new Vector2(0, Mathf.Sin(raw * Mathf.Pi) * _cellSize * 0.13f);
        if (animation.CapturedPiece is { } captured && raw < 0.82f)
            DrawPiece(captured.Piece, to, Mathf.Clamp((0.82f - raw) / 0.32f, 0, 1));
        DrawPiece(animation.Piece, center, 1, 1.055f);
    }

    private void DrawPiece(ChessPiece piece, Vector2 center, float alpha = 1, float scale = 1)
    {
        var radius = _cellSize * 0.39f * scale;
        var sideColor = new Color(piece.Side == PieceSide.Red ? RedColor : BlackColor, alpha);
        DrawCircle(center + new Vector2(2, 3 + (scale - 1) * 30), radius,
            new Color(0, 0, 0, 0.28f * alpha));
        DrawCircle(center, radius, new Color(PieceFillColor, alpha));
        // These circles are redrawn each animation frame. Thirty-two segments remain visually
        // smooth at the supported board size while substantially reducing transient draw work.
        DrawArc(center, radius, 0, Mathf.Tau, 32, sideColor, Mathf.Max(2, _cellSize * 0.045f), true);
        DrawArc(center, radius * 0.82f, 0, Mathf.Tau, 32, sideColor, Mathf.Max(1, _cellSize * 0.018f), true);

        var label = GetPieceLabel(piece);
        var fontSize = Mathf.RoundToInt(_cellSize * 0.48f);
        DrawString(_pieceFont, center + new Vector2(-radius, fontSize * 0.35f), label,
            HorizontalAlignment.Center, radius * 2, fontSize, sideColor);
    }

    private float SelectionLift() =>
        _cellSize * (0.075f + 0.012f * Mathf.Sin((float)_selectionTime * 4.2f));

    private Vector2 ToScreen(BoardPosition position) =>
        _bottomSide == PieceSide.Red
            ? _origin + new Vector2(position.File * _cellSize, (9 - position.Rank) * _cellSize)
            : _origin + new Vector2((8 - position.File) * _cellSize, position.Rank * _cellSize);

    private static string GetPieceLabel(ChessPiece piece) => piece.Kind.Value switch
    {
        "general" => piece.Side == PieceSide.Red ? "帅" : "将",
        "advisor" => piece.Side == PieceSide.Red ? "仕" : "士",
        "elephant" => piece.Side == PieceSide.Red ? "相" : "象",
        "horse" => "马",
        "chariot" => "车",
        "cannon" => piece.Side == PieceSide.Red ? "炮" : "砲",
        "soldier" => piece.Side == PieceSide.Red ? "兵" : "卒",
        _ => "?"
    };

    private sealed class MoveAnimation(
        ChessPiece piece,
        PlacedPiece? capturedPiece,
        BoardPosition from,
        BoardPosition to,
        float duration,
        bool captured)
    {
        public ChessPiece Piece { get; } = piece;
        public PlacedPiece? CapturedPiece { get; } = capturedPiece;
        public BoardPosition From { get; } = from;
        public BoardPosition To { get; } = to;
        public float Duration { get; } = duration;
        public bool Captured { get; } = captured;
        public float Elapsed { get; set; }
    }
}
