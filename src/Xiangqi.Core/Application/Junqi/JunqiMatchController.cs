using System.Text.Json;
using Xiangqi.Core.Domain.Junqi;

namespace Xiangqi.Core.Application.Junqi;

public readonly record struct JunqiSubmitResult(bool Accepted, bool Pending, string? Error);

/// <summary>
/// Player-view boundary for a Junqi match. Private layouts and combat rules remain in the
/// authoritative aggregate; presentation receives only its viewer-specific snapshot and submits
/// cell intents. Events run synchronously on the driving thread. The screen owns disposal.
/// </summary>
public interface IJunqiMatchController : IAsyncDisposable
{
    event Action? Changed;
    event Action<string>? Message;
    JunqiPlayerSnapshot Snapshot { get; }
    JunqiSide? LocalSide { get; }
    bool IsReady { get; }
    bool IsConnected { get; }
    bool CanSubmitMove { get; }
    IReadOnlyList<JunqiCell> LegalDestinations(JunqiCell from);
    ValueTask<JunqiSubmitResult> SubmitMoveAsync(JunqiCell from, JunqiCell to,
        CancellationToken cancellationToken = default);
    bool TryCreateFinishedRecord(string firstPlayer, string secondPlayer,
        out JunqiGameRecord? record, out string? error);
}

public sealed class LocalJunqiMatchController : IJunqiMatchController
{
    private readonly IReadOnlyList<JunqiPlacement> _first;
    private readonly IReadOnlyList<JunqiPlacement> _second;
    private readonly JunqiGame _game;
    public LocalJunqiMatchController(IReadOnlyList<JunqiPlacement>? first = null,
        IReadOnlyList<JunqiPlacement>? second = null)
    {
        _first = first ?? JunqiGame.CreateRandomLayout(JunqiSide.First, 1);
        _second = second ?? JunqiGame.CreateRandomLayout(JunqiSide.Second, 26);
        _game = new(_first, _second);
    }
    public event Action? Changed;
    public event Action<string>? Message { add { } remove { } }
    public JunqiPlayerSnapshot Snapshot => _game.Snapshot.ForPlayer(_game.Snapshot.SideToMove);
    public JunqiSide? LocalSide => null;
    public bool IsReady => true;
    public bool IsConnected => true;
    public bool CanSubmitMove => !_game.Snapshot.IsFinished;
    public IReadOnlyList<JunqiCell> LegalDestinations(JunqiCell from) => _game.LegalDestinations(from);
    public ValueTask<JunqiSubmitResult> SubmitMoveAsync(JunqiCell from, JunqiCell to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var result = _game.TryMove(new(from, to));
        if (result.IsSuccess) Changed?.Invoke();
        return ValueTask.FromResult(new JunqiSubmitResult(result.IsSuccess, false, result.Error));
    }
    public bool TryCreateFinishedRecord(string firstPlayer, string secondPlayer,
        out JunqiGameRecord? record, out string? error)
    {
        var snapshot = _game.Snapshot;
        if (!snapshot.IsFinished) { record = null; error = "对局尚未结束。"; return false; }
        record = BuildRecord(firstPlayer, secondPlayer, _first, _second, _game.History, snapshot.Winner);
        error = null; return true;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static JunqiGameRecord BuildRecord(string firstPlayer, string secondPlayer,
        IReadOnlyList<JunqiPlacement> first, IReadOnlyList<JunqiPlacement> second,
        IReadOnlyList<(JunqiMove Move, JunqiBattleResult Battle, JunqiSide? FlagReveal)> history,
        JunqiSide? winner) => new(firstPlayer, secondPlayer, 0, JunqiGame.StandardMandatoryAttackPly,
            first.ToArray(), second.ToArray(), history.Select((x, index) => new JunqiRecordedMove(
                index % 2 == 0 ? JunqiSide.First : JunqiSide.Second, x.Move, x.Battle, x.FlagReveal)).ToArray(), winner);
}

/// <summary>
/// Two-player host-authoritative Junqi coordinator. Each peer creates its own legal private setup;
/// the client sends only that setup to the authenticated host. During play the host emits a
/// different redacted view for the remote player. Both layouts are released only in the terminal
/// state so either peer can create a fully replayable AC2 record. A P2P host necessarily learns the
/// remote layout and is therefore trusted as referee; a neutral server is required to remove that trust.
/// </summary>
public sealed class NetworkJunqiMatchController : IJunqiMatchController
{
    private readonly ITwoPlayerPacketTransport _transport;
    private IReadOnlyList<JunqiPlacement>? _localLayout;
    private IReadOnlyList<JunqiPlacement>? _firstLayout;
    private IReadOnlyList<JunqiPlacement>? _secondLayout;
    private readonly List<JunqiRecordedMove> _moves = [];
    private readonly Dictionary<JunqiCell, IReadOnlyList<JunqiCell>> _legal = [];
    private JunqiGame? _authority;
    private JunqiPlayerSnapshot _view;
    private JunqiSide? _hostSide;
    private long _nextRequest;
    private bool _disposed;

    public NetworkJunqiMatchController(ITwoPlayerPacketTransport transport, JunqiSide? hostSide)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (transport.IsPacketHost && hostSide is null || hostSide is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(hostSide));
        _hostSide = hostSide; LocalSide = transport.IsPacketHost ? hostSide : hostSide?.Opponent();
        _localLayout = LocalSide is { } local ? JunqiGame.CreateRandomLayout(local, local == JunqiSide.First ? 1 : 26) : null;
        _view = _localLayout is null ? EmptyView() : WaitingView(_localLayout, LocalSide!.Value);
        if (transport.IsPacketHost)
        {
            if (hostSide == JunqiSide.First) _firstLayout = _localLayout; else _secondLayout = _localLayout;
        }
        _transport.PacketReceived += OnPacket; _transport.PacketStatusChanged += OnStatus;
    }

    public event Action? Changed;
    public event Action<string>? Message;
    public JunqiPlayerSnapshot Snapshot => _view;
    public JunqiSide? LocalSide { get; private set; }
    public bool IsReady => _authority is not null || !_transport.IsPacketHost && _hostSide is not null && _view.Pieces.Count == 50;
    public bool IsConnected => _transport.IsPacketConnected;
    public bool CanSubmitMove => IsReady && IsConnected && !_view.IsFinished && _view.SideToMove == LocalSide;
    public IReadOnlyList<JunqiCell> LegalDestinations(JunqiCell from) => _legal.TryGetValue(from, out var value) ? value : [];

    public async ValueTask<JunqiSubmitResult> SubmitMoveAsync(JunqiCell from, JunqiCell to,
        CancellationToken cancellationToken = default)
    {
        if (!CanSubmitMove) return new(false, false, "当前不能行棋。");
        if (_authority is null)
        {
            await SendAsync(new JunqiPacket("move", ++_nextRequest, _view.PlyNumber, (int)LocalSide!.Value,
                from.Row, from.Column, to.Row, to.Column, null, null, null, null, null), cancellationToken);
            return new(true, true, null);
        }
        var result = _authority.TryMove(new(from, to));
        if (!result.IsSuccess) return new(false, false, result.Error);
        AppendMove(result); PublishHostView(); await SendStateAsync(0, result, cancellationToken); return new(true, false, null);
    }

    public bool TryCreateFinishedRecord(string firstPlayer, string secondPlayer,
        out JunqiGameRecord? record, out string? error)
    {
        record = null;
        if (!_view.IsFinished || _firstLayout is null || _secondLayout is null)
        { error = "对局未结束或赛后布阵尚未释放。"; return false; }
        var candidate = new JunqiGameRecord(firstPlayer, secondPlayer, 0, JunqiGame.StandardMandatoryAttackPly,
            _firstLayout, _secondLayout, _moves.ToArray(), _view.Winner);
        try { _ = candidate.Replay(); record = candidate; error = null; return true; }
        catch (InvalidDataException exception) { error = exception.Message; return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _transport.PacketReceived -= OnPacket; _transport.PacketStatusChanged -= OnStatus;
        await _transport.DisposeAsync();
    }

    private void OnStatus(TransportStatus status)
    {
        Message?.Invoke(status.Detail ?? status.State.ToString());
        if (status.State == TransportConnectionState.Connected && _transport.IsPacketHost)
            _ = SendAsync(new JunqiPacket("hello", 0, 0, (int)_hostSide!.Value, -1, -1, -1, -1,
                null, null, null, null, null));
        Changed?.Invoke();
    }

    private void OnPacket(ReadOnlyMemory<byte> bytes)
    {
        if (MatchWireProtocol.TryDecode(bytes.ToArray(), out var shared) &&
            shared.Type is MatchWireMessageType.PlayerProfile or MatchWireMessageType.Chat) return;
        if (!JunqiWireProtocol.TryDecode(bytes.Span, out var packet, out var error))
        { Message?.Invoke(error ?? "收到无效军旗数据。"); return; }
        switch (packet!.Type)
        {
            case "hello" when !_transport.IsPacketHost: ReceiveHello(packet); break;
            case "setup" when _transport.IsPacketHost: ReceiveSetup(packet); break;
            case "move" when _transport.IsPacketHost: ReceiveMove(packet); break;
            case "state" when !_transport.IsPacketHost: ReceiveState(packet); break;
            case "reject" when !_transport.IsPacketHost: Message?.Invoke(packet.Error ?? "房主拒绝了请求。"); break;
            default: Message?.Invoke("军旗消息与当前端角色不匹配。"); break;
        }
    }

    private void ReceiveHello(JunqiPacket packet)
    {
        if (IsReady) return;
        if (packet.Side is < 0 or > 1 || _hostSide is { } known && (int)known != packet.Side)
        { Message?.Invoke("军旗房主席位无效。"); return; }
        _hostSide = (JunqiSide)packet.Side; LocalSide = _hostSide.Value.Opponent();
        var layout = JunqiGame.CreateRandomLayout(LocalSide.Value, LocalSide == JunqiSide.First ? 1 : 26);
        _localLayout = layout;
        var codes = LayoutCodes(layout);
        if (LocalSide == JunqiSide.First) _firstLayout = layout; else _secondLayout = layout;
        _view = WaitingView(layout, LocalSide.Value);
        _ = SendAsync(new JunqiPacket("setup", 0, 0, (int)LocalSide.Value, -1, -1, -1, -1,
            codes, null, null, null, null)); Changed?.Invoke();
    }

    private void ReceiveSetup(JunqiPacket packet)
    {
        if (_authority is not null) return;
        if (_hostSide is null || packet.Side != (int)_hostSide.Value.Opponent() || packet.Layout is not { Length: 25 })
        { _ = Reject(0, "军旗私密布阵无效。"); return; }
        try
        {
            var side = (JunqiSide)packet.Side;
            var layout = JunqiGame.Build(side, packet.Layout, side == JunqiSide.First ? 1 : 26);
            if (side == JunqiSide.First) _firstLayout = layout; else _secondLayout = layout;
            _authority = new(_firstLayout!, _secondLayout!); PublishHostView(); _ = SendStateAsync(0, null);
        }
        catch (ArgumentException exception) { _ = Reject(0, exception.Message); }
    }

    private void ReceiveMove(JunqiPacket packet)
    {
        if (_authority is null || _hostSide is null || packet.Side != (int)_hostSide.Value.Opponent() ||
            packet.RequestId <= 0 || packet.ExpectedPly != _authority.Snapshot.PlyNumber)
        { _ = Reject(packet.RequestId, "军旗着法身份或局面版本无效。"); return; }
        var result = _authority.TryMove(new(new(packet.FromRow, packet.FromColumn), new(packet.ToRow, packet.ToColumn)));
        if (!result.IsSuccess) { _ = Reject(packet.RequestId, result.Error ?? "军旗着法无效。"); return; }
        AppendMove(result); PublishHostView(); _ = SendStateAsync(packet.RequestId, result);
    }

    private void ReceiveState(JunqiPacket packet)
    {
        string? error = null;
        if (packet.View is null || !TryView(packet.View, out var view, out error) ||
            view!.PlyNumber == 0 && _view.PlyNumber != 0 ||
            view.PlyNumber != 0 && view.PlyNumber != _view.PlyNumber + 1 ||
            packet.Battle is { } declared && !Enum.IsDefined((JunqiBattleResult)declared) ||
            packet.FlagReveal is < 0 or > 1)
        { Message?.Invoke(error ?? "军旗权威局面序号不连续。"); return; }
        if (view.PlyNumber == _view.PlyNumber + 1 &&
            (packet.Battle is null || packet.Side is < 0 or > 1 ||
             !new JunqiCell(packet.FromRow, packet.FromColumn).IsInside ||
             !new JunqiCell(packet.ToRow, packet.ToColumn).IsInside))
        { Message?.Invoke("军旗权威着法字段无效。"); return; }
        if (view.PlyNumber == _view.PlyNumber + 1 && packet.Battle is { } battle)
            _moves.Add(new((JunqiSide)packet.Side, new(new(packet.FromRow, packet.FromColumn), new(packet.ToRow, packet.ToColumn)),
                (JunqiBattleResult)battle, packet.FlagReveal is null ? null : (JunqiSide)packet.FlagReveal.Value));
        _view = view; LoadLegal(packet.Legal);
        if (_view.IsFinished && packet.Layout is not null && packet.OtherLayout is not null && LocalSide is { } local)
        {
            try
            {
                var own = JunqiGame.Build(local, packet.Layout, local == JunqiSide.First ? 1 : 26);
                var other = JunqiGame.Build(local.Opponent(), packet.OtherLayout, local == JunqiSide.First ? 26 : 1);
                _firstLayout = local == JunqiSide.First ? own : other; _secondLayout = local == JunqiSide.Second ? own : other;
            }
            catch (ArgumentException exception) { Message?.Invoke($"赛后布阵无效：{exception.Message}"); }
        }
        Changed?.Invoke();
    }

    private void AppendMove(JunqiMoveResult result) => _moves.Add(new(_moves.Count % 2 == 0 ? JunqiSide.First : JunqiSide.Second,
        result.Move, result.Battle, result.FlagRevealedFor));
    private void PublishHostView()
    {
        _view = _authority!.Snapshot.ForPlayer(_hostSide!.Value); RefreshLegal(_authority, _view.SideToMove); Changed?.Invoke();
    }
    private async ValueTask SendStateAsync(long requestId, JunqiMoveResult? result,
        CancellationToken cancellationToken = default)
    {
        var remote = _hostSide!.Value.Opponent(); var view = _authority!.Snapshot.ForPlayer(remote);
        var legal = LegalDto(_authority, view.SideToMove);
        var ownCodes = view.IsFinished ? LayoutCodes(remote == JunqiSide.First ? _firstLayout! : _secondLayout!) : null;
        var otherCodes = view.IsFinished ? LayoutCodes(remote == JunqiSide.First ? _secondLayout! : _firstLayout!) : null;
        await SendAsync(new JunqiPacket("state", requestId, view.PlyNumber,
            result is null ? (int)_hostSide.Value : (int)view.SideToMove.Opponent(),
            result?.Move.From.Row ?? -1, result?.Move.From.Column ?? -1, result?.Move.To.Row ?? -1, result?.Move.To.Column ?? -1,
            ownCodes, otherCodes, ToDto(view), legal, result?.Error, result is null ? null : (int)result.Value.Battle,
            result?.FlagRevealedFor is null ? null : (int)result.Value.FlagRevealedFor.Value), cancellationToken);
    }
    private ValueTask Reject(long id, string error) => SendAsync(new JunqiPacket("reject", id, _view.PlyNumber,
        (int)_hostSide!.Value, -1, -1, -1, -1, null, null, null, null, error));
    private ValueTask SendAsync(JunqiPacket packet, CancellationToken cancellationToken = default) =>
        _transport.SendPacketAsync(JunqiWireProtocol.Encode(packet), cancellationToken);
    private void RefreshLegal(JunqiGame game, JunqiSide side)
    {
        _legal.Clear(); if (side != LocalSide) return;
        foreach (var item in game.Snapshot.Pieces.Where(x => x.Piece.Side == side)) _legal[item.Cell] = game.LegalDestinations(item.Cell);
    }
    private static JunqiLegalDto[] LegalDto(JunqiGame game, JunqiSide side) => game.Snapshot.Pieces.Where(x => x.Piece.Side == side)
        .Select(x => new JunqiLegalDto(x.Cell.Row, x.Cell.Column, game.LegalDestinations(x.Cell).Select(c => new[] { c.Row, c.Column }).ToArray())).ToArray();
    private void LoadLegal(JunqiLegalDto[]? values)
    {
        _legal.Clear(); if (values is null) return;
        foreach (var item in values)
        {
            var from = new JunqiCell(item.Row, item.Column); if (!from.IsInside || item.Targets is null) continue;
            _legal[from] = item.Targets.Where(x => x is { Length: 2 }).Select(x => new JunqiCell(x[0], x[1])).Where(x => x.IsInside).Distinct().ToArray();
        }
    }
    private static JunqiPlayerSnapshot WaitingView(IReadOnlyList<JunqiPlacement> layout, JunqiSide viewer) => new(
        layout.Select(x => new JunqiPublicPlacement(x.Cell, x.Piece.Id, x.Piece.Side, x.Piece.Kind)).ToArray(),
        JunqiSide.First, false, null, JunqiEndReason.None, 0, 0, new HashSet<JunqiSide>());
    private static JunqiPlayerSnapshot EmptyView() => new([], JunqiSide.First, false, null,
        JunqiEndReason.None, 0, 0, new HashSet<JunqiSide>());
    private static string LayoutCodes(IReadOnlyList<JunqiPlacement> layout) => new(JunqiTopology.Instance.SetupCells(layout[0].Piece.Side)
        .Select(cell => JunqiGame.Code(layout.Single(x => x.Cell == cell).Piece.Kind)).ToArray());
    private static JunqiViewDto ToDto(JunqiPlayerSnapshot view) => new(view.Pieces.Select(x => new JunqiPublicDto(
        x.Cell.Row, x.Cell.Column, x.PieceId, (int)x.Side, x.Kind is null ? null : (int)x.Kind)).ToArray(),
        (int)view.SideToMove, view.IsFinished, view.Winner is null ? null : (int)view.Winner,
        (int)view.EndReason, view.PlyNumber, view.QuietPlyCount, view.ExposedFlags.Select(x => (int)x).ToArray());
    private static bool TryView(JunqiViewDto value, out JunqiPlayerSnapshot? view, out string? error)
    {
        view = null; error = null;
        if (value.Pieces is null || value.Pieces.Length is 0 or > 50 || value.Side is < 0 or > 1 || value.Ply < 0 ||
            value.Winner is < 0 or > 1 || !Enum.IsDefined((JunqiEndReason)value.EndReason) || value.Quiet < 0)
        { error = "军旗公开局面字段无效。"; return false; }
        var cells = new HashSet<JunqiCell>(); var ids = new HashSet<int>(); var pieces = new List<JunqiPublicPlacement>();
        foreach (var item in value.Pieces)
        {
            var cell = new JunqiCell(item.Row, item.Column);
            if (!cell.IsInside || item.Id <= 0 || item.Side is < 0 or > 1 || item.Kind is not null && !Enum.IsDefined((JunqiPieceKind)item.Kind) ||
                !cells.Add(cell) || !ids.Add(item.Id)) { error = "军旗公开棋子无效或重复。"; return false; }
            pieces.Add(new(cell, item.Id, (JunqiSide)item.Side, item.Kind is null ? null : (JunqiPieceKind)item.Kind));
        }
        if ((value.Exposed ?? []).Any(x => x is < 0 or > 1)) { error = "军旗亮旗方字段无效。"; return false; }
        var exposed = (value.Exposed ?? []).Select(x => (JunqiSide)x).ToHashSet();
        view = new(pieces, (JunqiSide)value.Side, value.Finished, value.Winner is null ? null : (JunqiSide)value.Winner,
            (JunqiEndReason)value.EndReason, value.Ply, value.Quiet, exposed); return true;
    }
}

internal sealed record JunqiPacket(string Type, long RequestId, int ExpectedPly, int Side,
    int FromRow, int FromColumn, int ToRow, int ToColumn, string? Layout, string? OtherLayout,
    JunqiViewDto? View, JunqiLegalDto[]? Legal, string? Error, int? Battle = null, int? FlagReveal = null);
internal sealed record JunqiViewDto(JunqiPublicDto[] Pieces, int Side, bool Finished, int? Winner,
    int EndReason, int Ply, int Quiet, int[] Exposed);
internal sealed record JunqiPublicDto(int Row, int Column, int Id, int Side, int? Kind);
internal sealed record JunqiLegalDto(int Row, int Column, int[][] Targets);

internal static class JunqiWireProtocol
{
    public const int MaximumPacketBytes = 16_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ReadOnlyMemory<byte> Encode(JunqiPacket packet)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (body.Length > MaximumPacketBytes) throw new InvalidOperationException("军旗数据包超过上限。");
        var result = new byte[body.Length + 2]; result[0] = 1; result[1] = 0x51; body.CopyTo(result, 2); return result;
    }
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out JunqiPacket? packet, out string? error)
    {
        packet = null; error = null;
        if (bytes.Length is < 3 or > MaximumPacketBytes + 2 || bytes[0] != 1 || bytes[1] != 0x51)
        { error = "军旗协议版本或长度无效。"; return false; }
        try
        {
            packet = JsonSerializer.Deserialize<JunqiPacket>(bytes[2..], Json);
            if (packet is null || packet.Type is not ("hello" or "setup" or "move" or "state" or "reject")) throw new JsonException();
            return true;
        }
        catch (JsonException) { error = "军旗数据包 JSON 无效。"; return false; }
    }
}
