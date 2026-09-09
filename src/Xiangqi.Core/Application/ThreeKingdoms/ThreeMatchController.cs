using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Application.ThreeKingdoms;

public enum ThreeCommandKind { Move, Resign, Undo, Draw, Vote, Forfeit, Connection, Tick }

/// <summary>
/// UI/传输共用的语义命令。Actor 必须由控制器绑定到已认证席位，而非相信报文自述身份。
/// Connection/Tick 是权威内部命令，不允许来自玩家输入；非相关字段保持默认值。
/// </summary>
public sealed record ThreeCommand(ThreeCommandKind Kind, Kingdom Actor = Kingdom.Wei, int From = 0, int To = 0,
    long OfferId = 0, bool Accepted = false, Kingdom Target = Kingdom.Wei, double Seconds = 0);

/// <summary>
/// 房间表现层端口：本地热座与多人网络使用相同命令/事件，不向 UI 暴露连接、序列号或选举。
/// LocalKingdom=null 表示本地三人热座。Tick 由主线程推进，所有事件也必须在该线程发布。
/// Session 快照只读使用；棋局命令走 Submit，聊天走共享 RoomChatManager/SendChat，避免消息
/// 历史依赖对局是否开始。实现负责调用领域规则，Dispose 只释放本房间持有的资源。
/// </summary>
public interface IThreeMatchController : IDisposable
{
    ThreeMatchSession Session { get; }
    Kingdom? LocalKingdom { get; }
    IReadOnlyList<string> PlayerNames { get; }
    IReadOnlyList<string> PlayerIds => PlayerNames;
    IReadOnlyList<bool> Connections => Session.Connected;
    IReadOnlyList<double> DisplayedGameTimes => Session.GameTimes;
    IReadOnlyList<double> DisplayedStepTimes => Session.StepTimes;
    RoomChatManager Chat { get; }
    string Status { get; }
    event Action? Changed;
    event Action<Kingdom?, string>? Message;
    string? Submit(ThreeCommand command);
    string? SendChat(Kingdom actor, Kingdom? target, string text);
    void Tick(double delta);
}

public static class ThreeCommands
{
    public static bool IsPlayerCommand(ThreeCommand c) => Enum.IsDefined(c.Kind) &&
        c.Kind is not ThreeCommandKind.Connection and not ThreeCommandKind.Tick;
    public static string? Apply(ThreeMatchSession session, ThreeCommand c)
    {
        if (!Enum.IsDefined(c.Kind) || !Enum.IsDefined(c.Actor)) return "无效命令。";
        switch (c.Kind)
        {
            case ThreeCommandKind.Move: return session.Move(c.Actor, new(new BoardCellId(c.From), new BoardCellId(c.To)));
            case ThreeCommandKind.Resign: return session.Resign(c.Actor);
            case ThreeCommandKind.Undo: return session.RequestOffer(c.Actor, KingdomOfferKind.Undo);
            case ThreeCommandKind.Draw: return session.RequestOffer(c.Actor, KingdomOfferKind.Draw);
            case ThreeCommandKind.Vote: return session.Vote(c.Actor, c.OfferId, c.Accepted);
            case ThreeCommandKind.Forfeit: return session.ForfeitDisconnected(c.Actor, c.Target);
            case ThreeCommandKind.Connection: session.SetConnected(c.Target, c.Accepted); return null;
            case ThreeCommandKind.Tick: session.Advance(c.Seconds); return null;
            default: return "无效命令。";
        }
    }
}

public sealed class LocalThreeMatchController : IThreeMatchController
{
    public ThreeMatchSession Session { get; }
    public Kingdom? LocalKingdom => null;
    public IReadOnlyList<string> PlayerNames { get; } = Array.AsReadOnly(new[] { "玩家 1", "玩家 2", "玩家 3" });
    public string Status => "本地三人 · 魏 → 吴 → 蜀";
    public RoomChatManager Chat { get; } = new(3);
    public event Action? Changed;
    public event Action<Kingdom?, string>? Message;
    public LocalThreeMatchController(TimeControl time, AdvisorConquestPolicy policy)
    {
        Session = new(time, policy);
        Session.Changed += OnChanged; Session.Message += OnMessage;
    }
    public LocalThreeMatchController(TimeControl time, ThreeKingdomsGame game)
    {
        Session = new(game, time);
        Session.Changed += OnChanged; Session.Message += OnMessage;
    }
    public string? Submit(ThreeCommand c) => ThreeCommands.IsPlayerCommand(c) ? ThreeCommands.Apply(Session, c) : "无权发送内部命令。";
    public string? SendChat(Kingdom actor, Kingdom? target, string text) =>
        Chat.Post((int)actor, target is { } recipient ? (int)recipient : null, text, out _);
    public void Tick(double delta) => Session.Advance(delta);
    private void OnChanged() => Changed?.Invoke();
    private void OnMessage(Kingdom? k, string text) => Message?.Invoke(k, text);
    public void Dispose() { Session.Changed -= OnChanged; Session.Message -= OnMessage; }
}
