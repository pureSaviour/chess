using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Go;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;

internal static class GoTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("围棋提取无气棋块且拒绝自杀", CaptureAndSuicide),
        ("围棋禁止全局同形立即回提", PositionalSuperko),
        ("围棋双虚后标记死子并双方确认数子", PassAndAreaScoring),
        ("围棋数子将无归属空点双方均分", NeutralPointsAreShared),
        ("围棋认输立即结束并指定对手获胜", Resignation),
        ("围棋 SGF GM[1] 往返虚着死子与结果", SgfRoundTrip),
        ("围棋 SGF 拒绝错误棋种尺寸与非法主线", SgfRejectsInvalid),
        ("围棋联机房主配置与语义操作可同步", NetworkRoundTrip),
        ("围棋联机拒绝重复与畸形操作包", NetworkRejectsMalformed)
    ];

    private static void CaptureAndSuicide()
    {
        var capture = new GoGame();
        Success(capture.Place(new(1, 0)));
        Success(capture.Place(new(0, 0)));
        Success(capture.Place(new(0, 1)));
        Equal(2, capture.Snapshot.Stones.Count);
        False(capture.Snapshot.Stones.Any(x => x.Point == new GridPoint(0, 0)));

        var suicide = new GoGame();
        Success(suicide.Place(new(10, 10)));
        Success(suicide.Place(new(1, 0)));
        Success(suicide.Place(new(11, 10)));
        Success(suicide.Place(new(0, 1)));
        Equal(GoRejection.Suicide, suicide.Place(new(0, 0)).Rejection);
        Equal(4, suicide.Snapshot.Stones.Count);
    }

    private static void PositionalSuperko()
    {
        var game = new GoGame();
        Success(game.Place(new(1, 2))); Success(game.Place(new(2, 2)));
        Success(game.Place(new(3, 2))); Success(game.Place(new(1, 1)));
        Success(game.Place(new(2, 3))); Success(game.Place(new(3, 1)));
        Success(game.Place(new(10, 10))); Success(game.Place(new(2, 0)));
        Success(game.Place(new(2, 1)));
        Equal(GoRejection.RepeatedPosition, game.Place(new(2, 2)).Rejection);
        Equal(9L, game.Snapshot.ActionNumber);
    }

    private static void PassAndAreaScoring()
    {
        var game = ScoringGame();
        Success(game.ToggleDead(new(18, 18)));
        Equal(1, game.Snapshot.DeadStones.Count);
        Success(game.ConfirmScore()); Success(game.ConfirmScore());
        Equal(GoPhase.Finished, game.Phase);
        Equal(GoEndReason.AreaScore, game.EndReason);
        Equal(GoStone.Black, game.Winner);
        Equal(361d, game.Score!.Value.Black);
        Equal(7.5d, game.Score.Value.White);
    }

    private static void Resignation()
    {
        var game = new GoGame();
        Success(game.Resign());
        Equal(GoPhase.Finished, game.Phase);
        Equal(GoStone.White, game.Winner);
        Equal(GoEndReason.Resignation, game.EndReason);
    }

    private static void NeutralPointsAreShared()
    {
        var game = new GoGame(); Success(game.Pass()); Success(game.Pass());
        Success(game.ConfirmScore()); Success(game.ConfirmScore());
        Equal(180.5d, game.Score!.Value.Black); Equal(188d, game.Score.Value.White);
        Equal(GoStone.White, game.Winner);
    }

    private static void SgfRoundTrip()
    {
        var game = ScoringGame();
        Success(game.ToggleDead(new(18, 18)));
        Success(game.ConfirmScore()); Success(game.ConfirmScore());
        var record = GoGameRecord.FromGame(game, new Dictionary<string, string> { ["PB"] = "黑方", ["PW"] = "白方" });
        var text = GoSgfCodec.Write(record);
        True(text.Contains("GM[1]")); True(text.Contains("KM[7.5]")); True(text.Contains(";B[]")); True(text.Contains("AE[ss]"));
        True(GoSgfCodec.TryParse(text, out var parsed, out var error), error);
        var replay = parsed!.Replay();
        Equal(GoPhase.Finished, replay.Phase); Equal(GoStone.Black, replay.Winner);
    }

    private static void SgfRejectsInvalid()
    {
        False(GoSgfCodec.TryParse("(;FF[4]GM[4]SZ[19])", out _, out _));
        False(GoSgfCodec.TryParse("(;FF[4]GM[1]SZ[13])", out _, out _));
        False(GoSgfCodec.TryParse("(;FF[4]GM[1]SZ[19]RU[Chinese]KM[7.5];W[aa])", out _, out _));
        Throws<InvalidDataException>(() => GoSgfCodec.Write(new(
            [new(7, GoStone.Black, GoActionKind.Place, new GridPoint(0, 0))],
            new HashSet<GridPoint>(), null, null, null, new Dictionary<string, string>())));
    }

    private static void NetworkRoundTrip()
    {
        var (hostPort, clientPort) = FakePacketTransport.Pair();
        var host = new NetworkGoMatchController(hostPort, new(GoStone.Black));
        var client = new NetworkGoMatchController(clientPort, null);
        hostPort.Connect(); clientPort.Connect();
        True(host.IsReady); True(client.IsReady); Equal(GoStone.White, client.LocalColor);
        True(host.SubmitAsync(GoActionKind.Place, new(3, 3)).AsTask().GetAwaiter().GetResult().Accepted);
        Equal(1L, client.Snapshot.ActionNumber);
        True(client.SubmitAsync(GoActionKind.Pass).AsTask().GetAwaiter().GetResult().Accepted);
        Equal(2L, host.Snapshot.ActionNumber); Equal(2L, client.Snapshot.ActionNumber);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void NetworkRejectsMalformed()
    {
        var (hostPort, clientPort) = FakePacketTransport.Pair();
        var host = new NetworkGoMatchController(hostPort, new(GoStone.Black));
        var client = new NetworkGoMatchController(clientPort, null);
        var message = string.Empty; host.Message += value => message = value;
        hostPort.Connect(); clientPort.Connect();
        True(host.SubmitAsync(GoActionKind.Place, new(3, 3)).AsTask().GetAwaiter().GetResult().Accepted);
        True(client.SubmitAsync(GoActionKind.Pass).AsTask().GetAwaiter().GetResult().Accepted);
        var before = host.Snapshot.ActionNumber;
        clientPort.ReplayLastToPeer();
        Equal(before, host.Snapshot.ActionNumber); True(message.Contains("序号"));
        clientPort.SendRawToPeer(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Kind = "action", Sequence = before, ActionKind = (int)GoActionKind.Place,
            Column = -1, Row = 3
        }));
        Equal(before, host.Snapshot.ActionNumber); True(message.Contains("无效"));
        hostPort.Receive("{}"u8.ToArray());
        True(message.Contains("版本")); Equal(before, host.Snapshot.ActionNumber);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static GoGame ScoringGame()
    {
        var game = new GoGame();
        Success(game.Place(new(0, 0))); Success(game.Place(new(18, 18)));
        Success(game.Pass()); Success(game.Pass());
        Equal(GoPhase.Scoring, game.Phase);
        return game;
    }
    private static void Success(GoActionResult result)
    { if (!result.IsSuccess) throw new InvalidOperationException(result.Rejection.ToString()); }
    private static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakePacketTransport(bool host) : ITwoPlayerPacketTransport
    {
        private FakePacketTransport? _peer;
        public event Action<ReadOnlyMemory<byte>>? PacketReceived;
        public event Action<TransportStatus>? PacketStatusChanged;
        public bool IsPacketConnected { get; private set; }
        public bool IsPacketHost { get; } = host;
        public byte[]? LastPacket { get; private set; }
        public static (FakePacketTransport Host, FakePacketTransport Client) Pair()
        { var h = new FakePacketTransport(true); var c = new FakePacketTransport(false); h._peer = c; c._peer = h; return (h, c); }
        public void Connect() { IsPacketConnected = true; PacketStatusChanged?.Invoke(new(TransportConnectionState.Connected)); }
        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); LastPacket = packet.ToArray(); _peer!.PacketReceived?.Invoke(LastPacket); return ValueTask.CompletedTask; }
        public void ReplayLastToPeer() => _peer!.PacketReceived?.Invoke(LastPacket!);
        public void SendRawToPeer(ReadOnlyMemory<byte> packet) => _peer!.PacketReceived?.Invoke(packet);
        public void Receive(ReadOnlyMemory<byte> packet) => PacketReceived?.Invoke(packet);
        public ValueTask DisposeAsync() { IsPacketConnected = false; return ValueTask.CompletedTask; }
    }
}
