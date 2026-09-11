namespace Xiangqi.Core.Domain;

public readonly record struct ChessMove(BoardPosition From, BoardPosition To);

public enum MoveRejection
{
    None,
    GameAlreadyFinished,
    OutsideBoard,
    NoPieceAtSource,
    NotPlayersTurn,
    OccupiedByFriendlyPiece,
    PieceRuleViolation,
    WouldLeaveGeneralInCheck
}

public readonly record struct MoveResult(
    bool IsSuccess,
    ChessMove Move,
    ChessPiece? MovedPiece,
    ChessPiece? CapturedPiece,
    MoveRejection Rejection)
{
    public static MoveResult Rejected(ChessMove move, MoveRejection rejection) =>
        new(false, move, null, null, rejection);
}

public enum GameEndReason { None, Checkmate, Stalemate, GeneralCaptured, Timeout, Draw, Resignation }

public sealed record GameSnapshot(
    IReadOnlyList<PlacedPiece> Pieces,
    PieceSide SideToMove,
    bool IsInCheck,
    bool IsFinished,
    PieceSide? Winner,
    GameEndReason EndReason,
    int PlyNumber);
