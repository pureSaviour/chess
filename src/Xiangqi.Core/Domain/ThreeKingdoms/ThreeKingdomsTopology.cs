using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Domain.ThreeKingdoms;

/// <summary>固定国家顺序同时规定标准轮次：魏→吴→蜀。不能使用经典红黑双方的 Opponent()。</summary>
public enum Kingdom : byte { Wei, Wu, Shu }

/// <summary>
/// 三国局部整数坐标。每国九路五行，r=0 为底线，r=4 为国界；编号与皮肤、视角及收编无关。
/// f 从该国视角的右侧向左递增。跨国必须查询拓扑，禁止直接对 Region 或屏幕坐标加减来走棋。
/// </summary>
public readonly record struct KingdomSquare(Kingdom Region, int File, int Rank)
{
    public bool IsValid => Enum.IsDefined(Region) && File is >= 0 and < 9 && Rank is >= 0 and < 5;
    public BoardCellId Cell => IsValid ? new(45 * (int)Region + 9 * Rank + File)
        : throw new ArgumentOutOfRangeException(nameof(KingdomSquare));
    public static KingdomSquare FromCell(BoardCellId cell) => cell.Value is >= 0 and < 135
        ? new((Kingdom)(cell.Value / 45), cell.Value % 9, cell.Value % 45 / 9)
        : throw new ArgumentOutOfRangeException(nameof(cell));
    public bool IsPalace => File is >= 3 and <= 5 && Rank <= 2;
}

/// <summary>
/// 已确认的 135 点三国拓扑。96 个国内四边形、12 个跨界四边形及一个中心三角形完整铺满棋盘。
/// 中心不是一个度数为六的共享点：三个中路端点各有五个邻点。车炮射线记录进入方向，
/// 从中路向外时可以分支；经过桥接边后必须进入目标国内部，不能沿中央三角形绕圈。
/// 全部几何候选在构造时计算，运行时查询只读表，不依赖棋子占位或 Godot。
/// </summary>
public sealed class ThreeKingdomsTopology : IBoardTopology
{
    public static ThreeKingdomsTopology Instance { get; } = new();
    private readonly IReadOnlyList<BoardCellId>[] _neighbors = new IReadOnlyList<BoardCellId>[135];
    private readonly IReadOnlyList<IReadOnlyList<BoardCellId>>[] _rays = new IReadOnlyList<IReadOnlyList<BoardCellId>>[135];
    private readonly IReadOnlyList<BoardCellId>[] _diagonals = new IReadOnlyList<BoardCellId>[135];
    private readonly IReadOnlyList<JumpPattern>[] _horses = new IReadOnlyList<JumpPattern>[135];
    private readonly IReadOnlyList<JumpPattern>[] _elephants = new IReadOnlyList<JumpPattern>[135];
    public string Id => "three-kingdoms";
    public int Version => 1;
    public int CellCount => 135;
    public IReadOnlyList<BoardEdge> Edges { get; }
    public IReadOnlyList<IReadOnlyList<BoardCellId>> Faces { get; }
    public bool Contains(BoardCellId cell) => cell.Value is >= 0 and < 135;
    public IReadOnlyList<BoardCellId> Neighbors(BoardCellId cell) => _neighbors[Checked(cell)];
    public IReadOnlyList<IReadOnlyList<BoardCellId>> Rays(BoardCellId cell) => _rays[Checked(cell)];
    public IReadOnlyList<BoardCellId> Diagonals(BoardCellId cell) => _diagonals[Checked(cell)];
    public IReadOnlyList<JumpPattern> HorseJumps(BoardCellId cell) => _horses[Checked(cell)];
    public IReadOnlyList<JumpPattern> ElephantJumps(BoardCellId cell) => _elephants[Checked(cell)];
    private static BoardCellId C(int k, int f, int r) => new(45 * (k % 3) + 9 * r + f);
    private int Checked(BoardCellId cell) => Contains(cell) ? cell.Value : throw new ArgumentOutOfRangeException(nameof(cell));

    private ThreeKingdomsTopology()
    {
        var edges = new List<BoardEdge>();
        var faces = new List<IReadOnlyList<BoardCellId>>();
        for (var k = 0; k < 3; k++)
        {
            for (var r = 0; r < 5; r++)
            for (var f = 0; f < 9; f++)
            {
                if (f < 8) edges.Add(new(C(k, f, r), C(k, f + 1, r)));
                if (r < 4) edges.Add(new(C(k, f, r), C(k, f, r + 1)));
                if (f < 8 && r < 4) faces.Add(Array.AsReadOnly(new[]
                    { C(k, f, r), C(k, f + 1, r), C(k, f + 1, r + 1), C(k, f, r + 1) }));
            }
            for (var i = 0; i <= 4; i++)
            {
                edges.Add(new(C(k, 4 + i, 4), C(k + 1, 4 - i, 4)));
                if (i < 4) faces.Add(Array.AsReadOnly(new[]
                    { C(k, 4 + i, 4), C(k, 5 + i, 4), C(k + 1, 3 - i, 4), C(k + 1, 4 - i, 4) }));
            }
        }
        faces.Add(Array.AsReadOnly(new[] { C(0, 4, 4), C(1, 4, 4), C(2, 4, 4) }));
        Edges = edges.AsReadOnly();
        Faces = faces.AsReadOnly();
        for (var i = 0; i < 135; i++)
        {
            var cell = new BoardCellId(i);
            _neighbors[i] = Array.AsReadOnly(edges.Where(e => e.A == cell || e.B == cell)
                .Select(e => e.A == cell ? e.B : e.A).OrderBy(c => c.Value).ToArray());
        }
        for (var i = 0; i < 135; i++)
        {
            var cell = new BoardCellId(i);
            var rays = new List<IReadOnlyList<BoardCellId>>();
            foreach (var next in Neighbors(cell)) Trace(cell, next, [cell], rays);
            _rays[i] = rays.AsReadOnly();
            var diagonals = faces.Where(face => face.Count == 4 && face.Contains(cell))
                .Select(face => Opposite(face, cell)).Distinct().ToArray();
            _diagonals[i] = Array.AsReadOnly(diagonals);
            var horses = new HashSet<JumpPattern>();
            foreach (var ray in rays.Where(ray => ray.Count >= 2))
            foreach (var face in faces.Where(face => face.Count == 4 && IsEdge(face, ray[0], ray[1])))
            foreach (var target in face.Where(target => target != ray[0] && IsEdge(face, ray[1], target)))
                if (target != cell) horses.Add(new(target, ray[0]));
            _horses[i] = Array.AsReadOnly(horses.OrderBy(p => p.Destination.Value).ThenBy(p => p.Leg.Value).ToArray());
            var elephants = new HashSet<JumpPattern>();
            foreach (var first in faces.Where(face => face.Count == 4 && face.Contains(cell)))
            {
                var eye = Opposite(first, cell);
                // Opposite quadrilateral faces share only the eye, not an edge: this preserves
                // the diagonal's direction without introducing a screen-space angle heuristic.
                foreach (var second in faces.Where(face => face.Count == 4 && face.Contains(eye) &&
                    face.Count(first.Contains) == 1))
                    elephants.Add(new(Opposite(second, eye), eye));
            }
            _elephants[i] = Array.AsReadOnly(elephants.OrderBy(p => p.Destination.Value).ThenBy(p => p.Leg.Value).ToArray());
        }
    }

    private void Trace(BoardCellId previous, BoardCellId current, List<BoardCellId> path,
        List<IReadOnlyList<BoardCellId>> rays)
    {
        if (path.Contains(current)) throw new InvalidOperationException("Topology ray contains a cycle.");
        var extended = new List<BoardCellId>(path) { current };
        var continuations = Continue(previous, current).ToArray();
        if (continuations.Length == 0) rays.Add(Array.AsReadOnly(extended.Skip(1).ToArray()));
        else foreach (var next in continuations) Trace(current, next, extended, rays);
    }

    private IEnumerable<BoardCellId> Continue(BoardCellId previous, BoardCellId current)
    {
        var a = KingdomSquare.FromCell(previous);
        var b = KingdomSquare.FromCell(current);
        if (a.Region != b.Region)
        {
            yield return new KingdomSquare(b.Region, b.File, 3).Cell;
            yield break;
        }
        var df = b.File - a.File;
        var dr = b.Rank - a.Rank;
        var next = new KingdomSquare(b.Region, b.File + df, b.Rank + dr);
        if (next.IsValid) yield return next.Cell;
        else if (dr == 1 && b.Rank == 4)
            foreach (var neighbor in Neighbors(current))
                if (KingdomSquare.FromCell(neighbor).Region != b.Region) yield return neighbor;
    }

    private static BoardCellId Opposite(IReadOnlyList<BoardCellId> face, BoardCellId cell)
    {
        for (var i = 0; i < 4; i++) if (face[i] == cell) return face[(i + 2) % 4];
        throw new ArgumentException("Cell is not on face.");
    }
    private static bool IsEdge(IReadOnlyList<BoardCellId> face, BoardCellId a, BoardCellId b)
    {
        for (var i = 0; i < face.Count; i++)
            if (face[i] == a && (face[(i + 1) % face.Count] == b || face[(i + face.Count - 1) % face.Count] == b)) return true;
        return false;
    }
}
