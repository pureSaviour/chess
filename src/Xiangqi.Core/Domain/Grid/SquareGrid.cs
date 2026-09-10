namespace Xiangqi.Core.Domain.Grid;

/// <summary>
/// Zero-based point on a square intersection grid. It carries no player, pixel, occupancy, or
/// game-rule semantics; consumers must validate it against the grid that owns their position.
/// </summary>
public readonly record struct GridPoint(int Column, int Row);

/// <summary>
/// Immutable geometry shared by intersection-board games. The grid validates a configurable
/// square size and enumerates bounded rays; it does not store stones, judge moves, project to UI,
/// or own resources. All members are synchronous and thread-safe.
/// </summary>
public sealed class SquareGrid
{
    public const int MinimumSize = 3;
    public const int MaximumSize = 25;

    public SquareGrid(int size)
    {
        if (size is < MinimumSize or > MaximumSize)
            throw new ArgumentOutOfRangeException(nameof(size), $"棋盘边长必须在 {MinimumSize}..{MaximumSize} 之间。");
        Size = size;
    }

    public int Size { get; }
    public bool Contains(GridPoint point) => point.Column >= 0 && point.Column < Size && point.Row >= 0 && point.Row < Size;

    public IEnumerable<GridPoint> Ray(GridPoint origin, int columnStep, int rowStep)
    {
        if (!Contains(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (columnStep == 0 && rowStep == 0) throw new ArgumentException("射线步长不能同时为零。");
        for (var point = new GridPoint(origin.Column + columnStep, origin.Row + rowStep);
             Contains(point); point = new GridPoint(point.Column + columnStep, point.Row + rowStep))
            yield return point;
    }
}
