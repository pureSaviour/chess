using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

public enum PresentationCueKind
{
    Move,
    Capture,
    Check,
    Checkmate,
    Stalemate,
    IllegalMove,
    Undo,
    NewGame,
    SpecialPattern,
    Timeout,
    Draw
}

/// <summary>
/// Semantic output from the application layer. Presentation decides whether a cue becomes
/// particles, sound, camera motion or a full-screen title. This keeps effects replaceable
/// without embedding scene-node knowledge into rules.
/// </summary>
public sealed record PresentationCue(
    PresentationCueKind Kind,
    ChessMove? Move = null,
    ChessPiece? Subject = null,
    string? Message = null,
    string? CueId = null);

/// <summary>
/// Optional recognizer for named formations such as a future "Double Chariot Mate" effect.
/// Detectors observe completed positions and emit semantics; they never change legality.
/// </summary>
public interface IPositionPatternDetector
{
    IEnumerable<PresentationCue> Detect(GameSnapshot before, GameSnapshot after, MoveResult move);
}
