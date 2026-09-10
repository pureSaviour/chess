using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

public readonly record struct MoveCommand(long Sequence, ChessMove Move);
public readonly record struct TimeoutCommand(long PlyNumber, PieceSide Loser);
public readonly record struct ClockSyncCommand(long PlyNumber, ClockSnapshot Clock);
public readonly record struct ChatCommand(RoomChatEntry Entry);
public readonly record struct PlayerProfileCommand(
    string PlayerId, string DisplayName, int AvatarWidth, int AvatarHeight, byte[] AvatarRgba);

public enum MatchOfferKind : byte { Undo = 1, Draw = 2 }

public readonly record struct MatchOfferRequest(long RequestId, long PlyNumber, MatchOfferKind Kind);
public readonly record struct MatchOfferResponse(
    long RequestId, long PlyNumber, MatchOfferKind Kind, bool Accepted);
public readonly record struct MatchOfferResolution(
    MatchOfferKind Kind, bool Accepted, bool WasLocalRequest);

public enum TransportConnectionState { Connecting, Connected, Disconnected, Faulted }

public readonly record struct TransportStatus(TransportConnectionState State, string? Detail = null);

/// <summary>
/// Reliable, authenticated two-player packet connection. It transports bounded opaque payloads
/// and reports connection state, but does not interpret a game's protocol, authorize commands,
/// own match state, or call UI code from a background thread. Implementations publish events on
/// their driving thread and must make disposal idempotent.
/// </summary>
public interface ITwoPlayerPacketTransport : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? PacketReceived;
    event Action<TransportStatus>? PacketStatusChanged;
    bool IsPacketConnected { get; }
    bool IsPacketHost { get; }
    ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
}

/// <summary>
/// Transport seam for future LAN (Godot ENet) and Steam implementations. A transport moves
/// commands only; the receiving GameSession/ChessGame remains authoritative and re-validates
/// every move. Snapshot hashes and reconnect negotiation can be layered onto this contract.
/// </summary>
public interface IMatchTransport : IAsyncDisposable
{
    event Action<MoveCommand>? MoveReceived;
    event Action<NetworkMatchConfiguration>? MatchConfigurationReceived;
    event Action<TimeoutCommand>? TimeoutReceived;
    event Action<ClockSyncCommand>? ClockSyncReceived;
    event Action<ChatCommand>? ChatReceived;
    event Action<MatchOfferRequest>? MatchOfferReceived;
    event Action<MatchOfferResponse>? MatchOfferResponseReceived;
    event Action<PlayerProfileCommand>? PlayerProfileReceived;
    event Action<TransportStatus>? StatusChanged;
    bool IsConnected { get; }
    ValueTask SendMoveAsync(MoveCommand command, CancellationToken cancellationToken = default);
    ValueTask SendMatchConfigurationAsync(NetworkMatchConfiguration configuration, CancellationToken cancellationToken = default);
    ValueTask SendTimeoutAsync(TimeoutCommand command, CancellationToken cancellationToken = default);
    ValueTask SendClockSyncAsync(ClockSyncCommand command, CancellationToken cancellationToken = default);
    ValueTask SendChatAsync(ChatCommand command, CancellationToken cancellationToken = default);
    ValueTask SendMatchOfferAsync(MatchOfferRequest command, CancellationToken cancellationToken = default);
    ValueTask SendMatchOfferResponseAsync(MatchOfferResponse command, CancellationToken cancellationToken = default);
    ValueTask SendPlayerProfileAsync(PlayerProfileCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates one deterministic GameSession with an arbitrary command transport. Local and
/// remote moves use the same validation path; sequence checks make duplicate or missing packets
/// fail closed instead of silently desynchronizing the two boards.
/// </summary>
public sealed class NetworkGameCoordinator : IAsyncDisposable
{
    private readonly GameSession _session;
    private readonly IMatchTransport _transport;
    private MatchOfferRequest? _outgoingOffer;
    private MatchOfferRequest? _incomingOffer;
    private long _nextOfferId;

    public NetworkGameCoordinator(GameSession session, IMatchTransport transport, PieceSide localSide)
    {
        _session = session;
        _transport = transport;
        LocalSide = localSide;
        _transport.MoveReceived += OnMoveReceived;
        _transport.TimeoutReceived += OnTimeoutReceived;
        _transport.ChatReceived += OnChatReceived;
        _transport.MatchOfferReceived += OnMatchOfferReceived;
        _transport.MatchOfferResponseReceived += OnMatchOfferResponseReceived;
        _transport.StatusChanged += OnTransportStatusChanged;
        Chat = new RoomChatManager(2);
    }

    public event Action<string>? SynchronizationFailed;
    public event Action<TransportStatus>? StatusChanged;
    public event Action<MatchOfferRequest>? MatchOfferReceived;
    public event Action<MatchOfferResolution>? MatchOfferResolved;
    public event Action<bool>? MatchOfferPendingChanged;
    public PieceSide LocalSide { get; }
    /// <summary>Bounded message board shared with three-player rooms; transport IDs never become display names.</summary>
    public RoomChatManager Chat { get; }
    public bool HasPendingOffer => _outgoingOffer is not null || _incomingOffer is not null;
    public bool CanRequestLocalUndo => _transport.IsConnected && !_session.Snapshot.IsFinished &&
                                       _session.CanUndo &&
                                       _session.Snapshot.SideToMove == LocalSide.Opponent();
    public bool CanMakeLocalMove => _transport.IsConnected && !_session.Snapshot.IsFinished &&
                                    !HasPendingOffer && _session.Snapshot.SideToMove == LocalSide;

    public async ValueTask<MoveResult> SubmitLocalMoveAsync(
        BoardPosition from, BoardPosition to, CancellationToken cancellationToken = default)
    {
        var move = new ChessMove(from, to);
        if (!CanMakeLocalMove)
            return MoveResult.Rejected(move, MoveRejection.NotPlayersTurn);

        var result = _session.TryMove(from, to);
        if (result.IsSuccess)
            await _transport.SendMoveAsync(
                new MoveCommand(_session.Snapshot.PlyNumber, result.Move), cancellationToken);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MoveReceived -= OnMoveReceived;
        _transport.TimeoutReceived -= OnTimeoutReceived;
        _transport.ChatReceived -= OnChatReceived;
        _transport.MatchOfferReceived -= OnMatchOfferReceived;
        _transport.MatchOfferResponseReceived -= OnMatchOfferResponseReceived;
        _transport.StatusChanged -= OnTransportStatusChanged;
        await _transport.DisposeAsync();
    }

    public async ValueTask<string?> SendChatAsync(
        string text, int? targetSeat = null, CancellationToken cancellationToken = default)
    {
        var error = Chat.Post((int)LocalSide, targetSeat, text, out var entry);
        if (error is not null) return error;
        await _transport.SendChatAsync(new ChatCommand(entry!), cancellationToken);
        return null;
    }

    /// <summary>
    /// Opens one state-bound negotiation. While an offer is pending, moves are disabled so an
    /// accepted undo/draw can only apply to the exact ply on which both players made the decision.
    /// </summary>
    public async ValueTask RequestMatchOfferAsync(
        MatchOfferKind kind, CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected || _session.Snapshot.IsFinished)
            throw new InvalidOperationException("当前不能发起对局申请。");
        if (HasPendingOffer) throw new InvalidOperationException("已有一项申请正在等待处理。");
        if (kind == MatchOfferKind.Undo && !CanRequestLocalUndo)
            throw new InvalidOperationException("只有自己刚走完且对方尚未落子时才能申请悔棋。");

        var request = new MatchOfferRequest(++_nextOfferId, _session.Snapshot.PlyNumber, kind);
        _outgoingOffer = request;
        MatchOfferPendingChanged?.Invoke(true);
        try
        {
            await _transport.SendMatchOfferAsync(request, cancellationToken);
        }
        catch
        {
            _outgoingOffer = null;
            MatchOfferPendingChanged?.Invoke(false);
            throw;
        }
    }

    public async ValueTask RespondToMatchOfferAsync(
        long requestId, bool accepted, CancellationToken cancellationToken = default)
    {
        if (_incomingOffer is not { } request || request.RequestId != requestId)
            throw new InvalidOperationException("该申请已失效。");

        var canApply = request.PlyNumber == _session.Snapshot.PlyNumber &&
                       !_session.Snapshot.IsFinished &&
                       (request.Kind != MatchOfferKind.Undo ||
                        (_session.CanUndo && _session.Snapshot.SideToMove == LocalSide));
        var finalAccepted = accepted && canApply;
        await _transport.SendMatchOfferResponseAsync(new MatchOfferResponse(
            request.RequestId, request.PlyNumber, request.Kind, finalAccepted), cancellationToken);

        _incomingOffer = null;
        MatchOfferPendingChanged?.Invoke(false);
        if (finalAccepted && !ApplyOffer(request.Kind))
            SynchronizationFailed?.Invoke("已同意的对局申请无法应用到当前局面。");
        MatchOfferResolved?.Invoke(new MatchOfferResolution(request.Kind, finalAccepted, false));
    }

    public async ValueTask<bool> DeclareTimeoutAsync(
        PieceSide loser, CancellationToken cancellationToken = default)
    {
        if (!_session.DeclareTimeout(loser)) return false;
        await _transport.SendTimeoutAsync(
            new TimeoutCommand(_session.Snapshot.PlyNumber, loser), cancellationToken);
        return true;
    }

    private void OnMoveReceived(MoveCommand command)
    {
        if (HasPendingOffer)
        {
            SynchronizationFailed?.Invoke("协商期间收到了远端着法，已拒绝以避免局面分歧。");
            return;
        }
        var expected = _session.Snapshot.PlyNumber + 1L;
        if (command.Sequence != expected)
        {
            SynchronizationFailed?.Invoke($"走子序号不一致：期望 {expected}，收到 {command.Sequence}。");
            return;
        }

        var result = _session.TryMove(command.Move.From, command.Move.To);
        if (!result.IsSuccess)
            SynchronizationFailed?.Invoke($"远端着法未通过本地规则校验：{result.Rejection}。");
    }

    private void OnTransportStatusChanged(TransportStatus status)
    {
        if ((status.State is TransportConnectionState.Disconnected or TransportConnectionState.Faulted) &&
            HasPendingOffer)
        {
            var offer = _outgoingOffer ?? _incomingOffer!.Value;
            var wasLocal = _outgoingOffer is not null;
            _outgoingOffer = null;
            _incomingOffer = null;
            MatchOfferPendingChanged?.Invoke(false);
            MatchOfferResolved?.Invoke(new MatchOfferResolution(offer.Kind, false, wasLocal));
        }
        StatusChanged?.Invoke(status);
    }

    private void OnChatReceived(ChatCommand command) =>
        Chat.AcceptLive(command.Entry, (int)LocalSide.Opponent());

    private void OnMatchOfferReceived(MatchOfferRequest request)
    {
        var valid = !HasPendingOffer && !_session.Snapshot.IsFinished &&
                    request.PlyNumber == _session.Snapshot.PlyNumber &&
                    (request.Kind != MatchOfferKind.Undo ||
                     (_session.CanUndo && _session.Snapshot.SideToMove == LocalSide));
        if (!valid)
        {
            _ = RejectInvalidOfferAsync(request);
            return;
        }

        _incomingOffer = request;
        MatchOfferPendingChanged?.Invoke(true);
        MatchOfferReceived?.Invoke(request);
    }

    private void OnMatchOfferResponseReceived(MatchOfferResponse response)
    {
        if (_outgoingOffer is not { } request || response.RequestId != request.RequestId ||
            response.PlyNumber != request.PlyNumber || response.Kind != request.Kind)
        {
            SynchronizationFailed?.Invoke("收到无法匹配的对局申请响应。");
            return;
        }

        _outgoingOffer = null;
        MatchOfferPendingChanged?.Invoke(false);
        var accepted = response.Accepted && request.PlyNumber == _session.Snapshot.PlyNumber;
        if (accepted && !ApplyOffer(request.Kind))
        {
            accepted = false;
            SynchronizationFailed?.Invoke("对方同意了申请，但本地无法应用到当前局面。");
        }
        MatchOfferResolved?.Invoke(new MatchOfferResolution(request.Kind, accepted, true));
    }

    private async Task RejectInvalidOfferAsync(MatchOfferRequest request)
    {
        try
        {
            await _transport.SendMatchOfferResponseAsync(new MatchOfferResponse(
                request.RequestId, request.PlyNumber, request.Kind, false));
        }
        catch (Exception error)
        {
            SynchronizationFailed?.Invoke(error.Message);
        }
    }

    private bool ApplyOffer(MatchOfferKind kind) => kind switch
    {
        MatchOfferKind.Undo => _session.Undo(),
        MatchOfferKind.Draw => _session.DeclareDraw(),
        _ => false
    };

    private void OnTimeoutReceived(TimeoutCommand command)
    {
        if (command.PlyNumber != _session.Snapshot.PlyNumber)
        {
            SynchronizationFailed?.Invoke(
                $"超时裁决局面序号不一致：本地 {_session.Snapshot.PlyNumber}，收到 {command.PlyNumber}。");
            return;
        }
        _session.DeclareTimeout(command.Loser);
    }
}
