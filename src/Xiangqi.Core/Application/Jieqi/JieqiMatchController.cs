using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application.Jieqi;

public readonly record struct JieqiSubmitResult(bool Accepted, bool Pending, MoveRejection Rejection, string? Error)
{
    public static JieqiSubmitResult Applied() => new(true, false, MoveRejection.None, null);
    public static JieqiSubmitResult Queued() => new(true, true, MoveRejection.None, null);
    public static JieqiSubmitResult Rejected(MoveRejection rejection, string? error = null) => new(false, false, rejection, error);
}

/// <summary>
/// UI-facing Jieqi match boundary. Consumers submit intents and receive redacted snapshots; the
/// interface never exposes a live seed or authoritative hidden-piece aggregate. Events are
/// synchronous on the controller's driving thread. The screen owns and disposes the controller.
/// </summary>
public interface IJieqiMatchController : IAsyncDisposable
{
    event Action? Changed;
    event Action<string>? Message;
    GameSnapshot Snapshot { get; }
    PieceSide? LocalSide { get; }
    bool IsConnected { get; }
    bool CanSubmitMove { get; }
    TimeControl TimeControl { get; }
    ClockSnapshot Clock { get; }
    void Tick(double deltaSeconds);
    IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from);
    ValueTask<JieqiSubmitResult> SubmitMoveAsync(BoardPosition from, BoardPosition to,
        CancellationToken cancellationToken = default);
    ValueTask<bool> SubmitResignationAsync(PieceSide side, CancellationToken cancellationToken = default);
    bool TryCreateFinishedRecord(IReadOnlyDictionary<string, string> tags,
        out JieqiGameRecord? record, out string? error);
}

public sealed class LocalJieqiMatchController : IJieqiMatchController
{
    private readonly JieqiGameSession _session;
    private readonly MatchClock _clock;
    public LocalJieqiMatchController(ulong seed, TimeControl? timeControl = null)
    {
        TimeControl = (timeControl ?? TimeControl.Unlimited).Validate();
        _clock = new MatchClock(TimeControl) { IsRunning = true };
        _session = new(seed); _session.StateChanged += OnChanged;
        _clock.BeginTurn(_session.Snapshot.SideToMove);
    }
    public event Action? Changed;
    public event Action<string>? Message { add { } remove { } }
    public GameSnapshot Snapshot => _session.Snapshot;
    public PieceSide? LocalSide => null;
    public bool IsConnected => true;
    public bool CanSubmitMove => !Snapshot.IsFinished;
    public TimeControl TimeControl { get; }
    public ClockSnapshot Clock => _clock.Snapshot;
    public void Tick(double deltaSeconds)
    {
        var loser = _clock.Advance(deltaSeconds);
        if (loser is null) return;
        _clock.IsRunning = false;
        _session.DeclareLoss(loser.Value, GameEndReason.Timeout);
    }
    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from) => _session.GetLegalDestinations(from);
    public ValueTask<JieqiSubmitResult> SubmitMoveAsync(BoardPosition from, BoardPosition to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _session.TryMove(from, to);
        if (result.IsSuccess) _clock.BeginTurn(_session.Snapshot.SideToMove);
        return ValueTask.FromResult(result.IsSuccess ? JieqiSubmitResult.Applied() : JieqiSubmitResult.Rejected(result.Rejection));
    }
    public ValueTask<bool> SubmitResignationAsync(PieceSide side, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var applied = _session.DeclareLoss(side, GameEndReason.Resignation);
        if (applied) _clock.IsRunning = false;
        return ValueTask.FromResult(applied);
    }
    public bool TryCreateFinishedRecord(IReadOnlyDictionary<string, string> tags,
        out JieqiGameRecord? record, out string? error)
    {
        try { record = _session.CreateRecord(tags); error = null; return true; }
        catch (InvalidOperationException exception) { record = null; error = exception.Message; return false; }
    }
    public ValueTask DisposeAsync() { _session.StateChanged -= OnChanged; return ValueTask.CompletedTask; }
    private void OnChanged(GameSnapshot _) => Changed?.Invoke();
}

/// <summary>
/// Host-authoritative hidden-information controller. Only the host owns JieqiGameSession and the
/// seed; a client stores structurally validated public snapshots and sends state-bound requests.
/// The host commits SHA-256(seed, protocol, variant) before play and releases the seed only in a
/// terminal state. This detects reshuffling but cannot prevent a P2P host from inspecting memory.
/// </summary>
public sealed class NetworkJieqiMatchController : IJieqiMatchController, IRematchableMatchController
{
    private readonly ITwoPlayerPacketTransport _transport;
    private JieqiGameSession? _authority;
    private ulong? _authoritySeed;
    private readonly List<ChessMove> _moves = [];
    private readonly Dictionary<BoardPosition, IReadOnlyList<BoardPosition>> _legal = [];
    private GameSnapshot _public;
    private string? _commitment;
    private ulong? _releasedSeed;
    private long _nextRequest;
    private MatchClock _clock;
    private TimeControl _timeControl;
    private double _clockSyncAccumulator;
    private TaskCompletionSource<bool>? _pendingResignation;
    private long _pendingResignationRequest;
    private bool _disposed;
    private readonly TwoPlayerRematchNegotiation _rematch=new();

    public NetworkJieqiMatchController(ITwoPlayerPacketTransport transport, PieceSide? hostSide,
        ulong? hostSeed = null, TimeControl? timeControl = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.IsPacketHost && hostSide is null || hostSide is { } configured && !Enum.IsDefined(configured))
            throw new ArgumentOutOfRangeException(nameof(hostSide));
        _transport = transport; HostSide = hostSide;
        _timeControl = (timeControl ?? TimeControl.Unlimited).Validate();
        _clock = new MatchClock(_timeControl);
        LocalSide = transport.IsPacketHost ? hostSide : hostSide?.Opponent();
        if (transport.IsPacketHost)
        {
            _authoritySeed = hostSeed ?? RandomSeed(); _authority = new(_authoritySeed.Value);
            _public = _authority.Snapshot; _commitment = Commitment(_authoritySeed.Value);
            _clock.BeginTurn(_public.SideToMove);
            RefreshLegal();
        }
        else _public = new JieqiGameSession(0).Snapshot;
        _transport.PacketReceived += OnPacket;
        _transport.PacketStatusChanged += OnStatus;
    }

    public event Action? Changed;
    public event Action<string>? Message;
    public PieceSide? HostSide { get; private set; }
    public GameSnapshot Snapshot => _public;
    public PieceSide? LocalSide { get; private set; }
    public bool IsConnected => _transport.IsPacketConnected;
    public bool CanSubmitMove => IsConnected && !_public.IsFinished && _public.SideToMove == LocalSide;
    public TimeControl TimeControl => _timeControl;
    public ClockSnapshot Clock => _clock.Snapshot;
    public RematchSnapshot Rematch=>_rematch.Snapshot;
    public bool CanRequestRematch=>IsConnected&&_public.IsFinished&&!_rematch.Snapshot.LocalReady;
    public async ValueTask RequestRematchAsync(CancellationToken cancellationToken=default){if(!CanRequestRematch)throw new InvalidOperationException("当前不能申请再来一局。");var start=_rematch.MarkLocalReady();await SendAsync(new JieqiPacket("rematch",_rematch.Snapshot.Round,_public.PlyNumber,(int)LocalSide!.Value,-1,-1,-1,-1,null,null,null,null,"1"),cancellationToken);if(start)StartRematch();Changed?.Invoke();}

    public void Tick(double deltaSeconds)
    {
        if (_public.IsFinished) return;
        var loser = _clock.Advance(deltaSeconds);
        if (_authority is null) return;
        if (loser is not null)
        {
            _clock.IsRunning = false;
            if (_authority.DeclareLoss(loser.Value, GameEndReason.Timeout)) PublishAuthorityState();
            _ = SendStateAsync(0, null);
            return;
        }
        if (!IsConnected || !_timeControl.IsEnabled) return;
        _clockSyncAccumulator += deltaSeconds;
        if (_clockSyncAccumulator < 0.25) return;
        _clockSyncAccumulator = 0;
        _ = SendStateAsync(0, null);
    }
    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from) =>
        _legal.TryGetValue(from, out var value) ? value : [];

    public async ValueTask<JieqiSubmitResult> SubmitMoveAsync(BoardPosition from, BoardPosition to,
        CancellationToken cancellationToken = default)
    {
        if (!CanSubmitMove) return JieqiSubmitResult.Rejected(MoveRejection.NotPlayersTurn, "当前不能行棋。");
        if (_authority is null)
        {
            var request = ++_nextRequest;
            await SendAsync(new JieqiPacket("move", request, _public.PlyNumber, (int)LocalSide!.Value,
                from.File, from.Rank, to.File, to.Rank, null, null, null, null), cancellationToken);
            return JieqiSubmitResult.Queued();
        }
        var result = _authority.TryMove(from, to);
        if (!result.IsSuccess) return JieqiSubmitResult.Rejected(result.Rejection);
        _clock.BeginTurn(_authority.Snapshot.SideToMove);
        _moves.Add(result.Move); PublishAuthorityState();
        await SendStateAsync(0, result.Move, cancellationToken);
        return JieqiSubmitResult.Applied();
    }

    public async ValueTask<bool> SubmitResignationAsync(PieceSide side,
        CancellationToken cancellationToken = default)
    {
        if (_public.IsFinished || LocalSide != side) return false;
        if (_authority is null)
        {
            _pendingResignationRequest = ++_nextRequest;
            _pendingResignation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync(new JieqiPacket("resign", _pendingResignationRequest, _public.PlyNumber, (int)side,
                -1, -1, -1, -1, null, null, null, null), cancellationToken);
            try { return await _pendingResignation.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (TimeoutException) { return false; }
            finally { _pendingResignation = null; _pendingResignationRequest = 0; }
        }
        if (!_authority.DeclareLoss(side, GameEndReason.Resignation)) return false;
        _clock.IsRunning = false;
        PublishAuthorityState();
        await SendStateAsync(0, null, cancellationToken);
        return true;
    }

    public bool TryCreateFinishedRecord(IReadOnlyDictionary<string, string> tags,
        out JieqiGameRecord? record, out string? error)
    {
        record = null;
        if (!_public.IsFinished || (_authoritySeed is null && _releasedSeed is null))
        { error = "对局未结束或尚未收到赛后种子。"; return false; }
        var seed = _authoritySeed ?? _releasedSeed!.Value;
        var candidate = new JieqiGameRecord(seed, _moves.ToArray(), tags);
        try { _ = candidate.Replay(); record = candidate; error = null; return true; }
        catch (InvalidDataException exception) { error = exception.Message; return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _transport.PacketReceived -= OnPacket; _transport.PacketStatusChanged -= OnStatus;
        _pendingResignation?.TrySetResult(false);
        await _transport.DisposeAsync();
    }

    private void OnStatus(TransportStatus status)
    {
        Message?.Invoke(status.Detail ?? status.State.ToString());
        _clock.IsRunning = status.State == TransportConnectionState.Connected && !_public.IsFinished;
        if (status.State == TransportConnectionState.Connected && _authority is not null) _ = SendStateAsync(0, null);
        Changed?.Invoke();
    }

    private void OnPacket(ReadOnlyMemory<byte> bytes)
    {
        if (MatchWireProtocol.TryDecode(bytes.ToArray(), out var shared) &&
            shared.Type is MatchWireMessageType.PlayerProfile or MatchWireMessageType.Chat) return;
        if (!JieqiWireProtocol.TryDecode(bytes.Span, out var packet, out var error))
        { Message?.Invoke(error ?? "收到无效揭棋数据。"); return; }
        if (packet!.Type == "move") ReceiveMoveRequest(packet);
        else if (packet.Type == "resign") ReceiveResignationRequest(packet);
        else if (packet.Type == "state") ReceiveState(packet);
        else if(packet.Type=="rematch")ReceiveRematch(packet);
        else if(packet.Type=="sync"&&_authority is not null)_=SendStateAsync(0,null);
        else if (packet.Type == "reject" && _authority is null)
        {
            if (packet.RequestId == _pendingResignationRequest) _pendingResignation?.TrySetResult(false);
            Message?.Invoke(packet.Error ?? "房主拒绝了请求。");
        }
        else Message?.Invoke("收到与本机角色不匹配的揭棋消息。");
    }
    private void ReceiveRematch(JieqiPacket packet){if(!_public.IsFinished||packet.Error!="1"||!_rematch.AcceptRemoteReady(packet.RequestId)){Message?.Invoke("忽略了失效的再来一局请求。");return;}if(_rematch.Snapshot.LocalReady)StartRematch();else{Message?.Invoke("对手已选择再来一局。");Changed?.Invoke();}}
    private void StartRematch(){if(LocalSide is null||HostSide is null)return;LocalSide=LocalSide.Value.Opponent();HostSide=HostSide.Value.Opponent();_moves.Clear();_legal.Clear();_releasedSeed=null;if(_transport.IsPacketHost){_authoritySeed=RandomSeed();_authority=new(_authoritySeed.Value);_public=_authority.Snapshot;_commitment=Commitment(_authoritySeed.Value);RefreshLegal();}else{_authoritySeed=null;_authority=null;_commitment=null;_public=new JieqiGameSession(0).Snapshot;}_clock=new(_timeControl){IsRunning=_transport.IsPacketHost};_clockSyncAccumulator=0;_rematch.BeginNextRound();Message?.Invoke("新对局已开始，双方已交换红黑。");Changed?.Invoke();if(!_transport.IsPacketHost)_=SendAsync(new JieqiPacket("sync",0,0,(int)LocalSide.Value,-1,-1,-1,-1,null,null,null,null));}

    private void ReceiveMoveRequest(JieqiPacket packet)
    {
        if (_authority is null || packet.Side != (int)HostSide!.Value.Opponent() || packet.ExpectedPly != _public.PlyNumber ||
            packet.RequestId <= 0)
        { _ = SendRejectAsync(packet.RequestId, "着法身份、序号或局面版本无效。"); return; }
        var from = new BoardPosition(packet.FromFile, packet.FromRank); var to = new BoardPosition(packet.ToFile, packet.ToRank);
        var result = _authority.TryMove(from, to);
        if (!result.IsSuccess) { _ = SendRejectAsync(packet.RequestId, $"非法着法：{result.Rejection}。"); return; }
        _clock.BeginTurn(_authority.Snapshot.SideToMove);
        _moves.Add(result.Move); PublishAuthorityState(); _ = SendStateAsync(packet.RequestId, result.Move);
    }

    private void ReceiveResignationRequest(JieqiPacket packet)
    {
        if (_authority is null || packet.Side != (int)HostSide!.Value.Opponent() ||
            packet.ExpectedPly != _public.PlyNumber || packet.RequestId <= 0 || _public.IsFinished)
        { _ = SendRejectAsync(packet.RequestId, "认输身份、序号或局面版本无效。"); return; }
        if (!_authority.DeclareLoss((PieceSide)packet.Side, GameEndReason.Resignation))
        { _ = SendRejectAsync(packet.RequestId, "当前不能认输。"); return; }
        _clock.IsRunning = false;
        PublishAuthorityState();
        _ = SendStateAsync(packet.RequestId, null);
    }

    private void ReceiveState(JieqiPacket packet)
    {
        if (_authority is not null || packet.State is null || packet.Commitment is not { Length: 64 } ||
            !packet.Commitment.All(Uri.IsHexDigit))
        { Message?.Invoke("揭棋权威状态无效。"); return; }
        if (packet.Side is < 0 or > 1 || HostSide is { } knownHost && (int)knownHost != packet.Side)
        { Message?.Invoke("揭棋房主席位配置无效。"); return; }
        HostSide ??= (PieceSide)packet.Side;
        LocalSide ??= HostSide.Value.Opponent();
        if (_commitment is not null && !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(_commitment), Encoding.ASCII.GetBytes(packet.Commitment)))
        { Message?.Invoke("房主更换了揭棋随机承诺，已拒绝同步。"); return; }
        if (!TrySnapshot(packet.State, out var next, out var error))
        { Message?.Invoke(error ?? "揭棋公开局面无效。"); return; }
        if (!TryParseClock(packet, next!.SideToMove, out var timeControl, out var clock, out var clockError))
        { Message?.Invoke(clockError ?? "揭棋棋钟无效。"); return; }
        if (next!.PlyNumber < _public.PlyNumber || next.PlyNumber > _public.PlyNumber + 1)
        { Message?.Invoke("揭棋局面序号不连续。"); return; }
        var advanced = next.PlyNumber == _public.PlyNumber + 1;
        if (!advanced && !SameSnapshot(next, _public) &&
            !(!_public.IsFinished && next.IsFinished && next.Pieces.SequenceEqual(_public.Pieces) &&
              next.SideToMove == _public.SideToMove &&
              next.EndReason is GameEndReason.Timeout or GameEndReason.Resignation))
        { Message?.Invoke("揭棋同手数状态包含未经着法产生的局面变化。"); return; }
        var move = new ChessMove(new(packet.FromFile, packet.FromRank), new(packet.ToFile, packet.ToRank));
        if (advanced && (!move.From.IsInside || !move.To.IsInside))
        { Message?.Invoke("揭棋权威着法坐标无效。"); return; }
        var pendingMoves = advanced ? _moves.Append(move).ToArray() : _moves.ToArray();
        if (packet.Seed is not null)
        {
            if (!next.IsFinished || !ulong.TryParse(packet.Seed, System.Globalization.NumberStyles.HexNumber, null, out var seed) ||
                Commitment(seed) != packet.Commitment || !VerifyReplay(seed, pendingMoves, next))
            { Message?.Invoke("赛后揭棋种子未通过承诺或重放校验。"); return; }
            _releasedSeed = seed;
        }
        else if (next.IsFinished) { Message?.Invoke("揭棋终局缺少赛后种子。"); return; }
        if (advanced) _moves.Add(move);
        _timeControl = timeControl;
        _clock = new MatchClock(timeControl);
        _clock.Apply(clock);
        _commitment = packet.Commitment; _public = next; LoadLegal(packet.Legal);
        _clock.IsRunning = IsConnected && !next.IsFinished;
        if (next.IsFinished && packet.RequestId == _pendingResignationRequest)
            _pendingResignation?.TrySetResult(next.EndReason == GameEndReason.Resignation);
        Changed?.Invoke();
    }

    private static bool VerifyReplay(ulong seed, IEnumerable<ChessMove> moves, GameSnapshot expected)
    {
        var game = new JieqiGameSession(seed);
        foreach (var move in moves) if (!game.TryMove(move.From, move.To).IsSuccess) return false;
        if (expected.IsFinished && expected.EndReason is (GameEndReason.Timeout or GameEndReason.Resignation) &&
            expected.Winner is { } winner && !game.DeclareLoss(winner.Opponent(), expected.EndReason)) return false;
        return game.Snapshot == expected || SameSnapshot(game.Snapshot, expected);
    }

    private void PublishAuthorityState() { _public = _authority!.Snapshot; RefreshLegal(); Changed?.Invoke(); }
    private void RefreshLegal()
    {
        _legal.Clear();
        foreach (var piece in _public.Pieces.Where(x => x.Piece.Side == _public.SideToMove))
            _legal[piece.Position] = _authority!.GetLegalDestinations(piece.Position);
    }
    private void LoadLegal(JieqiLegal[]? values)
    {
        _legal.Clear(); if (values is null) return;
        foreach (var item in values)
        {
            var from = new BoardPosition(item.File, item.Rank);
            if (!from.IsInside || item.Targets is null) continue;
            var targets = item.Targets.Where(x => x is { Length: 2 }).Select(x => new BoardPosition(x[0], x[1])).Where(x => x.IsInside).Distinct().ToArray();
            _legal[from] = targets;
        }
    }
    private async ValueTask SendStateAsync(long requestId, ChessMove? move, CancellationToken cancellationToken = default)
    {
        var packet = new JieqiPacket("state", requestId, _public.PlyNumber, (int)HostSide!.Value,
            move?.From.File ?? -1, move?.From.Rank ?? -1, move?.To.File ?? -1, move?.To.Rank ?? -1,
            _commitment, _public.IsFinished ? _authoritySeed!.Value.ToString("X16") : null,
            FromSnapshot(_public), _legal.Select(x => new JieqiLegal(x.Key.File, x.Key.Rank,
                x.Value.Select(y => new[] { y.File, y.Rank }).ToArray())).ToArray(), null,
            new JieqiClock(_clock.Snapshot.RedGameSeconds, _clock.Snapshot.BlackGameSeconds,
                _clock.Snapshot.MoveSeconds, (int)_clock.Snapshot.SideToMove),
            _timeControl.GameSeconds, _timeControl.MoveSeconds);
        await SendAsync(packet, cancellationToken);
    }
    private ValueTask SendRejectAsync(long id, string error) => SendAsync(new JieqiPacket("reject", id,
        _public.PlyNumber, (int)HostSide!.Value, -1, -1, -1, -1, null, null, null, null, error));
    private ValueTask SendAsync(JieqiPacket packet, CancellationToken cancellationToken = default) =>
        _transport.SendPacketAsync(JieqiWireProtocol.Encode(packet), cancellationToken);
    private static ulong RandomSeed() { Span<byte> bytes = stackalloc byte[8]; RandomNumberGenerator.Fill(bytes); return BitConverter.ToUInt64(bytes); }
    private static string Commitment(ulong seed) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"jieqi-authority-v1:{seed:X16}"))).ToLowerInvariant();
    private static JieqiState FromSnapshot(GameSnapshot value) => new(value.Pieces.Select(x => new JieqiPieceDto(
        x.Position.File, x.Position.Rank, x.Piece.PieceInstanceId, (int)x.Piece.Side, x.Piece.Kind.Value)).ToArray(),
        (int)value.SideToMove, value.IsInCheck, value.IsFinished, value.Winner is null ? null : (int)value.Winner,
        (int)value.EndReason, value.PlyNumber);
    private static bool TrySnapshot(JieqiState value, out GameSnapshot? snapshot, out string? error)
    {
        snapshot = null; error = null;
        if (value.Pieces is null || value.Pieces.Length is 0 or > 32 || value.Ply < 0 ||
            value.Side is < 0 or > 1 || value.Winner is < 0 or > 1 || !Enum.IsDefined((GameEndReason)value.EndReason))
        { error = "揭棋公开局面字段无效。"; return false; }
        var kinds = new HashSet<string>(new[] { BuiltInPieceKinds.Covered, BuiltInPieceKinds.General,
            BuiltInPieceKinds.Advisor, BuiltInPieceKinds.Elephant, BuiltInPieceKinds.Horse,
            BuiltInPieceKinds.Chariot, BuiltInPieceKinds.Cannon, BuiltInPieceKinds.Soldier }.Select(x => x.Value!));
        var cells = new HashSet<BoardPosition>(); var ids = new HashSet<int>(); var pieces = new List<PlacedPiece>();
        foreach (var item in value.Pieces)
        {
            var cell = new BoardPosition(item.File, item.Rank);
            if (!cell.IsInside || item.Id <= 0 || item.Side is < 0 or > 1 || item.Kind is null || !kinds.Contains(item.Kind) ||
                !cells.Add(cell) || !ids.Add(item.Id)) { error = "揭棋公开棋子无效或重复。"; return false; }
            pieces.Add(new(cell, new ChessPiece(item.Id, (PieceSide)item.Side, new PieceKind(item.Kind))));
        }
        snapshot = new(pieces.ToArray(), (PieceSide)value.Side, value.Check, value.Finished,
            value.Winner is null ? null : (PieceSide)value.Winner.Value, (GameEndReason)value.EndReason, value.Ply);
        return true;
    }
    private static bool SameSnapshot(GameSnapshot a, GameSnapshot b) => a.SideToMove == b.SideToMove &&
        a.IsInCheck == b.IsInCheck && a.IsFinished == b.IsFinished && a.Winner == b.Winner && a.EndReason == b.EndReason &&
        a.PlyNumber == b.PlyNumber && a.Pieces.OrderBy(x => x.Piece.PieceInstanceId).SequenceEqual(b.Pieces.OrderBy(x => x.Piece.PieceInstanceId));

    private static bool TryParseClock(JieqiPacket packet, PieceSide expectedTurn,
        out TimeControl timeControl, out ClockSnapshot clock, out string? error)
    {
        timeControl = TimeControl.Unlimited;
        clock = default;
        error = null;
        if (packet.GameSeconds is < 0 or > 864000 || packet.MoveSeconds is < 0 or > 86400 || packet.Clock is null ||
            packet.Clock.Side is < 0 or > 1 || !double.IsFinite(packet.Clock.Red) ||
            !double.IsFinite(packet.Clock.Black) || !double.IsFinite(packet.Clock.Move) ||
            packet.Clock.Red < 0 || packet.Clock.Black < 0 || packet.Clock.Move < 0 ||
            (PieceSide)packet.Clock.Side != expectedTurn ||
            (packet.GameSeconds == 0 ? packet.Clock.Red != 0 || packet.Clock.Black != 0 :
                packet.Clock.Red > packet.GameSeconds || packet.Clock.Black > packet.GameSeconds) ||
            (packet.MoveSeconds == 0 ? packet.Clock.Move != 0 : packet.Clock.Move > packet.MoveSeconds))
        { error = "揭棋棋钟字段无效。"; return false; }
        timeControl = new(packet.GameSeconds, packet.MoveSeconds);
        clock = new(packet.Clock.Red, packet.Clock.Black, packet.Clock.Move, (PieceSide)packet.Clock.Side);
        return true;
    }
}

internal sealed record JieqiPacket(string Type, long RequestId, long ExpectedPly, int Side,
    int FromFile, int FromRank, int ToFile, int ToRank, string? Commitment, string? Seed,
    JieqiState? State, JieqiLegal[]? Legal, string? Error = null, JieqiClock? Clock = null,
    int GameSeconds = 0, int MoveSeconds = 0);
internal sealed record JieqiState(JieqiPieceDto[] Pieces, int Side, bool Check, bool Finished, int? Winner, int EndReason, int Ply);
internal sealed record JieqiPieceDto(int File, int Rank, int Id, int Side, string? Kind);
internal sealed record JieqiLegal(int File, int Rank, int[][] Targets);
internal sealed record JieqiClock(double Red, double Black, double Move, int Side);

internal static class JieqiWireProtocol
{
    public const int MaximumPacketBytes = 16_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ReadOnlyMemory<byte> Encode(JieqiPacket packet)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (body.Length > MaximumPacketBytes) throw new InvalidOperationException("揭棋数据包超过上限。");
        var result = new byte[body.Length + 2]; result[0] = 2; result[1] = 0x4a; body.CopyTo(result, 2); return result;
    }
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out JieqiPacket? packet, out string? error)
    {
        packet = null; error = null;
        if (bytes.Length is < 3 or > MaximumPacketBytes + 2 || bytes[0] != 2 || bytes[1] != 0x4a)
        { error = "揭棋协议版本或长度无效。"; return false; }
        try
        {
            packet = JsonSerializer.Deserialize<JieqiPacket>(bytes[2..], Json);
            if (packet is null || packet.Type is not ("move" or "resign" or "state" or "reject" or "rematch" or "sync")) throw new JsonException();
            return true;
        }
        catch (JsonException) { error = "揭棋数据包 JSON 无效。"; return false; }
    }
}
