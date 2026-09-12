using System.Collections.Concurrent;
using Godot;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.GameModules;

/// <summary>
/// Main-thread bridge for package inspection and installation. Blocking ZIP and hashing work runs
/// on a worker; immutable results are raised from _Process. The node owns cancellation and never
/// loads package content into Godot or the CLR.
/// </summary>
public partial class GamePackageInstallDriver : Node
{
    private readonly GamePackageReader _reader;
    private readonly GamePackageInstaller _installer;
    private readonly string _installRoot;
    private readonly ConcurrentQueue<DriverNotification> _notifications = new();
    private CancellationTokenSource? _operation;
    private string? _packagePath;
    private GamePackageInspection? _inspection;

    public GamePackageInstallDriver(Version hostApiVersion, string installRoot)
    {
        _reader = new GamePackageReader(hostApiVersion);
        _installer = new GamePackageInstaller(_reader);
        _installRoot = Path.GetFullPath(installRoot);
    }

    public event Action<GamePackageInspection>? Inspected;
    public event Action<string>? Installed;
    public event Action<string>? Failed;
    public bool IsRunning => _operation is not null;

    public void Inspect(string packagePath)
    {
        if (_operation is not null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        _packagePath = Path.GetFullPath(packagePath);
        _inspection = null;
        _operation = new CancellationTokenSource();
        _ = InspectOnWorker(_packagePath, _operation.Token);
    }

    public void InstallConfirmed(string expectedSha256, bool developerMode = false)
    {
        if (_operation is not null) return;
        if (_packagePath is null || _inspection is null ||
            !string.Equals(_inspection.PackageSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("必须先确认最近一次扫描得到的完整包哈希。");
        _operation = new CancellationTokenSource();
        _ = InstallOnWorker(_packagePath, expectedSha256, developerMode, _operation.Token);
    }

    public override void _Process(double delta)
    {
        while (_notifications.TryDequeue(out var item))
        {
            _operation?.Dispose();
            _operation = null;
            if (item.Inspection is { } inspection)
            {
                _inspection = inspection;
                Inspected?.Invoke(inspection);
            }
            else if (item.InstalledPath is { } installed) Installed?.Invoke(installed);
            else if (item.Error is { } error) Failed?.Invoke(error);
        }
    }

    public override void _ExitTree()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private async Task InspectOnWorker(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() => _reader.Inspect(path, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _notifications.Enqueue(new(result, null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(null, null, error.Message)); }
    }

    private async Task InstallOnWorker(string path, string expectedSha256, bool developerMode,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() =>
                _installer.InstallConfirmed(path, _installRoot, expectedSha256, developerMode, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _notifications.Enqueue(new(null, result, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(null, null, error.Message)); }
    }

    private readonly record struct DriverNotification(GamePackageInspection? Inspection,
        string? InstalledPath, string? Error);
}
