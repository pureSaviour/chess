using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Domain.Gomoku;

public enum StoneColor : byte { Black, White }
public enum GomokuRuleSet : byte { Freestyle, RenjuForbiddenMoves }
public enum GomokuEndReason : byte { FiveInRow, BoardFull }
public enum GomokuMoveRejection : byte { None, Finished, Occupied, OutOfBounds, NotPlayersTurn, Overline, DoubleFour, DoubleThree }

public readonly record struct GomokuMove(long Sequence, StoneColor Color, GridPoint Point);
public readonly record struct PlacedStone(GridPoint Point, StoneColor Color);
public sealed record GomokuSnapshot(int BoardSize, GomokuRuleSet Rules, IReadOnlyList<PlacedStone> Stones,
    StoneColor SideToMove, long PlyNumber, bool IsFinished, StoneColor? Winner, GomokuEndReason? EndReason,
    GomokuMove? LastMove);
public readonly record struct GomokuMoveResult(bool IsSuccess, GomokuMove Move, GomokuMoveRejection Rejection)
{
    public static GomokuMoveResult Rejected(GomokuMove move, GomokuMoveRejection rejection) => new(false, move, rejection);
}

/// <summary>
/// Authoritative Gomoku aggregate. It owns occupancy, turn, history and terminal state. With
/// Renju enabled only Black is forbidden from overline, double-four and double-three; opening
/// swap protocols are intentionally outside this ruleset. TryPlace is the sole mutation entry,
/// rejects invalid input without state changes, and synchronously returns the semantic reason.
/// </summary>
public sealed class GomokuGame
{
    private static readonly (int Dc, int Dr)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];
    private readonly SquareGrid _grid;
    private readonly Dictionary<GridPoint, StoneColor> _stones = [];
    private readonly List<GomokuMove> _history = [];

    public GomokuGame(int boardSize = 15, GomokuRuleSet rules = GomokuRuleSet.Freestyle,
        IEnumerable<PlacedStone>? initialStones = null, StoneColor sideToMove = StoneColor.Black)
    {
        _grid = new SquareGrid(boardSize);
        if (!Enum.IsDefined(rules)) throw new ArgumentOutOfRangeException(nameof(rules));
        if (!Enum.IsDefined(sideToMove)) throw new ArgumentOutOfRangeException(nameof(sideToMove));
        Rules = rules; SideToMove = sideToMove;
        foreach (var stone in initialStones ?? [])
        {
            if (!_grid.Contains(stone.Point) || !Enum.IsDefined(stone.Color) || !_stones.TryAdd(stone.Point, stone.Color))
                throw new ArgumentException("初始棋盘包含越界、重叠或无效棋子。", nameof(initialStones));
        }
    }

    public GomokuRuleSet Rules { get; }
    public StoneColor SideToMove { get; private set; }
    public bool IsFinished { get; private set; }
    public StoneColor? Winner { get; private set; }
    public GomokuEndReason? EndReason { get; private set; }
    public IReadOnlyList<GomokuMove> History => _history.AsReadOnly();
    public GomokuSnapshot Snapshot => new(_grid.Size, Rules,
        _stones.Select(x => new PlacedStone(x.Key, x.Value)).OrderBy(x => x.Point.Row).ThenBy(x => x.Point.Column).ToArray(),
        SideToMove, _history.Count, IsFinished, Winner, EndReason, _history.Count == 0 ? null : _history[^1]);

    public GomokuMoveResult TryPlace(GridPoint point, StoneColor? actor = null)
    {
        var color = actor ?? SideToMove;
        var move = new GomokuMove(_history.Count + 1L, color, point);
        if (IsFinished) return GomokuMoveResult.Rejected(move, GomokuMoveRejection.Finished);
        if (!_grid.Contains(point)) return GomokuMoveResult.Rejected(move, GomokuMoveRejection.OutOfBounds);
        if (color != SideToMove) return GomokuMoveResult.Rejected(move, GomokuMoveRejection.NotPlayersTurn);
        if (_stones.ContainsKey(point)) return GomokuMoveResult.Rejected(move, GomokuMoveRejection.Occupied);

        _stones.Add(point, color);
        var line = Directions.Max(direction => CountLine(point, color, direction.Dc, direction.Dr));
        if (Rules == GomokuRuleSet.RenjuForbiddenMoves && color == StoneColor.Black)
        {
            var rejection = IsOverline(point, color) ? GomokuMoveRejection.Overline :
                line == 5 ? GomokuMoveRejection.None :
                CountFourFormations(point, color) >= 2 ? GomokuMoveRejection.DoubleFour :
                CountOpenThreeFormations(point, color) >= 2 ? GomokuMoveRejection.DoubleThree : GomokuMoveRejection.None;
            if (rejection != GomokuMoveRejection.None) { _stones.Remove(point); return GomokuMoveResult.Rejected(move, rejection); }
        }

        _history.Add(move);
        var wins = Rules == GomokuRuleSet.RenjuForbiddenMoves && color == StoneColor.Black ? line == 5 : line >= 5;
        if (wins) { IsFinished = true; Winner = color; EndReason = GomokuEndReason.FiveInRow; }
        else if (_stones.Count == _grid.Size * _grid.Size) { IsFinished = true; EndReason = GomokuEndReason.BoardFull; }
        else SideToMove = color == StoneColor.Black ? StoneColor.White : StoneColor.Black;
        return new GomokuMoveResult(true, move, GomokuMoveRejection.None);
    }

    private int CountLine(GridPoint point, StoneColor color, int dc, int dr) =>
        1 + Count(point, color, dc, dr) + Count(point, color, -dc, -dr);

    private int Count(GridPoint point, StoneColor color, int dc, int dr)
    {
        var count = 0;
        for (var p = new GridPoint(point.Column + dc, point.Row + dr); _grid.Contains(p) &&
             _stones.TryGetValue(p, out var found) && found == color; p = new GridPoint(p.Column + dc, p.Row + dr)) count++;
        return count;
    }

    private bool IsOverline(GridPoint point, StoneColor color) => Directions.Any(d => CountLine(point, color, d.Dc, d.Dr) >= 6);

    private int CountFourFormations(GridPoint origin, StoneColor color)
    {
        var formations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dc, dr) in Directions)
        {
            for (var offset = -4; offset <= 4; offset++)
            {
                var empty = new GridPoint(origin.Column + offset * dc, origin.Row + offset * dr);
                if (!_grid.Contains(empty) || _stones.ContainsKey(empty)) continue;
                _stones.Add(empty, color);
                if (CountLine(empty, color, dc, dr) == 5)
                {
                    var run = CollectExactRun(empty, color, dc, dr);
                    if (run.Contains(origin)) formations.Add($"{dc},{dr}:" + string.Join(';', run.Where(p => p != empty).OrderBy(p => p.Row).ThenBy(p => p.Column)));
                }
                _stones.Remove(empty);
            }
        }
        return formations.Count;
    }

    private int CountOpenThreeFormations(GridPoint origin, StoneColor color)
    {
        var formations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dc, dr) in Directions)
        for (var offset = -4; offset <= 4; offset++)
        {
            var extension = new GridPoint(origin.Column + offset * dc, origin.Row + offset * dr);
            if (!_grid.Contains(extension) || _stones.ContainsKey(extension)) continue;
            _stones.Add(extension, color);
            if (!IsOverline(extension, color) && CountFourFormations(extension, color) < 2)
            {
                var winning = new HashSet<GridPoint>();
                for (var next = -5; next <= 5; next++)
                {
                    var empty = new GridPoint(origin.Column + next * dc, origin.Row + next * dr);
                    if (!_grid.Contains(empty) || _stones.ContainsKey(empty)) continue;
                    _stones.Add(empty, color);
                    if (CountLine(empty, color, dc, dr) == 5) winning.Add(empty);
                    _stones.Remove(empty);
                }
                if (winning.Count >= 2)
                {
                    var stones = CollectLineStones(origin, color, dc, dr).Where(p => p != extension).ToArray();
                    formations.Add($"{dc},{dr}:" + string.Join(';', stones.OrderBy(p => p.Row).ThenBy(p => p.Column)));
                }
            }
            _stones.Remove(extension);
        }
        return formations.Count;
    }

    private List<GridPoint> CollectExactRun(GridPoint point, StoneColor color, int dc, int dr)
    {
        var start = point;
        while (_stones.TryGetValue(new(start.Column - dc, start.Row - dr), out var previous) && previous == color)
            start = new(start.Column - dc, start.Row - dr);
        var result = new List<GridPoint>();
        for (var p = start; _stones.TryGetValue(p, out var value) && value == color; p = new(p.Column + dc, p.Row + dr)) result.Add(p);
        return result;
    }

    private IEnumerable<GridPoint> CollectLineStones(GridPoint origin, StoneColor color, int dc, int dr)
    {
        for (var offset = -4; offset <= 4; offset++)
        {
            var p = new GridPoint(origin.Column + offset * dc, origin.Row + offset * dr);
            if (_grid.Contains(p) && _stones.TryGetValue(p, out var found) && found == color) yield return p;
        }
    }
}
