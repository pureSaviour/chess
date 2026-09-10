using System.Collections.Concurrent;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Gomoku;

/// <summary>Engine-neutral request for one Gomoku move. Position is immutable and authoritative;
/// ThinkMilliseconds is a bounded per-move budget, not permission to mutate the match.</summary>
public readonly record struct GomokuAiSearchRequest(GomokuSnapshot Position, int ThinkMilliseconds);

/// <summary>Replaceable Gomoku analysis port. Implementations own their worker/process resources,
/// accept only one search at a time, honor cancellation, and return an untrusted candidate point.
/// The match controller always validates that point again through <see cref="GomokuGame"/>.</summary>
public interface IGomokuAiEngine : IAsyncDisposable
{
    string DisplayName { get; }
    bool IsReady { get; }
    ValueTask InitializeAsync(int boardSize, GomokuRuleSet rules, CancellationToken cancellationToken = default);
    ValueTask<GridPoint> FindMoveAsync(GomokuAiSearchRequest request, CancellationToken cancellationToken = default);
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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<AiCompletion> _completions = new();
    private bool _initialized;
    private bool _searching;
    private bool _disposed;

    public AiGomokuMatchController(IGomokuAiEngine engine, int boardSize, GomokuRuleSet rules,
        StoneColor humanColor, int thinkMilliseconds)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (!Enum.IsDefined(humanColor)) throw new ArgumentOutOfRangeException(nameof(humanColor));
        if (thinkMilliseconds is < 100 or > 120_000) throw new ArgumentOutOfRangeException(nameof(thinkMilliseconds));
        _game = new GomokuGame(boardSize, rules);
        _humanColor = humanColor;
        _thinkMilliseconds = thinkMilliseconds;
    }

    public event Action? Changed;
    public event Action<string>? Message;
    public GomokuSnapshot Snapshot => _game.Snapshot;
    public StoneColor? LocalColor => _humanColor;
    public bool IsReady => _initialized && _engine.IsReady;
    public bool IsConnected => true;
    public bool CanPlace => IsReady && !_searching && !_game.IsFinished && _game.SideToMove == _humanColor;

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
            var point = await _engine.FindMoveAsync(new(snapshot, _thinkMilliseconds), _lifetime.Token);
            _completions.Enqueue(new(snapshot.PlyNumber, point, null));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _completions.Enqueue(new(snapshot.PlyNumber, default, error.Message)); }
    }

    private readonly record struct AiCompletion(long ExpectedPly, GridPoint Point, string? Error);
}
