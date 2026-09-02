# 中国象棋项目技术架构（Agent 入口）

> 本文是项目的技术事实入口，供后续 Agent、维护者和扩展功能开发者快速建立完整心智模型。修改分层边界、网络协议、核心生命周期或目录结构时，应同步更新本文。

## 1. 项目定位与硬性约束

本项目是一款使用 Godot 4.5 Mono 与 C# 实现的中国象棋游戏，支持本地双人、ENet 局域网和 Steam 好友 Lobby/P2P 对弈。当前标准玩法包含完整基础棋规、计时、超时判负、聊天、悔棋协商、和棋协商、结算动画、棋子动画、音效、特殊局面特效及玩家资料交换。

必须长期保持以下约束：

- 所有逻辑和界面脚本都使用 C#；项目不使用 GDScript。
- `Xiangqi.Core` 不引用 Godot、Steamworks.NET 或任何具体 UI/网络实现。
- 棋规只由 `ChessGame` 判定；UI 和网络层不得复制或绕过棋规。
- UI 只提交用户意图并消费不可变快照/语义事件，不直接写棋盘状态。
- ENet 与 Steam 必须实现同一个 `IMatchTransport`，并承载相同的 `MatchWireProtocol`。
- Steam 代码只在 `GODOT_WINDOWS` 下编译；Android 不能引用 Steamworks.NET 或 `steam_api64.dll`。
- 外部收到的数据必须先做长度、枚举、坐标、序号和局面版本校验。
- Godot `Node`、纹理、Steam 回调和网络连接均需要明确的所有者与释放边界。

## 2. 技术栈与构建目标

| 部分 | 技术/版本 | 说明 |
| --- | --- | --- |
| 引擎 | Godot 4.5.1 Mono | `project.godot` 的主场景是 `src/Presentation/Main.tscn` |
| 展示层 | C# + Godot .NET API | UI 主要由 C# 代码创建，场景文件只保留根节点入口 |
| 桌面目标 | .NET 8 | 编辑器、Windows、Core 与测试默认目标 |
| Android 目标 | .NET 9 | `GodotTargetPlatform=android` 时主项目条件切换 |
| 局域网 | Godot `ENetMultiplayerPeer` | UDP，默认端口 `27888`，房间最多两人 |
| Steam | Steamworks SDK 1.65 + Steamworks.NET | Windows x64；开发阶段使用 Spacewar App ID 480 |
| 测试 | 独立 .NET 控制台测试 | 不依赖 Godot，不使用第三方测试框架 |

关键工程文件：

- `Xiangqi.sln`：主项目、核心库、核心测试三个工程。
- `Xiangqi.csproj`：Godot C# 主项目，只显式包含 `src/Presentation/**/*.cs`。
- `src/Xiangqi.Core/Xiangqi.Core.csproj`：纯 C# 领域/应用库。
- `tests/Xiangqi.Core.Tests/Xiangqi.Core.Tests.csproj`：可直接运行的回归测试程序。
- `project.godot`：Godot 项目、窗口尺寸与兼容渲染器设置。
- `export_presets.cfg`：Windows Desktop 与 Android 导出预设。

## 3. 总体分层与依赖方向

```text
Godot 输入 / Steam 回调 / ENet 数据包
                 │
                 ▼
Presentation + Infrastructure Adapters
Main / Screens / BoardView / Effects / Audio / Profiles / Networking
                 │  只调用门面、命令、快照、接口
                 ▼
Application
GameSession / NetworkGameCoordinator / MatchClock / MatchWireProtocol
                 │
                 ▼
Domain
ChessGame / BoardState / Pieces / IPieceRule / 标准棋规
```

依赖只能向下：

- `Domain`：零外部依赖，拥有权威棋盘和规则。
- `Application`：依赖 `Domain`，将领域操作组织成 UI/网络可调用的用例，并发出语义事件。
- `Presentation`：依赖 `Application` 和 `Domain` 的只读类型，负责 Godot 节点、输入与渲染。
- `Presentation/Networking`：是基础设施适配器，依赖传输接口和协议，但不能决定棋步是否合法。

项目采用“端口与适配器 + Godot 事件驱动”的解耦方式，思想上类似 MVVM：`GameSnapshot` 相当于只读状态，`GameSession`/`NetworkGameCoordinator` 相当于命令与协调层，Godot `Control` 只是视图和输入适配器。没有为了形式而引入绑定框架。

## 4. 目录与文件职责

```text
.
├─ assets/
│  └─ audio/                     落子、吃子、选择音效及第三方许可
├─ docs/
│  ├─ architecture.md            架构摘要和当前规则边界
│  └─ steam-integration.md       Steam 配置、测试和发布注意事项
├─ src/
│  ├─ Xiangqi.Core/
│  │  ├─ Domain/                 权威领域模型与棋规
│  │  │  └─ Rules/               棋子规则接口、注册表、标准棋子规则
│  │  └─ Application/            对局门面、网络协调、时钟、协议、表现语义
│  └─ Presentation/
│     ├─ Main.cs                 组合根、路由、平台服务初始化、调试入口
│     ├─ Main.tscn               Godot 主场景入口
│     ├─ BoardView.cs            棋盘绘制、坐标变换、选中/移动动画
│     ├─ Screens/                主菜单、玩法 Gallery、设置、对局屏幕
│     ├─ UI/                     共享主题和控件工厂
│     ├─ Audio/                  语义事件到音效的映射
│     ├─ Effects/                语义事件到视觉特效/结算动画的映射
│     ├─ Profiles/               玩家资料快照、头像转换与资源所有权
│     └─ Networking/             ENet、Steam Lobby、Steam P2P 适配器
├─ tests/Xiangqi.Core.Tests/     核心棋规与协议回归测试
├─ third_party/Steamworks.NET/   固定版本的托管绑定、许可和版本说明
├─ steam_api64.dll               Steamworks SDK Windows x64 原生运行库
├─ Xiangqi.csproj
├─ Xiangqi.sln
└─ project.godot
```

`.godot/`、`bin/`、`obj/`、`build/`、`export/` 和根目录导出数据目录都是生成物，不得提交。`steam_appid.txt` 是本地 Debug/Spacewar 标记，也不得提交到正式版本历史。

## 5. 领域层：棋盘、棋子与权威棋局

### 5.1 坐标系

`BoardPosition(File, Rank)` 使用逻辑坐标：

- `File` 为 0..8，共 9 路。
- `Rank` 为 0..9，共 10 行。
- 红方初始区域靠近低 Rank，红兵前进方向为 `+1`。
- 黑方初始区域靠近高 Rank，黑卒前进方向为 `-1`。

坐标方向属于领域事实；屏幕上下翻转属于 `BoardView` 的展示职责。不得为了黑方视角修改领域坐标或网络坐标。

### 5.2 棋子抽象

`ChessPiece` 由三部分组成：

- `PieceInstanceId`：单局内稳定身份，移动后不变，供动画和未来回放引用。
- `PieceSide`：红/黑阵营。
- `PieceKind`：字符串值对象，而不是枚举。

`PieceKind` 刻意不用 enum，使新棋子可以通过新字符串标识和规则注册扩展，而不必改变领域模型或既有枚举序列。内置种类定义在 `BuiltInPieceKinds`：`general`、`advisor`、`elephant`、`horse`、`chariot`、`cannon`、`soldier`。

### 5.3 棋盘状态

`BoardState` 实现 `IReadOnlyBoard`，负责：

- 9×10 边界与位置查询；
- 棋子移动和吃子；
- 直线两点之间的阻挡计数；
- 克隆，用于试走和历史快照。

棋子具体规则只接收 `IReadOnlyBoard`，因此规则对象不能直接修改棋盘。

### 5.4 棋子规则接口

`IPieceRule` 只回答某一棋种的局部几何与阻挡是否合法：

```csharp
PieceKind Kind { get; }
bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to);
```

`PieceRuleRegistry` 将 `PieceKind` 映射到规则实现。标准实现包括将/帅、士/仕、象/相、马、车、炮、兵/卒。轮次、己方棋子占位、不能送将、将帅照面和终局判定不属于单个棋子的职责，统一由 `ChessGame` 处理。

### 5.5 权威聚合 `ChessGame`

所有本地点击、远端命令、未来 AI、棋谱回放都必须最终调用 `ChessGame`，不能直接调用 `BoardState.Move`。

一次着法的验证顺序为：

1. 检查起终点是否在棋盘内。
2. 检查起点是否有棋子。
3. 检查是否轮到该阵营。
4. 检查终点是否为己方棋子。
5. 调用对应 `IPieceRule` 检查棋种几何/阻挡。
6. 克隆棋盘并试走，检查己方将帅是否受攻击。
7. 成功后压入 Memento、执行移动并增加 `PlyNumber`。
8. 切换行棋方，重新计算将军和是否存在合法着法。
9. 无合法着法时按是否被将军判定将死或困毙。

`GameSnapshot` 是对外只读状态，包含排序后的棋子列表、当前行棋方、将军状态、终局状态、胜方、结束原因与半回合编号。UI 与网络协调器只能基于快照显示或校验，不能持有领域内部 `BoardState` 引用。

悔棋通过内部 Memento 栈恢复完整状态；超时和双方同意和棋也由 `ChessGame` 写入权威终局状态。

## 6. 应用层：用例、计时与语义输出

### 6.1 `GameSession`

`GameSession` 是 UI 面向的单局门面：

- 接受选择查询与 `TryMove`；
- 暴露不可变 `Snapshot`；
- 提供 `Undo`、`NewGame`、`DeclareTimeout`、`DeclareDraw`；
- 通过 `StateChanged` 发布新状态；
- 通过 `CueRaised` 发布表现语义。

UI 不根据吃子、将军或终局自行猜测特效，而是消费 `PresentationCue`。

### 6.2 表现语义与特效扩展

`PresentationCue` 包含：

- `Kind`：Move、Capture、Check、Checkmate、Stalemate、IllegalMove、Undo、NewGame、SpecialPattern、Timeout、Draw。
- 可选 `Move`、`Subject`、`Message`、`CueId`。

`IPositionPatternDetector` 在已完成的合法着法后观察前后快照，只返回语义提示，不能改变合法性或状态。`DoubleChariotMateDetector` 是当前“双车错”识别器。

增加新特效时优先新增 detector/cue，再由展示层决定如何播放；不要在规则类里创建 Godot 节点、播放声音或写 UI 文本。

### 6.3 棋钟

`TimeControl(GameSeconds, MoveSeconds)` 中零表示不限时。`MatchClock` 是纯 C# 状态机：

- `Advance(delta)` 扣减当前方局时和步时；
- 任一启用的时间耗尽时返回超时方；
- `BeginTurn` 切换行棋方并重置步时；
- `Apply` 让客户端应用房主快照。

联机时房主是时间和超时判负的权威端；客户端只平滑显示并接收 `ClockSyncCommand` 校正。当前同步间隔由 `GameScreen` 控制，约 0.25 秒。

### 6.4 `NetworkGameCoordinator`

协调器将一个 `GameSession` 与任意 `IMatchTransport` 组合：

- 本地着法先经核心验证，成功后发送带序号的 `MoveCommand`。
- 远端着法必须满足期望的下一个序号，再走同一个 `GameSession.TryMove` 验证路径。
- 远端超时命令必须绑定当前 `PlyNumber`。
- 聊天仅传递规范化文本，不影响棋局。
- 悔棋/和棋申请包含请求 ID、申请类型与发起时的 `PlyNumber`。
- 有申请待处理时暂停走子，展示层同步暂停棋钟。
- 悔棋只允许“本地刚走完且对方尚未落子”时发起；接收端也重复验证。
- 过期、并发、无法匹配或越权的申请失败关闭，不尝试自动合并。

`NetworkGameCoordinator` 负责协议语义和局面一致性；具体套接字、Steam API、UI 对话框均不属于它。

## 7. 展示层与 UI 结构

### 7.1 组合根和页面路由

`Main.tscn` 只实例化 `Main.cs`。`Main` 是应用组合根，负责：

- 加载并应用 `AppSettings`；
- 初始化 Windows Steam 服务；
- 构造页面并切换当前 `Control`；
- 创建 ENet/Steam transport 并注入 `GameScreen`；
- 把自定义玩家资料组合到平台默认资料；
- 提供 Debug 冒烟测试入口。

页面之间不直接引用彼此，通过 `Action` 回调报告“进入 Gallery”“保存设置”“返回”等导航意图。`SwitchTo` 统一从场景树移除旧页面并 `QueueFree`。

导航结构：

```text
MainMenuScreen
├─ 玩法选择 → GalleryScreen
│  ├─ 本地双人 → GameScreen
│  ├─ 创建/加入 ENet → GameScreen
│  └─ 创建 Steam Lobby → 等待好友 → GameScreen
├─ 设置 → SettingsScreen → 保存/取消 → MainMenuScreen
└─ 退出
```

### 7.2 页面职责

- `MainMenuScreen`：标题、玩法、设置、退出；不持有游戏状态。
- `GalleryScreen`：标准中国象棋玩法卡片、局时/步时、先后手或随机猜先、LAN 地址/端口、Steam 创建入口。
- `SettingsScreen`：编辑 `AppSettings` 的副本，保存时一次性提交；支持画面、音量、特效、自定义 ID 和头像路径。
- `GameScreen`：对局展示协调器，连接棋盘、时钟、网络、聊天、协商、音频、特效和结算 UI；它不实现棋规。

### 7.3 对局界面布局

`GameScreen` 主要由以下区域组成：

```text
顶部导航/连接状态/申请按钮
┌────────────────────────棋盘列──────────────────────┬──联机消息栏──┐
│ 左侧起始的对方头像、昵称、ID                 对方时钟 │ 聊天记录     │
│                  BoardView                         │ 输入与发送   │
│ 左侧起始的本地头像、昵称、ID                 本地时钟 │             │
└────────────────────────────────────────────────────┴──────────────┘
```

本地玩家无论执红或执黑都显示在棋盘下方。`ConfigureNetworkMatch` 设置 `_localSide` 并写入 `BoardView.BottomSide`，仅改变渲染映射，不改变逻辑坐标。

### 7.4 `BoardView`

`BoardView` 是纯展示/输入适配节点：

- `_Draw` 程序化绘制棋盘、河界、九宫线、棋子、选中高亮和合法位置。
- `_GuiInput` 把鼠标屏幕坐标转换为领域 `BoardPosition`。
- `BottomSide` 控制红/黑视角转换。
- `SetPosition` 显示权威快照。
- `AnimateMove(before, after, move, captured)` 对移动棋子做平滑插值与抬升轨迹。
- 动画期间权威状态已经是 `after`，只是在视觉上暂时隐藏目标棋子并绘制移动副本。
- `MoveAnimationFinished` 通知 `GameScreen` 播放普通落子或吃子音效。

`GameScreen` 会在移动发生时缓存前后快照和着法，确保本地与远端着法走同一动画路径。动画未完成时阻止重复输入。棋子圆弧段数和计时标签更新已做热路径控制，避免不必要的逐帧布局与绘图开销。

### 7.5 UI 样式

`UiStyle` 集中定义颜色、Panel、Button、Title、PageMargin 和 muted label。新增页面或控件应优先复用这里的工厂，避免散落颜色/字号。对局棋盘自身颜色属于 `BoardView` 的局部视觉系统。

### 7.6 设置持久化

`AppSettings` 使用 `ConfigFile` 保存到 `user://settings.cfg`：

- display：全屏、垂直同步、帧率上限、特效开关；
- audio：Master、Music、SFX 音量；
- profile：自定义玩家 ID、头像文件路径。

设置页编辑的是 `Copy()`，只有点击保存才 `SaveAndApply()`；取消不会残留半应用状态。头像路径只保存在本机，绝不通过网络发送。

## 8. 音频与视觉特效

### 8.1 音频

`GameplayAudioPlayer` 预加载并管理选择、落子、吃子三类音频播放器：

- 选中棋子时调用 `PlaySelection()`；
- 移动动画完成后按是否吃子调用 `PlayLanding(captured)`；
- 其他语义可通过 `PlayCue` 扩展。

所有效果音输出到 `SFX` 总线，由设置页控制。素材位于 `assets/audio/`，来源和许可证记录在 `assets/audio/THIRD_PARTY.md`。替换素材时保持语义文件名或更新唯一映射点。

### 8.2 特效

`CueEffectLayer` 接收 `PresentationCue`，负责全屏标题、颜色、放射线、淡入淡出和结算动画。`EffectBurstView` 是程序化放射效果节点。胜利、失败与和棋均走 `PlaySettlement`，本地视角由 `GameScreen` 根据 `Winner` 与 `_localSide` 计算。

效果系统分两段：

1. Core detector 决定“发生了什么”（例如 `double_chariot_mate`）。
2. Presentation 决定“怎么表现”（文字、粒子、声音、镜头等）。

这样未来可替换美术表现而不影响棋规和网络确定性。

## 9. 玩家资料与资源所有权

### 9.1 数据模型

- `PlayerProfileSnapshot`：平台、显示 ID、昵称、可选 RGBA8 头像。
- `PlayerAvatarPixels`：拥有临时 byte 数组；`Dispose` 会清零并释放引用。
- `MatchPlayerProfiles`：拥有本局双方快照，可以替换本地/对方资料，并统一释放。
- `CustomPlayerProfileFactory`：读取本机图片，转换 RGBA8 并缩放为最多 64×64；生成有界网络命令。

数据对象故意不保存 Godot `Texture2D`、Steam image handle 或本机路径。平台获取、协议传输、渲染资源三种生命周期彼此分离。

### 9.2 默认与自定义优先级

Steam 对局：

1. `SteamLobbyService` 根据 Lobby 成员 SteamID 查询 Steam 昵称、SteamID64 和大头像。
2. 大头像尚未下载时等待 `AvatarImageLoaded_t`，最多约 3 秒后回退到中头像/文字头像。
3. 本地若配置自定义 ID 或头像，则本地显示自定义值并通过 P2P 发送给对方。
4. 对方收到自定义资料后，只替换本局顶部资料；Steam 默认资料仍是安全回退。

LAN 对局使用通用默认身份；如有自定义资料，同样通过 `IMatchTransport` 发送。因此资料同步不与 Steam API 耦合。

### 9.3 释放边界

- Steam 服务等待期的 `_profileCache` 由 `SteamLobbyService` 拥有。
- `MatchReady` 后所有权转交 `MatchPlayerProfiles`，服务清空缓存但不销毁已转交数据。
- `GameScreen` 把像素转换为 `ImageTexture`，纹理由 `_profileTextures` 拥有。
- 断线、故障、退出对局或 `_ExitTree` 时，先解绑纹理，再 Dispose 纹理和像素。
- 房间结束后 UI 显示“临时资料已释放”，不能继续引用对手头像。

## 10. 网络抽象

### 10.1 `IMatchTransport`

传输接口只发送/接收类型化命令并报告连接状态：

- Move
- MatchConfiguration
- Timeout
- ClockSync
- Chat
- MatchOffer / MatchOfferResponse
- PlayerProfile

接口实现必须：

- 保持可靠、有序的对局消息语义；
- 将收到的字节交给 `MatchWireProtocol.TryDecode`；
- 拒绝不合法包，不把未经校验的对象抛给协调器；
- 实现幂等 `DisposeAsync`；
- 在断线/故障时更新 `IsConnected` 并发出 `StatusChanged`。

未来 SteamSockets、WebSocket 或专用服务器适配器都应实现此接口，而不是让 `GameScreen` 添加平台分支。

### 10.2 ENet 实现

`EnetMatchTransport`：

- 房主 `CreateServer(port, 1)`，仅允许一个远端玩家；
- 客户端 `CreateClient(address, port)`；
- 使用 Reliable 传输模式；
- 记录唯一 `_remotePeerId`，拒绝额外连接；
- 每帧 `Poll` 并处理可用包；
- 默认 UDP 端口为 `27888`；合法用户端口范围为 1024..65535。

### 10.3 Steam Lobby 与 P2P

`SteamLobbyService` 是进程内唯一 SteamAPI 所有者：

- `SteamAPI.InitEx`/`Shutdown`；
- 每帧 `SteamAPI.RunCallbacks`；
- 创建 FriendsOnly、最多两人的 Lobby；
- 写入本项目标记、协议版本、局时、步时和房主阵营；
- 处理 Overlay 邀请和 `+connect_lobby` 启动参数；
- 验证 Spacewar 公共环境中的 Lobby 确实属于本项目；
- 获取双方 Steam 资料；
- 两名成员和配置都就绪后创建 `SteamMatchTransport`；
- 等待房间取消、传输关闭或对方退出时调用 `LeaveLobby`。

Lobby metadata 键：

| 键 | 含义 |
| --- | --- |
| `xq_product` | 固定产品标记 `csharp-xiangqi` |
| `xq_protocol` | `MatchWireProtocol.Version` |
| `xq_game_seconds` | 局时秒数，0 为无限 |
| `xq_move_seconds` | 步时秒数，0 为无限 |
| `xq_host_side` | 房主阵营的 byte 值 |

`SteamMatchTransport` 使用 Steam Networking Messages：

- 只接受构造时确定的对手 SteamID；
- 固定 MatchChannel；
- 使用 `k_nSteamNetworkingSend_Reliable`；
- 收包后验证发送者、包长、协议版本和消息内容；
- Dispose 时关闭与该用户的 channel 并释放回调；
- session failed 后延迟到下一帧释放，避免在 Steam 回调栈内销毁回调对象。

Steam 只在 Windows 编译。Android 构建通过条件引用彻底排除托管绑定和原生 DLL。

## 11. 二进制网络协议

### 11.1 通用帧

所有消息使用小端二进制格式：

```text
byte 0: ProtocolVersion（当前为 3）
byte 1: MatchWireMessageType
byte 2..N: 类型专属 payload
```

全局最大包长为 16600 字节。协议解码是封闭 switch，不使用对象反序列化、反射类型名或任意 JSON 对象。

### 11.2 消息类型

| Type | 值 | Payload | 校验重点 |
| --- | ---: | --- | --- |
| Move | 1 | sequence:int64 + from/to 各两个 int32 | 固定 26 字节；序号 > 0；坐标在棋盘内 |
| Configuration | 2 | gameSeconds:int32 + moveSeconds:int32 + hostSide:byte | 固定 11 字节；时间非负；阵营枚举有效 |
| Timeout | 3 | ply:int64 + loser:byte | 固定 11 字节；ply 非负；阵营有效 |
| ClockSync | 4 | ply:int64 + 三个 double + side:byte | 固定 35 字节；有限、非负；阵营有效 |
| Chat | 5 | utf8Length:uint16 + UTF-8 | 非空、无控制字符、最多 512 UTF-8 字节 |
| MatchOffer | 6 | requestId:int64 + ply:int64 + kind:byte | 固定 19 字节；ID > 0；ply 非负；种类有效 |
| MatchOfferResponse | 7 | 上述字段 + accepted:bool | 固定 20 字节，且必须匹配本地待处理申请 |
| PlayerProfile | 8 | ID、昵称、宽高、RGBA 长度和数据 | 文字各最多 96 UTF-8 字节；头像最多 64×64 RGBA8 |

修改任何消息布局时必须：

1. 提升 `MatchWireProtocol.Version`。
2. 同步所有 transport 的 dispatch switch。
3. 更新 `IMatchTransport` 和 FakeTransport。
4. 增加 RoundTrip 与畸形包测试。
5. 更新 Steam Lobby 的版本兼容检查和本文档。

### 11.3 一致性策略

网络不发送任意棋盘快照来覆盖本地状态。双方从相同标准开局出发，所有着法都由本地 `ChessGame` 复验。当前一致性依靠：

- 严格递增的 `MoveCommand.Sequence`；
- 超时和申请绑定 `PlyNumber`；
- 申请期间停止新着法；
- 房主权威时钟。

未来断线重连应先增加稳定状态哈希、握手和受验证的重同步协议，而不是直接信任远端序列化对象。

## 12. 关键运行时流程

### 12.1 本地落子

```text
BoardView.SquarePressed
→ GameScreen 选择/目标处理
→ GameSession.TryMove
→ ChessGame.ValidateMove + 状态变更
→ CueRaised / StateChanged
→ GameScreen 缓存前后 Snapshot
→ BoardView.AnimateMove
→ MoveAnimationFinished
→ GameplayAudioPlayer.PlayLanding
```

### 12.2 联机落子

```text
本地输入
→ NetworkGameCoordinator.SubmitLocalMoveAsync
→ 本地 ChessGame 验证并提交
→ transport.SendMoveAsync(sequence, move)
→ 对端 transport 解码
→ NetworkGameCoordinator 校验 sequence
→ 对端同一 GameSession.TryMove 再验证
→ 双方分别从 StateChanged 驱动相同动画
```

### 12.3 Steam 房间

```text
Gallery 创建房间
→ SteamLobbyService.CreateLobby
→ 写入 metadata
→ 打开 Steam 邀请 Overlay
→ 好友进入 / 命令行 connect_lobby
→ 验证成员数与 metadata
→ 请求双方 Steam 资料
→ 创建 SteamMatchTransport
→ MatchReady
→ Main 注入 GameScreen
→ 发送配置/自定义资料
→ 对局
→ 退出、掉线或 session failure
→ Dispose transport + LeaveLobby + 释放资料
```

### 12.4 悔棋/和棋

```text
申请方校验资格和当前 ply
→ 发送 MatchOffer
→ 双方暂停落子和棋钟
→ 接收方再次校验请求与局面
→ 接受/拒绝
→ MatchOfferResponse 严格匹配 requestId/kind/ply
→ 接受悔棋：双方 GameSession.Undo
→ 接受和棋：双方 GameSession.DeclareDraw
→ 恢复控制或播放和棋结算
```

## 13. 生命周期、线程与错误处理

### 13.1 Godot 生命周期

- 构造函数只创建/组合节点和字段；依赖场景树的逻辑放在 `_Ready`。
- `_Process` 只做必须的回调泵、时钟和动画推进，避免文件 I/O、同步网络等待和大对象分配。
- `_ExitTree` 必须解绑事件并释放网络、纹理和服务。
- `QueueFree` 用于 Godot 节点；实现 `IDisposable`/`IAsyncDisposable` 的托管/原生包装还要显式释放。

### 13.2 事件解绑

订阅以下长生命周期对象时必须在同一所有者内解绑：

- `GameSession.StateChanged/CueRaised`；
- `BoardView` 输入/动画事件；
- `IMatchTransport` 状态和命令事件；
- `NetworkGameCoordinator` 语义事件；
- Steamworks.NET `Callback<T>` / `CallResult<T>`。

### 13.3 异步与回调

Godot 和 Steam 回调以主线程/帧循环为边界。不要在 Steam 原生回调执行栈内立即销毁其回调对象；需要时等待一个 `ProcessFrame`。`DisposeAsync` 应幂等，重复退出页面、远端掉线和主进程退出不能导致二次释放异常。

### 13.4 Debug 输出

对局状态不占用正式 UI 的独立面板；Debug 构建可打印到控制台。用户可见的可恢复错误通过 `AcceptDialog` 或连接状态标签显示。收到无法信任的网络数据时应拒绝并报告同步失败，不能尝试猜测修复棋局。

## 14. 扩展指南

### 14.1 添加新棋子

1. 创建稳定的新 `PieceKind("...")`。
2. 实现 `IPieceRule`，只写局部几何和阻挡。
3. 在目标玩法使用的 `PieceRuleRegistry` 注册。
4. 在对应开局工厂放置棋子；不要硬改标准开局，建议未来引入玩法定义对象。
5. 为 `BoardView.GetPieceLabel` 或未来皮肤映射添加显示。
6. 添加合法走法、阻挡、送将和联机确定性测试。

如果新棋子需要全局规则（例如影响多枚棋子），不要把副作用塞入 `CanMove`；应扩展领域聚合的统一验证/结算阶段。

### 14.2 添加新特殊特效

1. 实现 `IPositionPatternDetector`。
2. 返回具有稳定 `CueId` 的 `PresentationCue(SpecialPattern)`。
3. 在创建 `GameSession` 时注册 detector。
4. 在 `CueEffectLayer` 和可选音频映射中增加表现。
5. 写 detector 的纯 C# 测试。

### 14.3 添加新传输后端

1. 实现 `IMatchTransport`。
2. 原样使用 `MatchWireProtocol` 编解码。
3. 明确可靠、有序语义和最多一个对手。
4. 校验远端身份并拒绝第三方数据。
5. 实现连接状态事件和幂等释放。
6. 在 `Main`/Gallery 的组合根添加创建入口，不修改规则/UI 命令模型。

### 14.4 添加新协议消息

遵循第 11.2 节的五项要求。只有跨端必须共享的语义才进入 Core 协议；Godot 节点路径、颜色、音频资源路径、Steam handle 等展示/平台细节不得进入协议。

### 14.5 添加新玩法

建议新增玩法定义抽象，至少提供：

- 开局状态工厂；
- `PieceRuleRegistry` 工厂；
- 玩法 ID 与协议兼容版本；
- 可用 detector 列表；
- Gallery 展示信息。

不要让 `GalleryScreen` 直接拼装棋子或让 `ChessGame.CreateStandard` 演变为大量平台/UI 分支。

## 15. 测试与验证

构建与核心测试：

```powershell
dotnet build .\Xiangqi.sln
dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj -c Debug
```

Godot 路径由环境变量 `GODOT_PATH` 提供。项目内置 Debug 冒烟参数：

| 参数 | 用途 |
| --- | --- |
| `--smoke-ui` | Gallery、设置、本地对局实例化 |
| `--smoke-profile-ui` | 双方资料卡和临时头像纹理 |
| `--smoke-lan-host/client` | 双进程 LAN 连接、同步与聊天 |
| `--smoke-timeout-host/client` | 双端计时和超时裁决 |
| `--smoke-steam-init` | SteamAPI、App ID、当前用户 |
| `--smoke-steam-lobby` | 创建、退出、再次创建 Spacewar Lobby |

修改范围对应的最低验证：

- Domain/Application：运行全部 Core 测试。
- UI/动画：Windows 构建 + 对应 Godot 实机冒烟。
- 协议/transport：Core 协议测试 + 双进程 LAN 冒烟。
- Steam：Windows 条件构建 + 登录 Steam 后相关 smoke；双人功能最终需要两个账号/客户端。
- 跨平台代码：再执行 Android arm64 条件构建，确认 Steam 引用未泄漏。

## 16. 当前边界与后续方向

已知未实现/未定规则：

- 长将、长捉、重复局面等赛事规则尚未实现，需要先确定采用的规则版本。
- AI、棋谱记谱/回放、观战和断线重连尚未实现。
- Steam 当前使用 App ID 480，只能用于开发测试；正式发布必须完成 Steam Direct、取得自有 App ID 并做双账号验收。
- 当前网络使用确定性命令同步，没有状态哈希或快照恢复。
- 自定义头像固定为 64×64 RGBA8，以控制单包上限；未来若提高分辨率应设计分片、压缩和速率限制。

推荐演进顺序：玩法定义抽象 → 棋谱/状态哈希 → 断线重连 → AI/匹配服务 → Steam 正式 App ID 与部署配置。

## 17. Agent 修改检查清单

开始修改前：

- 确认目标属于 Domain、Application、Presentation 还是 Infrastructure。
- 搜索是否已有可复用抽象，避免第二套棋规、协议或样式系统。
- 检查 `GODOT_WINDOWS` 边界和 Android 兼容性。
- 明确新增 Node、事件、纹理、byte buffer、transport 的所有者。

提交前：

- 不包含 GDScript。
- Core 没有新增 Godot/Steam 依赖。
- UI 没有直接修改权威棋盘。
- 网络输入全部有界且失败关闭。
- 新订阅都有解绑，新资源都有释放。
- 协议变化已提升版本并补测试。
- 至少完成与修改范围对应的构建/测试。
- 未提交 `.godot/`、`bin/`、`obj/`、导出产物、日志、崩溃转储或 `steam_appid.txt`。
- 若架构事实发生变化，已同步更新本文。
