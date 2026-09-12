using System.Collections.Concurrent;
using Godot;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.GameModules;

/// <summary>
/// Godot main-thread bridge for one signed update feed. It owns HttpClient and a single operation
/// cancellation source. Network, hashing and extraction run on workers; _Process publishes only
/// immutable results. Official entries require a pinned publisher fingerprint and are staged
/// automatically; third-party entries require explicit download and install calls from the UI.
/// </summary>
public partial class GameModuleUpdateDriver : Node
{
    private readonly System.Net.Http.HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    { Timeout = TimeSpan.FromMinutes(10) };
    private readonly GameModuleUpdateClient _client;
    private readonly GamePackageInstaller _installer;
    private readonly GameModuleVersionStore _versions;
    private readonly Uri _indexUri;
    private readonly string _feedPublicKey;
    private readonly IReadOnlySet<string> _officialPublisherFingerprints;
    private readonly string _moduleRoot;
    private readonly Version _hostApiVersion;
    private readonly ConcurrentQueue<UpdateNotification> _notifications = new();
    private CancellationTokenSource? _operation;

    public GameModuleUpdateDriver(Uri indexUri, string feedPublicKey,
        IEnumerable<string> officialPublisherFingerprints, Version hostApiVersion, string moduleRoot)
    {
        _indexUri = indexUri ?? throw new ArgumentNullException(nameof(indexUri));
        _feedPublicKey = feedPublicKey;
        _officialPublisherFingerprints = officialPublisherFingerprints
            .Select(GamePackageReader.NormalizeSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _moduleRoot = Path.GetFullPath(moduleRoot);
        _hostApiVersion = hostApiVersion;
        _client = new(_http, hostApiVersion, Path.Combine(_moduleRoot, ".downloads"));
        _installer = new(new(hostApiVersion));
        _versions = new(_moduleRoot);
    }

    public event Action<IReadOnlyList<GameModuleUpdateEntry>>? Checked;
    public event Action<GameModuleDownloadProgress>? DownloadProgress;
    public event Action<DownloadedGameModuleUpdate>? ThirdPartyDownloaded;
    public event Action<GameModuleId, Version>? Staged;
    public event Action<string>? Failed;
    public bool IsRunning => _operation is not null;

    public IReadOnlyList<GameModuleId> ApplyPendingAtStartup() => _versions.ApplyPendingAtStartup();

    public void Check(IReadOnlyDictionary<GameModuleId, Version> builtInVersions)
    {
        if (_operation is not null) return;
        var installed = new Dictionary<GameModuleId, Version>(builtInVersions);
        foreach (var (id, version) in _versions.LatestKnownVersions())
            if (!installed.TryGetValue(id, out var existing) ||
                GameModuleUpdateClient.IsNewer(version, existing)) installed[id] = version;
        Start(token => CheckWorker(installed, token));
    }

    public void DownloadThirdParty(GameModuleUpdateEntry entry)
    {
        if (GameModuleUpdatePolicy.Evaluate(entry, _officialPublisherFingerprints) !=
            GameModuleUpdateDisposition.RequireThirdPartyConfirmation)
            throw new InvalidOperationException("该条目不是可由用户批准的第三方更新。");
        Start(token => DownloadWorker(entry, official: false, token));
    }

    public void InstallThirdParty(DownloadedGameModuleUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Start(token => InstallWorker(update, token));
    }

    public override void _Process(double delta)
    {
        while (_notifications.TryDequeue(out var item))
        {
            if (!item.ProgressOnly)
            {
                _operation?.Dispose();
                _operation = null;
            }
            if (item.Progress is { } progress) DownloadProgress?.Invoke(progress);
            if (item.Updates is { } updates) Checked?.Invoke(updates);
            if (item.Download is { } download) ThirdPartyDownloaded?.Invoke(download);
            if (item.StagedId is { } id && item.StagedVersion is { } version) Staged?.Invoke(id, version);
            if (item.Error is { } error) Failed?.Invoke(error);
        }
    }

    public override void _ExitTree()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
        _http.Dispose();
    }

    private void Start(Func<CancellationToken, Task> operation)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource();
        _ = operation(_operation.Token);
    }

    private async Task CheckWorker(IReadOnlyDictionary<GameModuleId, Version> installed,
        CancellationToken cancellationToken)
    {
        try
        {
            var index = await _client.FetchIndexAsync(_indexUri, _feedPublicKey, cancellationToken);
            var updates = GameModuleUpdateClient.FindUpdates(index, installed);
            var visible = new List<GameModuleUpdateEntry>();
            foreach (var entry in updates)
            {
                var disposition = GameModuleUpdatePolicy.Evaluate(entry, _officialPublisherFingerprints);
                if (disposition == GameModuleUpdateDisposition.Reject)
                    throw new InvalidDataException($"官方更新 {entry.ModuleId} 的发布者指纹未被宿主信任。");
                if (disposition == GameModuleUpdateDisposition.AutomaticOfficialDownload)
                    await DownloadAndStage(entry, cancellationToken);
                else visible.Add(entry);
            }
            _notifications.Enqueue(new(false, Updates: visible.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(false, Error: error.Message)); }
    }

    private async Task DownloadWorker(GameModuleUpdateEntry entry, bool official,
        CancellationToken cancellationToken)
    {
        try
        {
            if (official) await DownloadAndStage(entry, cancellationToken);
            else
            {
                var download = await _client.DownloadAsync(entry, Progress(), cancellationToken);
                _notifications.Enqueue(new(false, Download: download));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(false, Error: error.Message)); }
    }

    private async Task DownloadAndStage(GameModuleUpdateEntry entry, CancellationToken cancellationToken)
    {
        var download = await _client.DownloadAsync(entry, Progress(), cancellationToken);
        await Task.Run(() => InstallAndStage(download, cancellationToken), cancellationToken);
        _notifications.Enqueue(new(true, StagedId: entry.ModuleId, StagedVersion: entry.Version));
    }

    private async Task InstallWorker(DownloadedGameModuleUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => InstallAndStage(update, cancellationToken), cancellationToken);
            _notifications.Enqueue(new(false, StagedId: update.Entry.ModuleId,
                StagedVersion: update.Entry.Version));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { _notifications.Enqueue(new(false, Error: error.Message)); }
    }

    private void InstallAndStage(DownloadedGameModuleUpdate update, CancellationToken cancellationToken)
    {
        var installedRoot = _installer.InstallConfirmed(update.PackagePath, _moduleRoot,
            update.Inspection.PackageSha256,
            cancellationToken: cancellationToken);
        var evidence = new InstalledGameModuleVersion(update.Entry.Version,
            update.Inspection.PackageSha256, update.Entry.PublisherKeyFingerprint,
            update.Inspection.Manifest.ExecutionMode, update.Inspection.Manifest.Permissions);
        _ = new DeclarativeGameModuleLoader(_hostApiVersion).Load(installedRoot, evidence,
            cancellationToken);
        _versions.Stage(update.Inspection);
    }

    private Progress<GameModuleDownloadProgress> Progress() => new(value =>
        _notifications.Enqueue(new(true, Progress: value)));

    private sealed record UpdateNotification(bool ProgressOnly,
        GameModuleDownloadProgress? Progress = null,
        IReadOnlyList<GameModuleUpdateEntry>? Updates = null,
        DownloadedGameModuleUpdate? Download = null,
        GameModuleId? StagedId = null,
        Version? StagedVersion = null,
        string? Error = null);
}
