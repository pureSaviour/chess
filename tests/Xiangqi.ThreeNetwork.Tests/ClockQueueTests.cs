using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;

static class ClockQueueTests
{
    public static void Run()
    {
        var mesh = new FakeMesh();
        var rooms = mesh.Ends.Select(t => new ThreeNetworkController(t,
            new TimeControl(864000, 0), AdvisorConquestPolicy.SharedTerritory)).ToArray();
        void Pump(double seconds)
        {
            for (var t = 0d; t < seconds; t += 0.05)
                foreach (var room in rooms) room.Tick(0.05);
        }
        void Check(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
        Pump(3);
        var before = rooms[0].Session.GameTimes.Sum();
        for (var i = 0; i < 40; i++)
            rooms[0].Submit(new(ThreeCommandKind.Move, Kingdom.Wei, 27, 36));
        // Keep the leader running while its outgoing queue cannot yet be acknowledged.
        for (var i = 0; i < 40; i++)
        {
            rooms[0].Tick(0.05);
            rooms[0].Submit(new(ThreeCommandKind.Move, Kingdom.Wei, 27, 36));
        }
        Pump(8);
        Check(rooms[0].Session.GameTimes.Sum() < before - 9,
            "A full command queue discarded clock debt.");
        before = rooms[0].Session.GameTimes.Sum();
        mesh.Online[1] = false; mesh.Online[2] = false;
        foreach (var room in rooms) room.Tick(172800);
        mesh.Online[1] = true; mesh.Online[2] = true;
        Pump(8);
        Check(rooms.All(r => r.Session.GameTimes.Sum() < before - 172799 && !r.Status.Contains("冲突")),
            "Long isolation dropped clock debt beyond one packet's 24-hour limit.");
        foreach (var room in rooms) room.Dispose();
        Console.WriteLine("PASS 同步队列背压与超过单包上限的离线耗时不丢失");
    }
}
