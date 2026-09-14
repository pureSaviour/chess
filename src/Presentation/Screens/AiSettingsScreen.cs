using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Presentation.AI;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Edits a detached AppSettings copy. Engine executables are only supplied by the pinned package
/// installer; this screen never accepts or persists an arbitrary player-selected path.
/// </summary>
public partial class AiSettingsScreen : Control
{
    private readonly VBoxContainer _advancedSettings;
    public bool AreAdvancedSettingsVisible => _advancedSettings.Visible;
    public bool HasManualEnginePicker => FindChildren("*", "FileDialog", true, false).Count > 0;
    public bool HasReplayAnalysisTimeControl => FindChildren("ReplayAnalysisThinkSeconds", "SpinBox", true, false).Count == 1;

    public AiSettingsScreen(AppSettings current, Action<AppSettings> save, Action back, EnginePackageInstaller installer)
    {
        var draft = current.Copy();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 34); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 14); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("人机设置"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var panel = UiStyle.CreatePanel(UiStyle.PanelLight, 14); panel.CustomMinimumSize = new(760, 500); panel.SizeFlagsVertical = SizeFlags.ExpandFill; root.AddChild(panel);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; panel.AddChild(scroll);
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; content.AddThemeConstantOverride("separation", 16); scroll.AddChild(content);
        var basic = new GridContainer { Columns = 2 }; basic.AddThemeConstantOverride("h_separation", 20); basic.AddThemeConstantOverride("v_separation", 14); content.AddChild(basic);
        basic.AddChild(new Label { Text = "难度" }); var difficulty = CreateDifficultyPicker(draft.AiDifficulty); basic.AddChild(difficulty);
        basic.AddChild(new Label { Text = "玩家执子" }); var side = new OptionButton(); side.AddItem("玩家执红"); side.AddItem("玩家执黑"); side.AddItem("随机猜先"); side.Select(draft.AiSideChoice); basic.AddChild(side);
        basic.AddChild(new Label { Text = "局时（分钟，0 不限）" }); var game = new SpinBox { MinValue = 0, MaxValue = 180, Value = draft.AiGameSeconds / 60.0, Step = 1 }; basic.AddChild(game);
        basic.AddChild(new Label { Text = "步时（秒，0 不限）" }); var move = new SpinBox { MinValue = 0, MaxValue = 300, Value = draft.AiMoveSeconds, Step = 5 }; basic.AddChild(move);

        var advancedToggle = new CheckButton { Text = "高级设置" }; content.AddChild(advancedToggle);
        _advancedSettings = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill }; _advancedSettings.AddThemeConstantOverride("separation", 12); content.AddChild(_advancedSettings);
        advancedToggle.Toggled += visible => _advancedSettings.Visible = visible;
        _advancedSettings.AddChild(new Label { Text = "应用仅使用一键配置的固定皮卡鱼版本，不支持选择其他引擎程序。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var installRow = new HBoxContainer(); installRow.AddThemeConstantOverride("separation", 12); _advancedSettings.AddChild(installRow);
        var install = UiStyle.CreateButton("一键下载并配置皮卡鱼", true); installRow.AddChild(install);
        var installStatus = new Label { Text = installer.TryGetInstalled(EnginePackageKind.Pikafish, out var installed)
            ? $"已配置皮卡鱼 {installed.Version}" : "尚未配置", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart }; installRow.AddChild(installStatus);
        var consent = new ConfirmationDialog { Title = "皮卡鱼许可确认", DialogText = EnginePackageInstaller.LicenseNotice(EnginePackageKind.Pikafish), OkButtonText = "同意并下载", CancelButtonText = "取消" }; AddChild(consent);
        var driver = new EngineInstallDriver(installer); AddChild(driver);
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 20); grid.AddThemeConstantOverride("v_separation", 14); _advancedSettings.AddChild(grid);
        grid.AddChild(new Label { Text = "自定义每步思考（秒）" }); var think = new SpinBox { MinValue = .1, MaxValue = 120, Value = draft.AiThinkMilliseconds / 1000.0, Step = .1 }; grid.AddChild(think);
        grid.AddChild(new Label { Text = "复盘每局面思考（秒）" }); var replayThink = new SpinBox { Name = "ReplayAnalysisThinkSeconds", MinValue = .1, MaxValue = 120, Value = draft.ReplayAnalysisMilliseconds / 1000.0, Step = .1 }; grid.AddChild(replayThink);
        grid.AddChild(new Label { Text = "最大深度（0=不限）" }); var depth = new SpinBox { MinValue = 0, MaxValue = 128, Value = draft.AiMaxDepth, Step = 1 }; grid.AddChild(depth);
        grid.AddChild(new Label { Text = "线程数" }); var threads = new SpinBox { MinValue = 1, MaxValue = 256, Value = draft.AiThreads, Step = 1 }; grid.AddChild(threads);
        grid.AddChild(new Label { Text = "哈希内存（MB）" }); var hash = new SpinBox { MinValue = 16, MaxValue = 4096, Value = draft.AiHashMegabytes, Step = 16 }; grid.AddChild(hash);
        var saveButton = UiStyle.CreateButton("保存人机设置", true); content.AddChild(saveButton);
        void Capture()
        {
            draft.AiSideChoice = side.Selected;
            draft.AiGameSeconds = (int)Math.Round(game.Value * 60); draft.AiMoveSeconds = (int)move.Value;
            draft.AiThinkMilliseconds = (int)Math.Round(think.Value * 1000);
            draft.ReplayAnalysisMilliseconds = (int)Math.Round(replayThink.Value * 1000);
            draft.AiDifficulty = difficulty.Selected; draft.AiMaxDepth = (int)depth.Value;
            draft.AiThreads = (int)threads.Value; draft.AiHashMegabytes = (int)hash.Value;
        }
        saveButton.Pressed += () => { Capture(); save(draft); };
        install.Pressed += () => consent.PopupCentered(new Vector2I(650, 300));
        consent.Confirmed += () => { Capture(); install.Disabled = saveButton.Disabled = true; installStatus.Text = "准备下载…"; driver.Start(EnginePackageKind.Pikafish); };
        driver.ProgressChanged += value => installStatus.Text = value.TotalBytes is > 0
            ? $"{value.Stage} {Math.Clamp(value.CompletedBytes * 100 / value.TotalBytes.Value, 0, 100)}%" : value.Stage;
        driver.Completed += result => { installStatus.Text = $"已配置皮卡鱼 {result.Version}"; save(draft); };
        driver.Failed += error => { install.Disabled = saveButton.Disabled = false; installStatus.Text = $"安装失败：{error}"; };
#if GODOT_ANDROID
        install.Disabled = true; install.TooltipText = "Android 当前不支持外部进程引擎。";
#endif
    }

    private static OptionButton CreateDifficultyPicker(int selected)
    {
        var picker = new OptionButton();
        foreach (var name in new[] { "入门", "简单", "适中", "困难", "大师", "自定义" }) picker.AddItem(name);
        picker.Select(Math.Clamp(selected, 0, (int)AiDifficultyLevel.Custom));
        return picker;
    }
}
