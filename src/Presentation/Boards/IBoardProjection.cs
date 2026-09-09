using Godot;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Presentation.Boards;

/// <summary>
/// 引擎侧投影端口。只处理局部像素和命中半径，不决定走法。Resize 之后的点、边和点击
/// 必须引用同一缓存；命中不得反向推导棋规。未来旋转/其他棋盘可替换此实现。
/// </summary>
public interface IBoardProjection
{
    float CellSize { get; }
    Vector2 Point(BoardCellId cell);
    BoardCellId? Hit(Vector2 position);
    void Resize(Vector2 size);
}

/// <summary>六片双线性投影，共享中路顶点；固定正六边形比例，显示缩放不改变拓扑 CellId。</summary>
public sealed class ThreeBoardProjection : IBoardProjection
{
    private readonly Vector2[] _points = new Vector2[135];
    private Vector2 _size = new(-1, -1);
    public Vector2[] Outline { get; } = new Vector2[6];
    public Vector2 Center { get; private set; }
    public float Radius { get; private set; }
    public Kingdom BottomKingdom { get; private set; } = Kingdom.Wei;
    public float CellSize => Radius / 8f;
    public Vector2 Point(BoardCellId cell) => _points[cell.Value];
    public Vector2 Vertex(int i) => Outline[(i + 6) % 6];
    /// <summary>Changes display orientation only; logical cell IDs and topology remain unchanged.</summary>
    public void SetBottomKingdom(Kingdom kingdom)
    {
        if (!Enum.IsDefined(kingdom)) throw new ArgumentOutOfRangeException(nameof(kingdom));
        if (BottomKingdom == kingdom) return;
        BottomKingdom = kingdom;
        _size = new(-1, -1);
    }
    public void Resize(Vector2 size)
    {
        if (_size == size) return;
        _size = size;
        Center = size * 0.5f;
        // Reserve room for pieces centered on the six outline vertices and the carved outer frame.
        Radius = Math.Max(1, Math.Min((size.X - 24) / 2.1f, (size.Y - 34) / (MathF.Sqrt(3) + 0.105f)));
        var h = MathF.Sqrt(3) / 2;
        Vector2[] v = [new(-0.5f, -h), new(0.5f, -h), new(1, 0), new(0.5f, h), new(-0.5f, h), new(-1, 0)];
        for (var i = 0; i < 6; i++) Outline[i] = Center + v[i] * Radius;
        for (var k = 0; k < 3; k++)
        {
            var a = Vertex(2 * k); var b = Vertex(2 * k + 1); var m = (a + b) / 2;
            var l = (Vertex(2 * k - 1) + a) / 2; var r = (b + Vertex(2 * k + 2)) / 2;
            for (var f = 0; f < 9; f++)
            for (var rank = 0; rank < 5; rank++)
            {
                var s = (f <= 4 ? f : f - 4) / 4f;
                var home = f <= 4 ? a.Lerp(m, s) : m.Lerp(b, s);
                var front = f <= 4 ? l.Lerp(Center, s) : Center.Lerp(r, s);
                _points[45 * k + 9 * rank + f] = home.Lerp(front, rank / 5f);
            }
        }
        var rotation = BottomKingdom switch
        {
            Kingdom.Wei => Mathf.Pi,
            Kingdom.Wu => Mathf.Pi / 3f,
            Kingdom.Shu => -Mathf.Pi / 3f,
            _ => 0
        };
        for (var i = 0; i < Outline.Length; i++) Outline[i] = Center + (Outline[i] - Center).Rotated(rotation);
        for (var i = 0; i < _points.Length; i++) _points[i] = Center + (_points[i] - Center).Rotated(rotation);
    }
    public BoardCellId? Hit(Vector2 position)
    {
        var best = CellSize * CellSize * 0.49f * 0.49f; BoardCellId? result = null;
        for (var i = 0; i < _points.Length; i++)
        {
            var d = position.DistanceSquaredTo(_points[i]);
            if (d <= best) { best = d; result = new(i); }
        }
        return result;
    }
}
