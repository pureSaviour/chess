# 中国象棋（Godot 4 / C#）

一个以 C# 编写逻辑的 Godot 4.7.2 中国象棋游戏。当前支持经典象棋四种入口、本地双人、ENet 局域网、Steam 好友和外接 UCI AI（如皮卡鱼）对弈，并提供棋盘路数、可切换棋盘皮肤及局面候选走法询问。

画廊新增“三国象棋”：正六边形三国棋盘、收编、三人计时/悔棋/和棋、国号聊天、本地三人及 LAN/Steam 三席入口；选中红光和最近着法白光共用 shader。LAN 默认 TCP 48028，支持保留房间时自动重连与房主迁移（真实回环三端测试通过）；Steam 三账号实联仍待验收。规则和网络边界见 [三国象棋说明](docs/three-kingdoms.md)。

画廊另有“揭棋”本地玩法：暗子首步按槽位棋种移动、落子翻明，支持包含确定性 Seed 的 PGN/ICCS 扩展、每类最多十局最近记录、永久保存和带刻度跳转/暗子身份淡显的复盘。LAN/Steam 脱敏状态协议尚未开放，详见 [揭棋玩法说明](docs/jieqi.md)。

## 运行

环境变量 `GODOT_NEW_PATH` 应指向 Godot 4.7.2 .NET 发行版目录。用该目录中的 Godot 可执行文件打开本目录，或在 PowerShell 中运行：

```powershell
& (Join-Path $env:GODOT_NEW_PATH 'Godot_v4.7.2-stable_mono_win64.exe') --path .
```

构建与规则测试：

```powershell
dotnet build .\Xiangqi.sln
dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj
dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj
```

### Android 导出

桌面主项目和测试使用 `net10.0`；规则库与传输层同时提供 `net9.0` 和 `net10.0`，供对应平台选择。Godot 4.7.2 的预编译 Android 导出模板目前只接受 `net9.0`，因此 Android 条件目标暂时保持 `net9.0`。兼容依据、实际验证和 Android `net10.0` 调研见 [Godot 4.7 / .NET 10 迁移记录](docs/godot-4.7-migration.md)。

本机还需要在 Godot 的“编辑器设置 → 导出 → Android”中配置 Java SDK 与 Android SDK。Godot 4.7 推荐 OpenJDK 17，并需要 Android Platform-Tools 35.0.0 或更高版本、Build-Tools 35.0.1、Platform 35、最新 Command-line Tools、CMake 3.10.2.4988404 和 NDK 28.1.13356709。安装与路径配置完成后，可从导出窗口使用 `Android` 预设，或运行：

```powershell
New-Item -ItemType Directory .\build\android -Force | Out-Null
& (Join-Path $env:GODOT_NEW_PATH 'Godot_v4.7.2-stable_mono_win64_console.exe') `
	--headless --path . --export-debug Android .\build\android\Xiangqi-debug.apk
```

## 代码位置

- `src/Xiangqi.Core/Domain`：棋盘、棋子、规则注册和权威对局状态。
- `src/Xiangqi.Core/Application`：面向 UI/网络的对局门面与语义特效提示。
- `src/Presentation`：Godot C# 界面/交互、AI/网络适配器及程序化棋盘 shader。
- `src/Xiangqi.Core/Application/AiContracts.cs`：引擎无关 AI 端口、UCI 记谱转换与人机协调器。
- `src/Presentation/Screens`：主菜单、Gallery、设置和对局界面。
- `src/Presentation/Networking`：ENet 局域网与 Steam Lobby/P2P 传输适配器。
- `src/Presentation/Profiles`：平台无关的玩家资料快照、头像内存所有权和自定义资料转换。
- `src/Presentation/Effects`：语义提示对应的程序化视觉特效。
- `src/Presentation/Audio`：语义提示到可替换音频资源的映射与播放。
- `assets/audio`：落子、吃子、选中音效及第三方素材许可说明。
- `assets/themes`：随游戏发布的 XML 主题包；玩家主题安装到 Godot `user://themes`。
- `tests/Xiangqi.Core.Tests`：无第三方测试框架的规则回归测试。
- [TECHNICAL_ARCHITECTURE.md](TECHNICAL_ARCHITECTURE.md)：总架构、分玩法架构与文档路由。
- [AGENTS.md](AGENTS.md)：Agent 的抽象规划、测试、详细文档及逻辑/UI 分离要求。
- [docs/abstractions.md](docs/abstractions.md)：抽象层用法、注入/生命周期、扩展示例和注意事项。
- [docs/theme-package-proposal.md](docs/theme-package-proposal.md)：schema v1、semantic atlas、shader uniform 与安装/安全接口。
- [docs/architecture.md](docs/architecture.md)：经典玩法架构；三国见 [docs/three-kingdoms.md](docs/three-kingdoms.md)。
- [docs/architecture-review.md](docs/architecture-review.md)：当前分层检查结果与尚未解决的限制。

## 后续范围

经典/三国/揭棋棋谱与复盘主线已经实现；赛事级长将/长捉裁决仍未实现。AI 已支持 UCI 外部引擎和 MultiPV 局面询问，不是自然语言对话模型。经典局域网使用 UDP 端口 `27888`（可在 Gallery 修改）；创建房间时可以选择房主执红、执黑或随机猜先，并可设置局时和步时。房主负责权威计时和超时裁决，客户端接收周期时钟同步。双方收到的每步棋都会由本地规则核心复核。

局域网对局右侧提供双方可见的聊天记录和消息输入。玩家只有在自己刚走完、对方尚未落子时才能申请撤销这一手；双方随时可以申请和棋。同一时间只处理一项申请，等待响应时暂停落子与计时，拒绝后继续对局。同意悔棋后双方回退同一手棋，同意和棋后双方进入无胜方终局并播放和棋结算动画。

无论玩家执红还是执黑，本地棋子都会旋转到棋盘下方显示。对局结束时，双方分别显示胜利、失败或和棋动画；超时结果会注明己方或对方超时。

棋子选中后会高亮并轻微悬浮；合法落子使用平滑插值与抬升轨迹移动到终点，动画结束时按普通落子/吃子分别播放音效。当前三个音效来自 CC0 的 Kenney Interface Sounds，来源和原始文件映射见 `assets/audio/THIRD_PARTY.md`。视觉特效仍保持程序化实现，包括吃子、将军、绝杀、困毙和“双车错”提示；音效统一走设置页的 SFX 总线。

主题使用字符串 ID 并由 XML 同时配置经典/三国棋盘、棋子 atlas、颜色和语义 shader。全局设置页可手动重扫 `assets/themes` 与 `user://themes`，对局中不热重载；无效包隔离并回退经典主题。自定义接口见[主题包与 shader 文档](docs/theme-package-proposal.md)。

Windows x64 已接入 Steamworks SDK 1.65、好友 Lobby 邀请和 Steam Networking Messages P2P 传输，当前用 Spacewar App ID `480` 测试。对局会显示 Steam 昵称、SteamID64 与头像；设置页可覆盖 ID/头像，自定义内容经有界的可靠资料消息发送，图片文件路径不会离开本机。真实 Steam 客户端上的 API 初始化和连续创建/退出 Lobby 已通过；完整双人邀请与远端头像仍需两个在线账号/设备。App ID 480 不能用于正式发布，取得本游戏 App ID 后仍需替换并做双端验收。开发运行与版本锁定方式见 `docs/steam-integration.md`。
