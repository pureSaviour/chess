namespace Xiangqi.Core.Domain;

public enum PieceSide { Red, Black }

public static class PieceSideExtensions
{
    public static PieceSide Opponent(this PieceSide side) =>
        side == PieceSide.Red ? PieceSide.Black : PieceSide.Red;

    public static int ForwardDirection(this PieceSide side) =>
        side == PieceSide.Red ? 1 : -1;
}

/// <summary>
/// Stable, serializable piece identity. It is deliberately not an enum: future expansions
/// can introduce a kind without modifying the domain model or network protocol shape.
/// </summary>
public readonly record struct PieceKind(string Value)
{
    public override string ToString() => Value;
}

public static class BuiltInPieceKinds
{
    public static readonly PieceKind General = new("general");
    public static readonly PieceKind Advisor = new("advisor");
    public static readonly PieceKind Elephant = new("elephant");
    public static readonly PieceKind Horse = new("horse");
    public static readonly PieceKind Chariot = new("chariot");
    public static readonly PieceKind Cannon = new("cannon");
    public static readonly PieceKind Soldier = new("soldier");
}

/// <summary>
/// PieceInstanceId remains stable while a piece moves, allowing presentation and networking
/// layers to reference a piece without coupling to its current square.
/// </summary>
public readonly record struct ChessPiece(int PieceInstanceId, PieceSide Side, PieceKind Kind);

public readonly record struct PlacedPiece(BoardPosition Position, ChessPiece Piece);

