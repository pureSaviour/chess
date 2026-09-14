using System.Text.Json;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Go;

public readonly record struct GoRoomOptions(GoStone HostColor)
{
    public void Validate() { if (!Enum.IsDefined(HostColor)) throw new ArgumentOutOfRangeException(nameof(HostColor)); }
}
public readonly record struct GoSubmitResult(bool Accepted, bool Pending, GoRejection Rejection, string? Error);

/// <summary>
/// Read-only UI boundary for local or host-authoritative Go. Consumers submit semantic actions and
/// render immutable snapshots; they never mutate occupancy, remove captures or calculate territory.
/// Events are synchronous on the driving thread. The screen owns and disposes the controller once.
/// </summary>
public interface IGoMatchController : IAsyncDisposable
{
    event Action? Changed;
    event Action<string>? Message;
    GoSnapshot Snapshot { get; }
    GoStone? LocalColor { get; }
    bool IsReady { get; }
    bool IsConnected { get; }
    bool CanAct { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    void Poll();
    ValueTask<GoSubmitResult> SubmitAsync(GoActionKind kind, GridPoint? point = null,
        CancellationToken cancellationToken = default);
    GoGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null);
}

/// <summary>Caller-thread hot-seat controller; it owns one managed aggregate and no transport.</summary>
public sealed class LocalGoMatchController : IGoMatchController
{
    private readonly GoGame _game = new();
    public event Action? Changed;
    public event Action<string>? Message { add { } remove { } }
    public GoSnapshot Snapshot => _game.Snapshot;
    public GoStone? LocalColor => null;
    public bool IsReady => true;
    public bool IsConnected => true;
    public bool CanAct => _game.Phase != GoPhase.Finished;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }
    public ValueTask<GoSubmitResult> SubmitAsync(GoActionKind kind, GridPoint? point = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Apply(_game, kind, point);
        if (result.IsSuccess) Changed?.Invoke();
        return ValueTask.FromResult(new GoSubmitResult(result.IsSuccess, false, result.Rejection,
            result.IsSuccess ? null : result.Rejection.ToString()));
    }
    public GoGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) =>
        GoGameRecord.FromGame(_game, tags);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <summary>Routes one validated semantic command to the aggregate; it adds no permissions.</summary>
    public static GoActionResult Apply(GoGame game, GoActionKind kind, GridPoint? point,
        GoStone? actor = null) => kind switch
    {
        GoActionKind.Place when point is { } value => game.Place(value, actor),
        GoActionKind.Pass when point is null => game.Pass(actor),
        GoActionKind.MarkDead when point is { } value => game.ToggleDead(value, actor),
        GoActionKind.ConfirmScore when point is null => game.ConfirmScore(actor),
        GoActionKind.Resign when point is null => game.Resign(actor),
        _ => GoActionResult.Rejected(new(game.Actions.Count + 1, actor ?? game.SideToMove, kind, point),
            GoRejection.WrongPhase)
    };
}

/// <summary>
/// Versioned host-authoritative Go coordinator over the shared reliable two-player packet port.
/// The host validates every requested action and publishes a bounded action log; clients rebuild a
/// fresh aggregate and accept only the next validated state. Chat/profile packets are ignored here.
/// Unknown versions, malformed payloads, stale sequences and wrong roles fail closed without
/// changing the game. The controller owns the transport and disposal is idempotent.
/// </summary>
public sealed class NetworkGoMatchController : IGoMatchController
{
    private const int Protocol = 1, MaximumBytes = 32_000;
    private readonly ITwoPlayerPacketTransport _transport;
    private GoRoomOptions? _options;
    private GoGame? _game;
    private bool _disposed;

    public NetworkGoMatchController(ITwoPlayerPacketTransport transport, GoRoomOptions? hostOptions)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (transport.IsPacketHost != hostOptions.HasValue)
            throw new ArgumentException("只有房主可提供围棋房间配置。", nameof(hostOptions));
        if (hostOptions is { } options)
        {
            options.Validate(); _options = options; _game = new(); LocalColor = options.HostColor;
        }
        _transport.PacketReceived += OnPacket;
        _transport.PacketStatusChanged += OnStatus;
    }
    public event Action? Changed;
    public event Action<string>? Message;
    public GoSnapshot Snapshot => _game?.Snapshot ?? new GoGame().Snapshot;
    public GoStone? LocalColor { get; private set; }
    public bool IsReady => _game is not null && LocalColor is not null;
    public bool IsConnected => _transport.IsPacketConnected;
    public bool CanAct => IsReady && IsConnected && Snapshot.Phase != GoPhase.Finished && Snapshot.SideToMove == LocalColor;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }

    public async ValueTask<GoSubmitResult> SubmitAsync(GoActionKind kind, GridPoint? point = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanAct) return new(false, false, GoRejection.NotPlayersTurn, "当前不能执行该操作。");
        if (!_transport.IsPacketHost)
        {
            await Send(new(Protocol, "action", Snapshot.ActionNumber, (int)kind,
                point?.Column ?? -1, point?.Row ?? -1, null, null), cancellationToken);
            return new(true, true, GoRejection.None, null);
        }
        var result = LocalGoMatchController.Apply(_game!, kind, point, LocalColor);
        if (!result.IsSuccess) return new(false, false, result.Rejection, result.Rejection.ToString());
        Changed?.Invoke(); await Broadcast(cancellationToken);
        return new(true, false, GoRejection.None, null);
    }

    public GoGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) =>
        _game is null ? throw new InvalidOperationException("对局尚未就绪。") : GoGameRecord.FromGame(_game, tags);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.PacketReceived -= OnPacket;
        _transport.PacketStatusChanged -= OnStatus;
        await _transport.DisposeAsync();
    }

    private void OnStatus(TransportStatus status)
    {
        Message?.Invoke(status.Detail ?? status.State.ToString());
        if (status.State == TransportConnectionState.Connected && _transport.IsPacketHost) _ = SendHello();
        Changed?.Invoke();
    }
    private async Task SendHello()
    {
        await Send(new(Protocol, "hello", 0, 0, 0, 0, ((int)_options!.Value.HostColor).ToString(), null));
        await Broadcast();
    }
    private void OnPacket(ReadOnlyMemory<byte> bytes)
    {
        if (MatchWireProtocol.TryDecode(bytes.ToArray(), out var shared) &&
            shared.Type is MatchWireMessageType.PlayerProfile or MatchWireMessageType.Chat) return;
        if (bytes.Length is 0 or > MaximumBytes) { Message?.Invoke("围棋数据大小无效。"); return; }
        Packet? packet;
        try { packet = JsonSerializer.Deserialize<Packet>(bytes.Span); }
        catch (JsonException) { Message?.Invoke("围棋数据格式无效。"); return; }
        if (packet is null || packet.Version != Protocol) { Message?.Invoke("围棋协议版本不兼容。"); return; }
        if (packet.Kind == "hello" && !_transport.IsPacketHost) ReceiveHello(packet);
        else if (packet.Kind == "action" && _transport.IsPacketHost) ReceiveAction(packet);
        else if (packet.Kind == "state" && !_transport.IsPacketHost) ReceiveState(packet);
        else Message?.Invoke("围棋消息与当前端角色不匹配。");
    }
    private void ReceiveHello(Packet packet)
    {
        if (_game is not null || packet.Data is not ("0" or "1")) { Message?.Invoke("房主围棋配置无效。"); return; }
        var host = (GoStone)int.Parse(packet.Data);
        _options = new(host); _game = new(); LocalColor = Opponent(host); Changed?.Invoke();
    }
    private void ReceiveAction(Packet packet)
    {
        if (_game is null || packet.Sequence != _game.Snapshot.ActionNumber || LocalColor is null ||
            packet.ActionKind < 0 || packet.ActionKind > (int)GoActionKind.Resign ||
            (packet.Column == -1) != (packet.Row == -1) || packet.Column < -1 || packet.Row < -1)
        { Message?.Invoke("远端围棋操作的局面序号或类型无效。"); return; }
        var remote = Opponent(LocalColor.Value);
        GridPoint? point = packet.Column == -1 ? null : new GridPoint(packet.Column, packet.Row);
        var result = LocalGoMatchController.Apply(_game, (GoActionKind)packet.ActionKind, point, remote);
        if (!result.IsSuccess) { Message?.Invoke($"房主拒绝远端操作：{result.Rejection}。"); return; }
        Changed?.Invoke(); _ = Broadcast();
    }
    private void ReceiveState(Packet packet)
    {
        if (_options is null || packet.Data is null) return;
        try
        {
            var actions = DecodeActions(packet.Data);
            var candidate = new GoGame();
            foreach (var action in actions)
            {
                var result = LocalGoMatchController.Apply(candidate, action.Kind, action.Point, action.Actor);
                if (!result.IsSuccess || result.Action.Sequence != action.Sequence) throw new InvalidDataException();
            }
            if (_game is not null)
            {
                if (candidate.Snapshot.ActionNumber == _game.Snapshot.ActionNumber)
                { if (!candidate.Actions.SequenceEqual(_game.Actions)) throw new InvalidDataException(); return; }
                if (candidate.Snapshot.ActionNumber != _game.Snapshot.ActionNumber + 1) throw new InvalidDataException();
            }
            _game = candidate; Changed?.Invoke();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or FormatException)
        { Message?.Invoke("房主围棋状态无法通过重放验证。"); }
    }
    private ValueTask Broadcast(CancellationToken cancellationToken = default) =>
        Send(new(Protocol, "state", Snapshot.ActionNumber, 0, 0, 0, EncodeActions(_game!.Actions), null), cancellationToken);
    private static string EncodeActions(IReadOnlyList<GoAction> actions)
    {
        var bytes = new byte[actions.Count * 4];
        for (var i = 0; i < actions.Count; i++)
        {
            bytes[i * 4] = (byte)actions[i].Kind;
            bytes[i * 4 + 1] = (byte)actions[i].Actor;
            bytes[i * 4 + 2] = actions[i].Point is { } p ? checked((byte)p.Column) : byte.MaxValue;
            bytes[i * 4 + 3] = actions[i].Point is { } q ? checked((byte)q.Row) : byte.MaxValue;
        }
        return Convert.ToBase64String(bytes);
    }
    private static GoAction[] DecodeActions(string data)
    {
        var bytes = Convert.FromBase64String(data);
        if (bytes.Length % 4 != 0 || bytes.Length > GoGame.MaximumActions * 4) throw new InvalidDataException();
        var actions = new GoAction[bytes.Length / 4];
        for (var i = 0; i < actions.Length; i++)
        {
            if (bytes[i * 4] > (byte)GoActionKind.Resign || bytes[i * 4 + 1] > 1) throw new InvalidDataException();
            var hasPoint = bytes[i * 4 + 2] != byte.MaxValue || bytes[i * 4 + 3] != byte.MaxValue;
            if ((bytes[i * 4 + 2] == byte.MaxValue) != (bytes[i * 4 + 3] == byte.MaxValue)) throw new InvalidDataException();
            actions[i] = new(i + 1L, (GoStone)bytes[i * 4 + 1], (GoActionKind)bytes[i * 4],
                hasPoint ? new(bytes[i * 4 + 2], bytes[i * 4 + 3]) : null);
        }
        return actions;
    }
    private ValueTask Send(Packet packet, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("围棋数据超过协议上限。");
        return _transport.SendPacketAsync(bytes, cancellationToken);
    }
    private static GoStone Opponent(GoStone color) => color == GoStone.Black ? GoStone.White : GoStone.Black;
    private sealed record Packet(int Version, string Kind, long Sequence, int ActionKind,
        int Column, int Row, string? Data, string? Reserved);
}
