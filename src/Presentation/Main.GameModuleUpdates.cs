using Godot;
using Xiangqi.GameHost;
using Xiangqi.Presentation.GameModules;
using Xiangqi.Presentation.Screens;

namespace Xiangqi.Presentation;

public partial class Main
{
    private GameModuleUpdateDriver? _gameModuleUpdates;
    private ConfirmationDialog? _thirdPartyDownloadConsent;
    private ConfirmationDialog? _thirdPartyInstallConsent;
    private readonly Queue<GameModuleUpdateEntry> _thirdPartyUpdateQueue = new();
    private DownloadedGameModuleUpdate? _pendingThirdPartyInstall;
    private bool _manualModuleUpdateCheck;
    private IReadOnlyList<GameModuleRegistration> _downloadedGameModules = [];
    private readonly List<string> _moduleActivationWarnings = [];

    private void InitializeGameModuleUpdates()
    {
        var moduleRoot = ProjectSettings.GlobalizePath("user://modules");
        // Activation is a startup-only journal transition. Discovery below materializes only the
        // host-defined declarative ABI and never loads package-supplied code.
        GameModuleVersionStore versions;
        try
        {
            versions = new(moduleRoot);
            versions.ApplyPendingAtStartup();
            _downloadedGameModules = DiscoverDownloadedGameModules(moduleRoot, versions);
        }
        catch (Exception error)
        {
            GD.PushWarning($"玩法版本状态不可用，在线更新已禁用：{error.Message}");
            return;
        }

        var url = ProjectSettings.GetSetting("game_modules/update_index_url", "").AsString().Trim();
        var publicKey = ProjectSettings.GetSetting("game_modules/update_feed_public_key", "").AsString().Trim();
        if (url.Length == 0 || publicKey.Length == 0) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var indexUri) || indexUri.Scheme != Uri.UriSchemeHttps)
        {
            GD.PushWarning("玩法更新已禁用：game_modules/update_index_url 必须是 HTTPS 地址。");
            return;
        }

        var fingerprints = ProjectSettings.GetSetting(
                "game_modules/official_publisher_fingerprints", "").AsString()
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            _gameModuleUpdates = new(indexUri, publicKey, fingerprints, new Version(1, 0), moduleRoot);
        }
        catch (Exception error)
        {
            GD.PushWarning($"玩法更新已禁用：{error.Message}");
            return;
        }
        AddChild(_gameModuleUpdates);
        _gameModuleUpdates.Checked += OnGameModuleUpdatesChecked;
        _gameModuleUpdates.ThirdPartyDownloaded += OnThirdPartyModuleDownloaded;
        _gameModuleUpdates.Staged += OnGameModuleUpdateStaged;
        _gameModuleUpdates.Failed += OnGameModuleUpdateFailed;
        CreateGameModuleUpdateDialogs();
        _gameModuleUpdates.Check(CurrentGameModuleVersions());
    }

    private void CheckGameModuleUpdates()
    {
        if (_gameModuleUpdates is null)
        {
            ShowError("尚未配置玩法更新服务器。请在发布配置中设置 HTTPS 索引地址、索引公钥和官方发布者指纹。");
            return;
        }
        if (_gameModuleUpdates.IsRunning)
        {
            ShowError("玩法更新任务正在进行，请稍候。");
            return;
        }
        _manualModuleUpdateCheck = true;
        _thirdPartyUpdateQueue.Clear();
        _gameModuleUpdates.Check(CurrentGameModuleVersions());
    }

    private IReadOnlyDictionary<GameModuleId, Version> CurrentGameModuleVersions() =>
        CreateAvailableGameModules().ToDictionary(module => module.Manifest.Id,
            module => module.Manifest.Version);

    private IReadOnlyList<GameModuleRegistration> CreateAvailableGameModules() =>
        CreateBuiltInGameModules().Concat(_downloadedGameModules).ToArray();

    private IReadOnlyList<GameModuleRegistration> DiscoverDownloadedGameModules(string moduleRoot,
        GameModuleVersionStore versions)
    {
        var builtInIds = CreateBuiltInGameModules().Select(module => module.Manifest.Id).ToHashSet();
        var loader = new DeclarativeGameModuleLoader(new Version(1, 0));
        var registrations = new List<GameModuleRegistration>();
        foreach (var state in versions.Snapshot)
        {
            if (state.Active is not { } active) continue;
            try
            {
                if (builtInIds.Contains(state.ModuleId))
                    throw new InvalidDataException("下载模块不能覆盖编译进宿主的同名玩法。");
                var root = Path.Combine(moduleRoot, state.ModuleId.Value, active.Version.ToString());
                var loaded = loader.Load(root, active);
                ValidateDeclarativeCard(loaded);
                var routes = loaded.PackageManifest.Routes.Select(route =>
                    (route, (Action)(() => ShowDeclarativeGame(loaded))));
                registrations.Add(new(loaded.PackageManifest.Module, routes,
                    loaded.CardImage, loaded.CardImageExtension));
            }
            catch (Exception error)
            {
                versions.ReportActivationFailure(state.ModuleId, active.Version);
                GD.PushWarning($"玩法 {state.ModuleId} {active.Version} 已隔离：{error.Message}");
                _moduleActivationWarnings.Add($"{state.ModuleId} {active.Version}：{error.Message}");
            }
        }
        return registrations;
    }

    private static void ValidateDeclarativeCard(LoadedDeclarativeGameModule module)
    {
        using var image = new Image();
        var data = module.CardImage.ToArray();
        var error = module.CardImageExtension switch
        {
            ".png" => image.LoadPngFromBuffer(data),
            ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(data),
            ".webp" => image.LoadWebpFromBuffer(data),
            _ => Error.FileUnrecognized
        };
        if (error != Error.Ok || image.GetWidth() is <= 0 or > 4096 || image.GetHeight() is <= 0 or > 4096)
            throw new InvalidDataException($"玩法卡片图像无法安全解码：{error}。");
    }

    private void ShowGameModuleActivationWarnings()
    {
        if (_moduleActivationWarnings.Count == 0) return;
        ShowError("以下下载玩法未能安全启用，失败版本已隔离；如有上一个正常版本，将在下次启动自动回滚：\n\n" +
                  string.Join("\n", _moduleActivationWarnings));
    }

    private void ShowDeclarativeGame(LoadedDeclarativeGameModule module) =>
        SwitchTo(new DeclarativeLineGameScreen(module,
            ProjectSettings.GlobalizePath("user://games"), ShowGallery));

    private LoadedDeclarativeGameModule CreateDeclarativeUiSmokeModule()
    {
        var id = new GameModuleId("smoke.line-placement");
        var capability = GameModuleCapabilities.LocalPlay;
        var module = new GameModuleManifest(id, new Version(1, 0), "声明式测试玩法",
            "受限运行时 UI smoke", "content/card.png", capability, 999);
        var route = new GameModuleRoute("local", "开始游戏", "本地对局", capability);
        var package = new GamePackageManifest(1, module, [route], new Version(1, 0),
            GamePackageExecutionMode.Sandboxed, GamePackagePermissions.None, "内置 smoke",
            null, []);
        // Imported textures live in the PCK after export, not at a globalized OS path.
        // Keep the cached texture alive; only the temporary image copy belongs to this fixture.
        var texture = GD.Load<Texture2D>("res://assets/gallery/classic-xiangqi-card.png")
            ?? throw new InvalidDataException("UI smoke card texture could not be loaded.");
        using var image = texture.GetImage();
        var cardBytes = image.SavePngToBuffer();
        if (cardBytes.Length == 0)
            throw new InvalidDataException("UI smoke card texture could not be encoded as PNG.");
        return new(package, new(9, 9, 5, "黑方", "白方", 0), cardBytes, ".png");
    }

    private void OnGameModuleUpdatesChecked(IReadOnlyList<GameModuleUpdateEntry> thirdParty)
    {
        foreach (var entry in thirdParty) _thirdPartyUpdateQueue.Enqueue(entry);
        if (_manualModuleUpdateCheck && thirdParty.Count == 0)
            ShowError("更新检查完成。没有需要用户确认的第三方更新；官方更新如有发现，已在后台验证并暂存。");
        _manualModuleUpdateCheck = false;
        ShowNextThirdPartyUpdate();
    }

    private void ShowNextThirdPartyUpdate()
    {
        if (_gameModuleUpdates is null || _gameModuleUpdates.IsRunning ||
            _thirdPartyUpdateQueue.Count == 0 || _thirdPartyDownloadConsent is null) return;
        var entry = _thirdPartyUpdateQueue.Peek();
        _thirdPartyDownloadConsent.DialogText =
            $"发现第三方玩法更新：\n\n模块：{entry.ModuleId}\n版本：{entry.Version}\n" +
            $"下载地址：{entry.PackageUri}\n大小：{entry.PackageBytes:N0} 字节\n" +
            $"发布者密钥指纹：\n{entry.PublisherKeyFingerprint}\n包 SHA-256：\n{entry.PackageSha256}\n\n" +
            "第三方内容不是官方自动信任项。下载只会写入隔离的临时目录，不会执行玩法代码；" +
            "下载完成并验证签名后，还会再次显示权限和执行模式供你确认。是否信任该来源并下载？";
        _thirdPartyDownloadConsent.PopupCentered(new Vector2I(760, 600));
    }

    private void OnThirdPartyModuleDownloaded(DownloadedGameModuleUpdate update)
    {
        _pendingThirdPartyInstall = update;
        if (_thirdPartyInstallConsent is null) return;
        var inspection = update.Inspection;
        var manifest = inspection.Manifest;
        var disposition = GamePackageTrustPolicy.Evaluate(inspection.CreateTrustRequest());
        if (disposition == GamePackageTrustDisposition.Reject)
        {
            ShowError("下载包请求了当前模式不允许的权限，或完全信任代码没有有效签名，因此已拒绝安装。");
            FinishThirdPartyUpdate();
            return;
        }
        var mode = manifest.ExecutionMode switch
        {
            GamePackageExecutionMode.ResourceOnly => "纯资源",
            GamePackageExecutionMode.Sandboxed => "受限运行时",
            GamePackageExecutionMode.FullTrust => "完全信任代码",
            _ => "未知"
        };
        var warning = manifest.ExecutionMode == GamePackageExecutionMode.FullTrust
            ? "严重警告：完全信任代码启用后将拥有与游戏相同的本机权限。签名只证明来源与完整性，不证明代码安全。"
            : "安装不会执行内容；签名只证明来源与完整性，不证明内容绝对安全。";
        _thirdPartyInstallConsent.DialogText =
            $"玩法：{manifest.Module.DisplayName}\nID / 版本：{manifest.Module.Id} / {manifest.Module.Version}\n" +
            $"发布者：{manifest.Publisher}\n签名指纹：{inspection.PublisherKeyFingerprint}\n" +
            $"执行模式：{mode}\n请求权限：{DescribeUpdatePermissions(manifest.Permissions)}\n" +
            $"文件数：{manifest.Files.Count}\n包 SHA-256：\n{inspection.PackageSha256}\n\n{warning}\n\n是否安装并在下次启动切换版本？";
        _thirdPartyInstallConsent.PopupCentered(new Vector2I(760, 620));
    }

    private void OnGameModuleUpdateStaged(GameModuleId id, Version version)
    {
        ShowError($"玩法 {id} {version} 已完成签名与哈希验证并暂存，将在下次启动切换版本。旧版本保留用于回滚。");
        if (_pendingThirdPartyInstall is not null) FinishThirdPartyUpdate();
    }

    private void OnGameModuleUpdateFailed(string error)
    {
        ShowError($"玩法更新失败：{error}");
        if (_pendingThirdPartyInstall is not null || _thirdPartyUpdateQueue.Count > 0)
            FinishThirdPartyUpdate();
        _manualModuleUpdateCheck = false;
    }

    private void FinishThirdPartyUpdate()
    {
        _pendingThirdPartyInstall = null;
        if (_thirdPartyUpdateQueue.Count > 0) _thirdPartyUpdateQueue.Dequeue();
        ShowNextThirdPartyUpdate();
    }

    private void CreateGameModuleUpdateDialogs()
    {
        _thirdPartyDownloadConsent = new ConfirmationDialog
        {
            Title = "确认下载第三方玩法", OkButtonText = "信任来源并下载", CancelButtonText = "跳过",
            MinSize = new Vector2I(720, 560)
        };
        _thirdPartyInstallConsent = new ConfirmationDialog
        {
            Title = "确认安装第三方玩法", OkButtonText = "我理解风险，安装", CancelButtonText = "取消",
            MinSize = new Vector2I(720, 580)
        };
        AddChild(_thirdPartyDownloadConsent);
        AddChild(_thirdPartyInstallConsent);
        _thirdPartyDownloadConsent.Confirmed += () =>
        {
            if (_gameModuleUpdates is null || _thirdPartyUpdateQueue.Count == 0) return;
            _gameModuleUpdates.DownloadThirdParty(_thirdPartyUpdateQueue.Peek());
        };
        _thirdPartyDownloadConsent.Canceled += FinishThirdPartyUpdate;
        _thirdPartyInstallConsent.Confirmed += () =>
        {
            if (_gameModuleUpdates is not null && _pendingThirdPartyInstall is { } update)
                _gameModuleUpdates.InstallThirdParty(update);
        };
        _thirdPartyInstallConsent.Canceled += FinishThirdPartyUpdate;
    }

    private static string DescribeUpdatePermissions(GamePackagePermissions permissions)
    {
        if (permissions == GamePackagePermissions.None) return "无";
        var values = Enum.GetValues<GamePackagePermissions>()
            .Where(value => value != GamePackagePermissions.None && permissions.HasFlag(value));
        return string.Join("、", values);
    }
}
