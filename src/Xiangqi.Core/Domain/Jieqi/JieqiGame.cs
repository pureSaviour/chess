using Xiangqi.Core.Domain.Rules;

namespace Xiangqi.Core.Domain.Jieqi;

/// <summary>
/// One authoritative Jieqi piece. ActualKind is secret match state while CoverKind is the
/// classic starting-slot movement used by an unrevealed piece for its first move.
/// </summary>
public readonly record struct JieqiPiece(
    int PieceInstanceId, PieceSide Side, PieceKind ActualKind, PieceKind CoverKind, bool IsRevealed);

public readonly record struct PlacedJieqiPiece(BoardPosition Position, JieqiPiece Piece);

/// <summary>Immutable authoritative snapshot. Consumers must use ToPublicPosition for live UI/network output.</summary>
public sealed record JieqiSnapshot(
    IReadOnlyList<PlacedJieqiPiece> Pieces, PieceSide SideToMove, bool IsInCheck,
    bool IsFinished, PieceSide? Winner, GameEndReason EndReason, int PlyNumber)
{
    /// <summary>Redacts every unrevealed actual identity while preserving stable piece ids.</summary>
    public GameSnapshot ToPublicPosition() => new(
        Pieces.Select(item => new PlacedPiece(item.Position, new ChessPiece(
            item.Piece.PieceInstanceId, item.Piece.Side,
            item.Piece.IsRevealed ? item.Piece.ActualKind : BuiltInPieceKinds.Covered))).ToArray(),
        SideToMove, IsInCheck, IsFinished, Winner, EndReason, PlyNumber);

    /// <summary>Replay-only mapping used for a faint identity overlay; never expose it during a live match.</summary>
    public IReadOnlyDictionary<int, PieceKind> HiddenIdentityHints() => Pieces
        .Where(item => !item.Piece.IsRevealed)
        .ToDictionary(item => item.Piece.PieceInstanceId, item => item.Piece.ActualKind);
}

public readonly record struct JieqiMoveResult(
    bool IsSuccess, ChessMove Move, JieqiPiece? MovedPiece, JieqiPiece? CapturedPiece,
    bool Revealed, MoveRejection Rejection);

/// <summary>
/// Jieqi aggregate. It owns the secret permutation and applies commands atomically. It does not
/// serialize records, transport secrets, render covered pieces, or decide room permissions.
/// </summary>
public sealed class JieqiGame
{
    public const string ShuffleAlgorithm = "splitmix64-fy-v1";
    private Dictionary<BoardPosition, JieqiPiece> _pieces;
    private readonly PieceRuleRegistry _standard = PieceRuleRegistry.CreateStandard();
    private PieceSide _sideToMove = PieceSide.Red;
    private bool _isInCheck;
    private bool _isFinished;
    private PieceSide? _winner;
    private GameEndReason _endReason;
    private int _plyNumber;

    private JieqiGame(Dictionary<BoardPosition, JieqiPiece> pieces) => _pieces = pieces;

    /// <summary>Creates the standard layout deterministically from an explicit 64-bit seed.</summary>
    public static JieqiGame Create(ulong seed)
    {
        var pieces = new Dictionary<BoardPosition, JieqiPiece>();
        var nextId = 1;
        var rng = new SplitMix64(seed);
        AddSide(PieceSide.Red, 0, 2, 3);
        AddSide(PieceSide.Black, 9, 7, 6);
        return new JieqiGame(pieces);

        void AddSide(PieceSide side, int backRank, int cannonRank, int soldierRank)
        {
            var slots = StartingSlots(backRank, cannonRank, soldierRank).ToArray();
            var actual = slots.Select(x => x.Kind).ToArray();
            for (var i = actual.Length - 1; i > 0; i--)
            {
                var j = (int)(rng.Next() % (ulong)(i + 1));
                (actual[i], actual[j]) = (actual[j], actual[i]);
            }
            for (var i = 0; i < slots.Length; i++)
                pieces.Add(slots[i].Position, new JieqiPiece(nextId++, side, actual[i], slots[i].Kind, false));
            var generalPosition = new BoardPosition(4, backRank);
            pieces.Add(generalPosition, new JieqiPiece(nextId++, side,
                BuiltInPieceKinds.General, BuiltInPieceKinds.General, true));
        }
    }

    public JieqiSnapshot Snapshot => new(
        _pieces.OrderBy(x => x.Key.Rank).ThenBy(x => x.Key.File)
            .Select(x => new PlacedJieqiPiece(x.Key, x.Value)).ToArray(),
        _sideToMove, _isInCheck, _isFinished, _winner, _endReason, _plyNumber);

    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from)
    {
        if (_isFinished || !_pieces.TryGetValue(from, out var piece) || piece.Side != _sideToMove) return [];
        var result = new List<BoardPosition>();
        for (var rank = 0; rank < BoardState.RankCount; rank++)
        for (var file = 0; file < BoardState.FileCount; file++)
        {
            var to = new BoardPosition(file, rank);
            if (ValidateMove(_pieces, _sideToMove, new ChessMove(from, to)) == MoveRejection.None) result.Add(to);
        }
        return result;
    }

    public JieqiMoveResult TryMove(ChessMove move)
    {
        if (_isFinished) return Reject(MoveRejection.GameAlreadyFinished);
        var rejection = ValidateMove(_pieces, _sideToMove, move);
        if (rejection != MoveRejection.None) return Reject(rejection);
        var moved = _pieces[move.From];
        _pieces.TryGetValue(move.To, out var captured);
        _pieces.Remove(move.From);
        _pieces[move.To] = moved with { IsRevealed = true };
        _plyNumber++;
        if (captured.ActualKind == BuiltInPieceKinds.General)
        {
            _isFinished = true; _winner = moved.Side; _endReason = GameEndReason.GeneralCaptured; _isInCheck = false;
        }
        else
        {
            _sideToMove = _sideToMove.Opponent();
            _isInCheck = IsInCheck(_pieces, _sideToMove);
            if (!HasAnyLegalMove(_pieces, _sideToMove))
            {
                _isFinished = true; _winner = _sideToMove.Opponent();
                _endReason = _isInCheck ? GameEndReason.Checkmate : GameEndReason.Stalemate;
            }
        }
        return new JieqiMoveResult(true, move, moved, captured.ActualKind.Value is null ? null : captured,
            !moved.IsRevealed, MoveRejection.None);

        JieqiMoveResult Reject(MoveRejection why) => new(false, move, null, null, false, why);
    }

    /// <summary>Applies a room-authorized external loss without exposing or changing hidden identities.</summary>
    public bool DeclareLoss(PieceSide loser, GameEndReason reason)
    {
        if (_isFinished || !Enum.IsDefined(loser) ||
            reason is not (GameEndReason.Timeout or GameEndReason.Resignation)) return false;
        _isFinished = true;
        _winner = loser.Opponent();
        _endReason = reason;
        _isInCheck = false;
        return true;
    }

    private MoveRejection ValidateMove(Dictionary<BoardPosition, JieqiPiece> state, PieceSide side, ChessMove move)
    {
        if (!move.From.IsInside || !move.To.IsInside) return MoveRejection.OutsideBoard;
        if (!state.TryGetValue(move.From, out var piece)) return MoveRejection.NoPieceAtSource;
        if (piece.Side != side) return MoveRejection.NotPlayersTurn;
        if (state.TryGetValue(move.To, out var target) && target.Side == side) return MoveRejection.OccupiedByFriendlyPiece;
        if (!CanMove(state, piece, move.From, move.To)) return MoveRejection.PieceRuleViolation;
        var simulated = new Dictionary<BoardPosition, JieqiPiece>(state);
        simulated.Remove(move.From);
        simulated[move.To] = piece with { IsRevealed = true };
        return IsInCheck(simulated, side) ? MoveRejection.WouldLeaveGeneralInCheck : MoveRejection.None;
    }

    private bool CanMove(Dictionary<BoardPosition, JieqiPiece> state, JieqiPiece piece, BoardPosition from, BoardPosition to)
    {
        var kind = piece.IsRevealed ? piece.ActualKind : piece.CoverKind;
        var board = ProjectBoard(state);
        var projected = new ChessPiece(piece.PieceInstanceId, piece.Side, kind);
        if (piece.IsRevealed && kind == BuiltInPieceKinds.Advisor)
            return Math.Abs(to.File - from.File) == 1 && Math.Abs(to.Rank - from.Rank) == 1;
        if (piece.IsRevealed && kind == BuiltInPieceKinds.Elephant)
        {
            if (Math.Abs(to.File - from.File) != 2 || Math.Abs(to.Rank - from.Rank) != 2) return false;
            return board.GetPiece(new BoardPosition((from.File + to.File) / 2, (from.Rank + to.Rank) / 2)) is null;
        }
        return _standard.Get(kind).CanMove(board, projected, from, to);
    }

    private bool IsInCheck(Dictionary<BoardPosition, JieqiPiece> state, PieceSide side)
    {
        var general = state.FirstOrDefault(x => x.Value.Side == side && x.Value.ActualKind == BuiltInPieceKinds.General);
        if (general.Value.ActualKind != BuiltInPieceKinds.General) return true;
        return state.Any(enemy => enemy.Value.Side != side && CanMove(state, enemy.Value, enemy.Key, general.Key));
    }

    private bool HasAnyLegalMove(Dictionary<BoardPosition, JieqiPiece> state, PieceSide side)
    {
        foreach (var source in state.Where(x => x.Value.Side == side).Select(x => x.Key).ToArray())
        for (var rank = 0; rank < BoardState.RankCount; rank++)
        for (var file = 0; file < BoardState.FileCount; file++)
            if (ValidateMove(state, side, new ChessMove(source, new BoardPosition(file, rank))) == MoveRejection.None) return true;
        return false;
    }

    private static BoardState ProjectBoard(Dictionary<BoardPosition, JieqiPiece> state) => new(state.Select(x =>
        new PlacedPiece(x.Key, new ChessPiece(x.Value.PieceInstanceId, x.Value.Side,
            x.Value.IsRevealed ? x.Value.ActualKind : x.Value.CoverKind))));

    private static IEnumerable<(BoardPosition Position, PieceKind Kind)> StartingSlots(int back, int cannon, int soldier)
    {
        PieceKind[] row = [BuiltInPieceKinds.Chariot, BuiltInPieceKinds.Horse, BuiltInPieceKinds.Elephant,
            BuiltInPieceKinds.Advisor, BuiltInPieceKinds.Advisor, BuiltInPieceKinds.Elephant,
            BuiltInPieceKinds.Horse, BuiltInPieceKinds.Chariot];
        int[] files = [0, 1, 2, 3, 5, 6, 7, 8];
        for (var i = 0; i < files.Length; i++) yield return (new BoardPosition(files[i], back), row[i]);
        yield return (new BoardPosition(1, cannon), BuiltInPieceKinds.Cannon);
        yield return (new BoardPosition(7, cannon), BuiltInPieceKinds.Cannon);
        for (var file = 0; file < 9; file += 2) yield return (new BoardPosition(file, soldier), BuiltInPieceKinds.Soldier);
    }

    private sealed class SplitMix64(ulong state)
    {
        private ulong _state = state;
        public ulong Next()
        {
            var z = (_state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
