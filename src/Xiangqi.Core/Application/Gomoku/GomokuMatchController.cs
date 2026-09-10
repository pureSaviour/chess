using System.Text.Json;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Gomoku;

public readonly record struct GomokuRoomOptions(int BoardSize, GomokuRuleSet Rules, StoneColor HostColor)
{
    public void Validate() { _ = new SquareGrid(BoardSize); if (!Enum.IsDefined(Rules) || !Enum.IsDefined(HostColor)) throw new ArgumentOutOfRangeException(nameof(Rules)); }
}
public readonly record struct GomokuSubmitResult(bool Accepted, bool Pending, GomokuMoveRejection Rejection, string? Error);

/// <summary>Read-only player boundary for local or host-authoritative Gomoku. UI submits a point
/// and waits for Changed; it never edits occupancy or judges forbidden moves. Events are raised
/// synchronously on the driving thread. The screen owns the controller and disposes it once.</summary>
public interface IGomokuMatchController : IAsyncDisposable
{
    event Action? Changed;
    event Action<string>? Message;
    GomokuSnapshot Snapshot { get; }
    StoneColor? LocalColor { get; }
    bool IsReady { get; }
    bool IsConnected { get; }
    bool CanPlace { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    void Poll();
    ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default);
    GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null);
}

public sealed class LocalGomokuMatchController : IGomokuMatchController
{
    private readonly GomokuGame _game;
    public LocalGomokuMatchController(int boardSize = 15, GomokuRuleSet rules = GomokuRuleSet.Freestyle) => _game = new(boardSize, rules);
    public event Action? Changed;
    public event Action<string>? Message { add { } remove { } }
    public GomokuSnapshot Snapshot => _game.Snapshot;
    public StoneColor? LocalColor => null;
    public bool IsReady => true;
    public bool IsConnected => true;
    public bool CanPlace => !_game.IsFinished;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }
    public ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); var result = _game.TryPlace(point); if (result.IsSuccess) Changed?.Invoke(); return ValueTask.FromResult(new GomokuSubmitResult(result.IsSuccess, false, result.Rejection, result.IsSuccess ? null : result.Rejection.ToString())); }
    public GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) => new(_game.Snapshot.BoardSize, _game.Rules, [], StoneColor.Black, _game.History.ToArray(), tags ?? new Dictionary<string, string>());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Versioned, bounded, host-authoritative Gomoku coordinator over the shared reliable
/// packet port. Host configuration is sent in hello; client requests carry expected ply and the
/// host republishes a full validated main line. Unknown, malformed, stale, or role-inappropriate
/// packets fail closed. The trusted P2P host owns adjudication; reconnect/resume is not provided.</summary>
public sealed class NetworkGomokuMatchController : IGomokuMatchController
{
    private const int Protocol = 1, MaximumBytes = 16_000;
    private readonly ITwoPlayerPacketTransport _transport;
    private GomokuRoomOptions? _options;
    private GomokuGame? _game;
    private bool _disposed;

    public NetworkGomokuMatchController(ITwoPlayerPacketTransport transport, GomokuRoomOptions? hostOptions)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (transport.IsPacketHost != hostOptions.HasValue) throw new ArgumentException("只有房主可提供五子棋房间配置。", nameof(hostOptions));
        if (hostOptions is { } options) { options.Validate(); _options = options; _game = new(options.BoardSize, options.Rules); LocalColor = options.HostColor; }
        _transport.PacketReceived += OnPacket; _transport.PacketStatusChanged += OnStatus;
    }
    public event Action? Changed;
    public event Action<string>? Message;
    public GomokuSnapshot Snapshot => _game?.Snapshot ?? new(15, GomokuRuleSet.Freestyle, [], StoneColor.Black, 0, false, null, null, null);
    public StoneColor? LocalColor { get; private set; }
    public bool IsReady => _game is not null && LocalColor is not null;
    public bool IsConnected => _transport.IsPacketConnected;
    public bool CanPlace => IsReady && IsConnected && !Snapshot.IsFinished && Snapshot.SideToMove == LocalColor;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }
    public async ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default)
    {
        if (!CanPlace) return new(false, false, GomokuMoveRejection.NotPlayersTurn, "当前不能落子。");
        if (!_transport.IsPacketHost) { await Send(new Packet(Protocol, "move", Snapshot.PlyNumber, point.Column, point.Row, 0, 0, null), cancellationToken); return new(true, true, GomokuMoveRejection.None, null); }
        var result = _game!.TryPlace(point, LocalColor); if (!result.IsSuccess) return new(false, false, result.Rejection, result.Rejection.ToString());
        Changed?.Invoke(); await Broadcast(cancellationToken); return new(true, false, GomokuMoveRejection.None, null);
    }
    public GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null)
    { if (_game is null) throw new InvalidOperationException("对局尚未就绪。"); return new(_game.Snapshot.BoardSize, _game.Rules, [], StoneColor.Black, _game.History.ToArray(), tags ?? new Dictionary<string, string>()); }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; _transport.PacketReceived -= OnPacket; _transport.PacketStatusChanged -= OnStatus; await _transport.DisposeAsync(); }

    private void OnStatus(TransportStatus status)
    { Message?.Invoke(status.Detail ?? status.State.ToString()); if (status.State == TransportConnectionState.Connected && _transport.IsPacketHost) _ = SendHello(); Changed?.Invoke(); }
    private async Task SendHello() { var o = _options!.Value; await Send(new(Protocol, "hello", 0, 0, 0, o.BoardSize, (int)o.Rules, ((int)o.HostColor).ToString())); await Broadcast(); }
    private void OnPacket(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { Message?.Invoke("五子棋数据大小无效。"); return; }
        Packet? packet; try { packet = JsonSerializer.Deserialize<Packet>(bytes.Span); } catch (JsonException) { Message?.Invoke("五子棋数据格式无效。"); return; }
        if (packet is null || packet.Version != Protocol) { Message?.Invoke("五子棋协议版本不兼容。"); return; }
        if (packet.Kind == "hello" && !_transport.IsPacketHost) ReceiveHello(packet);
        else if (packet.Kind == "move" && _transport.IsPacketHost) ReceiveMove(packet);
        else if (packet.Kind == "state" && !_transport.IsPacketHost) ReceiveState(packet);
        else Message?.Invoke("五子棋消息与当前端角色不匹配。");
    }
    private void ReceiveHello(Packet packet)
    {
        if (_game is not null || packet.BoardSize is < SquareGrid.MinimumSize or > SquareGrid.MaximumSize || packet.Rules is < 0 or > 1 || packet.Data is not ("0" or "1")) { Message?.Invoke("房主五子棋配置无效。"); return; }
        var host = (StoneColor)int.Parse(packet.Data); _options = new(packet.BoardSize, (GomokuRuleSet)packet.Rules, host); _game = new(packet.BoardSize, (GomokuRuleSet)packet.Rules); LocalColor = host == StoneColor.Black ? StoneColor.White : StoneColor.Black; Changed?.Invoke();
    }
    private void ReceiveMove(Packet packet)
    {
        if (_game is null || packet.Ply != _game.Snapshot.PlyNumber || LocalColor is null) { Message?.Invoke("远端落子的局面序号无效。"); return; }
        var remote = LocalColor == StoneColor.Black ? StoneColor.White : StoneColor.Black; var result = _game.TryPlace(new(packet.Column, packet.Row), remote);
        if (!result.IsSuccess) { Message?.Invoke($"房主拒绝远端落子：{result.Rejection}。"); return; } Changed?.Invoke(); _ = Broadcast();
    }
    private void ReceiveState(Packet packet)
    {
        if (_options is null || packet.Data is null) return;
        try
        {
            var moves = DecodeHistory(packet.Data); var candidate = new GomokuGame(_options.Value.BoardSize, _options.Value.Rules);
            foreach (var move in moves) { var result = candidate.TryPlace(move.Point, move.Color); if (!result.IsSuccess || result.Move.Sequence != move.Sequence) throw new InvalidDataException(); }
            if (_game is not null)
            {
                var current = _game.History;
                if (candidate.Snapshot.PlyNumber == _game.Snapshot.PlyNumber)
                { if (!candidate.History.SequenceEqual(current)) throw new InvalidDataException(); return; }
                if (candidate.Snapshot.PlyNumber != _game.Snapshot.PlyNumber + 1) throw new InvalidDataException();
            }
            _game = candidate; Changed?.Invoke();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or FormatException) { Message?.Invoke("房主五子棋状态无法通过重放验证。"); }
    }
    private ValueTask Broadcast(CancellationToken cancellationToken = default) => Send(new(Protocol, "state", Snapshot.PlyNumber, 0, 0, Snapshot.BoardSize, (int)Snapshot.Rules, EncodeHistory(_game!.History)), cancellationToken);
    private static string EncodeHistory(IReadOnlyList<GomokuMove> moves)
    {
        var bytes = new byte[moves.Count * 3];
        for (var i = 0; i < moves.Count; i++) { bytes[i * 3] = (byte)moves[i].Color; bytes[i * 3 + 1] = checked((byte)moves[i].Point.Column); bytes[i * 3 + 2] = checked((byte)moves[i].Point.Row); }
        return Convert.ToBase64String(bytes);
    }
    private static GomokuMove[] DecodeHistory(string data)
    {
        var bytes = Convert.FromBase64String(data); if (bytes.Length % 3 != 0 || bytes.Length > SquareGrid.MaximumSize * SquareGrid.MaximumSize * 3) throw new InvalidDataException();
        var moves = new GomokuMove[bytes.Length / 3];
        for (var i = 0; i < moves.Length; i++) { if (bytes[i * 3] > 1) throw new InvalidDataException(); moves[i] = new(i + 1L, (StoneColor)bytes[i * 3], new(bytes[i * 3 + 1], bytes[i * 3 + 2])); }
        return moves;
    }
    private ValueTask Send(Packet packet, CancellationToken cancellationToken = default)
    { var bytes = JsonSerializer.SerializeToUtf8Bytes(packet); if (bytes.Length > MaximumBytes) throw new InvalidOperationException("五子棋数据超过协议上限。"); return _transport.SendPacketAsync(bytes, cancellationToken); }
    private sealed record Packet(int Version, string Kind, long Ply, int Column, int Row, int BoardSize, int Rules, string? Data);
}
