using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

/// <summary>
/// UI-facing match facade. It exposes immutable snapshots and intention-level commands only.
/// The same facade can be driven by local input, a replay, or validated network messages.
/// </summary>
public sealed class GameSession
{
    private readonly IReadOnlyList<IPositionPatternDetector> _patternDetectors;
    private ChessGame _game;
    private GameSnapshot _initialPosition;
    private readonly List<ChessMove> _moves = [];

    public GameSession(IEnumerable<IPositionPatternDetector>? patternDetectors = null)
    {
        _patternDetectors = patternDetectors?.ToArray() ?? [];
        _game = ChessGame.CreateStandard();
        _initialPosition = _game.Snapshot;
    }

    /// <summary>Creates a match from a verified FEN/puzzle position without bypassing ChessGame.</summary>
    public GameSession(GameSnapshot initialPosition, IEnumerable<IPositionPatternDetector>? patternDetectors = null)
    {
        _patternDetectors = patternDetectors?.ToArray() ?? [];
        _game = ChessGame.FromPosition(initialPosition);
        _initialPosition = _game.Snapshot;
    }

    public event Action<GameSnapshot>? StateChanged;
    public event Action<PresentationCue>? CueRaised;

    public GameSnapshot Snapshot => _game.Snapshot;
    public bool CanUndo => _game.CanUndo;
    public ClassicGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) => new(
        _initialPosition, Array.AsReadOnly(_moves.ToArray()),
        tags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    public ClassicTrialSession BeginTrial() => new(Snapshot);

    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from) =>
        _game.GetLegalDestinations(from);

    public MoveResult TryMove(BoardPosition from, BoardPosition to)
    {
        var before = _game.Snapshot;
        var result = _game.TryMove(new ChessMove(from, to));
        if (!result.IsSuccess)
        {
            CueRaised?.Invoke(new PresentationCue(
                PresentationCueKind.IllegalMove, result.Move, Message: GetRejectionText(result.Rejection)));
            return result;
        }

        var after = _game.Snapshot;
        _moves.Add(result.Move);
        foreach (var cue in DescribeMove(before, after, result)) CueRaised?.Invoke(cue);

        StateChanged?.Invoke(after);
        return result;
    }

    /// <summary>
    /// Produces the same ordered, presentation-neutral move/capture, terminal/check and registered
    /// pattern cues used by the authoritative session. Trial/replay UI may consume these cues for
    /// feedback, but they never mutate or submit either position. Inputs must describe one already
    /// validated successful move; invalid inputs are rejected.
    /// </summary>
    public IReadOnlyList<PresentationCue> DescribeMove(GameSnapshot before, GameSnapshot after, MoveResult result)
    {
        if (!result.IsSuccess) throw new ArgumentException("只能描述已通过棋规校验的着法。", nameof(result));
        var cues = new List<PresentationCue>
        {
            new(result.CapturedPiece is null ? PresentationCueKind.Move : PresentationCueKind.Capture,
                result.Move, result.MovedPiece)
        };
        if (after.IsFinished) cues.Add(new(after.EndReason == GameEndReason.Stalemate
            ? PresentationCueKind.Stalemate : PresentationCueKind.Checkmate, result.Move, result.MovedPiece));
        else if (after.IsInCheck) cues.Add(new(PresentationCueKind.Check, result.Move, result.MovedPiece));
        foreach (var detector in _patternDetectors) cues.AddRange(detector.Detect(before, after, result));
        return cues.AsReadOnly();
    }

    public bool Undo()
    {
        if (!_game.Undo()) return false;
        if (_moves.Count > 0) _moves.RemoveAt(_moves.Count - 1);
        CueRaised?.Invoke(new PresentationCue(PresentationCueKind.Undo));
        StateChanged?.Invoke(_game.Snapshot);
        return true;
    }

    public void NewGame()
    {
        _game = ChessGame.CreateStandard();
        _initialPosition = _game.Snapshot;
        _moves.Clear();
        CueRaised?.Invoke(new PresentationCue(PresentationCueKind.NewGame));
        StateChanged?.Invoke(_game.Snapshot);
    }

    public bool DeclareTimeout(PieceSide loser)
    {
        if (!_game.DeclareLoss(loser, GameEndReason.Timeout)) return false;
        CueRaised?.Invoke(new PresentationCue(PresentationCueKind.Timeout,
            Message: loser == PieceSide.Red ? "红方超时" : "黑方超时"));
        StateChanged?.Invoke(_game.Snapshot);
        return true;
    }

    /// <summary>Ends the match because the authorized player conceded; no move is appended.</summary>
    public bool DeclareResignation(PieceSide loser)
    {
        if (!_game.DeclareLoss(loser, GameEndReason.Resignation)) return false;
        StateChanged?.Invoke(_game.Snapshot);
        return true;
    }

    public bool DeclareDraw()
    {
        if (!_game.DeclareDraw()) return false;
        CueRaised?.Invoke(new PresentationCue(PresentationCueKind.Draw, Message: "双方同意和棋"));
        StateChanged?.Invoke(_game.Snapshot);
        return true;
    }

    private static string GetRejectionText(MoveRejection rejection) => rejection switch
    {
        MoveRejection.GameAlreadyFinished => "对局已经结束",
        MoveRejection.NotPlayersTurn => "还没有轮到这一方",
        MoveRejection.OccupiedByFriendlyPiece => "目标位置已有己方棋子",
        MoveRejection.PieceRuleViolation => "该棋子不能这样走",
        MoveRejection.WouldLeaveGeneralInCheck => "这步会使己方将帅受攻击",
        _ => "无效着法"
    };
}
