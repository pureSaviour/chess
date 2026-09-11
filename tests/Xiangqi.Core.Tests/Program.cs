using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Rules;
using Xiangqi.Core.Application;

var tests = new (string Name, Action Run)[]
{
    ("标准开局包含 32 枚棋子且红方先行", StandardOpening),
    ("兵只能前进且过河后可以横走", SoldierMovement),
    ("马腿受阻时不能移动", HorseLeg),
    ("炮吃子必须恰好隔一枚棋子", CannonScreen),
    ("象不能过河且象眼可被阻挡", ElephantBoundary),
    ("将仕受九宫限制且车不能穿子", PalaceAndChariotRules),
    ("移动遮挡棋子导致将帅照面时着法非法", FlyingGeneralSafety),
    ("无可应将着法时正确判定将死", Checkmate),
    ("双车错识别器只发出语义特效提示", DoubleChariotCue),
    ("联机协调器同步双方着法并校验序号", NetworkCoordinator),
    ("局时和步时任一耗尽都会返回超时方", MatchClockTimeout),
    ("超时裁决结束棋局并指定另一方获胜", TimeoutAdjudication),
    ("认输裁决在会话与联机协调器中指定对手获胜", ResignationAdjudication),
    ("悔棋完整恢复棋盘与行棋方", UndoRestoresState),
    ("双方同意和棋后对局无胜方结束", DrawAdjudication),
    ("联机聊天消息通过协调器双向传递", NetworkChat),
    ("共享房管校验目标、可见性、去重与历史导入", RoomChatManagement),
    ("远端悔棋申请获同意后撤销最近一手", NetworkUndoOffer),
    ("本地和棋申请获同意后结束双方对局", NetworkDrawOffer),
    ("拒绝对局申请时保持原有局面", NetworkRejectedOffer),
    ("只有最后行棋方可以申请悔棋", NetworkUndoEligibility),
    ("共享联机协议可往返全部对局消息", MatchWireRoundTrip),
    ("共享联机协议拒绝越界和畸形数据", MatchWireRejectsMalformedData),
    ("标准局面可编码为象棋 UCI FEN", UciFen),
    ("象棋 UCI 坐标可安全往返", UciCoordinates),
    ("PGN/ICCS 棋谱可合法往返并保留 FEN 残局", PgnRecordRoundTrip),
    ("棋谱导入拒绝非法着法", PgnRejectsIllegalMove),
    ("试下分支不修改权威棋局", TrialIsIsolated),
    ("试下着法复用权威会话的表现语义", TrialPresentationCues),
    ("复盘分析串行缓存并将评分归一到红方", ReplayAnalysis),
    ("AI 着法经 GameSession 校验且可整回合悔棋", AiCoordinator)
};

tests = tests.Concat(ThreeKingdomsTests.All).ToArray();
tests = tests.Concat(ThreeKingdomsRuleTests.All).ToArray();
tests = tests.Concat(ThreeExtensionTests.All).ToArray();
tests = tests.Concat(JieqiTests.All).ToArray();
tests = tests.Concat(JunqiTests.All).ToArray();
tests = tests.Concat(GomokuTests.All).ToArray();
var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL  {test.Name}\n      {error.Message}");
    }
}

Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void StandardOpening()
{
    var game = ChessGame.CreateStandard();
    Equal(32, game.Snapshot.Pieces.Count);
    Equal(PieceSide.Red, game.Snapshot.SideToMove);
    Contains(game.GetLegalDestinations(new BoardPosition(0, 3)), new BoardPosition(0, 4));
}

static void SoldierMovement()
{
    var rule = new SoldierRule();
    var board = new BoardState();
    var red = Piece(1, PieceSide.Red, BuiltInPieceKinds.Soldier);
    True(rule.CanMove(board, red, new BoardPosition(4, 3), new BoardPosition(4, 4)));
    False(rule.CanMove(board, red, new BoardPosition(4, 3), new BoardPosition(3, 3)));
    True(rule.CanMove(board, red, new BoardPosition(4, 5), new BoardPosition(3, 5)));
    False(rule.CanMove(board, red, new BoardPosition(4, 5), new BoardPosition(4, 4)));
}

static void HorseLeg()
{
    var horse = Piece(1, PieceSide.Red, BuiltInPieceKinds.Horse);
    var blocker = Piece(2, PieceSide.Red, BuiltInPieceKinds.Soldier);
    var clear = new BoardState();
    var blocked = new BoardState([new PlacedPiece(new BoardPosition(2, 1), blocker)]);
    var rule = new HorseRule();
    True(rule.CanMove(clear, horse, new BoardPosition(1, 1), new BoardPosition(3, 2)));
    False(rule.CanMove(blocked, horse, new BoardPosition(1, 1), new BoardPosition(3, 2)));
}

static void CannonScreen()
{
    var cannon = Piece(1, PieceSide.Red, BuiltInPieceKinds.Cannon);
    var screen = Piece(2, PieceSide.Red, BuiltInPieceKinds.Soldier);
    var target = Piece(3, PieceSide.Black, BuiltInPieceKinds.Horse);
    var rule = new CannonRule();
    var board = new BoardState([
        new PlacedPiece(new BoardPosition(4, 4), screen),
        new PlacedPiece(new BoardPosition(4, 7), target)
    ]);
    True(rule.CanMove(board, cannon, new BoardPosition(4, 2), new BoardPosition(4, 7)));

    var noScreen = new BoardState([new PlacedPiece(new BoardPosition(4, 7), target)]);
    False(rule.CanMove(noScreen, cannon, new BoardPosition(4, 2), new BoardPosition(4, 7)));
}

static void ElephantBoundary()
{
    var elephant = Piece(1, PieceSide.Red, BuiltInPieceKinds.Elephant);
    var rule = new ElephantRule();
    True(rule.CanMove(new BoardState(), elephant, new BoardPosition(2, 2), new BoardPosition(4, 4)));
    False(rule.CanMove(new BoardState(), elephant, new BoardPosition(4, 4), new BoardPosition(6, 6)));
    var blocked = new BoardState([
        new PlacedPiece(new BoardPosition(3, 3), Piece(2, PieceSide.Black, BuiltInPieceKinds.Soldier))
    ]);
    False(rule.CanMove(blocked, elephant, new BoardPosition(2, 2), new BoardPosition(4, 4)));
}

static void PalaceAndChariotRules()
{
    var board = new BoardState([
        new PlacedPiece(new BoardPosition(4, 2), Piece(8, PieceSide.Red, BuiltInPieceKinds.Soldier))
    ]);
    var general = Piece(1, PieceSide.Red, BuiltInPieceKinds.General);
    var advisor = Piece(2, PieceSide.Red, BuiltInPieceKinds.Advisor);
    var chariot = Piece(3, PieceSide.Red, BuiltInPieceKinds.Chariot);

    True(new GeneralRule().CanMove(new BoardState(), general,
        new BoardPosition(4, 1), new BoardPosition(4, 2)));
    False(new GeneralRule().CanMove(new BoardState(), general,
        new BoardPosition(4, 1), new BoardPosition(6, 1)));
    True(new AdvisorRule().CanMove(new BoardState(), advisor,
        new BoardPosition(4, 1), new BoardPosition(5, 2)));
    False(new AdvisorRule().CanMove(new BoardState(), advisor,
        new BoardPosition(5, 2), new BoardPosition(6, 3)));
    False(new ChariotRule().CanMove(board, chariot,
        new BoardPosition(4, 0), new BoardPosition(4, 4)));
}

static void FlyingGeneralSafety()
{
    var board = new BoardState([
        At(1, PieceSide.Red, BuiltInPieceKinds.General, 4, 0),
        At(2, PieceSide.Black, BuiltInPieceKinds.General, 4, 9),
        At(3, PieceSide.Red, BuiltInPieceKinds.Chariot, 4, 5)
    ]);
    var game = new ChessGame(board, PieceRuleRegistry.CreateStandard());
    var result = game.TryMove(new ChessMove(new BoardPosition(4, 5), new BoardPosition(3, 5)));
    False(result.IsSuccess);
    Equal(MoveRejection.WouldLeaveGeneralInCheck, result.Rejection);
}

static void Checkmate()
{
    var board = new BoardState([
        At(1, PieceSide.Red, BuiltInPieceKinds.General, 4, 0),
        At(2, PieceSide.Black, BuiltInPieceKinds.General, 4, 9),
        At(3, PieceSide.Red, BuiltInPieceKinds.Chariot, 3, 8),
        At(4, PieceSide.Red, BuiltInPieceKinds.Chariot, 5, 8),
        At(5, PieceSide.Red, BuiltInPieceKinds.Chariot, 4, 7)
    ]);
    var game = new ChessGame(board, PieceRuleRegistry.CreateStandard());
    var result = game.TryMove(new ChessMove(new BoardPosition(4, 7), new BoardPosition(4, 8)));
    True(result.IsSuccess);
    True(game.Snapshot.IsFinished);
    Equal(PieceSide.Red, game.Snapshot.Winner);
    Equal(GameEndReason.Checkmate, game.Snapshot.EndReason);
}

static void DoubleChariotCue()
{
    var board = new BoardState([
        At(1, PieceSide.Red, BuiltInPieceKinds.General, 4, 0),
        At(2, PieceSide.Black, BuiltInPieceKinds.General, 4, 9),
        At(3, PieceSide.Red, BuiltInPieceKinds.Chariot, 3, 8),
        At(4, PieceSide.Red, BuiltInPieceKinds.Chariot, 5, 8),
        At(5, PieceSide.Red, BuiltInPieceKinds.Chariot, 4, 7)
    ]);
    var game = new ChessGame(board, PieceRuleRegistry.CreateStandard());
    var before = game.Snapshot;
    var result = game.TryMove(new ChessMove(new BoardPosition(4, 7), new BoardPosition(4, 8)));
    var cues = new DoubleChariotMateDetector().Detect(before, game.Snapshot, result).ToArray();
    Equal(1, cues.Length);
    Equal(PresentationCueKind.SpecialPattern, cues[0].Kind);
    Equal(DoubleChariotMateDetector.CueId, cues[0].CueId);
}

static void NetworkCoordinator()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    var local = coordinator.SubmitLocalMoveAsync(
        new BoardPosition(0, 3), new BoardPosition(0, 4)).AsTask().GetAwaiter().GetResult();
    True(local.IsSuccess);
    Equal(1L, transport.LastSent?.Sequence ?? -1);
    transport.Receive(new MoveCommand(2,
        new ChessMove(new BoardPosition(0, 6), new BoardPosition(0, 5))));
    Equal(2, session.Snapshot.PlyNumber);
    Equal(PieceSide.Red, session.Snapshot.SideToMove);
    var failed = false;
    coordinator.SynchronizationFailed += _ => failed = true;
    transport.Receive(new MoveCommand(7,
        new ChessMove(new BoardPosition(2, 6), new BoardPosition(2, 5))));
    True(failed);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void MatchClockTimeout()
{
    var clock = new MatchClock(new TimeControl(10, 3)) { IsRunning = true };
    Equal<PieceSide?>(null, clock.Advance(2));
    True(Math.Abs(clock.Snapshot.RedGameSeconds - 8) < 0.001);
    True(Math.Abs(clock.Snapshot.MoveSeconds - 1) < 0.001);
    Equal<PieceSide?>(PieceSide.Red, clock.Advance(1.1));
    clock.BeginTurn(PieceSide.Black);
    Equal<PieceSide?>(null, clock.Advance(2.9));
    Equal<PieceSide?>(PieceSide.Black, clock.Advance(0.2));
}

static void TimeoutAdjudication()
{
    var session = new GameSession();
    True(session.DeclareTimeout(PieceSide.Black));
    True(session.Snapshot.IsFinished);
    Equal(PieceSide.Red, session.Snapshot.Winner);
    Equal(GameEndReason.Timeout, session.Snapshot.EndReason);
    False(session.DeclareTimeout(PieceSide.Red));
}

static void ResignationAdjudication()
{
    var local = new GameSession();
    True(local.DeclareResignation(PieceSide.Red));
    Equal(PieceSide.Black, local.Snapshot.Winner);
    Equal(GameEndReason.Resignation, local.Snapshot.EndReason);

    var network = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(network, transport, PieceSide.Black);
    True(coordinator.DeclareResignationAsync().AsTask().GetAwaiter().GetResult());
    Equal(PieceSide.Red, network.Snapshot.Winner);
    Equal(GameEndReason.Resignation, network.Snapshot.EndReason);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void UndoRestoresState()
{
    var game = ChessGame.CreateStandard();
    True(game.TryMove(new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4))).IsSuccess);
    Equal(PieceSide.Black, game.Snapshot.SideToMove);
    True(game.Undo());
    Equal(PieceSide.Red, game.Snapshot.SideToMove);
    True(game.Snapshot.Pieces.Any(p => p.Position == new BoardPosition(0, 3)));
    False(game.Snapshot.Pieces.Any(p => p.Position == new BoardPosition(0, 4)));
}

static void DrawAdjudication()
{
    var session = new GameSession();
    True(session.DeclareDraw());
    True(session.Snapshot.IsFinished);
    Equal<PieceSide?>(null, session.Snapshot.Winner);
    Equal(GameEndReason.Draw, session.Snapshot.EndReason);
    False(session.DeclareDraw());
}

static void NetworkChat()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    coordinator.SendChatAsync("你好").AsTask().GetAwaiter().GetResult();
    Equal("你好", transport.LastChat?.Entry.Text);
    RoomChatEntry? received = null;
    coordinator.Chat.MessageAdded += message => received = message;
    transport.Receive(new ChatCommand(new RoomChatEntry(Guid.NewGuid().ToString("N"), 1, 0, "轮到你了")));
    Equal("轮到你了", received?.Text);
    Equal(0, received?.TargetSeat);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void RoomChatManagement()
{
    var room = new RoomChatManager(3, 4);
    Equal<string?>(null, room.Post(0, 2, "  魏对蜀  ", out var directed));
    Equal("魏对蜀", directed?.Text);
    True(RoomChatManager.IsVisibleTo(directed!, 0));
    True(RoomChatManager.IsVisibleTo(directed!, 2));
    False(RoomChatManager.IsVisibleTo(directed!, 1));
    True(room.Post(0, 0, "自己", out _) is not null);
    True(room.Post(3, null, "越界", out _) is not null);
    var peer = new RoomChatManager(3, 4);
    Equal(1, peer.ImportTrustedHistory(room.Entries));
    Equal(0, peer.ImportTrustedHistory(room.Entries));
    False(peer.AcceptLive(new RoomChatEntry(Guid.NewGuid().ToString("N"), 1, null, "伪造"), 2));
}

static void NetworkUndoOffer()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    True(session.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    True(session.TryMove(new BoardPosition(0, 6), new BoardPosition(0, 5)).IsSuccess);
    MatchOfferRequest? received = null;
    coordinator.MatchOfferReceived += request => received = request;
    transport.Receive(new MatchOfferRequest(9, 2, MatchOfferKind.Undo));
    Equal(MatchOfferKind.Undo, received?.Kind);
    True(coordinator.HasPendingOffer);
    coordinator.RespondToMatchOfferAsync(9, true).AsTask().GetAwaiter().GetResult();
    Equal(1, session.Snapshot.PlyNumber);
    True(transport.LastOfferResponse?.Accepted == true);
    False(coordinator.HasPendingOffer);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void NetworkDrawOffer()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    coordinator.RequestMatchOfferAsync(MatchOfferKind.Draw).AsTask().GetAwaiter().GetResult();
    var request = transport.LastOffer ?? throw new InvalidOperationException("Offer was not sent.");
    True(coordinator.HasPendingOffer);
    transport.Receive(new MatchOfferResponse(
        request.RequestId, request.PlyNumber, request.Kind, true));
    True(session.Snapshot.IsFinished);
    Equal(GameEndReason.Draw, session.Snapshot.EndReason);
    Equal<PieceSide?>(null, session.Snapshot.Winner);
    False(coordinator.HasPendingOffer);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void NetworkRejectedOffer()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    transport.Receive(new MatchOfferRequest(3, 0, MatchOfferKind.Draw));
    coordinator.RespondToMatchOfferAsync(3, false).AsTask().GetAwaiter().GetResult();
    False(session.Snapshot.IsFinished);
    Equal(0, session.Snapshot.PlyNumber);
    True(transport.LastOfferResponse?.Accepted == false);
    False(coordinator.HasPendingOffer);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void NetworkUndoEligibility()
{
    var session = new GameSession();
    var transport = new FakeTransport();
    var coordinator = new NetworkGameCoordinator(session, transport, PieceSide.Red);
    False(coordinator.CanRequestLocalUndo);
    True(session.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    True(coordinator.CanRequestLocalUndo);

    var incomingRaised = false;
    coordinator.MatchOfferReceived += _ => incomingRaised = true;
    transport.Receive(new MatchOfferRequest(4, 1, MatchOfferKind.Undo));
    False(incomingRaised);
    True(transport.LastOfferResponse?.Accepted == false);

    coordinator.RequestMatchOfferAsync(MatchOfferKind.Undo).AsTask().GetAwaiter().GetResult();
    Equal(MatchOfferKind.Undo, transport.LastOffer?.Kind);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();

    var afterOpponentMove = new GameSession();
    True(afterOpponentMove.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    True(afterOpponentMove.TryMove(new BoardPosition(0, 6), new BoardPosition(0, 5)).IsSuccess);
    var secondCoordinator = new NetworkGameCoordinator(afterOpponentMove, new FakeTransport(), PieceSide.Red);
    False(secondCoordinator.CanRequestLocalUndo);
    var rejected = false;
    try
    {
        secondCoordinator.RequestMatchOfferAsync(MatchOfferKind.Undo).AsTask().GetAwaiter().GetResult();
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    True(rejected);
    secondCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void MatchWireRoundTrip()
{
    RoundTrip(new MoveCommand(3, new ChessMove(
        new BoardPosition(1, 2), new BoardPosition(1, 5))), MatchWireMessageType.Move);
    RoundTrip(new NetworkMatchConfiguration(
        new TimeControl(1200, 60), PieceSide.Black), MatchWireMessageType.Configuration);
    RoundTrip(new TimeoutCommand(8, PieceSide.Red), MatchWireMessageType.Timeout);
    RoundTrip(new ResignationCommand(8, PieceSide.Black), MatchWireMessageType.Resignation);
    RoundTrip(new ClockSyncCommand(9,
        new ClockSnapshot(101.5, 82.25, 11, PieceSide.Black)), MatchWireMessageType.ClockSync);
    RoundTrip(new ChatCommand(new RoomChatEntry(Guid.NewGuid().ToString("N"), 0, 1, "你好，Steam 好友")), MatchWireMessageType.Chat);
    RoundTrip(new MatchOfferRequest(42, 12, MatchOfferKind.Undo), MatchWireMessageType.MatchOffer);
    RoundTrip(new MatchOfferResponse(43, 15, MatchOfferKind.Draw, true),
        MatchWireMessageType.MatchOfferResponse);
    var profile = new PlayerProfileCommand("棋手-7", "远端玩家", 2, 2,
        Enumerable.Repeat((byte)180, 16).ToArray());
    var profilePacket = MatchWireProtocol.Encode(profile);
    True(MatchWireProtocol.TryDecode(profilePacket, out var decodedProfile));
    var receivedProfile = (PlayerProfileCommand)decodedProfile.Payload;
    Equal(profile.PlayerId, receivedProfile.PlayerId);
    Equal(profile.DisplayName, receivedProfile.DisplayName);
    True(profile.AvatarRgba.SequenceEqual(receivedProfile.AvatarRgba));
}

static void MatchWireRejectsMalformedData()
{
    var move = MatchWireProtocol.Encode(new MoveCommand(1,
        new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4))));
    move[0]++;
    False(MatchWireProtocol.TryDecode(move, out _));
    False(MatchWireProtocol.TryDecode(new byte[MatchWireProtocol.MaximumPacketBytes + 1], out _));

    var invalidClock = MatchWireProtocol.Encode(new ClockSyncCommand(0,
        new ClockSnapshot(double.NaN, 10, 5, PieceSide.Red)));
    False(MatchWireProtocol.TryDecode(invalidClock, out _));

    var rejectedChat = false;
    try
    {
        MatchWireProtocol.Encode(new ChatCommand(new RoomChatEntry(Guid.NewGuid().ToString("N"), 0, null, "line\nbreak")));
    }
    catch (ArgumentException)
    {
        rejectedChat = true;
    }
    True(rejectedChat);
}

static void UciFen()
{
    Equal(
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1",
        XiangqiUciNotation.ToFen(ChessGame.CreateStandard().Snapshot));
}

static void UciCoordinates()
{
    var expected = new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4));
    Equal("a3a4", XiangqiUciNotation.FormatMove(expected));
    True(XiangqiUciNotation.TryParseMove("A3a4", out var parsed));
    Equal(expected, parsed);
    False(XiangqiUciNotation.TryParseMove("j3a4", out _));
    False(XiangqiUciNotation.TryParseMove("a10a2", out _));
}

static void PgnRecordRoundTrip()
{
    var session = new GameSession();
    True(session.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    True(session.TryMove(new BoardPosition(0, 6), new BoardPosition(0, 5)).IsSuccess);
    var source = session.CreateRecord(new Dictionary<string, string> { ["Event"] = "测试", ["Result"] = "*" });
    var text = XiangqiPgnCodec.Serialize(source);
    True(text.Contains("[Format \"ICCS\"]", StringComparison.Ordinal));
    True(XiangqiPgnCodec.TryParse(text, out var imported, out var error));
    Equal(2, imported!.Moves.Count);
    Equal(2, imported.Replay()[^1].PlyNumber);

    const string fen = "4k4/9/9/9/9/9/9/9/9/4K4 b - - 0 7";
    True(XiangqiUciNotation.TryParseFen(fen, out var position, out error));
    Equal(PieceSide.Black, position.SideToMove);
    Equal(13, position.PlyNumber);
}

static void PgnRejectsIllegalMove()
{
    const string invalid = "[Game \"Chinese Chess\"]\n[Format \"ICCS\"]\n\n1. a0a9 *";
    False(XiangqiPgnCodec.TryParse(invalid, out _, out _));
}

static void TrialIsIsolated()
{
    var session = new GameSession();
    var trial = session.BeginTrial();
    True(trial.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    Equal(1, trial.Snapshot.PlyNumber);
    Equal(0, session.Snapshot.PlyNumber);
}

static void TrialPresentationCues()
{
    var session = new GameSession();
    var trial = session.BeginTrial();
    var before = trial.Snapshot;
    var result = trial.TryMove(new BoardPosition(0, 3), new BoardPosition(0, 4));
    var cues = session.DescribeMove(before, trial.Snapshot, result);
    Equal(PresentationCueKind.Move, cues[0].Kind);
    Throws<ArgumentException>(() => session.DescribeMove(before, before,
        MoveResult.Rejected(default, MoveRejection.PieceRuleViolation)));
}

static void ReplayAnalysis()
{
    var game = ChessGame.CreateStandard();
    var first = game.Snapshot;
    var move = new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4));
    True(game.TryMove(move).IsSuccess);
    var engine = new FakeReplayEngine();
    var coordinator = new ReplayAnalysisCoordinator(engine, [first, game.Snapshot], 100);
    coordinator.SelectAndSweepAsync(1).GetAwaiter().GetResult();
    Equal(2, coordinator.Cache.Count);
    True(coordinator.Cache[0].RedCentipawns > 0);
    True(coordinator.Cache[1].RedCentipawns < 0);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    True(engine.WasDisposed);
}

static void AiCoordinator()
{
    var session = new GameSession();
    var aiMove = new ChessMove(new BoardPosition(0, 6), new BoardPosition(0, 5));
    var engine = new FakeAiEngine(aiMove);
    var coordinator = new AiGameCoordinator(session, engine, PieceSide.Red);

    coordinator.InitializeAsync().AsTask().GetAwaiter().GetResult();
    True(engine.IsReady);
    True(coordinator.SubmitHumanMove(new BoardPosition(0, 3), new BoardPosition(0, 4)).IsSuccess);
    var result = coordinator.PlayAiTurnAsync(100).AsTask().GetAwaiter().GetResult();
    True(result is { IsSuccess: true });
    Equal(2, session.Snapshot.PlyNumber);
    Equal(PieceSide.Red, session.Snapshot.SideToMove);

    var analysis = coordinator.AnalyzeAsync(100, 3).AsTask().GetAwaiter().GetResult();
    Equal(aiMove, analysis.BestMove);
    Equal(3, engine.LastVariationCount);

    True(coordinator.UndoHumanTurn());
    Equal(0, session.Snapshot.PlyNumber);
    Equal(PieceSide.Red, session.Snapshot.SideToMove);
    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    True(engine.WasDisposed);
}

static void RoundTrip<T>(T expected, MatchWireMessageType type) where T : struct
{
    var packet = expected switch
    {
        MoveCommand value => MatchWireProtocol.Encode(value),
        NetworkMatchConfiguration value => MatchWireProtocol.Encode(value),
        TimeoutCommand value => MatchWireProtocol.Encode(value),
        ResignationCommand value => MatchWireProtocol.Encode(value),
        ClockSyncCommand value => MatchWireProtocol.Encode(value),
        ChatCommand value => MatchWireProtocol.Encode(value),
        MatchOfferRequest value => MatchWireProtocol.Encode(value),
        MatchOfferResponse value => MatchWireProtocol.Encode(value),
        _ => throw new NotSupportedException(typeof(T).Name)
    };
    True(MatchWireProtocol.TryDecode(packet, out var decoded));
    Equal(type, decoded.Type);
    Equal(expected, (T)decoded.Payload);
}

static ChessPiece Piece(int id, PieceSide side, PieceKind kind) => new(id, side, kind);
static PlacedPiece At(int id, PieceSide side, PieceKind kind, int file, int rank) =>
    new(new BoardPosition(file, rank), Piece(id, side, kind));

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true, received false.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}

static void Contains<T>(IEnumerable<T> values, T expected)
{
    if (!values.Contains(expected))
        throw new InvalidOperationException($"Collection did not contain '{expected}'.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeTransport : IMatchTransport
{
    public event Action<MoveCommand>? MoveReceived;
    public event Action<NetworkMatchConfiguration>? MatchConfigurationReceived;
    public event Action<TimeoutCommand>? TimeoutReceived;
    public event Action<ResignationCommand>? ResignationReceived;
    public event Action<ClockSyncCommand>? ClockSyncReceived;
    public event Action<ChatCommand>? ChatReceived;
    public event Action<MatchOfferRequest>? MatchOfferReceived;
    public event Action<MatchOfferResponse>? MatchOfferResponseReceived;
    public event Action<PlayerProfileCommand>? PlayerProfileReceived;
    public event Action<TransportStatus>? StatusChanged;
    public bool IsConnected { get; private set; } = true;
    public MoveCommand? LastSent { get; private set; }
    public ChatCommand? LastChat { get; private set; }
    public MatchOfferRequest? LastOffer { get; private set; }
    public MatchOfferResponse? LastOfferResponse { get; private set; }

    public ValueTask SendMoveAsync(MoveCommand command, CancellationToken cancellationToken = default)
    {
        LastSent = command;
        return ValueTask.CompletedTask;
    }

    public ValueTask SendMatchConfigurationAsync(NetworkMatchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        MatchConfigurationReceived?.Invoke(configuration);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendTimeoutAsync(TimeoutCommand command, CancellationToken cancellationToken = default)
    {
        TimeoutReceived?.Invoke(command);
        return ValueTask.CompletedTask;
    }
    public ValueTask SendResignationAsync(ResignationCommand command, CancellationToken cancellationToken = default)
    {
        ResignationReceived?.Invoke(command);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendClockSyncAsync(ClockSyncCommand command, CancellationToken cancellationToken = default)
    {
        ClockSyncReceived?.Invoke(command);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendChatAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        LastChat = command;
        return ValueTask.CompletedTask;
    }

    public ValueTask SendMatchOfferAsync(MatchOfferRequest command, CancellationToken cancellationToken = default)
    {
        LastOffer = command;
        return ValueTask.CompletedTask;
    }

    public ValueTask SendMatchOfferResponseAsync(MatchOfferResponse command, CancellationToken cancellationToken = default)
    {
        LastOfferResponse = command;
        return ValueTask.CompletedTask;
    }

    public ValueTask SendPlayerProfileAsync(PlayerProfileCommand command, CancellationToken cancellationToken = default)
    {
        PlayerProfileReceived?.Invoke(command);
        return ValueTask.CompletedTask;
    }

    public void Receive(MoveCommand command) => MoveReceived?.Invoke(command);
    public void Receive(ChatCommand command) => ChatReceived?.Invoke(command);
    public void Receive(MatchOfferRequest command) => MatchOfferReceived?.Invoke(command);
    public void Receive(MatchOfferResponse command) => MatchOfferResponseReceived?.Invoke(command);

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        StatusChanged?.Invoke(new TransportStatus(TransportConnectionState.Disconnected));
        return ValueTask.CompletedTask;
    }
}

sealed class FakeAiEngine(ChessMove move) : IXiangqiAiEngine
{
    public string DisplayName => "Test AI";
    public bool IsReady { get; private set; }
    public bool WasDisposed { get; private set; }
    public int LastVariationCount { get; private set; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsReady = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask NewGameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<AiSearchResult> SearchAsync(
        GameSnapshot position,
        AiSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastVariationCount = request.VariationCount;
        var variations = Enumerable.Range(1, request.VariationCount)
            .Select(rank => new AiVariation(rank, move, 8, 20 - rank, null, [move]))
            .ToArray();
        return ValueTask.FromResult(new AiSearchResult(move, variations));
    }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        IsReady = false;
        return ValueTask.CompletedTask;
    }
}

sealed class FakeReplayEngine : IXiangqiAiEngine
{
    public string DisplayName => "Replay test";
    public bool IsReady { get; private set; }
    public bool WasDisposed { get; private set; }
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { IsReady = true; return ValueTask.CompletedTask; }
    public ValueTask NewGameAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<AiSearchResult> SearchAsync(GameSnapshot position, AiSearchRequest request, CancellationToken cancellationToken = default)
    {
        var game = ChessGame.FromPosition(position);
        var source = position.Pieces.First(p => p.Piece.Side == position.SideToMove && game.GetLegalDestinations(p.Position).Count > 0).Position;
        var move = new ChessMove(source, game.GetLegalDestinations(source)[0]);
        return ValueTask.FromResult(new AiSearchResult(move, [new AiVariation(1, move, 6, 25, null, [move])]));
    }
    public ValueTask DisposeAsync() { WasDisposed = true; IsReady = false; return ValueTask.CompletedTask; }
}
