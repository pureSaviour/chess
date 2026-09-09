using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;

static class IsolationTests
{
    public static void Run()
    {
        var mesh = new FakeMesh();
        var rooms = mesh.Ends.Select(t => new ThreeNetworkController(t, new TimeControl(600, 60), AdvisorConquestPolicy.SharedTerritory)).ToArray();
        void Pump(double seconds)
        {
            for (var t = 0d; t < seconds; t += 0.05) foreach (var room in rooms) room.Tick(0.05);
        }
        void Check(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
        Pump(3);
        var old = rooms[0].Session.GameTimes[0];
        mesh.Online[1] = false; mesh.Online[2] = false;
        Pump(6);
        Check(rooms[0].DisplayedGameTimes[0] < old - 5, "Isolated display clock stopped.");
        Check(rooms[0].Submit(new(ThreeCommandKind.Move, Kingdom.Wei, 27, 36)) is not null, "Isolated move committed without replica.");
        Check(rooms[0].Submit(new(ThreeCommandKind.Forfeit, Kingdom.Wei, Target: Kingdom.Shu)) is null, "Isolated forfeit was not queued.");
        Check(rooms.All(r => r.Session.Game.Snapshot.Alive[2]), "Forfeit finalized before communication recovered.");
        mesh.Online[1] = true; Pump(6);
        Check(!rooms[0].Session.Game.Snapshot.Alive[2] && !rooms[1].Session.Game.Snapshot.Alive[2], "Deferred forfeit not committed after sync.");
        Check(rooms[0].Session.GameTimes[0] < old - 9, "Isolation elapsed time was refunded.");
        mesh.Online[2] = true; Pump(4);
        Check(rooms.All(r => !r.Session.Game.Snapshot.Alive[2] && !r.Status.Contains("冲突")), "Deferred forfeit's reconnect diverged.");
        foreach (var room in rooms) room.Dispose();
        Console.WriteLine("PASS 仅一端在线累计棋钟、不提交走子、暂存判负、恢复后统一结算");
    }
}
