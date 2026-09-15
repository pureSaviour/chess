using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Domain.Go;

/// <summary>
/// A non-authoritative Chinese-area estimate for one copied Go position. Territory sets contain
/// empty intersections after the locally marked dead chains are removed; neutral points are split
/// equally in <see cref="Score"/> to match <see cref="GoGame.CalculateScore"/>. The estimate never
/// changes a live game, protocol log, clock or SGF record.
/// </summary>
public sealed record GoScoreEstimate(GoScore Score, IReadOnlySet<GridPoint> BlackTerritory,
    IReadOnlySet<GridPoint> WhiteTerritory, IReadOnlySet<GridPoint> NeutralPoints,
    IReadOnlySet<GridPoint> MarkedDeadStones);

/// <summary>
/// Owns the temporary dead-chain choices for an anytime, manual score preview. Construction copies
/// and validates a trusted snapshot. <see cref="TryToggleDeadGroup"/> is synchronous and returns
/// false without mutation for an empty or out-of-board point. Instances are thread-confined managed
/// state, own no external resources and should be discarded when the authoritative action number
/// changes so a stale estimate is never presented as current.
/// </summary>
public sealed class GoManualScoreSession
{
    private static readonly (int Dc, int Dr)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private readonly int _boardSize;
    private readonly Dictionary<GridPoint, GoStone> _stones;
    private readonly HashSet<GridPoint> _markedDead;

    /// <summary>The copied authority version used by the UI to reject stale previews.</summary>
    public long SourceActionNumber { get; }
    /// <summary>Recalculates an immutable-result snapshot from the current local dead-chain choices.</summary>
    public GoScoreEstimate Estimate => GoAreaScoreCalculator.Calculate(_boardSize, _stones, _markedDead,
        GoGame.StandardKomi);

    /// <summary>Copies a validated standard-board snapshot and its existing scoring marks.</summary>
    /// <exception cref="ArgumentException">The snapshot is not 19-line or contains inconsistent points.</exception>
    public GoManualScoreSession(GoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BoardSize != GoGame.StandardBoardSize)
            throw new ArgumentException("数目估算只接受标准 19 路围棋快照。", nameof(snapshot));
        _boardSize = snapshot.BoardSize;
        _stones = new Dictionary<GridPoint, GoStone>();
        foreach (var stone in snapshot.Stones)
        {
            if (!Contains(stone.Point) || !_stones.TryAdd(stone.Point, stone.Color))
                throw new ArgumentException("围棋快照包含越界或重复棋子。", nameof(snapshot));
        }
        _markedDead = new HashSet<GridPoint>(snapshot.DeadStones);
        if (_markedDead.Any(point => !_stones.ContainsKey(point)))
            throw new ArgumentException("围棋快照的死子标记没有对应棋子。", nameof(snapshot));
        SourceActionNumber = snapshot.ActionNumber;
    }

    /// <summary>
    /// Toggles the complete connected same-color chain containing <paramref name="point"/>.
    /// </summary>
    /// <returns>True when a chain was toggled; false for an empty or out-of-board point.</returns>
    public bool TryToggleDeadGroup(GridPoint point)
    {
        if (!Contains(point) || !_stones.TryGetValue(point, out var color)) return false;
        var group = new HashSet<GridPoint> { point };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(point);
        while (queue.TryDequeue(out var current))
        foreach (var neighbor in Neighbors(current))
            if (_stones.TryGetValue(neighbor, out var found) && found == color && group.Add(neighbor))
                queue.Enqueue(neighbor);
        var markDead = group.Any(member => !_markedDead.Contains(member));
        foreach (var member in group)
        {
            if (markDead) _markedDead.Add(member);
            else _markedDead.Remove(member);
        }
        return true;
    }

    private bool Contains(GridPoint point) => point.Column >= 0 && point.Row >= 0 &&
        point.Column < _boardSize && point.Row < _boardSize;
    private IEnumerable<GridPoint> Neighbors(GridPoint point)
    {
        foreach (var (dc, dr) in Orthogonal)
        {
            var neighbor = new GridPoint(point.Column + dc, point.Row + dr);
            if (Contains(neighbor)) yield return neighbor;
        }
    }
}

internal static class GoAreaScoreCalculator
{
    private static readonly (int Dc, int Dr)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static GoScoreEstimate Calculate(int boardSize, IReadOnlyDictionary<GridPoint, GoStone> stones,
        IReadOnlySet<GridPoint> dead, double komi)
    {
        var effective = stones.Where(x => !dead.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        double black = effective.Count(x => x.Value == GoStone.Black);
        double white = effective.Count(x => x.Value == GoStone.White) + komi;
        var blackTerritory = new HashSet<GridPoint>();
        var whiteTerritory = new HashSet<GridPoint>();
        var neutral = new HashSet<GridPoint>();
        var visited = new HashSet<GridPoint>();
        for (var row = 0; row < boardSize; row++)
        for (var column = 0; column < boardSize; column++)
        {
            var start = new GridPoint(column, row);
            if (effective.ContainsKey(start) || !visited.Add(start)) continue;
            var area = new HashSet<GridPoint> { start };
            var border = new HashSet<GoStone>();
            var queue = new Queue<GridPoint>();
            queue.Enqueue(start);
            while (queue.TryDequeue(out var current))
            foreach (var neighbor in Neighbors(current, boardSize))
            {
                if (effective.TryGetValue(neighbor, out var stone)) border.Add(stone);
                else if (visited.Add(neighbor)) { area.Add(neighbor); queue.Enqueue(neighbor); }
            }
            if (border.SetEquals([GoStone.Black]))
            {
                black += area.Count;
                blackTerritory.UnionWith(area);
            }
            else if (border.SetEquals([GoStone.White]))
            {
                white += area.Count;
                whiteTerritory.UnionWith(area);
            }
            else
            {
                black += area.Count / 2d;
                white += area.Count / 2d;
                neutral.UnionWith(area);
            }
        }
        return new(new(black, white, komi), blackTerritory, whiteTerritory, neutral,
            new HashSet<GridPoint>(dead));
    }

    private static IEnumerable<GridPoint> Neighbors(GridPoint point, int boardSize)
    {
        foreach (var (dc, dr) in Orthogonal)
        {
            var neighbor = new GridPoint(point.Column + dc, point.Row + dr);
            if (neighbor.Column >= 0 && neighbor.Row >= 0 && neighbor.Column < boardSize && neighbor.Row < boardSize)
                yield return neighbor;
        }
    }
}
