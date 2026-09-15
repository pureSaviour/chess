using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Domain.Go;

public enum GoStone : byte { Black, White }
public enum GoPhase : byte { Playing, Scoring, Finished }
public enum GoActionKind : byte { Place, Pass, MarkDead, ConfirmScore, Resign }
public enum GoEndReason : byte { AreaScore, Resignation }
public enum GoRejection : byte
{
    None, Finished, WrongPhase, OutOfBounds, Occupied, NotPlayersTurn, Suicide,
    RepeatedPosition, EmptyPoint, AlreadyConfirmed, ActionLimit
}

public readonly record struct GoAction(long Sequence, GoStone Actor, GoActionKind Kind, GridPoint? Point = null);
/// <summary>One live board stone and the action number on which it was placed, for read-only presentation.</summary>
public readonly record struct GoPlacedStone(GridPoint Point, GoStone Color, long MoveNumber);
public readonly record struct GoScore(double Black, double White, double Komi)
{
    public GoStone? Winner => Math.Abs(Black - White) < .0001 ? null : Black > White ? GoStone.Black : GoStone.White;
    public double Margin => Math.Abs(Black - White);
}
public sealed record GoSnapshot(int BoardSize, IReadOnlyList<GoPlacedStone> Stones, GoStone SideToMove,
    long ActionNumber, int MoveNumber, GoPhase Phase, IReadOnlySet<GridPoint> DeadStones,
    IReadOnlySet<GoStone> ScoreConfirmations, GoStone? Winner, GoEndReason? EndReason,
    GoScore? Score, GoAction? LastAction);
public readonly record struct GoActionResult(bool IsSuccess, GoAction Action, GoRejection Rejection)
{
    public static GoActionResult Rejected(GoAction action, GoRejection rejection) => new(false, action, rejection);
}

/// <summary>
/// Authoritative 19-line Chinese-rules Go aggregate. It owns occupancy, capture, positional
/// superko history, pass/scoring state and area adjudication with 7.5-point komi. It does not
/// infer life and death: during scoring the players mark whole dead groups and both must confirm.
/// Every command is synchronous on the caller's thread and a rejection leaves all state unchanged.
/// The aggregate owns managed collections only and requires no disposal.
/// </summary>
public sealed class GoGame
{
    public const int StandardBoardSize = 19;
    public const double StandardKomi = 7.5;
    public const int MaximumActions = 4096;
    private static readonly (int Dc, int Dr)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private readonly SquareGrid _grid = new(StandardBoardSize);
    private readonly Dictionary<GridPoint, GoStone> _stones = [];
    private readonly Dictionary<GridPoint, long> _moveNumbers = [];
    private readonly List<GoAction> _actions = [];
    private readonly HashSet<string> _positions = [string.Empty];
    private readonly HashSet<GridPoint> _dead = [];
    private readonly HashSet<GoStone> _confirmations = [];
    private int _consecutivePasses;

    public GoStone SideToMove { get; private set; } = GoStone.Black;
    public GoPhase Phase { get; private set; } = GoPhase.Playing;
    public GoStone? Winner { get; private set; }
    public GoEndReason? EndReason { get; private set; }
    public GoScore? Score { get; private set; }
    public IReadOnlyList<GoAction> Actions => _actions.AsReadOnly();
    public GoSnapshot Snapshot => new(StandardBoardSize,
        _stones.Select(x => new GoPlacedStone(x.Key, x.Value, _moveNumbers[x.Key]))
            .OrderBy(x => x.Point.Row).ThenBy(x => x.Point.Column).ToArray(),
        SideToMove, _actions.Count, _actions.Count(x => x.Kind is GoActionKind.Place or GoActionKind.Pass),
        Phase, new HashSet<GridPoint>(_dead), new HashSet<GoStone>(_confirmations), Winner, EndReason,
        Score, _actions.Count == 0 ? null : _actions[^1]);

    public GoActionResult Place(GridPoint point, GoStone? actor = null)
    {
        var color = actor ?? SideToMove;
        var action = Next(color, GoActionKind.Place, point);
        var rejection = CommonRejection(action, GoPhase.Playing);
        if (rejection != GoRejection.None) return GoActionResult.Rejected(action, rejection);
        if (!_grid.Contains(point)) return GoActionResult.Rejected(action, GoRejection.OutOfBounds);
        if (_stones.ContainsKey(point)) return GoActionResult.Rejected(action, GoRejection.Occupied);

        var captured = new Dictionary<GridPoint, (GoStone Color, long MoveNumber)>();
        _stones.Add(point, color);
        _moveNumbers.Add(point, action.Sequence);
        foreach (var neighbor in Neighbors(point))
        {
            if (!_stones.TryGetValue(neighbor, out var other) || other == color) continue;
            var group = Group(neighbor);
            if (HasLiberty(group)) continue;
            foreach (var member in group)
            {
                captured[member] = (other, _moveNumbers[member]);
                _stones.Remove(member);
                _moveNumbers.Remove(member);
            }
        }
        if (!HasLiberty(Group(point)))
        {
            _stones.Remove(point);
            _moveNumbers.Remove(point);
            foreach (var item in captured)
            {
                _stones.Add(item.Key, item.Value.Color);
                _moveNumbers.Add(item.Key, item.Value.MoveNumber);
            }
            return GoActionResult.Rejected(action, GoRejection.Suicide);
        }
        var position = PositionKey();
        if (_positions.Contains(position))
        {
            _stones.Remove(point);
            _moveNumbers.Remove(point);
            foreach (var item in captured)
            {
                _stones.Add(item.Key, item.Value.Color);
                _moveNumbers.Add(item.Key, item.Value.MoveNumber);
            }
            return GoActionResult.Rejected(action, GoRejection.RepeatedPosition);
        }
        _positions.Add(position);
        _consecutivePasses = 0;
        Commit(action, Opponent(color));
        return new(true, action, GoRejection.None);
    }

    public GoActionResult Pass(GoStone? actor = null)
    {
        var color = actor ?? SideToMove;
        var action = Next(color, GoActionKind.Pass);
        var rejection = CommonRejection(action, GoPhase.Playing);
        if (rejection != GoRejection.None) return GoActionResult.Rejected(action, rejection);
        _consecutivePasses++;
        Commit(action, Opponent(color));
        if (_consecutivePasses >= 2) Phase = GoPhase.Scoring;
        return new(true, action, GoRejection.None);
    }

    public GoActionResult ToggleDead(GridPoint point, GoStone? actor = null)
    {
        var color = actor ?? SideToMove;
        var action = Next(color, GoActionKind.MarkDead, point);
        var rejection = CommonRejection(action, GoPhase.Scoring);
        if (rejection != GoRejection.None) return GoActionResult.Rejected(action, rejection);
        if (!_grid.Contains(point)) return GoActionResult.Rejected(action, GoRejection.OutOfBounds);
        if (!_stones.ContainsKey(point)) return GoActionResult.Rejected(action, GoRejection.EmptyPoint);
        var group = Group(point);
        var markDead = group.Any(member => !_dead.Contains(member));
        foreach (var member in group)
        {
            if (markDead) _dead.Add(member); else _dead.Remove(member);
        }
        _confirmations.Clear();
        Commit(action, color);
        return new(true, action, GoRejection.None);
    }

    public GoActionResult ConfirmScore(GoStone? actor = null)
    {
        var color = actor ?? SideToMove;
        var action = Next(color, GoActionKind.ConfirmScore);
        var rejection = CommonRejection(action, GoPhase.Scoring);
        if (rejection != GoRejection.None) return GoActionResult.Rejected(action, rejection);
        if (_confirmations.Contains(color)) return GoActionResult.Rejected(action, GoRejection.AlreadyConfirmed);
        _confirmations.Add(color);
        Commit(action, Opponent(color));
        if (_confirmations.Count == 2)
        {
            Score = CalculateScore();
            Winner = Score.Value.Winner;
            EndReason = GoEndReason.AreaScore;
            Phase = GoPhase.Finished;
        }
        return new(true, action, GoRejection.None);
    }

    public GoActionResult Resign(GoStone? actor = null)
    {
        var color = actor ?? SideToMove;
        var action = Next(color, GoActionKind.Resign);
        if (Phase == GoPhase.Finished) return GoActionResult.Rejected(action, GoRejection.Finished);
        if (color != SideToMove) return GoActionResult.Rejected(action, GoRejection.NotPlayersTurn);
        if (_actions.Count >= MaximumActions) return GoActionResult.Rejected(action, GoRejection.ActionLimit);
        _actions.Add(action);
        Winner = Opponent(color);
        EndReason = GoEndReason.Resignation;
        Phase = GoPhase.Finished;
        return new(true, action, GoRejection.None);
    }

    public GoScore CalculateScore()
        => GoAreaScoreCalculator.Calculate(StandardBoardSize, _stones, _dead, StandardKomi).Score;

    private GoRejection CommonRejection(GoAction action, GoPhase required) =>
        Phase == GoPhase.Finished ? GoRejection.Finished :
        Phase != required ? GoRejection.WrongPhase :
        action.Actor != SideToMove ? GoRejection.NotPlayersTurn :
        _actions.Count >= MaximumActions ? GoRejection.ActionLimit : GoRejection.None;
    private GoAction Next(GoStone actor, GoActionKind kind, GridPoint? point = null) =>
        new(_actions.Count + 1L, actor, kind, point);
    private void Commit(GoAction action, GoStone next) { _actions.Add(action); SideToMove = next; }
    private static GoStone Opponent(GoStone color) => color == GoStone.Black ? GoStone.White : GoStone.Black;
    private IEnumerable<GridPoint> Neighbors(GridPoint point)
    {
        foreach (var (dc, dr) in Orthogonal)
        {
            var neighbor = new GridPoint(point.Column + dc, point.Row + dr);
            if (_grid.Contains(neighbor)) yield return neighbor;
        }
    }
    private HashSet<GridPoint> Group(GridPoint start)
    {
        var color = _stones[start];
        var result = new HashSet<GridPoint> { start };
        var queue = new Queue<GridPoint>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var point))
        foreach (var neighbor in Neighbors(point))
            if (_stones.TryGetValue(neighbor, out var found) && found == color && result.Add(neighbor)) queue.Enqueue(neighbor);
        return result;
    }
    private bool HasLiberty(IEnumerable<GridPoint> group) => group.Any(point => Neighbors(point).Any(neighbor => !_stones.ContainsKey(neighbor)));
    private string PositionKey() => string.Join(';', _stones.OrderBy(x => x.Key.Row).ThenBy(x => x.Key.Column)
        .Select(x => $"{x.Key.Column},{x.Key.Row},{(byte)x.Value}"));
}
