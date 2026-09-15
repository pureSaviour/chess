using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

internal static class GomokuTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("通用方格棋盘支持自定义尺寸与射线边界", GridGeometry),
        ("五子棋黑先交替落子并判定连五", FreestyleWin),
        ("连珠模式禁止黑方长连、双四与双三", ForbiddenMoves),
        ("SGF GM[4] 可往返主线与禁手规则", SgfRoundTrip),
        ("SGF 拒绝越界和非法主线", SgfRejectsInvalid),
        ("五子棋联机房主配置与落子可同步", NetworkRoundTrip),
        ("五子棋联机超时后双方同意复赛并交换黑白", NetworkTimedRematch),
        ("五子棋 AI 协调器在主线程轮询后应用合法着法", AiRoundTrip),
        ("五子棋 AI 协调器拒绝非法输出", AiRejectsInvalid),
        ("五子棋 AI 协调器释放引擎且释放幂等", AiDisposal),
        ("五子棋难度映射深度与原生强度", DifficultyProfiles),
        ("五子棋入门档使用规则合法落点而不调用强引擎", BeginnerUsesLegalBaseline),
        ("五子棋复盘分析缓存黑方胜率和最佳着", ReplayAnalysis),
        ("五子棋复盘分析拒绝非法最佳着", ReplayRejectsInvalidBestMove)
    ];

    private static void GridGeometry()
    {
        var grid = new SquareGrid(19); Equal(19, grid.Size); True(grid.Contains(new(18, 18)));
        Equal(18, grid.Ray(new(0, 0), 1, 0).Count()); Throws<ArgumentOutOfRangeException>(() => new SquareGrid(26));
    }
    private static void FreestyleWin()
    {
        var game = new GomokuGame(15);
        for (var x = 0; x < 4; x++) { Success(game.TryPlace(new(x, 7))); Success(game.TryPlace(new(x, 8))); }
        Success(game.TryPlace(new(4, 7))); True(game.Snapshot.IsFinished); Equal(StoneColor.Black, game.Snapshot.Winner);
        Equal(1L, game.Snapshot.Stones.Single(x => x.Point == new GridPoint(0, 7)).MoveNumber);
        Equal(9L, game.Snapshot.Stones.Single(x => x.Point == new GridPoint(4, 7)).MoveNumber);
        Equal(GomokuMoveRejection.Finished, game.TryPlace(new(9, 9)).Rejection);
    }
    private static void ForbiddenMoves()
    {
        var overline = new GomokuGame(15, GomokuRuleSet.RenjuForbiddenMoves,
            Enumerable.Range(3, 5).Select(x => new PlacedStone(new(x, 7), StoneColor.Black)));
        Equal(GomokuMoveRejection.Overline, overline.TryPlace(new(8, 7)).Rejection);
        False(overline.Snapshot.Stones.Any(x => x.Point == new GridPoint(8, 7)));

        var setup = new[] { new GridPoint(5, 7), new GridPoint(6, 7), new GridPoint(8, 7),
            new GridPoint(7, 5), new GridPoint(7, 6), new GridPoint(7, 8) }
            .Select(p => new PlacedStone(p, StoneColor.Black));
        var doubleFour = new GomokuGame(15, GomokuRuleSet.RenjuForbiddenMoves, setup);
        Equal(GomokuMoveRejection.DoubleFour, doubleFour.TryPlace(new(7, 7)).Rejection);

        var threes = new[] { new GridPoint(6, 7), new GridPoint(8, 7),
            new GridPoint(7, 6), new GridPoint(7, 8) }
            .Select(p => new PlacedStone(p, StoneColor.Black));
        var doubleThree = new GomokuGame(15, GomokuRuleSet.RenjuForbiddenMoves, threes);
        Equal(GomokuMoveRejection.DoubleThree, doubleThree.TryPlace(new(7, 7)).Rejection);
    }
    private static void SgfRoundTrip()
    {
        var game = new GomokuGame(19, GomokuRuleSet.RenjuForbiddenMoves);
        Success(game.TryPlace(new(9, 9))); Success(game.TryPlace(new(9, 10)));
        var source = new GomokuGameRecord(19, game.Rules, [], StoneColor.Black, game.History.ToArray(), new Dictionary<string, string>());
        var text = GomokuSgfCodec.Write(source); True(text.Contains("GM[4]")); True(text.Contains("SZ[19]"));
        True(GomokuSgfCodec.TryParse(text, out var parsed, out var error), error); Equal(2L, parsed!.Replay().Snapshot.PlyNumber);
    }
    private static void SgfRejectsInvalid()
    {
        False(GomokuSgfCodec.TryParse("(;FF[4]GM[4]SZ[15];B[zz])", out _, out _));
        False(GomokuSgfCodec.TryParse("(;FF[4]GM[4]SZ[15];W[aa])", out _, out _));
    }
    private static void NetworkRoundTrip()
    {
        var (hostPort, clientPort) = FakePacketTransport.Pair();
        var host = new NetworkGomokuMatchController(hostPort, new(15, GomokuRuleSet.RenjuForbiddenMoves, StoneColor.Black));
        var client = new NetworkGomokuMatchController(clientPort, null);
        hostPort.Connect(); clientPort.Connect();
        True(host.IsReady); True(client.IsReady); Equal(StoneColor.White, client.LocalColor);
        True(host.SubmitAsync(new(7, 7)).AsTask().GetAwaiter().GetResult().Accepted); Equal(1L, client.Snapshot.PlyNumber);
        True(client.SubmitAsync(new(7, 8)).AsTask().GetAwaiter().GetResult().Accepted); Equal(2L, host.Snapshot.PlyNumber); Equal(2L, client.Snapshot.PlyNumber);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult(); client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void NetworkTimedRematch()
    {
        var (hostPort,clientPort)=FakePacketTransport.Pair();
        var host=new NetworkGomokuMatchController(hostPort,new(15,GomokuRuleSet.Freestyle,StoneColor.Black,new(0,1)));
        var client=new NetworkGomokuMatchController(clientPort,null);hostPort.Connect();clientPort.Connect();
        Equal(1,client.TimeControl.MoveSeconds);host.Tick(1.1);True(host.Snapshot.IsFinished);True(client.Snapshot.IsFinished);
        host.RequestRematchAsync().AsTask().GetAwaiter().GetResult();True(host.Rematch.LocalReady);True(client.Rematch.RemoteReady);
        client.RequestRematchAsync().AsTask().GetAwaiter().GetResult();False(host.Snapshot.IsFinished);False(client.Snapshot.IsFinished);
        Equal(StoneColor.White,host.LocalColor);Equal(StoneColor.Black,client.LocalColor);Equal(2L,host.Rematch.Round);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void AiRoundTrip()
    {
        var engine = new FakeAiEngine(new(7, 8));
        var controller = new AiGomokuMatchController(engine, 15, GomokuRuleSet.Freestyle,
            StoneColor.Black, 250);
        controller.InitializeAsync().AsTask().GetAwaiter().GetResult();
        True(controller.CanPlace);
        True(controller.SubmitAsync(new(7, 7)).AsTask().GetAwaiter().GetResult().Accepted);
        True(SpinWait.SpinUntil(() => { controller.Poll(); return controller.Snapshot.PlyNumber == 2; }, 1000));
        Equal(2L, controller.Snapshot.PlyNumber); Equal(new GridPoint(7, 8), controller.Snapshot.LastMove!.Value.Point);
        True(controller.CanPlace);
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void AiRejectsInvalid()
    {
        var engine = new FakeAiEngine(new(99, 99));
        var controller = new AiGomokuMatchController(engine, 15, GomokuRuleSet.RenjuForbiddenMoves,
            StoneColor.White, 250);
        var message = string.Empty; controller.Message += value => message = value;
        controller.InitializeAsync().AsTask().GetAwaiter().GetResult();
        True(SpinWait.SpinUntil(() => { controller.Poll(); return message.Length > 0; }, 1000));
        Equal(0L, controller.Snapshot.PlyNumber); True(message.Contains("非法着法"));
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void AiDisposal()
    {
        var engine = new FakeAiEngine(new(7, 7));
        var controller = new AiGomokuMatchController(engine, 15, GomokuRuleSet.Freestyle,
            StoneColor.Black, 250);
        controller.InitializeAsync().AsTask().GetAwaiter().GetResult();
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Equal(1, engine.DisposeCount);
    }
    private static void DifficultyProfiles()
    {
        var beginner = AiDifficultyProfiles.Gomoku(AiDifficultyLevel.Beginner);
        var master = AiDifficultyProfiles.Gomoku(AiDifficultyLevel.Master);
        True(beginner.MaxDepth > 0); True(beginner.Strength < master.Strength);
        True(beginner.UseDeterministicLegalMove); False(master.UseDeterministicLegalMove);
        Equal(0, master.MaxDepth);
        Throws<ArgumentOutOfRangeException>(() => new GomokuAiSearchRequest(
            new GomokuGame(15).Snapshot, 250, 129).Validate());
    }
    private static void BeginnerUsesLegalBaseline()
    {
        var engine = new FakeAiEngine(new(99, 99));
        var controller = new AiGomokuMatchController(engine, 15, GomokuRuleSet.Freestyle,
            StoneColor.Black, 100, 1, 1, 1, 64, true);
        controller.InitializeAsync().AsTask().GetAwaiter().GetResult();
        True(controller.SubmitAsync(new(7, 7)).AsTask().GetAwaiter().GetResult().Accepted);
        True(SpinWait.SpinUntil(() => { controller.Poll(); return controller.Snapshot.PlyNumber == 2; }, 1000));
        Equal(0, engine.SearchCount);
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void ReplayAnalysis()
    {
        var position = new GomokuGame(15).Snapshot;
        var engine = new FakeAiEngine(new(7, 7));
        const int replayThinkMilliseconds = 300;
        var coordinator = new GomokuReplayAnalysisCoordinator(engine, [position], replayThinkMilliseconds);
        coordinator.SelectAndSweepAsync(0).GetAwaiter().GetResult();
        Equal(.7f, coordinator.Cache[0].BlackWinRate);
        Equal(new GridPoint(7, 7), coordinator.Cache[0].BestMove);
        Equal(replayThinkMilliseconds, engine.LastAnalysisRequest.ThinkMilliseconds);
        Equal(0, engine.LastAnalysisRequest.MaxDepth);
        Equal(100, engine.LastAnalysisRequest.Strength);
        Equal(AiDifficultyProfiles.ReplayThreads, engine.LastAnalysisRequest.Threads);
        Equal(AiDifficultyProfiles.ReplayMemoryMegabytes, engine.LastAnalysisRequest.MemoryMegabytes);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Equal(1, engine.DisposeCount);
        Throws<ArgumentOutOfRangeException>(() => new GomokuReplayAnalysisCoordinator(
            new FakeAiEngine(new(7, 7)), [position], 99));
    }
    private static void ReplayRejectsInvalidBestMove()
    {
        var position = new GomokuGame(15).Snapshot;
        var engine = new FakeAiEngine(new(99, 99));
        var coordinator = new GomokuReplayAnalysisCoordinator(engine, [position]);
        Throws<InvalidDataException>(() => coordinator.SelectAndSweepAsync(0).GetAwaiter().GetResult());
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class FakePacketTransport(bool host) : ITwoPlayerPacketTransport
    {
        private FakePacketTransport? _peer;
        public event Action<ReadOnlyMemory<byte>>? PacketReceived;
        public event Action<TransportStatus>? PacketStatusChanged;
        public bool IsPacketConnected { get; private set; }
        public bool IsPacketHost { get; } = host;
        public static (FakePacketTransport Host, FakePacketTransport Client) Pair()
        { var h = new FakePacketTransport(true); var c = new FakePacketTransport(false); h._peer = c; c._peer = h; return (h, c); }
        public void Connect() { IsPacketConnected = true; PacketStatusChanged?.Invoke(new(TransportConnectionState.Connected)); }
        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _peer!.PacketReceived?.Invoke(packet.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { IsPacketConnected = false; return ValueTask.CompletedTask; }
    }
    private sealed class FakeAiEngine(GridPoint move) : IGomokuAiEngine
    {
        private int _searchCount;
        public string DisplayName => "Fake";
        public bool IsReady { get; private set; }
        public int SearchCount => _searchCount;
        public int DisposeCount { get; private set; }
        public GomokuAiSearchRequest LastAnalysisRequest { get; private set; }
        public ValueTask InitializeAsync(int boardSize, GomokuRuleSet rules, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); IsReady = true; return ValueTask.CompletedTask; }
        public ValueTask<GridPoint> FindMoveAsync(GomokuAiSearchRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref _searchCount); return ValueTask.FromResult(move); }
        public ValueTask<GomokuAnalysisResult> AnalyzeAsync(GomokuAiSearchRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); LastAnalysisRequest = request; return ValueTask.FromResult(new GomokuAnalysisResult(.7f, 120, 8, move)); }
        public ValueTask DisposeAsync() { DisposeCount++; IsReady = false; return ValueTask.CompletedTask; }
    }
    private static void Success(GomokuMoveResult result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Rejection.ToString()); }
    private static void True(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
