# Godot 4.7 迁移记录

更新：2026-09-09。本文记录从 Godot 4.5.1 .NET 到 Godot 4.7.2 .NET 的迁移边界、API 决策、验证证据，以及必须经维护者批准的 .NET 目标框架后续工作。

## 已实施的 Godot 迁移

- 主工程 SDK 固定为 `Godot.NET.Sdk/4.7.2`，`project.godot` 的功能标签为 `4.7`。
- 开发与验收入口改用机器级 `GODOT_NEW_PATH`；旧 `GODOT_PATH` 仅保留旧引擎，不参与本项目当前构建。
- README、主菜单版本标识、AI 适配器注释和总架构说明已同步到 4.7。
- 迁移不改变 Domain/Application/Transport 契约、棋规、协议版本、资源所有权或线程模型；未新增抽象。

当前仓库基线的最近功能提交已经包含 SDK 与 `project.godot` 的 4.7 标记，但仍混有 4.5 的运行命令和界面文案。本次复核将这些事实统一，并用 4.7.2 可执行文件完成运行时验证。

## 4.5→4.7 兼容检查与 API 决策

官方 [4.5→4.6](https://docs.godotengine.org/en/4.6/tutorials/migrating/upgrading_to_godot_4.6.html) 和 [4.6→4.7](https://docs.godotengine.org/en/4.7/tutorials/migrating/upgrading_to_godot_4.7.html) 迁移清单涉及编辑器导出接口、可空性元数据、部分 GUI/动画/音频/输入 API、场景序列化和渲染默认值。代码检索与 4.7.2 SDK 的零警告编译确认，本项目没有使用破坏性清单中的 `EditorExportPreset`、`RichTextLabel.AddImage/UpdateImage`、Accessibility、Animation.Length、输入设备 ID、3D/Jolt 或 Audio Area override API。项目显式保持 `canvas_items` stretch 与兼容渲染器，未依赖 4.7 的新项目默认值。

API 取舍：

- 保留现有 `CanvasItem.Draw*`、节点和 shader 实现。4.6 起的 2D batching/渲染器优化由引擎内部生效，无需更换调用 API；三个棋盘/UI smoke 已覆盖绘制和命中。
- 不把只在加载时创建、随后只读使用的 `ImageTexture` 改成 4.7 [`DrawableTexture2D`](https://docs.godotengine.org/en/4.7/tutorials/rendering/drawable_textures.html)。后者适合运行时 GPU 写纹理；本项目头像与主题 atlas 不进行逐帧纹理修改，替换没有已测性能收益，却会扩大 GPU 同步、资源释放和兼容渲染器风险。
- 保留 `OS.ExecuteWithPipe` 的非阻塞 UCI 适配。4.7 SDK 编译与 smoke 未显示弃用或替代要求；改用其他进程方案不会为当前每帧有界管道轮询提供可证明收益。
- 4.7 改变了 `CanvasItem` 抗锯齿线条的羽化行为，可能使线条观感略细。代码不通过猜测增加线宽；`--smoke-feedback` 与 `--smoke-three` 已通过，兼容渲染器 GPU 截图经人工检查未见网格断裂或明显线宽回归。

结论：采用 4.7 的引擎内部性能改进，不为“使用新 API”而重写当前稳定绘制、纹理或进程路径。若后续性能分析证明纹理更新或绘制提交是瓶颈，再用同场景帧时间、draw call、显存和低端设备数据驱动专项改造。

## .NET 版本评估（尚未实施）

当前目标保持不变：主工程桌面、Core、Transport 与测试为 `net8.0`，Android 条件目标为 `net9.0`。本机安装了 .NET SDK 10.0.303 以及 8/9/10 运行时；Godot 4.7.2 工具自身的 `GodotTools.runtimeconfig.json` 目标为 `net8.0`。[Godot 4.7 功能清单](https://docs.godotengine.org/en/4.7/about/list_of_features.html)表述为 C# 支持“.NET 8 及以上”，没有声明项目目标框架的封顶版本，因此“最高版本”应理解为当前受支持且仍在支持期内的稳定 .NET；在当前环境中候选为 .NET 10，而不是 Godot 强制要求的版本。

只用 MSBuild 命令行临时覆盖 `TargetFramework=net10.0` 的探测没有通过：Godot SDK 的 `.godot/mono/temp/obj/project.assets.json` 仍只生成 `net8.0` 目标，构建以 `NETSDK1005` 停止。该结果不是源代码不兼容证据，也没有改动项目文件；它说明正式验证必须在获批分支上同步修改工程 TFM、清理/隔离中间产物并完整 restore，不能把命令行覆盖或“安装了 .NET 10”当作 Godot/导出模板兼容证明。

迁移到 .NET 10 的潜在收益：更新的 JIT、GC、运行库和 C# 工具链可能改善纯托管热点、启动或分配表现，并延长运行时支持周期。但本项目主要帧绘制会跨 Godot C#→C++ 边界，网络与 AI 又以 I/O 为主；在没有基准数据前，整体游戏性能收益不应预设为显著。

兼容性与风险：

- 所有工程、测试、Windows 与 Android 条件目标需要作为一个受控变更同步，避免项目引用 TFM 不一致。
- 桌面开发机必须具备对应 SDK/运行时；发布物的自包含/框架依赖策略及 Godot 导出模板实际承载能力需要复核。
- C# Android 仍属实验支持。即使普通条件编译成功，也不能证明 .NET 10 Android publish、AAB/APK、16 KiB 页、裁剪/AOT 和真机运行兼容；这是最高风险项。
- Steamworks.NET 托管绑定与 Windows 原生 DLL 需要重新做装载和双账号实联；UCI 子进程、JSON/网络序列化和反射/源生成器需要回归。
- 新运行时可能改变 GC 暂停、JIT 预热、浮点/序列化边缘行为和产物体积，应以可重复基准与导出物比较决定，而不是只看编译通过。

建议：本轮不迁移 .NET。待维护者批准后另建短期分支，先做 `net10.0` 桌面试验和真实 Godot 运行，再验证 Android publish/导出与真机、Windows 导出、Steam/UCI，记录启动时间、稳定帧时间、分配/GC、包体积和完整回归结果；任一平台不受模板支持时继续保留当前分目标策略或退回 `net8.0`/`net9.0`。

## 本轮验证

- `dotnet build .\Xiangqi.sln`：通过，0 警告、0 错误。
- `dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj`：48/48 通过。
- `dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj`：模拟与真实 TCP 回环测试通过。
- `dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=android`：通过，0 警告、0 错误；不等于 Android 导出或真机验收。
- `dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=windows`：通过，0 警告、0 错误。
- `GODOT_NEW_PATH` 下 Godot 4.7.2 以 headless 和 NVIDIA/OpenGL 兼容渲染器分别执行 `--smoke-ui`、`--smoke-feedback`、`--smoke-three`：全部 PASS；生成的画廊、经典棋盘和三国棋盘截图已人工检查，未发现网格断裂或明显线宽回归。

尚未验收：Android APK/AAB 与真机、Steam 双/三账号、跨设备 LAN、不同 GPU/分辨率人工视觉检查。`.NET` 目标框架迁移明确尚未实施。
