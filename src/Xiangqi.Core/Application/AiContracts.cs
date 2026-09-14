using System.Globalization;
using System.Text;
using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

/// <summary>
/// Engine-independent configuration for one human-versus-AI match. The executable path is
/// consumed only by an infrastructure adapter; Core never starts a process or references Godot.
/// </summary>
public readonly record struct AiMatchConfiguration(
    TimeControl TimeControl,
    PieceSide HumanSide,
    int MoveTimeMilliseconds,
    string EnginePath,
    int MaxDepth = 0,
    int VariationCount = 1,
    int CandidateRank = 1,
    int MaximumCentipawnLoss = 0,
    bool UseDeterministicLegalMove = false)
{
    public PieceSide AiSide => HumanSide.Opponent();

    public AiMatchConfiguration Validate()
    {
        TimeControl.Validate();
        if (MoveTimeMilliseconds is < 100 or > 120_000)
            throw new ArgumentOutOfRangeException(nameof(MoveTimeMilliseconds), "AI 思考时间必须在 0.1 到 120 秒之间。");
        if (MaxDepth is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (VariationCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(VariationCount));
        if (CandidateRank is < 1 or > 8 || CandidateRank > VariationCount)
            throw new ArgumentOutOfRangeException(nameof(CandidateRank));
        if (MaximumCentipawnLoss is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumCentipawnLoss));
        if (string.IsNullOrWhiteSpace(EnginePath))
            throw new ArgumentException("请选择 AI 引擎程序。", nameof(EnginePath));
        return this with { EnginePath = EnginePath.Trim() };
    }
}

public readonly record struct AiSearchRequest(int MoveTimeMilliseconds, int VariationCount = 1, int MaxDepth = 0)
{
    public AiSearchRequest Validate() => MoveTimeMilliseconds is < 100 or > 120_000
        ? throw new ArgumentOutOfRangeException(nameof(MoveTimeMilliseconds))
        : VariationCount is < 1 or > 8
            ? throw new ArgumentOutOfRangeException(nameof(VariationCount))
            : MaxDepth is < 0 or > 128
                ? throw new ArgumentOutOfRangeException(nameof(MaxDepth))
                : this;
}

public readonly record struct AiVariation(
    int Rank,
    ChessMove Move,
    int Depth,
    int? CentipawnScore,
    int? MateIn,
    IReadOnlyList<ChessMove> PrincipalVariation);

public readonly record struct AiSearchResult(ChessMove BestMove, IReadOnlyList<AiVariation> Variations);

public readonly record struct ReplayAnalysisPoint(int PositionIndex, int RedCentipawns,
    ChessMove BestMove, int Depth, bool IsMate);

/// <summary>
/// Serializes replay analysis against one engine. Selecting a position cancels the prior sweep,
/// analyzes that position first, then fills remaining uncached positions. Scores are normalized
/// to Red's perspective; stale completions never publish after a newer selection. This class owns
/// the engine and disposes it exactly once, but owns no UI nodes or authoritative game session.
/// </summary>
public sealed class ReplayAnalysisCoordinator : IAsyncDisposable
{
    private readonly IXiangqiAiEngine _engine;
    private readonly IReadOnlyList<GameSnapshot> _positions;
    private readonly Dictionary<int, ReplayAnalysisPoint> _cache = [];
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;
    public event Action<ReplayAnalysisPoint>? AnalysisAvailable;
    public IReadOnlyDictionary<int, ReplayAnalysisPoint> Cache => _cache;

    public ReplayAnalysisCoordinator(IXiangqiAiEngine engine, IReadOnlyList<GameSnapshot> positions)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        if (positions.Count == 0) throw new ArgumentException("Replay has no positions.", nameof(positions));
    }

    public async Task SelectAndSweepAsync(int selectedIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (selectedIndex < 0 || selectedIndex >= _positions.Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        _active?.Cancel(); _active?.Dispose();
        var operation = _active = new CancellationTokenSource();
        var generation = ++_generation;
        try
        {
            await _engine.InitializeAsync(operation.Token);
            foreach (var index in Enumerable.Range(0, _positions.Count).OrderBy(i => i == selectedIndex ? -1 : Math.Abs(i - selectedIndex)))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (_cache.ContainsKey(index) || _positions[index].IsFinished) continue;
                var result = await _engine.SearchAsync(_positions[index],
                    new AiSearchRequest(AiDifficultyProfiles.ReplayThinkMilliseconds, 1, 0), operation.Token);
                if (generation != _generation) return;
                var verifier = ChessGame.FromPosition(_positions[index]);
                var verified = verifier.TryMove(result.BestMove);
                if (!verified.IsSuccess)
                    throw new InvalidDataException($"AI returned an illegal replay suggestion: {verified.Rejection}.");
                var variation = result.Variations.FirstOrDefault();
                var raw = variation.MateIn is { } mate ? Math.Sign(mate) * 30_000 : variation.CentipawnScore ?? 0;
                var redScore = _positions[index].SideToMove == PieceSide.Red ? raw : -raw;
                var point = new ReplayAnalysisPoint(index, redScore, result.BestMove, variation.Depth, variation.MateIn is not null);
                _cache[index] = point;
                AnalysisAvailable?.Invoke(point);
                await Task.Delay(AiDifficultyProfiles.ReplaySweepYieldMilliseconds, operation.Token);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        _active?.Cancel(); _active?.Dispose(); _active = null;
        await _engine.DisposeAsync();
    }
}

/// <summary>
/// Port implemented by local UCI processes today and potentially remote/model adapters later.
/// Implementations must never mutate the supplied snapshot and must be safely disposable.
/// </summary>
public interface IXiangqiAiEngine : IAsyncDisposable
{
    string DisplayName { get; }
    bool IsReady { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask NewGameAsync(CancellationToken cancellationToken = default);
    ValueTask<AiSearchResult> SearchAsync(
        GameSnapshot position,
        AiSearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts immutable domain snapshots to the notation understood by UCI xiangqi engines.
/// Domain rank 0 is Red's home rank, matching UCI coordinates such as a0; FEN rows are emitted
/// from Black's home rank (rank 9) down to Red's home rank.
/// </summary>
public static class XiangqiUciNotation
{
    private static readonly IReadOnlyDictionary<PieceKind, char> PieceLetters =
        new Dictionary<PieceKind, char>
        {
            [BuiltInPieceKinds.General] = 'k',
            [BuiltInPieceKinds.Advisor] = 'a',
            [BuiltInPieceKinds.Elephant] = 'b',
            [BuiltInPieceKinds.Horse] = 'n',
            [BuiltInPieceKinds.Chariot] = 'r',
            [BuiltInPieceKinds.Cannon] = 'c',
            [BuiltInPieceKinds.Soldier] = 'p'
        };

    public static string ToFen(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var occupied = snapshot.Pieces.ToDictionary(item => item.Position, item => item.Piece);
        var result = new StringBuilder(96);
        for (var rank = BoardState.RankCount - 1; rank >= 0; rank--)
        {
            var empty = 0;
            for (var file = 0; file < BoardState.FileCount; file++)
            {
                if (!occupied.TryGetValue(new BoardPosition(file, rank), out var piece))
                {
                    empty++;
                    continue;
                }
                if (empty > 0)
                {
                    result.Append(empty.ToString(CultureInfo.InvariantCulture));
                    empty = 0;
                }
                if (!PieceLetters.TryGetValue(piece.Kind, out var letter))
                    throw new NotSupportedException($"AI 引擎尚不支持棋种 '{piece.Kind.Value}'。");
                result.Append(piece.Side == PieceSide.Red ? char.ToUpperInvariant(letter) : letter);
            }
            if (empty > 0) result.Append(empty.ToString(CultureInfo.InvariantCulture));
            if (rank > 0) result.Append('/');
        }
        var side = snapshot.SideToMove == PieceSide.Red ? 'w' : 'b';
        var fullMove = snapshot.PlyNumber / 2 + 1;
        return $"{result} {side} - - 0 {fullMove}";
    }

    public static string FormatMove(ChessMove move) =>
        $"{FormatSquare(move.From)}{FormatSquare(move.To)}";

    public static bool TryParseMove(string? value, out ChessMove move)
    {
        move = default;
        if (value is null || value.Length != 4) return false;
        if (!TryParseSquare(value.AsSpan(0, 2), out var from) ||
            !TryParseSquare(value.AsSpan(2, 2), out var to)) return false;
        move = new ChessMove(from, to);
        return true;
    }

    /// <summary>
    /// Parses the board and active-color fields of a Xiangqi FEN position. Castling, en-passant
    /// and counters are accepted for interoperability but have no Xiangqi rule meaning. The
    /// returned position is non-terminal and must still pass domain validation before use.
    /// </summary>
    public static bool TryParseFen(string? value, out GameSnapshot snapshot, out string? error)
    {
        snapshot = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            error = "FEN 为空或过长。";
            return false;
        }
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[1] is not ("w" or "b"))
        {
            error = "FEN 必须包含棋盘和行棋方。";
            return false;
        }
        var ranks = fields[0].Split('/');
        if (ranks.Length != BoardState.RankCount)
        {
            error = "FEN 必须包含 10 行。";
            return false;
        }
        var reverseKinds = PieceLetters.ToDictionary(pair => pair.Value, pair => pair.Key);
        var pieces = new List<PlacedPiece>(32);
        var nextId = 0;
        for (var fenRank = 0; fenRank < ranks.Length; fenRank++)
        {
            var file = 0;
            foreach (var symbol in ranks[fenRank])
            {
                if (symbol is >= '1' and <= '9')
                {
                    file += symbol - '0';
                    continue;
                }
                var lower = char.ToLowerInvariant(symbol);
                if (!reverseKinds.TryGetValue(lower, out var kind) || file >= BoardState.FileCount)
                {
                    error = $"FEN 第 {fenRank + 1} 行包含无效棋子或列数。";
                    return false;
                }
                var side = char.IsUpper(symbol) ? PieceSide.Red : PieceSide.Black;
                pieces.Add(new PlacedPiece(new BoardPosition(file++, BoardState.RankCount - 1 - fenRank),
                    new ChessPiece(nextId++, side, kind)));
            }
            if (file != BoardState.FileCount)
            {
                error = $"FEN 第 {fenRank + 1} 行不是 9 列。";
                return false;
            }
        }
        var sideToMove = fields[1] == "w" ? PieceSide.Red : PieceSide.Black;
        var ply = 0;
        if (fields.Length >= 6 && int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var fullMove) && fullMove > 0)
            ply = (fullMove - 1) * 2 + (sideToMove == PieceSide.Black ? 1 : 0);
        snapshot = new GameSnapshot(Array.AsReadOnly(pieces.ToArray()), sideToMove, false, false, null, GameEndReason.None, ply);
        try { _ = ChessGame.FromPosition(snapshot); }
        catch (ArgumentException exception) { error = exception.Message; snapshot = null!; return false; }
        return true;
    }

    private static string FormatSquare(BoardPosition position)
    {
        if (!position.IsInside) throw new ArgumentOutOfRangeException(nameof(position));
        return string.Create(2, position, static (chars, square) =>
        {
            chars[0] = (char)('a' + square.File);
            chars[1] = (char)('0' + square.Rank);
        });
    }

    private static bool TryParseSquare(ReadOnlySpan<char> value, out BoardPosition position)
    {
        position = default;
        if (value.Length != 2) return false;
        var fileChar = char.ToLowerInvariant(value[0]);
        if (fileChar is < 'a' or > 'i' || value[1] is < '0' or > '9') return false;
        position = new BoardPosition(fileChar - 'a', value[1] - '0');
        return true;
    }
}

/// <summary>
/// Coordinates an AI with the same GameSession used by local and network play. Engine output is
/// treated as untrusted input: it is applied only to the exact requested ply and is revalidated
/// by ChessGame before becoming authoritative.
/// </summary>
public sealed class AiGameCoordinator : IAsyncDisposable
{
    private readonly GameSession _session;
    private readonly IXiangqiAiEngine _engine;
    private readonly Stack<int> _humanMovePlies = [];
    private CancellationTokenSource? _activeOperation;
    private bool _disposed;

    public AiGameCoordinator(GameSession session, IXiangqiAiEngine engine, PieceSide humanSide)
    {
        _session = session;
        _engine = engine;
        HumanSide = humanSide;
    }

    public event Action<bool>? ThinkingChanged;
    public PieceSide HumanSide { get; }
    public PieceSide AiSide => HumanSide.Opponent();
    public bool IsThinking => _activeOperation is not null;
    public bool CanMakeHumanMove => !_disposed && !IsThinking && !_session.Snapshot.IsFinished &&
                                    _session.Snapshot.SideToMove == HumanSide;
    public bool CanUndoHumanTurn => !_disposed && !IsThinking && _humanMovePlies.Count > 0;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _engine.InitializeAsync(cancellationToken);

    public MoveResult SubmitHumanMove(BoardPosition from, BoardPosition to)
    {
        var move = new ChessMove(from, to);
        if (!CanMakeHumanMove) return MoveResult.Rejected(move, MoveRejection.NotPlayersTurn);
        var result = _session.TryMove(from, to);
        if (result.IsSuccess) _humanMovePlies.Push(_session.Snapshot.PlyNumber);
        return result;
    }

    public async ValueTask<MoveResult?> PlayAiTurnAsync(
        int moveTimeMilliseconds,
        CancellationToken cancellationToken = default)
        => await PlayAiTurnAsync(new AiSearchRequest(moveTimeMilliseconds), 1, 0, false, cancellationToken);

    public async ValueTask<MoveResult?> PlayAiTurnAsync(
        AiSearchRequest request,
        int candidateRank,
        int maximumCentipawnLoss,
        bool useDeterministicLegalMove = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || IsThinking || _session.Snapshot.IsFinished ||
            _session.Snapshot.SideToMove != AiSide) return null;
        var snapshot = _session.Snapshot;
        using var linked = BeginOperation(cancellationToken);
        try
        {
            request.Validate();
            if (candidateRank < 1 || candidateRank > request.VariationCount)
                throw new ArgumentOutOfRangeException(nameof(candidateRank));
            if (maximumCentipawnLoss < 0) throw new ArgumentOutOfRangeException(nameof(maximumCentipawnLoss));
            AiSearchResult? search = useDeterministicLegalMove
                ? null
                : await _engine.SearchAsync(snapshot, request, linked.Token);
            if (_session.Snapshot.PlyNumber != snapshot.PlyNumber ||
                _session.Snapshot.SideToMove != AiSide || _session.Snapshot.IsFinished) return null;
            var selected = search is null
                ? SelectDeterministicLegalMove(snapshot)
                : SelectVerifiedCandidate(snapshot, search.Value, candidateRank, maximumCentipawnLoss);
            var result = _session.TryMove(selected.From, selected.To);
            if (!result.IsSuccess)
                throw new InvalidOperationException(
                    $"AI 返回了未通过本地棋规校验的着法 {XiangqiUciNotation.FormatMove(selected)}：{result.Rejection}。");
            return result;
        }
        finally
        {
            EndOperation(linked);
        }
    }

    private static ChessMove SelectDeterministicLegalMove(GameSnapshot snapshot)
    {
        var verifier = ChessGame.FromPosition(snapshot);
        var legal = snapshot.Pieces.Where(piece => piece.Piece.Side == snapshot.SideToMove)
            .OrderBy(piece => piece.Position.Rank).ThenBy(piece => piece.Position.File)
            .SelectMany(piece => verifier.GetLegalDestinations(piece.Position)
                .OrderBy(point => point.Rank).ThenBy(point => point.File)
                .Select(point => new ChessMove(piece.Position, point)))
            .ToArray();
        if (legal.Length == 0) throw new InvalidDataException("当前局面没有可供入门 AI 选择的合法着法。");
        return legal[(snapshot.PlyNumber * 37 + 11) % legal.Length];
    }

    private static ChessMove SelectVerifiedCandidate(GameSnapshot snapshot, AiSearchResult search,
        int candidateRank, int maximumCentipawnLoss)
    {
        var legal = new List<AiVariation>();
        foreach (var candidate in search.Variations.OrderBy(value => value.Rank))
        {
            var verifier = ChessGame.FromPosition(snapshot);
            if (verifier.TryMove(candidate.Move).IsSuccess) legal.Add(candidate);
        }
        if (legal.Count == 0) throw new InvalidDataException("AI 没有返回任何合法候选着法。");
        var best = legal[0];
        var requested = legal.FirstOrDefault(value => value.Rank == candidateRank);
        if (requested.Equals(default(AiVariation))) return best.Move;
        if (candidateRank == 1)
            return requested.Move;
        if (best.MateIn is not null || requested.MateIn is not null ||
            best.CentipawnScore is null || requested.CentipawnScore is null)
            return best.Move;
        return best.CentipawnScore.Value - requested.CentipawnScore.Value <= maximumCentipawnLoss
            ? requested.Move : best.Move;
    }

    public async ValueTask<AiSearchResult> AnalyzeAsync(
        int moveTimeMilliseconds,
        int variationCount = 3,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AiGameCoordinator));
        if (IsThinking) throw new InvalidOperationException("AI 正在思考当前着法，请稍后再询问。");
        var snapshot = _session.Snapshot;
        using var linked = BeginOperation(cancellationToken);
        try
        {
            var result = await _engine.SearchAsync(snapshot,
                new AiSearchRequest(moveTimeMilliseconds, variationCount), linked.Token);
            if (_session.Snapshot.PlyNumber != snapshot.PlyNumber)
                throw new InvalidOperationException("分析期间局面已改变，请重新询问。");
            return result;
        }
        finally
        {
            EndOperation(linked);
        }
    }

    public bool UndoHumanTurn()
    {
        if (!CanUndoHumanTurn) return false;
        var humanMovePly = _humanMovePlies.Pop();
        var changed = false;
        while (_session.CanUndo && _session.Snapshot.PlyNumber >= humanMovePly)
            changed |= _session.Undo();
        return changed;
    }

    public async ValueTask NewGameAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveOperation();
        _humanMovePlies.Clear();
        _session.NewGame();
        await _engine.NewGameAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActiveOperation();
        await _engine.DisposeAsync();
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperation = linked;
        ThinkingChanged?.Invoke(true);
        return linked;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_activeOperation, operation)) return;
        _activeOperation = null;
        ThinkingChanged?.Invoke(false);
    }

    private void CancelActiveOperation()
    {
        var active = _activeOperation;
        _activeOperation = null;
        if (active is null) return;
        active.Cancel();
        ThinkingChanged?.Invoke(false);
    }
}
