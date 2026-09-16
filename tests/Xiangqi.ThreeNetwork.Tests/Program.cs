using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Transport;

static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
static int C(int k, int f, int r) => 45 * k + 9 * r + f;
static ThreeCommand Move(int k) => new(ThreeCommandKind.Move, (Kingdom)k, C(k, 0, 3), C(k, 0, 4));
var waitingTransport = new WaitingRosterTransport();
using (var waiting = new ThreeNetworkController(waitingTransport, TimeControl.Unlimited,
           AdvisorConquestPolicy.SharedTerritory))
{
    Check(waiting.PlayerNames.SequenceEqual(new[] { "等待玩家", "等待玩家", "等待玩家" }),
        "An empty joining roster did not project three waiting seats.");
    Check(waiting.PlayerIds.Count == 3 && waiting.PlayerIds.All(string.IsNullOrEmpty),
        "An empty joining roster did not project three empty player IDs.");
    waitingTransport.SetRoster([new("host", "房主"), new("joiner", "加入者")]);
    Check(waiting.PlayerNames.SequenceEqual(new[] { "房主", "加入者", "等待玩家" }),
        "The waiting-seat projection did not refresh after the roster arrived.");
}
Console.WriteLine("PASS 加入房间时空名册投影为固定三席并在握手后刷新");
var peers = new FakeMesh();
var controllers = peers.Ends.Select((e, i) => new ThreeNetworkController(e, new TimeControl(600, 60),
    AdvisorConquestPolicy.SharedTerritory, new PlayerProfileCommand($"id-{i}", $"棋手 {i}", 1, 1, [(byte)i, 2, 3, 255]))).ToArray();
void Pump(double duration, params int[] excluded)
{
    for (var elapsed = 0d; elapsed < duration; elapsed += 0.05)
        for (var i = 0; i < 3; i++) if (!excluded.Contains(i)) controllers[i].Tick(0.05);
}
Pump(3);
Check(controllers.All(c => c.Session.IsRunning), "Three seats did not start.");
Check(controllers.All(c => c.PlayerProfiles.Select((profile, i) => profile is not null &&
    profile.Value.DisplayName == $"棋手 {i}" && profile.Value.AvatarRgba.SequenceEqual(new byte[] { (byte)i, 2, 3, 255 })).All(valid => valid)),
    "Three-player names and avatars did not synchronize.");
Check(controllers[0].Submit(Move(0)) is null, "Wei move rejected."); Pump(1);
Check(controllers.All(c => c.Session.Game.Snapshot.Ply == 1), "Move did not synchronize.");
Check(controllers[1].Submit(Move(0)) is not null, "Actor spoof accepted.");
peers.Online[0] = false; Pump(4);
Check(controllers[1].Status.Contains("房主吴") && controllers[2].Status.Contains("房主吴"), "Host migration failed.");
Check(controllers[1].Submit(Move(1)) is null, "New host cannot move."); Pump(1);
peers.Online[0] = true; Pump(5);
foreach (var t in controllers) Console.WriteLine($"{t.LocalKingdom}: {t.Status} ply={t.Session.Game.Snapshot.Ply} connected={string.Join(',', t.Session.Connected)}");
Check(controllers.All(c => c.Session.Game.Snapshot.Ply == 2 && c.Session.Connected.All(x => x)), "Reconnected host not caught up.");
Check(controllers[0].Status.Contains("房主吴"), "Old host improperly took authority back.");
Check(controllers.Select(c => c.Session.GameTimes[1]).Distinct().Count() == 1, "Clock mismatch after migration.");
Check(controllers[2].Submit(Move(2)) is null, "Shu move rejected."); Pump(1);
Check(controllers.All(c => c.Session.Game.Snapshot.Ply == 3), "Postmigration move differs.");
Check(controllers[2].Submit(new(ThreeCommandKind.Undo, Kingdom.Shu)) is null, "Network undo request failed."); Pump(1);
var offer = controllers[0].Session.Offer!;
Check(controllers[0].Submit(new(ThreeCommandKind.Vote, Kingdom.Wei, OfferId: offer.Id, Accepted: true)) is null, "Wei approval failed."); Pump(1);
Check(controllers.All(c => c.Session.Game.Snapshot.Ply == 3), "Undo did not wait for both opponents.");
Check(controllers[1].Submit(new(ThreeCommandKind.Vote, Kingdom.Wu, OfferId: offer.Id, Accepted: true)) is null, "Wu approval failed."); Pump(1);
Check(controllers.All(c => c.Session.Game.Snapshot.Ply == 2), "Network undo not synchronized.");
peers.Ends[0].Send("1", System.Text.Encoding.UTF8.GetBytes("{\"Version\":1,\"Kind\":\"request\",\"Request\":null}"));
peers.Ends[0].Send("1", System.Text.Encoding.UTF8.GetBytes("null"));
Pump(0.2);
peers.Online[0] = false; Pump(3);
Check(controllers[2].Submit(new(ThreeCommandKind.Forfeit, Kingdom.Shu, Target: Kingdom.Wei)) is null, "One survivor forfeit failed."); Pump(1);
Check(!controllers[1].Session.Game.Snapshot.Alive[0] && !controllers[2].Session.Game.Snapshot.Alive[0], "Disconnected loser was not synchronized.");
peers.Online[0] = true; Pump(4);
Check(controllers.All(c => !c.Session.Game.Snapshot.Alive[0]), "Reconnect resurrected eliminated side.");
Console.WriteLine("PASS 三端一致悔棋、畸形包拒绝、任一存活者断线判负与淘汰后重连");
foreach (var controller in controllers) controller.Dispose();
Console.WriteLine("PASS 模拟三人名册、鉴权、断线迁移、原房主重连、走子与棋钟同步");

// Real asynchronous TCP sockets, signed handshakes and peer-to-peer reconnect (not an in-memory transport).
IsolationTests.Run();
ClockQueueTests.Run();
RoomChatNetworkTests.Run();
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
using var a = ThreeLanTransport.Host(port, "A");
using var b = ThreeLanTransport.Join("127.0.0.1", port, "B");
using var c = ThreeLanTransport.Join("127.0.0.1", port, "C");
ThreeLanTransport[] transports = [a, b, c];
var real = transports.Select(t => new ThreeNetworkController(t, new TimeControl(600, 60), AdvisorConquestPolicy.SharedTerritory)).ToArray();
async Task RealPump(double seconds, params int[] excluded)
{
    var watch = Stopwatch.StartNew(); var last = watch.Elapsed.TotalSeconds;
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        var now = watch.Elapsed.TotalSeconds; var delta = now - last; last = now;
        for (var i = 0; i < 3; i++) if (!excluded.Contains(i)) real[i].Tick(delta);
        await Task.Delay(10);
    }
}
await RealPump(5);
Check(transports.All(t => t.Roster.Count == 3 && t.Connected.Count == 3), "TCP full mesh not established.");
Check(real.All(r => r.Session.IsRunning), "TCP match did not start.");
Check(real[0].Submit(Move(0)) is null, "TCP Wei rejected."); await RealPump(1);
Check(real.All(r => r.Session.Game.Snapshot.Ply == 1), "TCP move differs.");
await RealPump(9, 0); // No polling or heartbeat from original host; its sockets time out.
var newHost = Array.FindIndex(real, r => r.LocalKingdom == Kingdom.Wu);
Check(real[newHost].Submit(Move(1)) is null, "TCP migrated host rejected move: " + real[newHost].Status);
await RealPump(1, 0);
await RealPump(6);
Check(transports.All(t => t.Connected.Count == 3), "TCP peer reconnect failed.");
Check(real.All(r => r.Session.Game.Snapshot.Ply == 2 && r.Session.Connected.All(x => x)), "TCP reconnect replay failed.");
foreach (var r in real) r.Dispose();
Console.WriteLine("PASS 真实 TCP 三端签名握手、全连接、心跳断线、房主迁移与自动重连");

sealed class FakeMesh
{
    public bool[] Online { get; } = [true, true, true];
    public Endpoint[] Ends { get; }
    public FakeMesh() { Ends = Enumerable.Range(0, 3).Select(i => new Endpoint(this, i)).ToArray(); }
    public sealed class Endpoint(FakeMesh mesh, int index) : IThreeRoomTransport
    {
        private readonly Queue<(string, byte[])> _queue = new();
        public string LocalId => index.ToString();
        public IReadOnlyList<ThreePeer> Roster { get; } = new[] { new ThreePeer("0", "A"), new ThreePeer("1", "B"), new ThreePeer("2", "C") };
        public IReadOnlySet<string> Connected => mesh.Online[index] ? Enumerable.Range(0, 3).Where(i => mesh.Online[i]).Select(i => i.ToString()).ToHashSet()
            : new HashSet<string> { LocalId };
        public string Status => "test";
        public event Action<string, byte[]>? Received;
        public void Send(string id, byte[] bytes) { var i = int.Parse(id); if (mesh.Online[index] && mesh.Online[i]) mesh.Ends[i]._queue.Enqueue((LocalId, bytes)); }
        public void Poll(double delta) { while (_queue.TryDequeue(out var m)) Received?.Invoke(m.Item1, m.Item2); }
        public void Dispose() { }
    }
}

sealed class WaitingRosterTransport : IThreeRoomTransport
{
    private IReadOnlyList<ThreePeer> _roster = Array.Empty<ThreePeer>();
    public string LocalId => "joiner";
    public IReadOnlyList<ThreePeer> Roster => _roster;
    public IReadOnlySet<string> Connected { get; } = new HashSet<string> { "joiner" };
    public string Status => "waiting";
    public event Action<string, byte[]>? Received { add { } remove { } }
    public void SetRoster(IReadOnlyList<ThreePeer> roster) => _roster = roster;
    public void Send(string peerId, byte[] packet) { }
    public void Poll(double delta) { }
    public void Dispose() { }
}
