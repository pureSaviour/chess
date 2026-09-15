using System.Collections.Concurrent;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Gomoku;

/// <summary>Engine-neutral request for one Gomoku move or evaluation. Position is immutable and
/// authoritative; all resource/search values are bounded and never grant permission to mutate it.</summary>
public readonly record struct GomokuAiSearchRequest(
    GomokuSnapshot Position,
    int ThinkMilliseconds,
    int MaxDepth = 0,
    int Strength = 100,
    int Threads = 1,
    int MemoryMegabytes = 256)
{
    public GomokuAiSearchRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Position);
        if (ThinkMilliseconds is < 100 or > 120_000) throw new ArgumentOutOfRangeException(nameof(ThinkMilliseconds));
        if (MaxDepth is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (Strength is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Strength));
        if (Threads is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(Threads));
        if (MemoryMegabytes is < 16 or > 4096) throw new ArgumentOutOfRangeException(nameof(MemoryMegabytes));
        return this;
    }
}

/// <summary>Black-perspective evaluation plus the searched best move. BestMove is null only for
/// terminal positions synthesized by replay orchestration; engine results for live positions require it.</summary>
public readonly record struct GomokuAnalysisResult(float BlackWinRate, int Evaluation, int Depth,
    GridPoint? BestMove)
{
    public GomokuAnalysisResult Validate() => !float.IsFinite(BlackWinRate) || BlackWinRate is < 0 or > 1
        ? throw new ArgumentOutOfRangeException(nameof(BlackWinRate)) : this;
}

public readonly record struct GomokuReplayAnalysisPoint(int PositionIndex, float BlackWinRate,
    int Evaluation, int Depth, GridPoint? BestMove);

/// <summary>Replaceable Gomoku search and analysis port. Implementations own their worker/process
/// resources, accept only one search at a time, honor cancellation, and return untrusted moves.
/// Match and replay coordinators validate every returned point through <see cref="GomokuGame"/>.</summary>
public interface IGomokuAiEngine : IAsyncDisposable
{
    string DisplayName { get; }
    bool IsReady { get; }
    ValueTask InitializeAsync(int boardSize, GomokuRuleSet rules, CancellationToken cancellationToken = default);
    ValueTask<GridPoint> FindMoveAsync(GomokuAiSearchRequest request, CancellationToken cancellationToken = default);
    ValueTask<GomokuAnalysisResult> AnalyzeAsync(GomokuAiSearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Serial replay analyzer. The coordinator owns the engine, prioritizes the selected
/// position, caches black-perspective win rates, and suppresses stale results after cancellation.
/// The caller may inject a validated replay wall-clock budget; all other replay strength and
/// resource parameters remain fixed application policy.</summary>
public sealed class GomokuReplayAnalysisCoordinator : IAsyncDisposable
{
    private readonly IGomokuAiEngine _engine;
    private readonly IReadOnlyList<GomokuSnapshot> _positions;
    private readonly GomokuAiSearchRequest _template;
    private readonly Dictionary<int, GomokuReplayAnalysisPoint> _cache = [];
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;

    public GomokuReplayAnalysisCoordinator(IGomokuAiEngine engine,
        IReadOnlyList<GomokuSnapshot> positions,
        int thinkMilliseconds = AiDifficultyProfiles.ReplayThinkMilliseconds)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        if (positions.Count == 0) throw new ArgumentException("Replay has no positions.", nameof(positions));
        _template = new GomokuAiSearchRequest(positions[0], thinkMilliseconds,
            0, 100, AiDifficultyProfiles.ReplayThreads, AiDifficultyProfiles.ReplayMemoryMegabytes).Validate();
    }

    public event Action<GomokuReplayAnalysisPoint>? AnalysisAvailable;
    public IReadOnlyDictionary<int, GomokuReplayAnalysisPoint> Cache => _cache;

    public async Task SelectAndSweepAsync(int selectedIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (selectedIndex < 0 || selectedIndex >= _positions.Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        _active?.Cancel(); _active?.Dispose();
        var operation = _active = new CancellationTokenSource();
        var generation = ++_generation;
        try
        {
            var first = _positions[0];
            await _engine.InitializeAsync(first.BoardSize, first.Rules, operation.Token);
            foreach (var index in Enumerable.Range(0, _positions.Count)
                         .OrderBy(i => i == selectedIndex ? -1 : Math.Abs(i - selectedIndex)))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (_cache.ContainsKey(index)) continue;
                var position = _positions[index];
                GomokuAnalysisResult analysis;
                if (position.IsFinished)
                    analysis = new(position.Winner == StoneColor.Black ? 1f : position.Winner == StoneColor.White ? 0f : .5f,
                        position.Winner == StoneColor.Black ? 30_000 : position.Winner == StoneColor.White ? -30_000 : 0, 0, null);
                else
                    analysis = (await _engine.AnalyzeAsync(_template with { Position = position }, operation.Token)).Validate();
                if (!position.IsFinished)
                {
                    if (analysis.BestMove is not { } bestMove)
                        throw new InvalidDataException("五子棋引擎未返回复盘局面的最佳着法。");
                    var verifier = new GomokuGame(position.BoardSize, position.Rules,
                        position.Stones, position.SideToMove);
                    var verified = verifier.TryPlace(bestMove, position.SideToMove);
                    if (!verified.IsSuccess)
                        throw new InvalidDataException($"五子棋引擎返回非法复盘着法：{verified.Rejection}。");
                }
                if (generation != _generation) return;
                var point = new GomokuReplayAnalysisPoint(index, analysis.BlackWinRate, analysis.Evaluation,
                    analysis.Depth, analysis.BestMove);
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

/// <summary>Human-versus-engine application coordinator. It is the sole owner of the authoritative
/// game and engine. Background completions are queued; <see cref="Poll"/> must be called by the UI
/// thread to apply results and raise events. Stale, occupied, out-of-range, forbidden, or wrong-turn
/// engine moves fail closed without changing the position. Disposal cancels work and is idempotent.</summary>
public sealed class AiGomokuMatchController : IGomokuMatchController
{
    private readonly GomokuGame _game;
    private readonly IGomokuAiEngine _engine;
    private readonly StoneColor _humanColor;
    private readonly int _thinkMilliseconds;
    private readonly int _maxDepth;
    private readonly int _strength;
    private readonly int _threads;
    private readonly int _memoryMegabytes;
    private readonly bool _useDeterministicLegalMove;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<AiCompletion> _completions = new();
    private bool _initialized;
    private bool _searching;
    private bool _disposed;
    private readonly TwoSeatMatchClock _clock;

    public AiGomokuMatchController(IGomokuAiEngine engine, int boardSize, GomokuRuleSet rules,
        StoneColor humanColor, int thinkMilliseconds, int maxDepth = 0, int strength = 100,
        int threads = 1, int memoryMegabytes = 256, bool useDeterministicLegalMove = false,
        TimeControl? timeControl = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (!Enum.IsDefined(humanColor)) throw new ArgumentOutOfRangeException(nameof(humanColor));
        if (thinkMilliseconds is < 100 or > 120_000) throw new ArgumentOutOfRangeException(nameof(thinkMilliseconds));
        _game = new GomokuGame(boardSize, rules);
        var search = new GomokuAiSearchRequest(_game.Snapshot, thinkMilliseconds, maxDepth,
            strength, threads, memoryMegabytes).Validate();
        _humanColor = humanColor;
        _thinkMilliseconds = search.ThinkMilliseconds;
        _maxDepth = search.MaxDepth;
        _strength = search.Strength;
        _threads = search.Threads;
        _memoryMegabytes = search.MemoryMegabytes;
        _useDeterministicLegalMove = useDeterministicLegalMove;
        TimeControl=(timeControl??TimeControl.Unlimited).Validate();_clock=new(TimeControl){IsRunning=true};
    }

    public event Action? Changed;
    public event Action<string>? Message;
    public GomokuSnapshot Snapshot => _game.Snapshot;
    public StoneColor? LocalColor => _humanColor;
    public bool IsReady => _initialized && _engine.IsReady;
    public bool IsConnected => true;
    public bool CanPlace => IsReady && !_searching && !_game.IsFinished && _game.SideToMove == _humanColor;
    public TimeControl TimeControl { get; }
    public SeatClockSnapshot Clock=>_clock.Snapshot;
    public void Tick(double seconds){var loser=_clock.Advance(seconds);if(loser is null)return;_clock.IsRunning=false;if(_game.DeclareTimeout((StoneColor)loser.Value)){_lifetime.Cancel();Changed?.Invoke();}}

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _engine.InitializeAsync(_game.Snapshot.BoardSize, _game.Rules, linked.Token);
        _initialized = true;
        if (_game.SideToMove != _humanColor) StartSearch();
    }

    public ValueTask<GomokuSubmitResult> SubmitAsync(GridPoint point, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanPlace) return ValueTask.FromResult(new GomokuSubmitResult(false, false,
            GomokuMoveRejection.NotPlayersTurn, "当前不能落子。"));
        var result = _game.TryPlace(point, _humanColor);
        if (!result.IsSuccess) return ValueTask.FromResult(new GomokuSubmitResult(false, false,
            result.Rejection, result.Rejection.ToString()));
        _clock.BeginTurn((int)_game.SideToMove);
        Changed?.Invoke();
        if (!_game.IsFinished) StartSearch();
        return ValueTask.FromResult(new GomokuSubmitResult(true, false, GomokuMoveRejection.None, null));
    }

    public void Poll()
    {
        if (_disposed) return;
        while (_completions.TryDequeue(out var completion))
        {
            _searching = false;
            if (completion.Error is not null) { Message?.Invoke($"{_engine.DisplayName} 思考失败：{completion.Error}"); continue; }
            var snapshot = _game.Snapshot;
            if (snapshot.PlyNumber != completion.ExpectedPly || snapshot.SideToMove == _humanColor)
            { Message?.Invoke("已丢弃过期的 AI 着法。"); continue; }
            var result = _game.TryPlace(completion.Point, snapshot.SideToMove);
            if (!result.IsSuccess) { Message?.Invoke($"AI 返回非法着法：{result.Rejection}。"); continue; }
            _clock.BeginTurn((int)_game.SideToMove);
            Changed?.Invoke();
        }
    }

    public GomokuGameRecord CreateRecord(IReadOnlyDictionary<string, string>? tags = null) =>
        new(_game.Snapshot.BoardSize, _game.Rules, [], StoneColor.Black, _game.History.ToArray(),
            tags ?? new Dictionary<string, string>());

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _engine.DisposeAsync();
        _lifetime.Dispose();
    }

    private void StartSearch()
    {
        if (_searching || _disposed || _game.IsFinished) return;
        _searching = true;
        var snapshot = _game.Snapshot;
        _ = SearchAsync(snapshot);
    }

    private async Task SearchAsync(GomokuSnapshot snapshot)
    {
        try
        {
            var point = _useDeterministicLegalMove
                ? SelectDeterministicLegalPoint(snapshot)
                : await _engine.FindMoveAsync(new(snapshot, _thinkMilliseconds, _maxDepth,
                    _strength, _threads, _memoryMegabytes), _lifetime.Token);
            _completions.Enqueue(new(snapshot.PlyNumber, point, null));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _completions.Enqueue(new(snapshot.PlyNumber, default, error.Message)); }
    }

    private static GridPoint SelectDeterministicLegalPoint(GomokuSnapshot snapshot)
    {
        var legal = new List<GridPoint>();
        for (var row = 0; row < snapshot.BoardSize; row++)
        for (var column = 0; column < snapshot.BoardSize; column++)
        {
            var point = new GridPoint(column, row);
            var verifier = new GomokuGame(snapshot.BoardSize, snapshot.Rules,
                snapshot.Stones, snapshot.SideToMove);
            if (verifier.TryPlace(point, snapshot.SideToMove).IsSuccess) legal.Add(point);
        }
        if (legal.Count == 0) throw new InvalidDataException("当前局面没有可供入门 AI 选择的合法落点。");
        return legal[(int)((snapshot.PlyNumber * 37 + 11) % legal.Count)];
    }

    private readonly record struct AiCompletion(long ExpectedPly, GridPoint Point, string? Error);
}
