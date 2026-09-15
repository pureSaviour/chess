using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

public readonly record struct TimeControl(int GameSeconds, int MoveSeconds)
{
    public static readonly TimeControl Unlimited = new(0, 0);
    public bool IsEnabled => GameSeconds > 0 || MoveSeconds > 0;
    public TimeControl Validate() => GameSeconds < 0 || MoveSeconds < 0
        ? throw new ArgumentOutOfRangeException(nameof(TimeControl), "计时时间不能小于零。")
        : this;
}

public enum TwoPlayerGameMode : byte { Classic, Jieqi, Junqi, Gomoku, Go }

public readonly record struct NetworkMatchConfiguration(
    TimeControl TimeControl, PieceSide HostSide, TwoPlayerGameMode Mode = TwoPlayerGameMode.Classic,
    int BoardSize = 0, int RuleFlags = 0)
{
    public PieceSide ClientSide => HostSide.Opponent();
}

public readonly record struct ClockSnapshot(
    double RedGameSeconds,
    double BlackGameSeconds,
    double MoveSeconds,
    PieceSide SideToMove);

public enum MoveClockUrgency : byte
{
    Hidden,
    Normal,
    Critical
}

/// <summary>
/// Engine-independent projection used by player-card renderers. The ratio is always in [0, 1];
/// an unlimited, inactive or already-finished turn is hidden. The final ten seconds are critical.
/// This type owns no timer and never changes authoritative clock state.
/// </summary>
public readonly record struct MoveClockIndicator(bool IsVisible, double RemainingRatio, MoveClockUrgency Urgency)
{
    public static MoveClockIndicator From(double remainingSeconds, int moveLimitSeconds, bool isActive)
    {
        if (!double.IsFinite(remainingSeconds))
            throw new ArgumentOutOfRangeException(nameof(remainingSeconds), "步时必须是有限数值。");
        if (moveLimitSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(moveLimitSeconds), "步时上限不能小于零。");
        if (!isActive || moveLimitSeconds == 0)
            return new(false, 0, MoveClockUrgency.Hidden);
        var remaining = Math.Clamp(remainingSeconds, 0, moveLimitSeconds);
        return new(true, remaining / moveLimitSeconds,
            remaining <= 10 ? MoveClockUrgency.Critical : MoveClockUrgency.Normal);
    }
}

/// <summary>
/// Engine-independent chess clock supporting total-game and per-move allowances. Zero means
/// unlimited. Network hosts own timeout adjudication; clients apply host snapshots for display,
/// avoiding divergent results caused by latency or frame-rate differences.
/// </summary>
public sealed class MatchClock
{
    private readonly TimeControl _control;
    private double _redGame;
    private double _blackGame;
    private double _move;
    private PieceSide _sideToMove = PieceSide.Red;

    public MatchClock(TimeControl control)
    {
        _control = control.Validate();
        _redGame = UnlimitedAsInfinity(control.GameSeconds);
        _blackGame = UnlimitedAsInfinity(control.GameSeconds);
        _move = UnlimitedAsInfinity(control.MoveSeconds);
    }

    public bool IsRunning { get; set; }
    public TimeControl Control => _control;
    public ClockSnapshot Snapshot => new(
        InfinityAsUnlimited(_redGame), InfinityAsUnlimited(_blackGame),
        InfinityAsUnlimited(_move), _sideToMove);

    public PieceSide? Advance(double seconds)
    {
        if (!IsRunning || seconds <= 0 || !_control.IsEnabled) return null;
        if (_sideToMove == PieceSide.Red) _redGame -= seconds;
        else _blackGame -= seconds;
        _move -= seconds;
        var gameExpired = _sideToMove == PieceSide.Red ? _redGame <= 0 : _blackGame <= 0;
        return gameExpired || _move <= 0 ? _sideToMove : null;
    }

    public void BeginTurn(PieceSide side)
    {
        _sideToMove = side;
        _move = UnlimitedAsInfinity(_control.MoveSeconds);
    }

    public void Apply(ClockSnapshot snapshot)
    {
        _redGame = UnlimitedAsInfinity(snapshot.RedGameSeconds);
        _blackGame = UnlimitedAsInfinity(snapshot.BlackGameSeconds);
        _move = UnlimitedAsInfinity(snapshot.MoveSeconds);
        _sideToMove = snapshot.SideToMove;
    }

    private static double UnlimitedAsInfinity(double seconds) => seconds <= 0 ? double.PositiveInfinity : seconds;
    private static double InfinityAsUnlimited(double seconds) => double.IsPositiveInfinity(seconds) ? 0 : Math.Max(0, seconds);
}
