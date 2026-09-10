using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;

namespace Xiangqi.Core.Application.Jieqi;

/// <summary>
/// UI-facing Jieqi facade. It accepts move intents and publishes immutable snapshots; it never
/// exposes the seed through live network messages or grants presentation direct board mutation.
/// </summary>
public sealed class JieqiGameSession
{
    private readonly ulong _seed;
    private readonly JieqiGame _game;
    private readonly List<ChessMove> _moves = [];
    public event Action<GameSnapshot>? StateChanged;
    public event Action<GameSnapshot, GameSnapshot, ChessMove, bool>? MoveCommitted;

    public JieqiGameSession(ulong seed) { _seed = seed; _game = JieqiGame.Create(seed); }
    /// <summary>Live redacted state; unrevealed identities never cross this application boundary.</summary>
    public GameSnapshot Snapshot => _game.Snapshot.ToPublicPosition();
    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from) => _game.GetLegalDestinations(from);
    public JieqiMoveResult TryMove(BoardPosition from, BoardPosition to)
    {
        var before = _game.Snapshot.ToPublicPosition();
        var result = _game.TryMove(new ChessMove(from, to));
        if (!result.IsSuccess) return result;
        _moves.Add(result.Move);
        var after = _game.Snapshot.ToPublicPosition();
        MoveCommitted?.Invoke(before, after, result.Move, result.CapturedPiece is not null); StateChanged?.Invoke(after);
        return result;
    }
    public JieqiGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null)
    {
        if (!_game.Snapshot.IsFinished)
            throw new InvalidOperationException("揭棋秘密种子只能在对局结束后写入棋谱。");
        return new(_seed, _moves.ToArray(), tags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
