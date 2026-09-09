namespace Xiangqi.Core.Domain.Topology;

/// <summary>
/// 拓扑版本内稳定的交点编号。它不是屏幕坐标，也不编码棋子控制权。
/// 默认值是交点 0；输入有效性统一通过对应拓扑的 Contains 校验。
/// </summary>
public readonly record struct BoardCellId(int Value);

/// <summary>无向普通邻接边。九宫斜线和棋子的跳跃路径不是普通邻接边。</summary>
public readonly record struct BoardEdge(BoardCellId A, BoardCellId B);

/// <summary>跳跃候选模式。马只检查 Leg 马腿；象只检查 Leg 象眼，不能把整条跳跃当作车的路径。</summary>
public readonly record struct JumpPattern(BoardCellId Destination, BoardCellId Leg);

/// <summary>
/// 不可变棋盘拓扑端口：定义交点、边、面、连续射线及跳跃模式，不引用引擎、浮点投影、
/// 玩家或占位状态。规则组合这些候选路径与只读占位进行验证，展示层使用同一编号绘图。
/// 返回集合须为只读集合；射线不包含起点，按沿线距离排序，分支分别返回完整路径。
/// 新棋盘可以提供另一实现，不需要让棋种规则依赖显示角度或欧氏距离。
/// </summary>
public interface IBoardTopology
{
    string Id { get; }
    int Version { get; }
    int CellCount { get; }
    bool Contains(BoardCellId cell);
    IReadOnlyList<BoardEdge> Edges { get; }
    IReadOnlyList<IReadOnlyList<BoardCellId>> Faces { get; }
    IReadOnlyList<BoardCellId> Neighbors(BoardCellId cell);
    IReadOnlyList<IReadOnlyList<BoardCellId>> Rays(BoardCellId cell);
    IReadOnlyList<BoardCellId> Diagonals(BoardCellId cell);
    IReadOnlyList<JumpPattern> HorseJumps(BoardCellId cell);
    IReadOnlyList<JumpPattern> ElephantJumps(BoardCellId cell);
}
