using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

static class ThreeKingdomsRuleTests
{
    public static IEnumerable<(string Name, Action Run)> All => new (string, Action)[]
    {
        ("三国48子开局轮次与单手悔棋", Opening),
        ("三国收编/复活和两种士策略", Conquest),
        ("三国兵出生方向与永久出境限制", Soldiers),
        ("三国任一存活者断线判负与障碍", Disconnect),
        ("三国悔棋全员同意且不返还耗时", Negotiation),
        ("三国断线等待继续计时含超时淘汰", Timeout),
        ("三国中心车炮分支不绕弯及炮架", Center)
    };
    static BoardCellId C(Kingdom k, int f, int r) => new KingdomSquare(k, f, r).Cell;
    static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    static KingdomPlacement P(int id, PieceKind kind, Kingdom birth, Kingdom? owner, int f, int r,
        Kingdom? region = null, bool left = false) => new(C(region ?? birth, f, r), new(id, kind, birth, owner, left));
    static List<KingdomPlacement> Kings() => Enum.GetValues<Kingdom>()
        .Select(k => P((int)k, BuiltInPieceKinds.General, k, k, 4, 0)).ToList();
    static void Opening()
    {
        var game = new ThreeKingdomsGame();
        Check(game.Snapshot.Pieces.Count == 48, "Opening not 48.");
        foreach (var k in Enum.GetValues<Kingdom>())
        {
            Check(game.Snapshot.Turn == k, "Turn differs.");
            Check(game.Move(k, new(C(k, 0, 3), C(k, 0, 4))).Success, "Opening pawn rejected.");
        }
        Check(!game.CanUndo(Kingdom.Wei) && game.CanUndo(Kingdom.Shu), "Only last actor can undo.");
        Check(game.Undo(Kingdom.Shu) && game.Snapshot.Ply == 2 && game.Snapshot.Turn == Kingdom.Shu, "Undo not one ply.");
        Check(!game.Undo(Kingdom.Shu), "Repeated undo accepted.");
    }
    static void Conquest()
    {
        foreach (var policy in Enum.GetValues<AdvisorConquestPolicy>())
        {
            var pieces = Kings();
            pieces.Add(P(10, BuiltInPieceKinds.Chariot, Kingdom.Wei, Kingdom.Wei, 4, 1, Kingdom.Shu));
            pieces.Add(P(11, BuiltInPieceKinds.Advisor, Kingdom.Shu, Kingdom.Shu, 3, 0));
            pieces.Add(P(12, BuiltInPieceKinds.Advisor, Kingdom.Wei, Kingdom.Wei, 3, 2));
            pieces.Add(P(13, BuiltInPieceKinds.Elephant, Kingdom.Wei, Kingdom.Wei, 2, 4));
            var game = new ThreeKingdomsGame(pieces, Kingdom.Wei, policy);
            var old = game.Snapshot;
            Check(game.Move(Kingdom.Wei, new(C(Kingdom.Shu, 4, 1), C(Kingdom.Shu, 4, 0))).Success, "Capture general rejected.");
            Check(!game.Snapshot.Alive[2] && game.Snapshot.TerritoryOwners[2] == Kingdom.Wei, "Conquest failed.");
            Check(game.Snapshot.Pieces.Any(p => p.Piece.Id == 11) == (policy == AdvisorConquestPolicy.SharedTerritory), "Advisor policy differs.");
            Check(game.LegalDestinations(C(Kingdom.Wei, 3, 2)).Contains(C(Kingdom.Wei, 2, 3)) ==
                (policy == AdvisorConquestPolicy.SharedTerritory), "Advisor palace not relaxed only in shared policy.");
            Check(game.LegalDestinations(C(Kingdom.Wei, 2, 4)).Any(c => KingdomSquare.FromCell(c).Region == Kingdom.Shu), "Original elephant cannot enter allied land.");
            Check(game.Undo(Kingdom.Wei) && game.Snapshot.Pieces.SequenceEqual(old.Pieces) && game.Snapshot.Alive.All(a => a), "Conquest undo incomplete.");
        }
    }
    static void Soldiers()
    {
        var pieces = Kings();
        pieces.Add(P(10, BuiltInPieceKinds.Soldier, Kingdom.Shu, Kingdom.Wei, 2, 3));
        pieces.Add(P(11, BuiltInPieceKinds.Soldier, Kingdom.Wei, Kingdom.Wei, 6, 4, Kingdom.Shu, true));
        var game = new ThreeKingdomsGame(pieces, Kingdom.Wei);
        Check(game.LegalDestinations(C(Kingdom.Shu, 2, 3)).SequenceEqual(new[] { C(Kingdom.Shu, 2, 4) }), "Conscript birth direction lost.");
        Check(!game.LegalDestinations(C(Kingdom.Shu, 6, 4)).Any(c => KingdomSquare.FromCell(c).Region == Kingdom.Wei), "Soldier returned home.");
        Check(game.LegalDestinations(C(Kingdom.Shu, 6, 4)).Contains(C(Kingdom.Shu, 6, 3)), "Foreign soldier cannot retreat.");
        pieces = Kings();
        pieces.Add(P(10, BuiltInPieceKinds.Chariot, Kingdom.Wei, Kingdom.Wei, 4, 1, Kingdom.Shu));
        pieces.Add(P(11, BuiltInPieceKinds.Soldier, Kingdom.Wei, Kingdom.Wei, 2, 4));
        game = new ThreeKingdomsGame(pieces, Kingdom.Wei);
        game.Move(Kingdom.Wei, new(C(Kingdom.Shu, 4, 1), C(Kingdom.Shu, 4, 0)));
        Check(!game.LegalDestinations(C(Kingdom.Wei, 2, 4)).Contains(C(Kingdom.Shu, 6, 4)), "Soldier entered conquered land.");
    }
    static void Disconnect()
    {
        var session = new ThreeMatchSession(TimeControl.Unlimited);
        session.SetConnected(Kingdom.Wei, false);
        Check(session.ForfeitDisconnected(Kingdom.Wu, Kingdom.Wei) is null, "One survivor vote did not forfeit host.");
        Check(!session.Game.Snapshot.Alive[0] && session.Game.Snapshot.Pieces.Count(p => p.Piece.Owner is null) == 15, "Obstacles/general removal differs.");
        session.SetConnected(Kingdom.Wei, true);
        Check(session.Move(Kingdom.Wei, new(C(Kingdom.Wei, 0, 3), C(Kingdom.Wei, 0, 4))) is not null, "Reconnect resurrected forfeited player.");
        Check(session.RequestOffer(Kingdom.Wu, KingdomOfferKind.Draw) is null && session.Offer!.Required.SequenceEqual(new[] { Kingdom.Shu }), "Eliminated player required to vote.");
        Check(session.Vote(Kingdom.Shu, session.Offer!.Id, true) is null && session.Game.Snapshot.IsFinished, "Two-player draw failed.");
    }
    static void Negotiation()
    {
        var s = new ThreeMatchSession(new TimeControl(600, 60));
        s.Advance(10);
        s.Move(Kingdom.Wei, new(C(Kingdom.Wei, 0, 3), C(Kingdom.Wei, 0, 4)));
        s.Advance(7);
        s.RequestOffer(Kingdom.Wei, KingdomOfferKind.Undo);
        var id = s.Offer!.Id;
        s.Advance(15);
        s.Vote(Kingdom.Wu, id, true);
        Check(s.Game.Snapshot.Ply == 1, "Only one approval undid.");
        s.Vote(Kingdom.Shu, id, true);
        Check(s.Game.Snapshot.Ply == 0 && s.GameTimes[0] == 590 && s.GameTimes[1] == 593 && s.StepTimes[0] == 50, "Undo refunded time or voting consumed time.");
        Check(s.Vote(Kingdom.Shu, id, true) is not null, "Stale vote accepted.");
    }
    static void Timeout()
    {
        var s = new ThreeMatchSession(new TimeControl(600, 10));
        s.SetConnected(Kingdom.Wei, false);
        s.Advance(9);
        Check(s.Game.Snapshot.Alive[0] && s.StepTimes[0] == 1, "Disconnected clock paused.");
        Check(s.Advance(1) == Kingdom.Wei && !s.Game.Snapshot.Alive[0] && s.Game.Snapshot.Turn == Kingdom.Wu, "Timeout failed.");
    }
    static void Center()
    {
        var p = Kings(); p.Add(P(10, BuiltInPieceKinds.Chariot, Kingdom.Wei, Kingdom.Wei, 4, 3));
        var g = new ThreeKingdomsGame(p, Kingdom.Wei);
        Check(g.LegalDestinations(C(Kingdom.Wei, 4, 3)).Contains(C(Kingdom.Wu, 4, 0)), "Central branch missing.");
        Check(!g.LegalDestinations(C(Kingdom.Wei, 4, 3)).Contains(C(Kingdom.Wu, 5, 4)), "Chariot turned after crossing.");
        p[3] = P(10, BuiltInPieceKinds.Cannon, Kingdom.Wei, Kingdom.Wei, 4, 3);
        g = new ThreeKingdomsGame(p, Kingdom.Wei);
        Check(!g.LegalDestinations(C(Kingdom.Wei, 4, 3)).Contains(C(Kingdom.Wu, 4, 0)), "Cannon captured without screen.");
        p.Add(P(11, BuiltInPieceKinds.Soldier, Kingdom.Wei, null, 4, 4));
        g = new ThreeKingdomsGame(p, Kingdom.Wei);
        Check(g.LegalDestinations(C(Kingdom.Wei, 4, 3)).Contains(C(Kingdom.Wu, 4, 0)), "Obstacle cannot serve as cannon screen.");
    }
}
