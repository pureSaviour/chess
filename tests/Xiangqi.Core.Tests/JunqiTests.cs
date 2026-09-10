using Xiangqi.Core.Domain.Junqi;
using Xiangqi.Core.Application.Junqi;

internal static class JunqiTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("军旗标准布阵与玩家视图脱敏", StandardSetupAndRedaction),
        ("军旗道路、直线铁路与工兵转弯", RoadAndRailMovement),
        ("军旗行营保护与大本营锁定", CampAndHeadquarters),
        ("军旗军衔、炸弹和地雷战斗", Combat),
        ("军旗司令阵亡亮旗与夺旗胜利", ExposureAndVictory),
        ("军旗拒绝越界、非法布阵与必攻超时", RejectionsAndMandatoryAttack),
        ("军旗 AC2 INFO 1.0 棋谱往返并复验裁决", Ac2RoundTrip),
        ("军旗联机房主裁决并按玩家脱敏", NetworkAuthorityAndRedaction)
    ];

    private static void StandardSetupAndRedaction()
    {
        var game = JunqiGame.CreateStandard(); var snapshot = game.Snapshot;
        Equal(50, snapshot.Pieces.Count); Equal(JunqiSide.First, snapshot.SideToMove);
        var first = snapshot.ForPlayer(JunqiSide.First);
        Equal(25, first.Pieces.Count(x => x.Side == JunqiSide.First && x.Kind is not null));
        Equal(0, first.Pieces.Count(x => x.Side == JunqiSide.Second && x.Kind is not null));
        Equal(60, JunqiTopology.Instance.Cells.Count());
        Equal(25, JunqiTopology.Instance.SetupCells(JunqiSide.First).Count);
    }

    private static void RoadAndRailMovement()
    {
        var game = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(6, 0)),
            (JunqiSide.Second, JunqiPieceKind.Captain, new JunqiCell(0, 0)));
        True(game.LegalDestinations(new(6, 0)).Contains(new(6, 4)));
        False(game.LegalDestinations(new(6, 0)).Contains(new(10, 4)));
        var engineer = GameWith((JunqiSide.First, JunqiPieceKind.Engineer, new JunqiCell(6, 0)),
            (JunqiSide.Second, JunqiPieceKind.Captain, new JunqiCell(0, 0)));
        True(engineer.LegalDestinations(new(6, 0)).Contains(new(10, 4)));
        True(engineer.TryMove(new(new(6, 0), new(10, 4))).IsSuccess);
    }

    private static void CampAndHeadquarters()
    {
        var game = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(6, 1)),
            (JunqiSide.Second, JunqiPieceKind.Lieutenant, new JunqiCell(7, 1)));
        False(game.TryMove(new(new(6, 1), new(7, 1))).IsSuccess);
        var hq = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(11, 1)),
            (JunqiSide.Second, JunqiPieceKind.Lieutenant, new JunqiCell(0, 0)));
        Equal(0, hq.LegalDestinations(new(11, 1)).Count);
    }

    private static void Combat()
    {
        var rank = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(6, 0)),
            (JunqiSide.Second, JunqiPieceKind.Lieutenant, new JunqiCell(5, 0)));
        Equal(JunqiBattleResult.AttackerWins, rank.TryMove(new(new(6, 0), new(5, 0))).Battle);
        var bomb = GameWith((JunqiSide.First, JunqiPieceKind.Bomb, new JunqiCell(6, 0)),
            (JunqiSide.Second, JunqiPieceKind.FieldMarshal, new JunqiCell(5, 0)));
        Equal(JunqiBattleResult.BothRemoved, bomb.TryMove(new(new(6, 0), new(5, 0))).Battle);
        var mine = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(2, 0)),
            (JunqiSide.Second, JunqiPieceKind.Landmine, new JunqiCell(1, 0)));
        Equal(JunqiBattleResult.DefenderWins, mine.TryMove(new(new(2, 0), new(1, 0))).Battle);
        True(mine.Snapshot.Pieces.Any(x => x.Cell == new JunqiCell(1, 0) && x.Piece.Kind == JunqiPieceKind.Landmine));
    }

    private static void ExposureAndVictory()
    {
        var game = JunqiGame.FromPosition([
            new(new JunqiCell(6, 0), new JunqiPiece(1, JunqiSide.First, JunqiPieceKind.Bomb)),
            new(new JunqiCell(5, 0), new JunqiPiece(2, JunqiSide.Second, JunqiPieceKind.FieldMarshal)),
            new(new JunqiCell(0, 1), new JunqiPiece(3, JunqiSide.Second, JunqiPieceKind.Flag))
        ]);
        var result = game.TryMove(new(new(6, 0), new(5, 0)));
        Equal(JunqiSide.Second, result.FlagRevealedFor);
        True(game.Snapshot.ForPlayer(JunqiSide.First).Pieces.Any(x =>
            x.Side == JunqiSide.Second && x.Kind == JunqiPieceKind.Flag));

        var flag = GameWith((JunqiSide.First, JunqiPieceKind.Engineer, new JunqiCell(1, 1)),
            (JunqiSide.Second, JunqiPieceKind.Flag, new JunqiCell(0, 1)));
        Equal(JunqiBattleResult.FlagCaptured, flag.TryMove(new(new(1, 1), new(0, 1))).Battle);
        Equal(JunqiSide.First, flag.Snapshot.Winner);
    }

    private static void RejectionsAndMandatoryAttack()
    {
        var invalid = JunqiGame.Build(JunqiSide.First, "abccddeeffggghhhiiijjkklj", 1).ToArray();
        var flag = Array.FindIndex(invalid, x => x.Piece.Kind == JunqiPieceKind.Flag);
        invalid[flag] = invalid[flag] with { Cell = new JunqiCell(10, 2) };
        Throws<ArgumentException>(() => new JunqiGame(invalid,
            JunqiGame.Build(JunqiSide.Second, "jlkkjjiiihhhgggffeeddccba", 26)));
        var game = GameWith((JunqiSide.First, JunqiPieceKind.Captain, new JunqiCell(6, 0)),
            (JunqiSide.Second, JunqiPieceKind.Captain, new JunqiCell(5, 4)), mandatory: 1);
        True(game.TryMove(new(new(6, 0), new(6, 1))).IsSuccess);
        Equal(JunqiEndReason.MandatoryAttack, game.Snapshot.EndReason);
        False(game.TryMove(new(new(-1, 0), new(0, 0))).IsSuccess);
    }

    private static void Ac2RoundTrip()
    {
        var game = JunqiGame.CreateStandard();
        var move = new JunqiMove(new(6, 0), new(5, 0));
        var result = game.TryMove(move); True(result.IsSuccess);
        var record = new JunqiGameRecord("先手", "后手", 1800, 31,
            JunqiGame.Build(JunqiSide.First, "abccddeeffggghhhiiijjkklj", 1),
            JunqiGame.Build(JunqiSide.Second, "jlkkjjiiihhhgggffeeddccba", 26),
            [new JunqiRecordedMove(JunqiSide.First, move, result.Battle, result.FlagRevealedFor)], null);
        var text = JunqiAc2Codec.Serialize(record);
        True(text.Contains("INFO[1.0]", StringComparison.Ordinal));
        True(text.Contains("MA[G0F0]", StringComparison.Ordinal));
        True(JunqiAc2Codec.TryParse(text, out var parsed, out var error));
        Equal(1, parsed!.Replay()[^1].PlyNumber);
        False(JunqiAc2Codec.TryParse(text.Replace("RA[1]", "RA[2]"), out _, out _));
    }

    private static void NetworkAuthorityAndRedaction()
    {
        var hostTransport = new FakePacketTransport(true); var clientTransport = new FakePacketTransport(false);
        hostTransport.Peer = clientTransport; clientTransport.Peer = hostTransport;
        var host = new NetworkJunqiMatchController(hostTransport, JunqiSide.Second);
        var client = new NetworkJunqiMatchController(clientTransport, JunqiSide.Second);
        hostTransport.Connect(); clientTransport.Connect();
        True(host.IsReady); True(client.IsReady); Equal(JunqiSide.First, client.LocalSide);
        Equal(25, client.Snapshot.Pieces.Count(x => x.Side == JunqiSide.First && x.Kind is not null));
        Equal(0, client.Snapshot.Pieces.Count(x => x.Side == JunqiSide.Second && x.Kind is not null));
        var source = client.Snapshot.Pieces.First(x => x.Side == JunqiSide.First && client.LegalDestinations(x.Cell).Count > 0).Cell;
        var target = client.LegalDestinations(source)[0];
        var result = client.SubmitMoveAsync(source, target).AsTask().GetAwaiter().GetResult();
        True(result.Accepted); Equal(1, host.Snapshot.PlyNumber); Equal(1, client.Snapshot.PlyNumber);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult(); client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static JunqiGame GameWith((JunqiSide Side, JunqiPieceKind Kind, JunqiCell Cell) first,
        (JunqiSide Side, JunqiPieceKind Kind, JunqiCell Cell) second, int mandatory = 31)
    {
        return JunqiGame.FromPosition([
            new(first.Cell, new JunqiPiece(1, first.Side, first.Kind)),
            new(second.Cell, new JunqiPiece(2, second.Side, second.Kind))
        ], mandatoryAttackPly: mandatory);
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
