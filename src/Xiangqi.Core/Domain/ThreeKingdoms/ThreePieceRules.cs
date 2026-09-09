using System.Collections.Frozen;
using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Domain.ThreeKingdoms;

/// <summary>
/// 三国棋种规则的只读棋盘端口。查询占位/地域/策略，不泄漏可变数组或收编、撤销能力。
/// 新棋种只能回答局部几何和阻挡；轮次、目标所有权与淘汰仍由 ThreeKingdomsGame 裁决。
/// </summary>
public interface IReadOnlyThreeBoard
{
    IBoardTopology Topology { get; }
    AdvisorConquestPolicy AdvisorPolicy { get; }
    KingdomPiece? At(BoardCellId cell);
    bool Owns(Kingdom owner, Kingdom region);
    bool HasConquered(Kingdom owner);
}

/// <summary>可注册棋种策略。调用时 from/to 已在棋盘内且不是同一交点，目标不为友子或障碍。</summary>
public interface IThreePieceRule
{
    PieceKind Kind { get; }
    bool CanMove(IReadOnlyThreeBoard board, KingdomPiece piece, BoardCellId from, BoardCellId to);
}

/// <summary>不可变棋种注册表；扩展使用字符串 PieceKind，无须修改聚合或网络坐标格式。</summary>
public sealed class ThreePieceRuleRegistry(IEnumerable<IThreePieceRule> rules)
{
    private readonly FrozenDictionary<PieceKind, IThreePieceRule> _rules = rules.ToFrozenDictionary(r => r.Kind);
    public bool TryGet(PieceKind kind, out IThreePieceRule rule) => _rules.TryGetValue(kind, out rule!);
    public static ThreePieceRuleRegistry Standard { get; } = new(ThreeBuiltInRules.All);
}

/// <summary>轻量规则适配器，也可用具名类实现 IThreePieceRule，便于未来独立增加/测试变体棋种。</summary>
public sealed class ThreePieceRule(PieceKind kind, Func<IReadOnlyThreeBoard, KingdomPiece, BoardCellId, BoardCellId, bool> rule) : IThreePieceRule
{
    public PieceKind Kind => kind;
    public bool CanMove(IReadOnlyThreeBoard board, KingdomPiece piece, BoardCellId from, BoardCellId to) => rule(board, piece, from, to);
}

public static class ThreeBuiltInRules
{
    public static IReadOnlyList<IThreePieceRule> All { get; } = Array.AsReadOnly<IThreePieceRule>([
        new ThreePieceRule(BuiltInPieceKinds.General, General),
        new ThreePieceRule(BuiltInPieceKinds.Advisor, Advisor),
        new ThreePieceRule(BuiltInPieceKinds.Elephant, Elephant),
        new ThreePieceRule(BuiltInPieceKinds.Horse, Horse),
        new ThreePieceRule(BuiltInPieceKinds.Soldier, Soldier),
        new ThreePieceRule(BuiltInPieceKinds.Chariot, Line),
        new ThreePieceRule(BuiltInPieceKinds.Cannon, Line)
    ]);
    private static bool Allied(IReadOnlyThreeBoard board, KingdomPiece p, BoardCellId c) => p.Owner is { } owner && board.Owns(owner, KingdomSquare.FromCell(c).Region);
    private static bool General(IReadOnlyThreeBoard b, KingdomPiece p, BoardCellId from, BoardCellId to)
    { var destination = KingdomSquare.FromCell(to); return destination.Region == p.BirthRegion && destination.IsPalace && b.Topology.Neighbors(from).Contains(to); }
    private static bool Advisor(IReadOnlyThreeBoard b, KingdomPiece p, BoardCellId from, BoardCellId to)
    {
        var target = KingdomSquare.FromCell(to);
        var shared = b.AdvisorPolicy == AdvisorConquestPolicy.SharedTerritory && p.Owner is { } owner && b.HasConquered(owner);
        return Allied(b, p, to) && (shared || (target.Region == p.BirthRegion && target.IsPalace)) && b.Topology.Diagonals(from).Contains(to);
    }
    private static bool Elephant(IReadOnlyThreeBoard b, KingdomPiece p, BoardCellId from, BoardCellId to) =>
        Allied(b, p, to) && b.Topology.ElephantJumps(from).Any(j => j.Destination == to && Allied(b, p, j.Leg) && b.At(j.Leg) is null);
    private static bool Horse(IReadOnlyThreeBoard b, KingdomPiece p, BoardCellId from, BoardCellId to) =>
        b.Topology.HorseJumps(from).Any(j => j.Destination == to && b.At(j.Leg) is null);
    private static bool Soldier(IReadOnlyThreeBoard board, KingdomPiece p, BoardCellId from, BoardCellId to)
    {
        if (!board.Topology.Neighbors(from).Contains(to) || p.Owner is not { } owner) return false;
        var a = KingdomSquare.FromCell(from); var b = KingdomSquare.FromCell(to);
        if (a.Region != b.Region && b.Region != owner && board.Owns(owner, b.Region)) return false;
        if (p.HasLeftBirthRegion && b.Region == p.BirthRegion) return false;
        if (a.Region == p.BirthRegion && !p.HasLeftBirthRegion)
            return a.Region == b.Region ? b.File == a.File && b.Rank == a.Rank + 1 : a.Rank == 4;
        return true;
    }
    private static bool Line(IReadOnlyThreeBoard b, KingdomPiece p, BoardCellId from, BoardCellId to)
    {
        var required = p.Kind == BuiltInPieceKinds.Cannon && b.At(to) is not null ? 1 : 0;
        foreach (var ray in b.Topology.Rays(from))
        {
            var screens = 0;
            foreach (var cell in ray)
            {
                if (cell == to) { if (screens == required) return true; break; }
                if (b.At(cell) is not null && ++screens > required) break;
            }
        }
        return false;
    }
}
