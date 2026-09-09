using Xiangqi.Core.Domain.Rules;

namespace Xiangqi.Core.Domain;

/// <summary>
/// Authoritative aggregate for a match. All command sources (Godot UI, LAN peers, replay or
/// a future Steam adapter) must submit moves here; none may mutate the board directly.
/// This single validation boundary keeps local and network play deterministic.
/// </summary>
public sealed class ChessGame
{
    private readonly PieceRuleRegistry _rules;
    private readonly Stack<Memento> _history = [];
    private BoardState _board;
    private PieceSide _sideToMove = PieceSide.Red;
    private bool _isInCheck;
    private bool _isFinished;
    private PieceSide? _winner;
    private GameEndReason _endReason;
    private int _plyNumber;

    public ChessGame(BoardState board, PieceRuleRegistry rules, PieceSide sideToMove = PieceSide.Red)
    {
        _board = board.Clone();
        _rules = rules;
        _sideToMove = sideToMove;
        _isInCheck = IsInCheck(_board, _sideToMove);
    }

    public static ChessGame CreateStandard(PieceRuleRegistry? rules = null) =>
        new(StandardPosition.Create(), rules ?? PieceRuleRegistry.CreateStandard());

    /// <summary>
    /// Recreates a playable aggregate from an immutable position. Terminal/result flags are not
    /// trusted: callers import a position and must replay moves or declare an outcome through the
    /// normal command methods. Piece ids and occupancy are preserved.
    /// </summary>
    public static ChessGame FromPosition(GameSnapshot position, PieceRuleRegistry? rules = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Pieces.Any(item => !item.Position.IsInside) ||
            position.Pieces.Select(item => item.Position).Distinct().Count() != position.Pieces.Count ||
            position.Pieces.Select(item => item.Piece.PieceInstanceId).Distinct().Count() != position.Pieces.Count)
            throw new ArgumentException("Position contains an invalid square or duplicate identity.", nameof(position));
        if (position.Pieces.Count(item => item.Piece.Kind == BuiltInPieceKinds.General && item.Piece.Side == PieceSide.Red) != 1 ||
            position.Pieces.Count(item => item.Piece.Kind == BuiltInPieceKinds.General && item.Piece.Side == PieceSide.Black) != 1)
            throw new ArgumentException("A Xiangqi position must contain exactly one general for each side.", nameof(position));
        return new ChessGame(new BoardState(position.Pieces), rules ?? PieceRuleRegistry.CreateStandard(), position.SideToMove)
        {
            _plyNumber = Math.Max(0, position.PlyNumber)
        };
    }

    /// <summary>Creates an isolated analysis copy. Mutating it cannot change the authoritative match.</summary>
    public ChessGame Fork() => new(_board, _rules, _sideToMove)
    {
        _isInCheck = _isInCheck,
        _isFinished = _isFinished,
        _winner = _winner,
        _endReason = _endReason,
        _plyNumber = _plyNumber
    };

    public GameSnapshot Snapshot => new(
        _board.GetPieces().OrderBy(p => p.Position.Rank).ThenBy(p => p.Position.File).ToArray(),
        _sideToMove, _isInCheck, _isFinished, _winner, _endReason, _plyNumber);

    public bool CanUndo => _history.Count > 0;

    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from)
    {
        if (_isFinished || _board.GetPiece(from) is not { } piece || piece.Side != _sideToMove)
            return [];

        var destinations = new List<BoardPosition>();
        for (var rank = 0; rank < BoardState.RankCount; rank++)
        for (var file = 0; file < BoardState.FileCount; file++)
        {
            var to = new BoardPosition(file, rank);
            if (ValidateMove(_board, _sideToMove, new ChessMove(from, to)) == MoveRejection.None)
                destinations.Add(to);
        }
        return destinations;
    }

    public MoveResult TryMove(ChessMove move)
    {
        if (_isFinished) return MoveResult.Rejected(move, MoveRejection.GameAlreadyFinished);
        var rejection = ValidateMove(_board, _sideToMove, move);
        if (rejection != MoveRejection.None) return MoveResult.Rejected(move, rejection);

        _history.Push(CaptureMemento());
        var moved = _board.GetPiece(move.From)!.Value;
        var captured = _board.Move(move.From, move.To);
        _plyNumber++;

        if (captured is { Kind: var capturedKind } && capturedKind == BuiltInPieceKinds.General)
        {
            _isFinished = true;
            _winner = moved.Side;
            _endReason = GameEndReason.GeneralCaptured;
            _isInCheck = false;
            return new MoveResult(true, move, moved, captured, MoveRejection.None);
        }

        _sideToMove = _sideToMove.Opponent();
        _isInCheck = IsInCheck(_board, _sideToMove);
        if (!HasAnyLegalMove(_board, _sideToMove))
        {
            _isFinished = true;
            _winner = _sideToMove.Opponent();
            _endReason = _isInCheck ? GameEndReason.Checkmate : GameEndReason.Stalemate;
        }

        return new MoveResult(true, move, moved, captured, MoveRejection.None);
    }

    public bool Undo()
    {
        if (!_history.TryPop(out var previous)) return false;
        _board = previous.Board;
        _sideToMove = previous.SideToMove;
        _isInCheck = previous.IsInCheck;
        _isFinished = previous.IsFinished;
        _winner = previous.Winner;
        _endReason = previous.EndReason;
        _plyNumber = previous.PlyNumber;
        return true;
    }

    public bool DeclareLoss(PieceSide loser, GameEndReason reason)
    {
        if (_isFinished) return false;
        if (reason != GameEndReason.Timeout)
            throw new ArgumentException("The supplied reason is not an external loss condition.", nameof(reason));
        _history.Push(CaptureMemento());
        _isFinished = true;
        _winner = loser.Opponent();
        _endReason = reason;
        _isInCheck = false;
        return true;
    }

    /// <summary>
    /// Finishes the authoritative match without a winner after both players agree to a draw.
    /// The previous state is retained as a memento so replay/admin tooling can still inspect or
    /// reverse the transition without mutating the board directly.
    /// </summary>
    public bool DeclareDraw()
    {
        if (_isFinished) return false;
        _history.Push(CaptureMemento());
        _isFinished = true;
        _winner = null;
        _endReason = GameEndReason.Draw;
        _isInCheck = false;
        return true;
    }

    private MoveRejection ValidateMove(BoardState board, PieceSide movingSide, ChessMove move)
    {
        if (!move.From.IsInside || !move.To.IsInside) return MoveRejection.OutsideBoard;
        var piece = board.GetPiece(move.From);
        if (piece is null) return MoveRejection.NoPieceAtSource;
        if (piece.Value.Side != movingSide) return MoveRejection.NotPlayersTurn;
        if (board.GetPiece(move.To) is { } target && target.Side == movingSide)
            return MoveRejection.OccupiedByFriendlyPiece;
        if (!_rules.Get(piece.Value.Kind).CanMove(board, piece.Value, move.From, move.To))
            return MoveRejection.PieceRuleViolation;

        var simulated = board.Clone();
        simulated.Move(move.From, move.To);
        return IsInCheck(simulated, movingSide)
            ? MoveRejection.WouldLeaveGeneralInCheck
            : MoveRejection.None;
    }

    private bool IsInCheck(BoardState board, PieceSide side)
    {
        var general = board.GetPieces().FirstOrDefault(item =>
            item.Piece.Side == side && item.Piece.Kind == BuiltInPieceKinds.General);
        if (general.Piece.Kind != BuiltInPieceKinds.General) return true;

        foreach (var enemy in board.GetPieces().Where(item => item.Piece.Side != side))
        {
            if (_rules.Get(enemy.Piece.Kind).CanMove(board, enemy.Piece, enemy.Position, general.Position))
                return true;
        }
        return false;
    }

    private bool HasAnyLegalMove(BoardState board, PieceSide side)
    {
        foreach (var source in board.GetPieces().Where(item => item.Piece.Side == side))
        for (var rank = 0; rank < BoardState.RankCount; rank++)
        for (var file = 0; file < BoardState.FileCount; file++)
        {
            if (ValidateMove(board, side, new ChessMove(source.Position, new BoardPosition(file, rank)))
                == MoveRejection.None) return true;
        }
        return false;
    }

    private Memento CaptureMemento() => new(
        _board.Clone(), _sideToMove, _isInCheck, _isFinished, _winner, _endReason, _plyNumber);

    private sealed record Memento(
        BoardState Board, PieceSide SideToMove, bool IsInCheck, bool IsFinished,
        PieceSide? Winner, GameEndReason EndReason, int PlyNumber);
}
