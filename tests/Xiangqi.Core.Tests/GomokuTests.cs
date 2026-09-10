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
        ("五子棋联机房主配置与落子可同步", NetworkRoundTrip)
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
        Equal(GomokuMoveRejection.Finished, game.TryPlace(new(9, 9)).Rejection);
    }
    private static void ForbiddenMoves()
    {
        var overline = new GomokuGame(15, GomokuRuleSet.RenjuForbiddenMoves,
            Enumerable.Range(3, 5).Select(x => new PlacedStone(new(x, 7), StoneColor.Black)));
        Equal(GomokuMoveRejection.Overline, overline.TryPlace(new(8, 7)).Rejection);

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
    private static void Success(GomokuMoveResult result) { if (!result.IsSuccess) throw new InvalidOperationException(result.Rejection.ToString()); }
    private static void True(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
