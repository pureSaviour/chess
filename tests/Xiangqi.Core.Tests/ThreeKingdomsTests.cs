using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

static class ThreeKingdomsTests
{
    private static readonly ThreeKingdomsTopology Board = ThreeKingdomsTopology.Instance;
    public static IEnumerable<(string Name, Action Run)> All => new (string, Action)[]
    {
        ("三国拓扑135点243边完整连通且无重边", Connectivity),
        ("三国中心为三个五邻接点且射线可逆无回环", Rays),
        ("三国马腿和象眼保留国内日田字规则", Jumps),
        ("三国拓扑国界桥接与旋转对称", Symmetry)
    };
    private static BoardCellId C(int k, int f, int r) => new KingdomSquare((Kingdom)k, f, r).Cell;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Connectivity()
    {
        Check(Board.CellCount == 135 && Board.Edges.Count == 243 && Board.Faces.Count == 109, "Unexpected topology size.");
        Check(Board.Faces.Count(f => f.Count == 3) == 1, "Exactly one central triangle is required.");
        Check(Board.Edges.Select(e => (Math.Min(e.A.Value, e.B.Value), Math.Max(e.A.Value, e.B.Value))).Distinct().Count() == 243, "Duplicate edge.");
        var visited = new HashSet<BoardCellId>();
        var stack = new Stack<BoardCellId>(); stack.Push(C(0, 0, 0));
        while (stack.TryPop(out var cell)) if (visited.Add(cell)) foreach (var next in Board.Neighbors(cell)) stack.Push(next);
        Check(visited.Count == 135, "Disconnected cells.");
    }
    private static void Rays()
    {
        for (var k = 0; k < 3; k++) Check(Board.Neighbors(C(k, 4, 4)).Count == 5, "Wrong central degree.");
        var routes = Board.Rays(C(0, 4, 3));
        Check(routes.Any(r => r.Take(3).SequenceEqual(new[] { C(0, 4, 4), C(1, 4, 4), C(1, 4, 3) })), "Missing Wu branch.");
        Check(routes.Any(r => r.Take(3).SequenceEqual(new[] { C(0, 4, 4), C(2, 4, 4), C(2, 4, 3) })), "Missing Shu branch.");
        for (var i = 0; i < 135; i++)
        foreach (var ray in Board.Rays(new(i)))
        {
            Check(ray.Distinct().Count() == ray.Count && !ray.Contains(new(i)), "Cyclic ray.");
            var reverse = ray.Reverse().Skip(1).Append(new BoardCellId(i)).ToArray();
            Check(Board.Rays(ray[^1]).Any(r => r.Take(reverse.Length).SequenceEqual(reverse)), "Ray not reversible.");
        }
    }
    private static void Jumps()
    {
        Check(Board.HorseJumps(C(0, 4, 2)).Count == 8, "Interior horse must have 8 destinations.");
        Check(Board.HorseJumps(C(0, 4, 2)).Contains(new JumpPattern(C(0, 6, 3), C(0, 5, 2))), "Horse leg differs.");
        Check(Board.ElephantJumps(C(0, 4, 2)).Contains(new JumpPattern(C(0, 6, 4), C(0, 5, 3))), "Elephant eye differs.");
        Check(!Board.Diagonals(C(0, 4, 4)).Contains(C(1, 4, 4)), "Triangle edge cannot become a diagonal.");
    }
    private static void Symmetry()
    {
        static BoardCellId Rotate(BoardCellId c) => new((c.Value + 45) % 135);
        for (var i = 0; i < 135; i++)
        {
            var cell = new BoardCellId(i);
            Check(Board.Neighbors(cell).Select(Rotate).ToHashSet().SetEquals(Board.Neighbors(Rotate(cell))), "Rotation changed adjacency.");
            Check(Board.HorseJumps(cell).Select(p => new JumpPattern(Rotate(p.Destination), Rotate(p.Leg)))
                .ToHashSet().SetEquals(Board.HorseJumps(Rotate(cell))), "Rotation changed horse paths.");
        }
    }
}
