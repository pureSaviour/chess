using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

/// <summary>
/// Recognizes the strict, mechanically verifiable core of a Double Chariot Mate: on a
/// checkmating position, one winning chariot attacks the losing general and another winning
/// chariot protects it along an unobstructed rank or file. More nuanced notation conventions
/// can be introduced as separate detectors without changing the effect player.
/// </summary>
public sealed class DoubleChariotMateDetector : IPositionPatternDetector
{
    public const string CueId = "double_chariot_mate";

    public IEnumerable<PresentationCue> Detect(GameSnapshot before, GameSnapshot after, MoveResult move)
    {
        if (!after.IsFinished || after.EndReason != GameEndReason.Checkmate || after.Winner is null)
            yield break;

        var loser = after.Winner.Value.Opponent();
        var general = after.Pieces.FirstOrDefault(item =>
            item.Piece.Side == loser && item.Piece.Kind == BuiltInPieceKinds.General);
        var chariots = after.Pieces.Where(item =>
            item.Piece.Side == after.Winner && item.Piece.Kind == BuiltInPieceKinds.Chariot).ToArray();

        foreach (var checking in chariots.Where(chariot => Attacks(after, chariot.Position, general.Position)))
        foreach (var supporting in chariots.Where(chariot => chariot.Piece != checking.Piece))
        {
            if (!Attacks(after, supporting.Position, checking.Position)) continue;
            yield return new PresentationCue(
                PresentationCueKind.SpecialPattern,
                move.Move,
                checking.Piece,
                "双车错",
                CueId);
            yield break;
        }
    }

    private static bool Attacks(GameSnapshot snapshot, BoardPosition from, BoardPosition to)
    {
        if (from == to || (from.File != to.File && from.Rank != to.Rank)) return false;
        return !snapshot.Pieces.Any(item => item.Position != from && item.Position != to &&
            IsStrictlyBetween(item.Position, from, to));
    }

    private static bool IsStrictlyBetween(BoardPosition value, BoardPosition from, BoardPosition to) =>
        from.File == to.File
            ? value.File == from.File && value.Rank > Math.Min(from.Rank, to.Rank) &&
              value.Rank < Math.Max(from.Rank, to.Rank)
            : value.Rank == from.Rank && value.File > Math.Min(from.File, to.File) &&
              value.File < Math.Max(from.File, to.File);
}
