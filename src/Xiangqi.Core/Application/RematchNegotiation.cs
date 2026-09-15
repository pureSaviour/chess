namespace Xiangqi.Core.Application;

/// <summary>Read-only room-level rematch state. A new round starts only after both peers mark the
/// same completed round ready. The round number rejects delayed/replayed readiness packets.</summary>
public readonly record struct RematchSnapshot(long Round, bool LocalReady, bool RemoteReady);

/// <summary>
/// Pure two-player rematch negotiation. It does not own a game, transport, colors or UI. The
/// composing match controller validates that the game is terminal, transmits the returned round,
/// and atomically creates the next aggregate and swaps logical colors when both flags are set.
/// Caller-thread use only; no resources are owned.
/// </summary>
public sealed class TwoPlayerRematchNegotiation
{
    private long _round = 1;
    private bool _local;
    private bool _remote;

    public RematchSnapshot Snapshot => new(_round, _local, _remote);
    public bool MarkLocalReady() { _local = true; return _remote; }
    public bool AcceptRemoteReady(long round)
    {
        if (round != _round) return false;
        _remote = true;
        return true;
    }
    public void BeginNextRound()
    {
        _round++;
        _local = false;
        _remote = false;
    }
}

/// <summary>Optional capability exposed only by connected two-player controllers. Requesting is
/// an intent; the controller owns protocol validation and starts a fresh aggregate only after the
/// opponent has independently requested the same round.</summary>
public interface IRematchableMatchController
{
    RematchSnapshot Rematch { get; }
    bool CanRequestRematch { get; }
    ValueTask RequestRematchAsync(CancellationToken cancellationToken = default);
}
