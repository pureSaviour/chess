using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Application.ThreeKingdoms;

/// <summary>
/// Isolated three-player analysis branch. It owns a fork of the authoritative aggregate and
/// permits the analyst to play every successive country. It has no clocks, offers, network
/// identity or merge operation; leaving trial simply discards this object.
/// </summary>
public sealed class ThreeTrialSession
{
    private readonly ThreeKingdomsGame _game;
    public long BaseRevision { get; }
    public KingdomSnapshot Snapshot => _game.Snapshot;

    internal ThreeTrialSession(ThreeKingdomsGame authoritativeGame, long baseRevision)
    {
        ArgumentNullException.ThrowIfNull(authoritativeGame);
        _game = authoritativeGame.Fork();
        BaseRevision = baseRevision;
    }

    public IReadOnlyList<BoardCellId> GetLegalDestinations(BoardCellId from) => _game.LegalDestinations(from);
    public KingdomMoveResult TryMove(BoardCellId from, BoardCellId to) =>
        _game.Move(_game.Snapshot.Turn, new KingdomMove(from, to));
}
