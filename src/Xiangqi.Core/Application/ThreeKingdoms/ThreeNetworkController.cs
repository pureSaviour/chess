using System.Security.Cryptography;
using System.Text.Json;
using Xiangqi.Core.Domain.ThreeKingdoms;

namespace Xiangqi.Core.Application.ThreeKingdoms;

/// <summary>
/// 三国房间的可迁移权威协调器。满三席才开局，所有有效动作（含棋钟增量）写入哈希链，
/// 通过认证发送者绑定席位。每台设备保存日志，断线者从共同前缀分页补齐，不重置棋钟。
/// 权威提议在会话副本上验证，至少另一端复验记入日志并回复确认后才在权威端提交；未确认
/// 操作不改变权威棋局。只有本机在线时不提交新历史，继续累计耗时并暂存判负请求。
/// 聊天使用独立的有界 RoomChatManager，在未满员或权威迁移时也可写入，并经认证同伴同步；
/// 定向消息复制到可信房间副本但只向发送者/目标展示，不提供端到端加密。
/// 房主消失后由仍存活且在线的最低席位接任；接任前等待状态交换并取最长一致日志。
/// 无法证明同一前缀的网络分区不会猜测合并棋局，而是进入明确的同步冲突状态。
/// 此机制用于可信玩家的点对点房间，不声称可抵抗恶意房主或提供公网竞技仲裁。
/// </summary>
public sealed class ThreeNetworkController : IThreeMatchController
{
    public const int Protocol = 3;
    public const int MaximumPacketBytes = 128 * 1024;
    private readonly IThreeRoomTransport _transport;
    private readonly TimeControl _configuredTime;
    private readonly AdvisorConquestPolicy _configuredPolicy;
    private readonly List<LogEntry> _log = [];
    private readonly Dictionary<string, (int Count, string Hash)> _heads = [];
    private readonly Dictionary<string, double> _seen = [];
    private readonly HashSet<string> _requests = [];
    private readonly Queue<ThreeCommand> _commands = new();
    private LogEntry? _pending;
    private double _resendAt;
    private readonly Dictionary<Kingdom, ThreeCommand> _deferredForfeits = [];
    private readonly bool[] _observed = new bool[3];
    private readonly PlayerProfileCommand?[] _profiles = new PlayerProfileCommand?[3];
    private readonly PlayerProfileCommand? _localProfile;
    private readonly double[] _displayGame = new double[3], _displayStep = new double[3];
    public IReadOnlyList<bool> Connections { get; }
    public IReadOnlyList<double> DisplayedGameTimes { get; }
    public IReadOnlyList<double> DisplayedStepTimes { get; }
    public RoomChatManager Chat { get; } = new(3);
    private string? _leader;
    private long _term;
    private double _now, _helloAt, _settleUntil, _elapsed;
    private bool _started, _replaying, _disposed;
    private string _connectionSet = "";
    private readonly System.Diagnostics.Stopwatch _wallClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastWall;
    private bool _wasSettling;
    private string? _fault;
    public ThreeMatchSession Session { get; private set; }
    public Kingdom? LocalKingdom => Seat(_transport.LocalId) is var i && i >= 0 ? (Kingdom)i : null;
    private string? _nameCacheKey;
    private IReadOnlyList<string> _names = Array.Empty<string>(), _ids = Array.Empty<string>();
    public IReadOnlyList<string> PlayerNames { get { CacheNames(); return _names; } }
    public IReadOnlyList<string> PlayerIds { get { CacheNames(); return _ids; } }
    public IReadOnlyList<PlayerProfileCommand?> PlayerProfiles { get; }
    private void CacheNames()
    {
        var roster = _transport.Roster;
        var key = string.Join('|', roster.Select(peer => peer.Id + "\u001f" + peer.Name));
        if (_nameCacheKey == key && _names.Count == 3 && _ids.Count == 3) return;
        _nameCacheKey = key;
        _names = Array.AsReadOnly(Enumerable.Range(0, 3).Select(i =>
            _profiles[i]?.DisplayName ?? (i < roster.Count ? roster[i].Name : "等待玩家")).ToArray());
        _ids = Array.AsReadOnly(Enumerable.Range(0, 3).Select(i => i < roster.Count ? roster[i].Id : "").ToArray());
    }
    public string Status => _fault ?? (!_started ? $"{_transport.Status} · 等待三席就绪（{_transport.Roster.Count}/3）" :
        $"联网 · 房主{ThreeMatchSession.Name((Kingdom)Math.Max(0, Seat(_leader)))} · {(!HasReplica ? "仅本机在线：累计耗时，等待通信恢复" : _now < _settleUntil ? "同步/迁移中" : "已同步")}");
    public event Action? Changed;
    public event Action<Kingdom?, string>? Message;
    private sealed record LogEntry(int Index, string Previous, string Hash, ThreeCommand Command);
    // Bounded JSON envelope is private to this protocol version. No polymorphic type names are deserialized.
    private sealed record Packet(int Version = Protocol, string Kind = "", long Term = 0, string Leader = "",
        int Count = 0, string Hash = "", int GameSeconds = 0, int MoveSeconds = 0, int Policy = 0,
        int Start = 0, LogEntry[]? Entries = null, ThreeCommand? Command = null,
        RoomChatEntry? Chat = null, RoomChatEntry[]? ChatEntries = null, PlayerProfileCommand? Profile = null,
        string Request = "", string Error = "");
    private string Head => _log.Count == 0 ? "genesis" : _log[^1].Hash;
    private bool IsLeader => _leader == _transport.LocalId;
    private bool HasReplica => _transport.Roster.Any(p => p.Id != _transport.LocalId && Online(p.Id));
    private bool CanPropose => IsLeader && HasReplica && _fault is null && _now >= _settleUntil;
    public ThreeNetworkController(IThreeRoomTransport transport, TimeControl time, AdvisorConquestPolicy policy,
        PlayerProfileCommand? localProfile = null)
    {
        _transport = transport; _configuredTime = time.Validate(); _configuredPolicy = policy;
        if (localProfile is { } profile && ValidProfile(profile)) _localProfile = CopyProfile(profile);
        Session = CreateSession(time, policy); Session.IsRunning = false;
        Connections = Array.AsReadOnly(_observed);
        DisplayedGameTimes = Array.AsReadOnly(_displayGame); DisplayedStepTimes = Array.AsReadOnly(_displayStep);
        PlayerProfiles = Array.AsReadOnly(_profiles);
        transport.Received += Receive;
    }
    private ThreeMatchSession CreateSession(TimeControl time, AdvisorConquestPolicy policy)
    {
        var session = new ThreeMatchSession(time, policy);
        session.Message += (k, text) => { if (!_replaying) Message?.Invoke(k, text); };
        return session;
    }
    public string? Submit(ThreeCommand c)
    {
        if (!ThreeCommands.IsPlayerCommand(c) || LocalKingdom is not { } side || c.Actor != side) return "只能操作自己的席位。";
        if (_started && _fault is null && !HasReplica && c.Kind == ThreeCommandKind.Forfeit &&
            Enum.IsDefined(c.Target) && c.Target != side && Session.Game.Snapshot.Alive[(int)side] && Session.Game.Snapshot.Alive[(int)c.Target])
        {
            _deferredForfeits[c.Target] = c;
            Message?.Invoke(null, $"已暂存对{ThreeMatchSession.Name(c.Target)}国的判负请求，恢复通信并完成同步后再结算。");
            return null;
        }
        if (!_started || _fault is not null || _now < _settleUntil || !Online(_leader)) return "房间正在等待或同步，请稍候。";
        if (!HasReplica) return "仅本机在线，暂不能提交走子；棋钟继续累计。";
        if (IsLeader)
        {
            return FlushTime() ?? Commit(c);
        }
        Send(_leader!, new(Kind: "request", Term: _term, Leader: _leader!, Command: c, Request: Guid.NewGuid().ToString("N")));
        return null;
    }
    public string? SendChat(Kingdom actor, Kingdom? target, string text)
    {
        if (LocalKingdom is not { } side || actor != side) return "只能以自己的席位发言。";
        var error = Chat.Post((int)side, target is { } recipient ? (int)recipient : null, text, out var entry);
        if (error is not null) return error;
        // Replicate to every current peer so room-manager migration retains directed history.
        // Presentation filters recipients; this is visibility routing rather than encryption.
        Broadcast(new(Kind: "chat", Chat: entry));
        return null;
    }
    public void Tick(double delta)
    {
        if (_disposed) return;
        _now += delta; _transport.Poll(delta);
        var wall = _wallClock.Elapsed.TotalSeconds;
        var resumed = wall - _lastWall > 1.5;
        if (resumed && _started && Session.Offer is null && !Session.Game.Snapshot.IsFinished)
            _elapsed += Math.Max(0, wall - _lastWall - delta);
        var observationChanged = false;
        for (var i = 0; i < 3; i++)
        { var live = i < _transport.Roster.Count && Online(_transport.Roster[i].Id); observationChanged |= _observed[i] != live; _observed[i] = live; }
        if (observationChanged) Changed?.Invoke();
        _lastWall = wall;
        var connections = string.Join('|', _transport.Connected.Order());
        if (connections != _connectionSet || resumed)
        {
            _connectionSet = connections;
            _settleUntil = _now + 2;
            if (_started) Broadcast(Configuration());
            Changed?.Invoke();
        }
        if (_started && Session.Offer is null && !Session.Game.Snapshot.IsFinished) _elapsed += delta;
        if (_transport.Roster.Count != 3) { UpdateDisplayedClocks(); return; }
        if (_leader is not null) _transport.SetAuthority(_leader);
        if (!_started)
        {
            if (Seat(_transport.LocalId) == 0 && _transport.Roster.All(p => _transport.Connected.Contains(p.Id)))
            {
                _leader = _transport.LocalId; _term = 1;
                Start(_configuredTime, _configuredPolicy); Broadcast(Configuration());
            }
        }
        else if (_fault is null)
        {
            if (!Online(_leader) && HasReplica)
            {
                var candidate = _transport.Roster.Select((p, i) => (p, i))
                    .FirstOrDefault(x => Session.Game.Snapshot.Alive[x.i] && Online(x.p.Id)).p;
                if (candidate is not null && candidate.Id == _transport.LocalId)
                {
                    _leader = candidate.Id; _term++; _settleUntil = _now + 2;
                    DropUnconfirmed();
                    Broadcast(Configuration()); Changed?.Invoke();
                    Message?.Invoke(null, $"房主断线，{ThreeMatchSession.Name(LocalKingdom!.Value)}国接管房间并校验棋谱。断线席位保留重连资格。");
                }
            }
            if (CanPropose)
            {
                // A new leader must first obtain any tail already seen by another reachable replica.
                var ahead = _heads.FirstOrDefault(p => Online(p.Key) && p.Value.Count > _log.Count);
                if (ahead.Key is not null) RequestTail(ahead.Key);
                else
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var connected = Online(_transport.Roster[i].Id);
                        if (Session.Connected[i] != connected && !_commands.Any(c => c.Kind == ThreeCommandKind.Connection && c.Target == (Kingdom)i) &&
                            !(_pending?.Command.Kind == ThreeCommandKind.Connection && _pending.Command.Target == (Kingdom)i))
                            Commit(new(ThreeCommandKind.Connection, Target: (Kingdom)i, Accepted: connected));
                    }
                    if (_elapsed >= 0.5 && _pending is null && _commands.Count == 0) FlushTime();
                    ProposeNext();
                }
            }
        }
        if (_now >= _helloAt)
        {
            SendHelloAndState();
        }
        if (_started && HasReplica && Online(_leader) && _now >= _settleUntil && _pending is null &&
            Session.Connected.SequenceEqual(_observed) && !_heads.Any(h => Online(h.Key) && h.Value.Count > _log.Count))
        {
            var deferred = _deferredForfeits.Values.ToArray(); _deferredForfeits.Clear();
            foreach (var command in deferred) { var error = Submit(command); if (error is not null) Message?.Invoke(null, error); }
        }
        UpdateDisplayedClocks();
    }
    private void SendHelloAndState()
    {
        if (_wasSettling != (_now < _settleUntil)) { _wasSettling = _now < _settleUntil; Changed?.Invoke(); }
        SendHello();
    }
    private void SendHello()
    {
        _helloAt = _now + 0.75;
        Broadcast(new(Kind: "hello"));
        var localSeat = Seat(_transport.LocalId);
        if (localSeat >= 0 && _localProfile is { } profile)
        {
            _profiles[localSeat] = profile;
            _nameCacheKey = null;
            Broadcast(new(Kind: "profile", Profile: profile));
        }
        if (_started) Broadcast(Configuration());
    }
    private bool Online(string? id) => id is not null && _transport.Connected.Contains(id) &&
        (id == _transport.LocalId || !_seen.TryGetValue(id, out var last) || _now - last < 5);
    private void UpdateDisplayedClocks()
    {
        var turn = (int)Session.Game.Snapshot.Turn;
        var provisional = _started && !HasReplica && Session.Offer is null && !Session.Game.Snapshot.IsFinished
            ? _elapsed + (_pending?.Command.Kind == ThreeCommandKind.Tick ? _pending.Command.Seconds : 0) + _commands.Where(c => c.Kind == ThreeCommandKind.Tick).Sum(c => c.Seconds) : 0;
        for (var i = 0; i < 3; i++)
        {
            _displayGame[i] = Math.Max(0, Session.GameTimes[i] - (i == turn ? provisional : 0));
            _displayStep[i] = Math.Max(0, Session.StepTimes[i] - (i == turn ? provisional : 0));
        }
    }
    private void Start(TimeControl time, AdvisorConquestPolicy policy)
    {
        Session = CreateSession(time, policy); _started = true; _settleUntil = _now + 1;
        Changed?.Invoke();
    }
    private string? FlushTime()
    {
        if (_elapsed <= 0 || !_started) return null;
        if (Session.Game.Snapshot.IsFinished || Session.Offer is not null || !Session.TimeControl.IsEnabled)
        { _elapsed = 0; return null; }
        // Deduct buffered time only after it enters the bounded queue. Split long
        // offline periods without discarding their remainder or allowing a move
        // to overtake clock debt when the queue is temporarily full.
        while (_elapsed > 0)
        {
            var elapsed = Math.Min(_elapsed, 86400);
            var error = Commit(new(ThreeCommandKind.Tick, Seconds: elapsed));
            if (error is not null) return error;
            _elapsed -= elapsed;
        }
        return null;
    }
    private string? Commit(ThreeCommand c)
    {
        if (_log.Count >= 200000) { _fault = "房间日志达到安全上限，已停止接收动作。"; Changed?.Invoke(); return _fault; }
        if (_commands.Count >= 32) return "同步队列已满，请稍候。";
        if (!Valid(c)) return "无效命令。";
        _commands.Enqueue(c);
        return null;
    }
    private void ProposeNext()
    {
        if (_pending is null && _commands.TryDequeue(out var command))
        {
            var error = Session.Validate(command);
            if (error is not null)
            {
                var actor = _transport.Roster[(int)command.Actor].Id;
                if (actor == _transport.LocalId) Message?.Invoke(null, error);
                else Send(actor, new(Kind: "error", Error: error));
                return;
            }
            _pending = new(_log.Count, Head, Hash(Head, command), command); _resendAt = 0;
        }
        if (_pending is { } entry && _now >= _resendAt)
        {
            _resendAt = _now + 0.5;
            Broadcast(new(Kind: "entries", Term: _term, Leader: _leader!, Start: entry.Index, Entries: [entry]));
        }
    }
    private void ConfirmPending(int count, string hash)
    {
        if (_pending is not { } entry || count != entry.Index + 1 || hash != entry.Hash) return;
        var before = Session.Game.Snapshot;
        if (entry.Index != _log.Count || entry.Previous != Head || ThreeCommands.Apply(Session, entry.Command) is not null) { Conflict(); return; }
        _log.Add(entry); _pending = null;
        if (entry.Command.Kind != ThreeCommandKind.Tick || before != Session.Game.Snapshot) Changed?.Invoke();
    }
    private void DropUnconfirmed()
    {
        var hasMove = _pending?.Command.Kind == ThreeCommandKind.Move || _commands.Any(c => ThreeCommands.IsPlayerCommand(c));
        _pending = null; _commands.Clear();
        if (hasMove) Message?.Invoke(null, "房主已变更，未获得远端确认的操作未提交，请重新操作。");
    }
    private void Receive(string sender, byte[] bytes)
    {
        if (bytes.Length > MaximumPacketBytes || Seat(sender) < 0) return;
        Packet? p;
        try { p = JsonSerializer.Deserialize<Packet>(bytes); }
        catch (JsonException) { return; }
        if (p is null || p.Version != Protocol || p.Count < 0 || p.Count > 200000 || p.Term < 0 ||
            p.Leader is null || p.Leader.Length > 80 || p.Hash is null || p.Hash.Length > 64 ||
            p.Request is null || p.Request.Length > 64 || p.Error is null || p.Error.Length > 300) return;
        _seen[sender] = _now;
        switch (p.Kind)
        {
            case "hello":
                SendChatHistory(sender);
                var ownSeat = Seat(_transport.LocalId);
                if (ownSeat >= 0 && _localProfile is { } ownProfile)
                    Send(sender, new(Kind: "profile", Profile: ownProfile));
                if (_started) Send(sender, Configuration());
                break;
            case "profile":
                if (p.Profile is not { } profile || !ValidProfile(profile)) return;
                var profileSeat = Seat(sender);
                _profiles[profileSeat] = CopyProfile(profile);
                _nameCacheKey = "";
                Changed?.Invoke();
                break;
            case "chat":
                Chat.AcceptLive(p.Chat, Seat(sender));
                break;
            case "chat-state":
                if (p.ChatEntries is { Length: <= RoomChatManager.DefaultCapacity } messages)
                    Chat.ImportTrustedHistory(messages);
                break;
            case "state":
                if (Seat(p.Leader) < 0 || p.GameSeconds is < 0 or > 864000 || p.MoveSeconds is < 0 or > 86400 || p.Policy is < 0 or > 255 || !Enum.IsDefined((AdvisorConquestPolicy)p.Policy)) return;
                if (!_started)
                {
                    // The immutable roster's first member establishes room rules, not an arbitrary peer.
                    if (Seat(sender) != 0 || p.Leader != sender || p.Term != 1) return;
                    _term = p.Term; _leader = p.Leader; Start(new(p.GameSeconds, p.MoveSeconds), (AdvisorConquestPolicy)p.Policy);
                }
                if (p.GameSeconds != Session.TimeControl.GameSeconds || p.MoveSeconds != Session.TimeControl.MoveSeconds || p.Policy != (int)Session.Game.AdvisorPolicy) { Conflict(); return; }
                _heads[sender] = (p.Count, p.Hash);
                if (p.Term > _term || (p.Term == _term && _leader != p.Leader && Seat(p.Leader) < Seat(_leader)))
                {
                    _term = p.Term; _leader = p.Leader; _settleUntil = _now + 2; Changed?.Invoke();
                    DropUnconfirmed();
                }
                if (p.Count == _log.Count && p.Hash != Head) { Conflict(); return; }
                if (p.Count > _log.Count) RequestTail(sender);
                else if (p.Count < _log.Count) SendTail(sender, p.Count, p.Hash);
                break;
            case "request":
                if (!IsLeader || p.Term != _term || _now < _settleUntil || _fault is not null ||
                    p.Command is not { } c || !Valid(c) || !ThreeCommands.IsPlayerCommand(c) || Seat(sender) != (int)c.Actor ||
                    p.Request.Length is < 1 or > 64 || !_requests.Add(sender + p.Request)) return;
                if (_requests.Count > 20000) _requests.Clear();
                var error = FlushTime() ?? Commit(c);
                if (error is not null) Send(sender, new(Kind: "error", Error: error));
                break;
            case "fetch": if (_started) SendTail(sender, p.Start, p.Hash); break;
            case "ack":
                if (IsLeader && p.Term == _term && sender != _transport.LocalId) ConfirmPending(p.Count, p.Hash);
                break;
            case "entries":
                if (!_started || p.Entries is not { Length: > 0 and <= 128 } entries || p.Term < _term ||
                    (sender != _leader && !IsLeader)) return;
                if (p.Start > _log.Count) { RequestTail(sender); return; }
                if (entries.Any(e => e is null || e.Previous is null || e.Hash is null || !Valid(e.Command))) return;
                _replaying = entries.Length > 1;
                var changed = false;
                foreach (var entry in entries)
                {
                    if (entry.Index < 0 || entry.Index > _log.Count) break;
                    if (entry.Index < _log.Count)
                    { if (_log[entry.Index].Hash != entry.Hash) Conflict(); continue; }
                    if (entry.Previous != Head || entry.Hash != Hash(Head, entry.Command) || !Valid(entry.Command)) { Conflict(); break; }
                    try
                    {
                        var before = Session.Game.Snapshot;
                        if (ThreeCommands.Apply(Session, entry.Command) is not null) { Conflict(); break; }
                        changed |= entry.Command.Kind != ThreeCommandKind.Tick || before != Session.Game.Snapshot;
                    }
                    catch (ArgumentException) { Conflict(); break; }
                    _log.Add(entry);
                    var confirmingOwnProposal = IsLeader && _pending?.Hash == entry.Hash;
                    if (confirmingOwnProposal) _pending = null;
                    if (!confirmingOwnProposal && entry.Command.Kind is (ThreeCommandKind.Tick or ThreeCommandKind.Move or ThreeCommandKind.Undo or ThreeCommandKind.Draw)) _elapsed = 0;
                }
                _replaying = false;
                if (sender == _leader && _fault is null) Send(sender, new(Kind: "ack", Term: _term, Count: _log.Count, Hash: Head));
                if (changed) Changed?.Invoke();
                if (_heads.TryGetValue(sender, out var head) && head.Count > _log.Count) RequestTail(sender);
                break;
            case "error": if (sender == _leader && p.Error.Length <= 300) Message?.Invoke(null, p.Error); break;
        }
    }
    private static bool Valid(ThreeCommand c) => c is not null && Enum.IsDefined(c.Kind) && Enum.IsDefined(c.Actor) &&
        Enum.IsDefined(c.Target) && double.IsFinite(c.Seconds) && c.Seconds is >= 0 and <= 86400;
    private static bool ValidProfile(PlayerProfileCommand profile)
    {
        if (profile.PlayerId is null || profile.DisplayName is null || profile.AvatarRgba is null) return false;
        try { return MatchWireProtocol.Encode(profile).Length <= MatchWireProtocol.MaximumPacketBytes; }
        catch (ArgumentException) { return false; }
    }
    private static PlayerProfileCommand CopyProfile(PlayerProfileCommand profile) =>
        profile with { AvatarRgba = profile.AvatarRgba.ToArray() };
    private void SendChatHistory(string id) => Send(id,
        new(Kind: "chat-state", ChatEntries: Chat.Entries.TakeLast(RoomChatManager.DefaultCapacity).ToArray()));
    private void RequestTail(string id) => Send(id, new(Kind: "fetch", Term: _term, Start: _log.Count, Hash: Head));
    private void SendTail(string id, int start, string hash)
    {
        if (start < 0 || start > _log.Count || hash != (start == 0 ? "genesis" : _log[start - 1].Hash)) return;
        if (start == _log.Count) return;
        Send(id, new(Kind: "entries", Term: _term, Leader: _leader!, Start: start, Entries: _log.Skip(start).Take(128).ToArray()));
    }
    private Packet Configuration() => new(Kind: "state", Term: _term, Leader: _leader ?? "", Count: _log.Count, Hash: Head,
        GameSeconds: Session.TimeControl.GameSeconds, MoveSeconds: Session.TimeControl.MoveSeconds, Policy: (int)Session.Game.AdvisorPolicy);
    private static string Hash(string previous, ThreeCommand command) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Previous = previous, Command = command })));
    private int Seat(string? id)
    { for (var i = 0; i < _transport.Roster.Count; i++) if (_transport.Roster[i].Id == id) return i; return -1; }
    private void Send(string id, Packet packet)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length <= MaximumPacketBytes) _transport.Send(id, bytes);
    }
    private void Broadcast(Packet p) { foreach (var peer in _transport.Roster) if (peer.Id != _transport.LocalId) Send(peer.Id, p); }
    private void Conflict() { _fault = "检测到不一致的网络棋谱，已停止行棋以防覆盖。请保留房间并反馈日志。"; Session.IsRunning = false; Changed?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _transport.Received -= Receive; _transport.Dispose(); }
}
