using System.Collections.Concurrent;
using Godot;

namespace Xiangqi.Presentation.AI;

/// <summary>Godot main-thread bridge for <see cref="EnginePackageInstaller"/>. Download callbacks
/// enqueue immutable notifications only; _Process raises UI-facing events on the scene thread.
/// The node owns cancellation for one install but does not own the shared installer.</summary>
public partial class EngineInstallDriver : Node
{
    private readonly EnginePackageInstaller _installer;
    private readonly ConcurrentQueue<InstallNotification> _notifications = new();
    private CancellationTokenSource? _operation;

    public EngineInstallDriver(EnginePackageInstaller installer) => _installer = installer;
    public event Action<EngineInstallProgress>? ProgressChanged;
    public event Action<EngineInstallResult>? Completed;
    public event Action<string>? Failed;
    public bool IsRunning => _operation is not null;

    public void Start(EnginePackageKind kind)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource();
        var progress = new Progress<EngineInstallProgress>(value => _notifications.Enqueue(new(value, null, null)));
        _ = Run(kind, progress, _operation.Token);
    }

    public override void _Process(double delta)
    {
        while (_notifications.TryDequeue(out var item))
        {
            if (item.Progress is { } progress) ProgressChanged?.Invoke(progress);
            else if (item.Result is { } result) { _operation?.Dispose(); _operation = null; Completed?.Invoke(result); }
            else if (item.Error is { } error) { _operation?.Dispose(); _operation = null; Failed?.Invoke(error); }
        }
    }

    public override void _ExitTree() { _operation?.Cancel(); _operation?.Dispose(); _operation = null; }

    private async Task Run(EnginePackageKind kind, IProgress<EngineInstallProgress> progress, CancellationToken cancellationToken)
    {
        try { _notifications.Enqueue(new(null, await _installer.InstallAsync(kind, progress, cancellationToken), null)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(null, null, error.Message)); }
    }

    private readonly record struct InstallNotification(EngineInstallProgress? Progress, EngineInstallResult? Result, string? Error);
}
