namespace Xiangqi.Core.Domain.Rules;

internal static class RuleGeometry
{
    public static bool IsInsidePalace(PieceSide side, BoardPosition position) =>
        position.File is >= 3 and <= 5 &&
        (side == PieceSide.Red ? position.Rank is >= 0 and <= 2 : position.Rank is >= 7 and <= 9);
}

public sealed class GeneralRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.General;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to)
    {
        var target = board.GetPiece(to);
        if (from.File == to.File && target is { Kind: var kind } &&
            kind == BuiltInPieceKinds.General && target.Value.Side != piece.Side)
        {
            return board.CountPiecesBetweenStraight(from, to) == 0;
        }

        var distance = Math.Abs(to.File - from.File) + Math.Abs(to.Rank - from.Rank);
        return distance == 1 && RuleGeometry.IsInsidePalace(piece.Side, to);
    }
}

public sealed class AdvisorRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Advisor;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to) =>
        Math.Abs(to.File - from.File) == 1 && Math.Abs(to.Rank - from.Rank) == 1 &&
        RuleGeometry.IsInsidePalace(piece.Side, to);
}

public sealed class ElephantRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Elephant;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to)
    {
        if (Math.Abs(to.File - from.File) != 2 || Math.Abs(to.Rank - from.Rank) != 2)
            return false;

        var remainsOnOwnSide = piece.Side == PieceSide.Red ? to.Rank <= 4 : to.Rank >= 5;
        var eye = new BoardPosition((from.File + to.File) / 2, (from.Rank + to.Rank) / 2);
        return remainsOnOwnSide && board.GetPiece(eye) is null;
    }
}

public sealed class HorseRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Horse;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to)
    {
        var fileDelta = to.File - from.File;
        var rankDelta = to.Rank - from.Rank;
        var isHorseShape = Math.Abs(fileDelta) * Math.Abs(rankDelta) == 2;
        if (!isHorseShape) return false;

        var leg = Math.Abs(fileDelta) == 2
            ? from.Offset(Math.Sign(fileDelta), 0)
            : from.Offset(0, Math.Sign(rankDelta));
        return board.GetPiece(leg) is null;
    }
}

public sealed class ChariotRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Chariot;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to) =>
        from != to && (from.File == to.File || from.Rank == to.Rank) &&
        board.CountPiecesBetweenStraight(from, to) == 0;
}

public sealed class CannonRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Cannon;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to)
    {
        if (from == to || (from.File != to.File && from.Rank != to.Rank)) return false;
        var screens = board.CountPiecesBetweenStraight(from, to);
        return board.GetPiece(to) is null ? screens == 0 : screens == 1;
    }
}

public sealed class SoldierRule : IPieceRule
{
    public PieceKind Kind => BuiltInPieceKinds.Soldier;

    public bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to)
    {
        var fileDelta = to.File - from.File;
        var rankDelta = to.Rank - from.Rank;
        if (fileDelta == 0 && rankDelta == piece.Side.ForwardDirection()) return true;

        var crossedRiver = piece.Side == PieceSide.Red ? from.Rank >= 5 : from.Rank <= 4;
        return crossedRiver && rankDelta == 0 && Math.Abs(fileDelta) == 1;
    }
}
