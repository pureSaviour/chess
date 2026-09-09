# Godot 4.7 / .NET 10 迁移记录

更新：2026-09-09。本文记录从 Godot 4.5.1 / .NET 8 到 Godot 4.7.2，以及桌面与测试升级到 .NET 10 的迁移边界、API 决策、验证证据和剩余平台风险。

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

## .NET 10 迁移

迁移前主工程桌面、Core、Transport 与测试为 `net8.0`，Android 条件目标为 `net9.0`。经维护者批准后，桌面主工程和两个测试工程升级为 `net10.0`；Core 与 Transport 同时目标 `net9.0;net10.0`，分别服务 Android 与桌面/测试。Godot SDK 继续固定为 4.7.2，没有升级其他依赖或改变业务代码。本机使用 .NET SDK 10.0.303 与运行时 10.0.11。[Godot 4.7 功能清单](https://docs.godotengine.org/en/4.7/about/list_of_features.html)表述为支持“.NET 8 及以上”，[Microsoft 生命周期说明](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)将 .NET 10 列为支持至 2028 年 11 月的 LTS。

审批前只用 MSBuild 命令行临时覆盖 `TargetFramework=net10.0` 的探测曾因 Godot 中间资产仍是 `net8.0` 而以 `NETSDK1005` 停止。正式修改相关工程 TFM 并重新执行 restore 后，桌面资产文件生成 `net10.0` 目标；这一区别验证了失败根因是未同步的项目/中间资产，而不是通过修改生成文件绕过兼容检查。

迁移到 .NET 10 的潜在收益：更新的 JIT、GC、运行库和 C# 工具链可能改善纯托管热点、启动或分配表现，并延长运行时支持周期。但本项目主要帧绘制会跨 Godot C#→C++ 边界，网络与 AI 又以 I/O 为主；在没有基准数据前，整体游戏性能收益不应预设为显著。

兼容性与风险：

- 所有工程、测试、Windows 与 Android 条件目标需要作为一个受控变更同步，避免项目引用 TFM 不一致。
- 桌面开发机必须具备对应 SDK/运行时；发布物的自包含/框架依赖策略及 Godot 导出模板实际承载能力需要复核。
- C# Android 仍属实验支持。即使普通条件编译成功，也不能证明 .NET 10 Android publish、AAB/APK、16 KiB 页、裁剪/AOT 和真机运行兼容；这是最高风险项。
- Steamworks.NET 托管绑定与 Windows 原生 DLL 需要重新做装载和双账号实联；UCI 子进程、JSON/网络序列化和反射/源生成器需要回归。
- 新运行时可能改变 GC 暂停、JIT 预热、浮点/序列化边缘行为和产物体积，应以可重复基准与导出物比较决定，而不是只看编译通过。

本次只迁移运行时与构建工具链，不采用新的 .NET API，不修改公开 API、序列化、协议、场景、资源、目录结构或运行逻辑。`.NET 10` 的 JIT、GC 和运行库优化会作用于纯托管路径，但本轮不以没有基准数据的性能预期为由扩展代码改动。

Android 未同步升级到 `net10.0`：使用 Godot 4.7.2 官方预编译模板进行实际 Debug APK 导出时，编辑器明确报告项目目标 `net10.0`、模板只支持 `net9.0`，并建议考虑 Gradle build。该结果说明普通 MSBuild 条件构建通过不能替代 Godot 导出验证。在官方支持范围与实际产物验证完成前，Android 保持 `net9.0`；不会通过修改生成文件或关闭兼容性检查绕过模板限制。

### Android `net10.0` 调研结论

- Godot 4.7 稳定分支的 [Android 导出源码](https://github.com/godotengine/godot/blob/4.7-stable/platform/android/export/export_plugin.cpp)明确规定：预编译模板只接受 `net9.0`，因为模板携带的 `.jar` 依赖可能只兼容 .NET 9；启用 Gradle build 时才跳过这项 TFM 校验。
- 官方 [Gradle Android 导出文档](https://docs.godotengine.org/en/4.7/tutorials/export/android_gradle_build.html)说明 Gradle 会在项目中安装 Android Java 工程并按次构建模板，但原生 `.so` 仍来自预编译模板。切换 Gradle 因而是正式支持的导出方式，不是非官方补丁；它会新增 `res://android/build` 并改变 Android 构建与维护范围。
- Godot 官方仓库的 [#118989](https://github.com/godotengine/godot/issues/118989)仍在跟踪预编译模板缺少 .NET 10 支持的问题；截至本文更新时状态为 open、待平台团队评估，没有对应里程碑或合并修复。因此不能把 Gradle 路径表述为 Godot 4.7 对 Android `.NET 10` 的完整官方兼容承诺。
- 本项目临时将 Android TFM 调整为 `net10.0` 后，`dotnet publish -c ExportDebug -r android-arm64` 成功，Core、Transport 与主程序集均生成 `net10.0` Android arm64 发布物。这确认 Godot SDK/托管发布阶段可用，但不证明 Gradle APK/AAB 打包和设备运行可用。
- 当前机器的 Godot 4.7 设置指向 `C:\Users\Lenovo‘\AppData\Local\Android\Sdk`，该目录不存在；因此 Gradle APK/AAB、签名、安装和真机启动尚不能实际验证。按照最小改动原则，本次不安装系统 SDK、不生成 Android Gradle 工程，也不修改现有导出预设。

结论：在 Godot 4.7.2 下，Android `net10.0` **存在可试验的 Gradle 导出路径，但当前不能确认端到端可用或达到可发布支持级别**。继续迁移前需要准备官方要求的 Android SDK/JDK、在隔离分支安装 Godot 4.7.2 匹配的 Gradle build template，并依次验证 Debug APK、Release AAB、至少 arm64 真机启动、16 KiB 页兼容及现有 smoke；验证通过前保留 Android `net9.0`。

## 本轮验证

- `dotnet build .\Xiangqi.sln`：通过，0 警告、0 错误。
- `dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj`：48/48 通过。
- `dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj`：模拟与真实 TCP 回环测试通过。
- `dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=android`：通过，0 警告、0 错误；不等于 Android 导出或真机验收。
- `dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=windows`：通过，0 警告、0 错误。
- `GODOT_NEW_PATH` 下 Godot 4.7.2 以 headless 和 NVIDIA/OpenGL 兼容渲染器分别执行 `--smoke-ui`、`--smoke-feedback`、`--smoke-three`：全部 PASS；生成的画廊、经典棋盘和三国棋盘截图已人工检查，未发现网格断裂或明显线宽回归。
- Windows Debug 导出成功，导出后的控制台程序执行 `--smoke-ui` 输出 PASS。
- Android 在临时 `net10.0` 目标下进行真实 Debug APK 导出：失败，Godot 4.7.2 官方模板报告只支持 `net9.0`；因此仓库配置恢复为 Android `net9.0`，待进一步确认 Gradle 路径。
- Android `net10.0` arm64 托管 publish 探测：通过；最终 APK/AAB 导出因当前配置的 Android SDK 目录不存在而未执行。

尚未验收：Android `net10.0` APK/AAB、Android 真机、Steam 双/三账号、跨设备 LAN、不同 GPU/分辨率人工视觉检查。构建成功、桌面运行通过都不替代这些平台验收。
