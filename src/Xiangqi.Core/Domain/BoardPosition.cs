namespace Xiangqi.Core.Domain;

/// <summary>
/// A board coordinate from Red's point of view. File is 0..8 from left to right;
/// rank is 0..9 from Red's home rank toward Black's home rank.
/// Keeping one canonical coordinate system prevents UI orientation from leaking into rules.
/// </summary>
public readonly record struct BoardPosition(int File, int Rank)
{
    public bool IsInside => File is >= 0 and < BoardState.FileCount
                            && Rank is >= 0 and < BoardState.RankCount;

    public BoardPosition Offset(int fileDelta, int rankDelta) =>
        new(File + fileDelta, Rank + rankDelta);
}

