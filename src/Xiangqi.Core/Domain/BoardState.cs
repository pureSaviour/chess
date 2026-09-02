namespace Xiangqi.Core.Domain;

/// <summary>
/// Read-only board contract consumed by piece rules. Custom rules receive enough spatial
/// information to decide movement, but cannot mutate game state or bypass turn validation.
/// </summary>
public interface IReadOnlyBoard
{
    ChessPiece? GetPiece(BoardPosition position);
    IEnumerable<PlacedPiece> GetPieces();
    int CountPiecesBetweenStraight(BoardPosition from, BoardPosition to);
}

public sealed class BoardState : IReadOnlyBoard
{
    public const int FileCount = 9;
    public const int RankCount = 10;
    private readonly Dictionary<BoardPosition, ChessPiece> _pieces;

    public BoardState(IEnumerable<PlacedPiece>? pieces = null)
    {
        _pieces = pieces?.ToDictionary(item => item.Position, item => item.Piece) ?? [];
    }

    public ChessPiece? GetPiece(BoardPosition position) =>
        _pieces.TryGetValue(position, out var piece) ? piece : null;

    public IEnumerable<PlacedPiece> GetPieces() =>
        _pieces.Select(pair => new PlacedPiece(pair.Key, pair.Value));

    public int CountPiecesBetweenStraight(BoardPosition from, BoardPosition to)
    {
        if (from.File != to.File && from.Rank != to.Rank)
            throw new ArgumentException("Positions must share a file or rank.");

        var fileStep = Math.Sign(to.File - from.File);
        var rankStep = Math.Sign(to.Rank - from.Rank);
        var cursor = from.Offset(fileStep, rankStep);
        var count = 0;
        while (cursor != to)
        {
            if (_pieces.ContainsKey(cursor)) count++;
            cursor = cursor.Offset(fileStep, rankStep);
        }
        return count;
    }

    internal void Add(BoardPosition position, ChessPiece piece)
    {
        if (!position.IsInside || !_pieces.TryAdd(position, piece))
            throw new ArgumentException($"Cannot place a piece at {position}.");
    }

    internal ChessPiece? Move(BoardPosition from, BoardPosition to)
    {
        var moving = _pieces[from];
        var captured = GetPiece(to);
        _pieces.Remove(from);
        _pieces[to] = moving;
        return captured;
    }

    internal BoardState Clone() => new(GetPieces());
}
