using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;

static class ThreeExtensionTests
{
    public static IEnumerable<(string Name, Action Run)> All => new (string, Action)[]
    {
        ("三国无着可走跳过该国且全体无着和棋", Immobile),
        ("三国被将军可以不解将", IgnoreCheck),
        ("三国可注册新棋种而不修改聚合", Extension),
        ("三国扩展 PGN 可往返并合法重放", RecordRoundTrip),
        ("三国试下分支与权威会话隔离", TrialIsolation)
    };
    static BoardCellId C(Kingdom k, int f, int r) => new KingdomSquare(k, f, r).Cell;
    static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    static void Immobile()
    {
        var placements = new List<KingdomPlacement>();
        foreach (var k in Enum.GetValues<Kingdom>())
        {
            placements.Add(new(C(k, 4, 0), new(1000 + (int)k, BuiltInPieceKinds.General, k, k)));
            foreach (var (f, r) in new[] { (3, 0), (5, 0), (4, 1) })
                placements.Add(new(C(k, f, r), new(C(k, f, r).Value, BuiltInPieceKinds.Soldier, k, null)));
        }
        Check(new ThreeKingdomsGame(placements, Kingdom.Wei).Snapshot.EndReason == KingdomEndReason.NoMoves, "All immobile did not draw.");
        placements.RemoveAll(p => p.Cell == C(Kingdom.Wu, 4, 1));
        var g = new ThreeKingdomsGame(placements, Kingdom.Wei);
        Check(g.Snapshot.Turn == Kingdom.Wu && !g.Snapshot.IsFinished, "Immobile Wei was not skipped.");
    }
    static void IgnoreCheck()
    {
        var p = Enum.GetValues<Kingdom>().Select(k => new KingdomPlacement(C(k, 4, 0), new((int)k, BuiltInPieceKinds.General, k, k))).ToList();
        p.Add(new(C(Kingdom.Wei, 4, 1), new(10, BuiltInPieceKinds.Chariot, Kingdom.Wu, Kingdom.Wu)));
        p.Add(new(C(Kingdom.Wei, 0, 3), new(11, BuiltInPieceKinds.Soldier, Kingdom.Wei, Kingdom.Wei)));
        var g = new ThreeKingdomsGame(p, Kingdom.Wei);
        Check(g.IsThreatened(Kingdom.Wei), "Threat not detected.");
        Check(g.Move(Kingdom.Wei, new(C(Kingdom.Wei, 0, 3), C(Kingdom.Wei, 0, 4))).Success, "Must-evade incorrectly enforced.");
    }
    static void Extension()
    {
        var kind = new PieceKind("test-scout");
        var rule = new ThreePieceRule(kind, (b, p, a, z) => b.Topology.Neighbors(a).Contains(z));
        var registry = new ThreePieceRuleRegistry(ThreeBuiltInRules.All.Append(rule));
        var p = new[] { new KingdomPlacement(C(Kingdom.Wei, 4, 2), new(1, kind, Kingdom.Wei, Kingdom.Wei)) };
        var g = new ThreeKingdomsGame(p, Kingdom.Wei, rules: registry);
        Check(g.LegalDestinations(C(Kingdom.Wei, 4, 2)).Count == 4, "Registered custom piece not used.");
    }
    static void RecordRoundTrip()
    {
        var session = new ThreeMatchSession(TimeControl.Unlimited);
        var state = session.Game.Snapshot;
        var from = state.Pieces.First(x => x.Piece.Owner == state.Turn && session.Game.LegalDestinations(x.Cell).Count > 0).Cell;
        var to = session.Game.LegalDestinations(from)[0];
        Check(session.Move(state.Turn, new(from, to)) is null, "Move failed.");
        var text = ThreeKingdomsPgnCodec.Serialize(session.CreateRecord());
        Check(ThreeKingdomsPgnCodec.TryParse(text, out var imported, out var error), error ?? "Import failed.");
        Check(imported!.Replay().Count == 2, "Replay length mismatch.");
    }
    static void TrialIsolation()
    {
        var session = new ThreeMatchSession(TimeControl.Unlimited);
        var trial = session.BeginTrial(); var state = trial.Snapshot;
        var from = state.Pieces.First(x => x.Piece.Owner == state.Turn && trial.GetLegalDestinations(x.Cell).Count > 0).Cell;
        var to = trial.GetLegalDestinations(from)[0];
        Check(trial.TryMove(from, to).Success, "Trial move failed.");
        Check(session.Game.Snapshot.Ply == 0 && trial.Snapshot.Ply == 1, "Trial leaked into authoritative game.");
    }
}
