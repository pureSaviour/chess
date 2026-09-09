using Xiangqi.Core.Domain.ThreeKingdoms;

namespace Xiangqi.Core.Application.ThreeKingdoms;

public enum KingdomOfferKind : byte { Undo, Draw }
public sealed record KingdomOffer(long Id, Kingdom Actor, KingdomOfferKind Kind, int Ply,
    IReadOnlyList<Kingdom> Required, IReadOnlyList<Kingdom> Accepted);

/// <summary>
/// 引擎无关的三人对局门面。保存棋钟、存活席位的协商票据和连接状态；不拥有套接字或界面。
/// 走子经唯一领域聚合验证。普通悔棋/和棋冻结计时并要求所有其他存活玩家同意；断线等待
/// 不冻结计时，任一其他存活玩家可判负。身份认证和主机选举由网络协调器处理。
/// </summary>
public sealed class ThreeMatchSession
{
    public ThreeKingdomsGame Game { get; }
    public TimeControl TimeControl { get; }
    private readonly double[] _gameTime = new double[3];
    private readonly double[] _stepTime = new double[3];
    private readonly bool[] _connected = [true, true, true];
    private long _nextOffer;
    private readonly List<(Kingdom Actor, KingdomMove Move)> _moves = [];
    private readonly KingdomSnapshot _initialPosition;
    public KingdomOffer? Offer { get; private set; }
    public bool IsRunning { get; set; } = true;
    public long Revision { get; private set; }
    public event Action? Changed;
    public event Action<KingdomSnapshot, KingdomSnapshot, KingdomMove, bool>? Moved;
    public event Action<Kingdom?, string>? Message;
    public IReadOnlyList<double> GameTimes { get; }
    public IReadOnlyList<double> StepTimes { get; }
    public IReadOnlyList<bool> Connected { get; }

    /// <summary>Forks the current authoritative position for local what-if analysis.</summary>
    public ThreeTrialSession BeginTrial() => new(Game, Revision);

    public ThreeMatchSession(TimeControl timeControl, AdvisorConquestPolicy policy = AdvisorConquestPolicy.SharedTerritory)
        : this(new ThreeKingdomsGame(policy), timeControl) { }
    public ThreeMatchSession(ThreeKingdomsGame game, TimeControl timeControl)
    {
        timeControl.Validate(); Game = game; TimeControl = timeControl;
        _initialPosition = game.Snapshot;
        GameTimes = Array.AsReadOnly(_gameTime); StepTimes = Array.AsReadOnly(_stepTime); Connected = Array.AsReadOnly(_connected);
        Array.Fill(_gameTime, (double)timeControl.GameSeconds);
        Array.Fill(_stepTime, (double)timeControl.MoveSeconds);
    }
    public ThreeGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) => new(
        _initialPosition, Array.AsReadOnly(_moves.ToArray()), Game.AdvisorPolicy,
        tags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>在无事件订阅的独立副本上复用同一执行路径，供网络提议校验；不复制棋规判断。</summary>
    internal string? Validate(ThreeCommand command)
    {
        var copy = new ThreeMatchSession(Game.Fork(), TimeControl)
        {
            Offer = Offer, _nextOffer = _nextOffer, IsRunning = IsRunning, Revision = Revision
        };
        Array.Copy(_gameTime, copy._gameTime, 3); Array.Copy(_stepTime, copy._stepTime, 3);
        Array.Copy(_connected, copy._connected, 3);
        return ThreeCommands.Apply(copy, command);
    }
    public string? Move(Kingdom actor, KingdomMove move)
    {
        if (!IsRunning || Offer is not null || !IsPresent(actor)) return "当前不能行棋。";
        var before = Game.Snapshot;
        var result = Game.Move(actor, move);
        if (!result.Success) return result.Error;
        _moves.Add((actor, move));
        _stepTime[(int)Game.Snapshot.Turn] = TimeControl.MoveSeconds;
        Revision++;
        Moved?.Invoke(before, Game.Snapshot, move, result.Captured is not null);
        if (result.Conquered is { } captured) Message?.Invoke(null, $"{Name(actor)}收编了{Name(captured)}的军队与国土。");
        Changed?.Invoke();
        return null;
    }

    /// <summary>返回超时国并原子淘汰；扣时仅由权威端调用，客户端通过有序命令重放同样的 delta。</summary>
    public Kingdom? Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!IsRunning || Offer is not null || Game.Snapshot.IsFinished) return null;
        var side = Game.Snapshot.Turn; var i = (int)side;
        if (TimeControl.GameSeconds > 0) _gameTime[i] = Math.Max(0, _gameTime[i] - seconds);
        if (TimeControl.MoveSeconds > 0) _stepTime[i] = Math.Max(0, _stepTime[i] - seconds);
        if ((TimeControl.GameSeconds > 0 && _gameTime[i] <= 0) || (TimeControl.MoveSeconds > 0 && _stepTime[i] <= 0))
        { Eliminate(side, "超时"); return side; }
        return null;
    }

    public string? Resign(Kingdom actor)
    {
        if (!IsPresent(actor) || Game.Snapshot.IsFinished || Offer is not null) return "当前不能认输。";
        Eliminate(actor, "认输"); return null;
    }

    public string? RequestOffer(Kingdom actor, KingdomOfferKind kind)
    {
        if (!Enum.IsDefined(kind) || !IsRunning || !IsPresent(actor) || Offer is not null || Game.Snapshot.IsFinished)
            return "当前不能发起协商。";
        if (kind == KingdomOfferKind.Undo && !Game.CanUndo(actor)) return "只有刚落子且下一位玩家尚未落子时可申请悔棋。";
        var required = Enum.GetValues<Kingdom>().Where(k => k != actor && Game.Snapshot.Alive[(int)k]).ToArray();
        Offer = new(++_nextOffer, actor, kind, Game.Snapshot.Ply, Array.AsReadOnly(required), Array.Empty<Kingdom>());
        Revision++; Changed?.Invoke(); return null;
    }

    public string? Vote(Kingdom actor, long offerId, bool accepted)
    {
        if (Offer is not { } offer || offer.Id != offerId || offer.Ply != Game.Snapshot.Ply ||
            !IsPresent(actor) || !offer.Required.Contains(actor) || offer.Accepted.Contains(actor)) return "申请已失效或无权投票。";
        if (!accepted) { Offer = null; Message?.Invoke(null, $"{Name(actor)}拒绝了协商。"); }
        else
        {
            var approvals = offer.Accepted.Append(actor).ToArray();
            if (approvals.Length == offer.Required.Count)
            {
                if (offer.Kind == KingdomOfferKind.Undo)
                {
                    if (Game.Undo(offer.Actor) && _moves.Count > 0) _moves.RemoveAt(_moves.Count - 1);
                }
                else Game.DeclareDraw();
                // Deliberately retain every player's elapsed game/step time on undo.
                Offer = null;
                Message?.Invoke(null, offer.Kind == KingdomOfferKind.Undo ? "所有其他存活玩家同意，已撤销一手（不返还耗时）。" : "所有存活玩家同意和棋。");
            }
            else Offer = offer with { Accepted = Array.AsReadOnly(approvals) };
        }
        Revision++; Changed?.Invoke(); return null;
    }

    public void SetConnected(Kingdom side, bool connected)
    {
        if (!Enum.IsDefined(side) || _connected[(int)side] == connected) return;
        _connected[(int)side] = connected;
        // A regular negotiation cannot keep clocks frozen indefinitely after a disconnect.
        if (!connected && Offer is not null) Offer = null;
        Revision++;
        Message?.Invoke(null, connected ? $"{Name(side)}已重连。" : $"{Name(side)}断线，等待重连；其回合仍计时，其他存活玩家可判其负。" );
        Changed?.Invoke();
    }
    public string? ForfeitDisconnected(Kingdom voter, Kingdom absent)
    {
        if (!Enum.IsDefined(absent) || !IsPresent(voter) || voter == absent || _connected[(int)absent] ||
            !Game.Snapshot.Alive[(int)absent] || Game.Snapshot.IsFinished) return "该席位不可判负。";
        Eliminate(absent, $"由{Name(voter)}判定断线负"); return null;
    }
    private bool IsPresent(Kingdom side) => Enum.IsDefined(side) && _connected[(int)side] && Game.Snapshot.Alive[(int)side];
    private void Eliminate(Kingdom side, string reason)
    {
        var previous = Game.Snapshot.Turn;
        if (!Game.Eliminate(side)) return;
        Offer = null;
        if (previous != Game.Snapshot.Turn) _stepTime[(int)Game.Snapshot.Turn] = TimeControl.MoveSeconds;
        Revision++; Message?.Invoke(null, $"{Name(side)}{reason}：将已移除，余子留作障碍。"); Changed?.Invoke();
    }
    public static string Name(Kingdom side) => side switch { Kingdom.Wei => "魏", Kingdom.Wu => "吴", Kingdom.Shu => "蜀", _ => "?" };
}
