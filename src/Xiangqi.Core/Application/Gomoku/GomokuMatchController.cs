using System.Text.Json;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Gomoku;

public readonly record struct GomokuRoomOptions(int BoardSize, GomokuRuleSet Rules, StoneColor HostColor,
    TimeControl TimeControl = default)
{
    public void Validate() { _ = new SquareGrid(BoardSize); if (!Enum.IsDefined(Rules) || !Enum.IsDefined(HostColor)) throw new ArgumentOutOfRangeException(nameof(Rules)); TimeControl.Validate(); }
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
    TimeControl TimeControl { get; }
    SeatClockSnapshot Clock { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    void Poll();
    void Tick(double seconds);
    ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default);
    GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null);
}

public sealed class LocalGomokuMatchController : IGomokuMatchController
{
    private readonly GomokuGame _game;
    private readonly TwoSeatMatchClock _clock;
    public LocalGomokuMatchController(int boardSize = 15, GomokuRuleSet rules = GomokuRuleSet.Freestyle,
        TimeControl? timeControl = null) { _game = new(boardSize, rules); TimeControl=(timeControl??TimeControl.Unlimited).Validate(); _clock=new(TimeControl){IsRunning=true}; }
    public event Action? Changed;
    public event Action<string>? Message { add { } remove { } }
    public GomokuSnapshot Snapshot => _game.Snapshot;
    public StoneColor? LocalColor => null;
    public bool IsReady => true;
    public bool IsConnected => true;
    public bool CanPlace => !_game.IsFinished;
    public TimeControl TimeControl { get; }
    public SeatClockSnapshot Clock => _clock.Snapshot;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }
    public void Tick(double seconds) { var loser=_clock.Advance(seconds); if(loser is null)return; _clock.IsRunning=false; if(_game.DeclareTimeout((StoneColor)loser.Value))Changed?.Invoke(); }
    public ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); var result = _game.TryPlace(point); if (result.IsSuccess){_clock.BeginTurn((int)_game.SideToMove);Changed?.Invoke();} return ValueTask.FromResult(new GomokuSubmitResult(result.IsSuccess, false, result.Rejection, result.IsSuccess ? null : result.Rejection.ToString())); }
    public GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) => new(_game.Snapshot.BoardSize, _game.Rules, [], StoneColor.Black, _game.History.ToArray(), tags ?? new Dictionary<string, string>());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Versioned, bounded, host-authoritative Gomoku coordinator over the shared reliable
/// packet port. Host configuration is sent in hello; client requests carry expected ply and the
/// host republishes a full validated main line. Unknown, malformed, stale, or role-inappropriate
/// packets fail closed. The trusted P2P host owns adjudication; reconnect/resume is not provided.</summary>
public sealed class NetworkGomokuMatchController : IGomokuMatchController, IRematchableMatchController
{
    private const int Protocol = 1, MaximumBytes = 16_000;
    private readonly ITwoPlayerPacketTransport _transport;
    private GomokuRoomOptions? _options;
    private GomokuGame? _game;
    private TwoSeatMatchClock _clock = new(TimeControl.Unlimited);
    private bool _disposed;
    private double _clockSyncAccumulator;
    private readonly TwoPlayerRematchNegotiation _rematch=new();

    public NetworkGomokuMatchController(ITwoPlayerPacketTransport transport, GomokuRoomOptions? hostOptions)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (transport.IsPacketHost != hostOptions.HasValue) throw new ArgumentException("只有房主可提供五子棋房间配置。", nameof(hostOptions));
        if (hostOptions is { } options) { options.Validate(); _options = options; _game = new(options.BoardSize, options.Rules); _clock=new(options.TimeControl); LocalColor = options.HostColor; }
        _transport.PacketReceived += OnPacket; _transport.PacketStatusChanged += OnStatus;
    }
    public event Action? Changed;
    public event Action<string>? Message;
    public GomokuSnapshot Snapshot => _game?.Snapshot ?? new(15, GomokuRuleSet.Freestyle, [], StoneColor.Black, 0, false, null, null, null);
    public StoneColor? LocalColor { get; private set; }
    public bool IsReady => _game is not null && LocalColor is not null;
    public bool IsConnected => _transport.IsPacketConnected;
    public bool CanPlace => IsReady && IsConnected && !Snapshot.IsFinished && Snapshot.SideToMove == LocalColor;
    public TimeControl TimeControl => _options?.TimeControl ?? TimeControl.Unlimited;
    public SeatClockSnapshot Clock => _clock.Snapshot;
    public RematchSnapshot Rematch=>_rematch.Snapshot;
    public bool CanRequestRematch=>IsConnected&&Snapshot.IsFinished&&!_rematch.Snapshot.LocalReady;
    public async ValueTask RequestRematchAsync(CancellationToken cancellationToken=default){if(!CanRequestRematch)throw new InvalidOperationException("当前不能申请再来一局。");var start=_rematch.MarkLocalReady();await Send(new(Protocol,"rematch",_rematch.Snapshot.Round,0,0,0,0,"1"),cancellationToken);if(start)StartRematch();Changed?.Invoke();}
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public void Poll() { }
    public void Tick(double seconds)
    {
        if (!_transport.IsPacketHost || !IsConnected || _game is null || _game.IsFinished) return;
        var loser=_clock.Advance(seconds);_clockSyncAccumulator+=seconds;if(loser is null){if(_clockSyncAccumulator>=.25){_clockSyncAccumulator=0;_=Broadcast();}return;}
        _clock.IsRunning=false; if(_game.DeclareTimeout((StoneColor)loser.Value)){Changed?.Invoke();_ = Broadcast();}
    }
    public async ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default)
    {
        if (!CanPlace) return new(false, false, GomokuMoveRejection.NotPlayersTurn, "当前不能落子。");
        if (!_transport.IsPacketHost) { await Send(new Packet(Protocol, "move", Snapshot.PlyNumber, point.Column, point.Row, 0, 0, null), cancellationToken); return new(true, true, GomokuMoveRejection.None, null); }
        var result = _game!.TryPlace(point, LocalColor); if (!result.IsSuccess) return new(false, false, result.Rejection, result.Rejection.ToString());
        _clock.BeginTurn((int)_game.SideToMove); Changed?.Invoke(); await Broadcast(cancellationToken); return new(true, false, GomokuMoveRejection.None, null);
    }
    public GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null)
    { if (_game is null) throw new InvalidOperationException("对局尚未就绪。"); return new(_game.Snapshot.BoardSize, _game.Rules, [], StoneColor.Black, _game.History.ToArray(), tags ?? new Dictionary<string, string>()); }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; _transport.PacketReceived -= OnPacket; _transport.PacketStatusChanged -= OnStatus; await _transport.DisposeAsync(); }

    private void OnStatus(TransportStatus status)
    { Message?.Invoke(status.Detail ?? status.State.ToString()); if (status.State == TransportConnectionState.Connected && _transport.IsPacketHost) _ = SendHello(); Changed?.Invoke(); }
    private async Task SendHello() { var o = _options!.Value; await Send(new(Protocol, "hello", 0, 0, 0, o.BoardSize, (int)o.Rules, ((int)o.HostColor).ToString(),o.TimeControl.GameSeconds,o.TimeControl.MoveSeconds)); await Broadcast(); _clock.IsRunning=true; }
    private void OnPacket(ReadOnlyMemory<byte> bytes)
    {
        if (MatchWireProtocol.TryDecode(bytes.ToArray(), out var shared) &&
            shared.Type is MatchWireMessageType.PlayerProfile or MatchWireMessageType.Chat) return;
        if (bytes.Length is 0 or > MaximumBytes) { Message?.Invoke("五子棋数据大小无效。"); return; }
        Packet? packet; try { packet = JsonSerializer.Deserialize<Packet>(bytes.Span); } catch (JsonException) { Message?.Invoke("五子棋数据格式无效。"); return; }
        if (packet is null || packet.Version != Protocol) { Message?.Invoke("五子棋协议版本不兼容。"); return; }
        if (packet.Kind == "hello" && !_transport.IsPacketHost) ReceiveHello(packet);
        else if (packet.Kind == "move" && _transport.IsPacketHost) ReceiveMove(packet);
        else if (packet.Kind == "state" && !_transport.IsPacketHost) ReceiveState(packet);
        else if(packet.Kind=="rematch")ReceiveRematch(packet);
        else Message?.Invoke("五子棋消息与当前端角色不匹配。");
    }
    private void ReceiveRematch(Packet packet){if(!Snapshot.IsFinished||packet.Data!="1"||!_rematch.AcceptRemoteReady(packet.Ply)){Message?.Invoke("忽略了失效的再来一局请求。");return;}if(_rematch.Snapshot.LocalReady)StartRematch();else{Message?.Invoke("对手已选择再来一局。");Changed?.Invoke();}}
    private void StartRematch(){if(_options is null||LocalColor is null)return;LocalColor=LocalColor==StoneColor.Black?StoneColor.White:StoneColor.Black;var o=_options.Value;_options=o with{HostColor=o.HostColor==StoneColor.Black?StoneColor.White:StoneColor.Black};_game=new(o.BoardSize,o.Rules);_clock=new(o.TimeControl){IsRunning=true};_clockSyncAccumulator=0;_rematch.BeginNextRound();Message?.Invoke("新对局已开始，双方已交换黑白。");Changed?.Invoke();if(_transport.IsPacketHost)_=Broadcast();}
    private void ReceiveHello(Packet packet)
    {
        if (_game is not null || packet.BoardSize is < SquareGrid.MinimumSize or > SquareGrid.MaximumSize || packet.Rules is < 0 or > 1 || packet.Data is not ("0" or "1")) { Message?.Invoke("房主五子棋配置无效。"); return; }
        if(packet.GameSeconds<0||packet.MoveSeconds<0){Message?.Invoke("房主五子棋计时配置无效。");return;} var host = (StoneColor)int.Parse(packet.Data); _options = new(packet.BoardSize, (GomokuRuleSet)packet.Rules, host,new(packet.GameSeconds,packet.MoveSeconds)); _game = new(packet.BoardSize, (GomokuRuleSet)packet.Rules); _clock=new(_options.Value.TimeControl); LocalColor = host == StoneColor.Black ? StoneColor.White : StoneColor.Black; Changed?.Invoke();
    }
    private void ReceiveMove(Packet packet)
    {
        if (_game is null || packet.Ply != _game.Snapshot.PlyNumber || LocalColor is null) { Message?.Invoke("远端落子的局面序号无效。"); return; }
        var remote = LocalColor == StoneColor.Black ? StoneColor.White : StoneColor.Black; var result = _game.TryPlace(new(packet.Column, packet.Row), remote);
        if (!result.IsSuccess) { Message?.Invoke($"房主拒绝远端落子：{result.Rejection}。"); return; } _clock.BeginTurn((int)_game.SideToMove); Changed?.Invoke(); _ = Broadcast();
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
                { if (!candidate.History.SequenceEqual(current)) throw new InvalidDataException();
                  if(packet.TimeoutSeat is 0 or 1)candidate.DeclareTimeout((StoneColor)packet.TimeoutSeat.Value);
                  if(packet.Clock is not null)_clock.Apply(new(packet.Clock.First,packet.Clock.Second,packet.Clock.Move,packet.Clock.Active));
                  _game=candidate;_clock.IsRunning=!candidate.IsFinished;Changed?.Invoke();return; }
                if (candidate.Snapshot.PlyNumber != _game.Snapshot.PlyNumber + 1) throw new InvalidDataException();
            }
            if(packet.TimeoutSeat is 0 or 1)candidate.DeclareTimeout((StoneColor)packet.TimeoutSeat.Value);
            if(packet.Clock is not null)_clock.Apply(new(packet.Clock.First,packet.Clock.Second,packet.Clock.Move,packet.Clock.Active));
            _game = candidate; _clock.IsRunning=!candidate.IsFinished; Changed?.Invoke();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or FormatException) { Message?.Invoke("房主五子棋状态无法通过重放验证。"); }
    }
    private ValueTask Broadcast(CancellationToken cancellationToken = default) {var c=_clock.Snapshot;int? loser=Snapshot.EndReason==GomokuEndReason.Timeout?Snapshot.Winner==StoneColor.Black?1:0:null;return Send(new(Protocol, "state", Snapshot.PlyNumber, 0, 0, Snapshot.BoardSize, (int)Snapshot.Rules, EncodeHistory(_game!.History),TimeControl.GameSeconds,TimeControl.MoveSeconds,new(c.FirstGameSeconds,c.SecondGameSeconds,c.MoveSeconds,c.ActiveSeat),loser), cancellationToken);}
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
    private sealed record Packet(int Version, string Kind, long Ply, int Column, int Row, int BoardSize, int Rules, string? Data,
        int GameSeconds=0,int MoveSeconds=0,SeatClockDto? Clock=null,int? TimeoutSeat=null);
    private sealed record SeatClockDto(double First,double Second,double Move,int Active);
}
