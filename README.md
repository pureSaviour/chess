# 中国象棋（Godot 4 / C#）

一个完全使用 C# 编写逻辑的 Godot 4.5 中国象棋游戏。当前支持主菜单、玩法 Gallery、画面/声音/玩家资料设置、本地双人、ENet 局域网和 Steam 好友对弈。

## 运行

环境变量 `GODOT_PATH` 应指向 Godot Mono 发行版目录。用该目录中的 Godot 可执行文件打开本目录，或在 PowerShell 中运行：

```powershell
& (Join-Path $env:GODOT_PATH 'Godot_v4.5.1-stable_mono_win64.exe') --path .
```

构建与规则测试：

```powershell
dotnet build .\Xiangqi.sln
dotnet .\tests\Xiangqi.Core.Tests\bin\Debug\net8.0\Xiangqi.Core.Tests.dll
```

### Android 导出

主项目默认使用 `net8.0`，当 Godot 以 Android 为目标平台构建时自动切换为 `net9.0`，以满足 Godot 4.5 的 C# Android 导出要求。规则库和桌面测试继续使用 `net8.0`。

本机还需要在 Godot 的“编辑器设置 → 导出 → Android”中配置 Java SDK 与 Android SDK。Godot 4.5 推荐 OpenJDK 17，并需要 Android Platform-Tools 35.0.0 或更高版本、Build-Tools 35.0.0、Platform 35、最新 Command-line Tools、CMake 3.10.2.4988404 和 NDK 28.1.13356709。安装与路径配置完成后，可从导出窗口使用 `Android` 预设，或运行：

```powershell
New-Item -ItemType Directory .\build\android -Force | Out-Null
& (Join-Path $env:GODOT_PATH 'Godot_v4.5.1-stable_mono_win64_console.exe') `
	--headless --path . --export-debug Android .\build\android\Xiangqi-debug.apk
```

## 代码位置

- `src/Xiangqi.Core/Domain`：棋盘、棋子、规则注册和权威对局状态。
- `src/Xiangqi.Core/Application`：面向 UI/网络的对局门面与语义特效提示。
- `src/Presentation`：Godot C# 棋盘绘制和交互。
- `src/Presentation/Screens`：主菜单、Gallery、设置和对局界面。
- `src/Presentation/Networking`：ENet 局域网与 Steam Lobby/P2P 传输适配器。
- `src/Presentation/Profiles`：平台无关的玩家资料快照、头像内存所有权和自定义资料转换。
- `src/Presentation/Effects`：语义提示对应的程序化视觉特效。
- `src/Presentation/Audio`：语义提示到可替换音频资源的映射与播放。
- `assets/audio`：落子、吃子、选中音效及第三方素材许可说明。
- `tests/Xiangqi.Core.Tests`：无第三方测试框架的规则回归测试。
- `docs/architecture.md`：分层边界、扩展点与当前规则范围。

## 后续范围

AI、棋谱以及长将/长捉裁决尚未实现。当前局域网使用 UDP 端口 `27888`（可在 Gallery 修改）；创建房间时可以选择房主执红、执黑或随机猜先，并可设置局时和步时。房主负责权威计时和超时裁决，客户端接收周期时钟同步。双方收到的每步棋都会由本地规则核心复核。

局域网对局右侧提供双方可见的聊天记录和消息输入。玩家只有在自己刚走完、对方尚未落子时才能申请撤销这一手；双方随时可以申请和棋。同一时间只处理一项申请，等待响应时暂停落子与计时，拒绝后继续对局。同意悔棋后双方回退同一手棋，同意和棋后双方进入无胜方终局并播放和棋结算动画。

无论玩家执红还是执黑，本地棋子都会旋转到棋盘下方显示。对局结束时，双方分别显示胜利、失败或和棋动画；超时结果会注明己方或对方超时。

棋子选中后会高亮并轻微悬浮；合法落子使用平滑插值与抬升轨迹移动到终点，动画结束时按普通落子/吃子分别播放音效。当前三个音效来自 CC0 的 Kenney Interface Sounds，来源和原始文件映射见 `assets/audio/THIRD_PARTY.md`。视觉特效仍保持程序化实现，包括吃子、将军、绝杀、困毙和“双车错”提示；音效统一走设置页的 SFX 总线。

Windows x64 已接入 Steamworks SDK 1.65、好友 Lobby 邀请和 Steam Networking Messages P2P 传输，当前用 Spacewar App ID `480` 测试。对局会显示 Steam 昵称、SteamID64 与头像；设置页可覆盖 ID/头像，自定义内容经有界的可靠资料消息发送，图片文件路径不会离开本机。真实 Steam 客户端上的 API 初始化和连续创建/退出 Lobby 已通过；完整双人邀请与远端头像仍需两个在线账号/设备。App ID 480 不能用于正式发布，取得本游戏 App ID 后仍需替换并做双端验收。开发运行与版本锁定方式见 `docs/steam-integration.md`。
