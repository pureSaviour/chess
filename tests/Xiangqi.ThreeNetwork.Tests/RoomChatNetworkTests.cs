using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;

static class RoomChatNetworkTests
{
    public static void Run()
    {
        var mesh = new WaitingMesh { RosterCount = 2 };
        var rooms = new List<ThreeNetworkController>
        {
            new(mesh.Ends[0], TimeControl.Unlimited, AdvisorConquestPolicy.SharedTerritory),
            new(mesh.Ends[1], TimeControl.Unlimited, AdvisorConquestPolicy.SharedTerritory)
        };
        void Pump(double seconds)
        {
            for (var elapsed = 0d; elapsed < seconds; elapsed += 0.05)
                foreach (var room in rooms) room.Tick(0.05);
        }
        static void Check(bool ok, string error)
        { if (!ok) throw new InvalidOperationException(error); }

        Pump(1);
        Check(rooms[0].SendChat(Kingdom.Wei, null, "未满员也能聊天") is null, "Waiting-room chat was rejected.");
        Pump(1);
        Check(rooms[1].Chat.Entries.Any(e => e.Text == "未满员也能聊天"), "Waiting peer missed room chat.");

        mesh.RosterCount = 3;
        rooms.Add(new(mesh.Ends[2], TimeControl.Unlimited, AdvisorConquestPolicy.SharedTerritory));
        Pump(4);
        Check(rooms[2].Chat.Entries.Any(e => e.Text == "未满员也能聊天"), "Late joiner did not receive message-board history.");

        mesh.Online[2] = false;
        Pump(1);
        Check(rooms[0].SendChat(Kingdom.Wei, Kingdom.Shu, "蜀重连后可见") is null, "Directed chat was rejected while recipient disconnected.");
        Pump(1);
        var relayed = rooms[1].Chat.Entries.Single(e => e.Text == "蜀重连后可见");
        Check(!RoomChatManager.IsVisibleTo(relayed, 1), "Unaddressed seat could present directed chat.");
        mesh.Online[2] = true;
        Pump(3);
        var recovered = rooms[2].Chat.Entries.Single(e => e.Text == "蜀重连后可见");
        Check(RoomChatManager.IsVisibleTo(recovered, 2), "Reconnected recipient cannot see directed history.");

        mesh.Online[0] = false;
        Pump(1);
        Check(rooms[1].SendChat(Kingdom.Wu, null, "原房管离线仍可发言") is null, "Chat depended on original host.");
        Pump(1);
        Check(rooms[2].Chat.Entries.Any(e => e.Text == "原房管离线仍可发言"), "Remaining peer missed post-host chat.");

        foreach (var room in rooms) room.Dispose();
        Console.WriteLine("PASS 未满员消息板、定向可见性、断线历史恢复与房主离线聊天");
    }

    private sealed class WaitingMesh
    {
        public int RosterCount { get; set; }
        public bool[] Online { get; } = [true, true, true];
        public Endpoint[] Ends { get; }
        public WaitingMesh() => Ends = Enumerable.Range(0, 3).Select(i => new Endpoint(this, i)).ToArray();

        public sealed class Endpoint(WaitingMesh mesh, int index) : IThreeRoomTransport
        {
            private static readonly ThreePeer[] Peers =
                [new("0", "曹操"), new("1", "孙权"), new("2", "刘备")];
            private readonly Queue<(string Sender, byte[] Bytes)> _queue = new();
            public string LocalId => index.ToString();
            public IReadOnlyList<ThreePeer> Roster => Peers.Take(mesh.RosterCount).ToArray();
            public IReadOnlySet<string> Connected => Enumerable.Range(0, mesh.RosterCount)
                .Where(i => mesh.Online[i]).Select(i => i.ToString()).ToHashSet();
            public string Status => "waiting-chat-test";
            public event Action<string, byte[]>? Received;
            public void Send(string peerId, byte[] packet)
            {
                var recipient = int.Parse(peerId);
                if (mesh.Online[index] && mesh.Online[recipient])
                    mesh.Ends[recipient]._queue.Enqueue((LocalId, packet));
            }
            public void Poll(double delta)
            { while (_queue.TryDequeue(out var item)) Received?.Invoke(item.Sender, item.Bytes); }
            public void Dispose() { }
        }
    }
}
