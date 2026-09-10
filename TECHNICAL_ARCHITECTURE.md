# 项目技术架构（Agent 与维护者入口）

更新：2026-09-10。本文描述当前代码，不把规划中的接口当成已经落地的功能。开发约束见 [AGENTS.md](AGENTS.md)。

## 1. 总架构

### 1.1 项目范围与架构结论

项目使用 Godot 4.7.2 Mono + C#，包含五个独立玩法：

- 经典象棋：本地双人、ENet 局域网、Steam 好友联机、外接 UCI AI。
- 三国象棋：本地三人、TCP 局域网、Steam 三席邀请；包含收编、三方协商、进程内断线恢复和房主迁移。
- 揭棋：本地、LAN 与 Steam 双人；房主权威脱敏协议、Seed 承诺/赛后验证、确定性暗子与扩展 PGN。
- 军旗：独立 12×5 图拓扑、双人规则、热座/LAN/Steam、玩家视图脱敏及 AC2 `INFO[1.0]` 棋谱。
- 五子棋：可配方格交叉点棋盘、可选连珠禁手、本地/LAN/Steam、SGF FF[4]/GM[4] 记录/导入，以及桌面外接 Rapfi/Piskvork AI。

五种玩法各有领域模型与对局编排，共用部分值对象、时间配置、设置、UI 样式、皮肤和反馈特效。并没有统一的“任意人数/任意棋盘玩法引擎”。

检查结论：**核心棋规已与 UI/引擎分离；抽象层部分成熟，尚未达到所有对局流程与 UI 完全解耦。** 经典房间仍承担较多应用编排；三国的控制器查询面仍暴露可变对象，拓扑/投影接口尚未形成完整注入链。详见 [架构检查记录](docs/architecture-review.md)，不能将这些边界描述成已经修复。

### 1.2 编译工程与依赖方向

```text
Xiangqi（Godot 应用）
  ├── Presentation：页面、棋盘、输入、外观、动效、音频
  ├── 同程序集内的适配器：ENet、Steam、UCI、玩家资料
  ├── → Xiangqi.Core
  └── → Xiangqi.Transport → Xiangqi.Core

Xiangqi.Core
  ├── Application：会话、控制器/协调器、端口、命令、协议、棋钟
  └── Domain：棋局、占位、棋种规则、坐标/拓扑、结果快照

Xiangqi.Core.Tests → Xiangqi.Core
Xiangqi.ThreeNetwork.Tests → Xiangqi.Core + Xiangqi.Transport
```

箭头表示项目引用。基础设施依赖 Core 中定义的端口，**不是 Domain 依赖 Infrastructure**。Presentation/Networking 和 Presentation/AI 虽然物理上位于展示工程，职责仍是基础设施适配，不是 UI 控件逻辑。

| 工程/文件 | 当前职责和约束 |
| --- | --- |
| [Xiangqi.csproj](Xiangqi.csproj) | Godot.NET.Sdk/4.7.2；仅编译 src/Presentation/**/*.cs；桌面 net10.0，Android 条件 net9.0 |
| [Xiangqi.Core.csproj](src/Xiangqi.Core/Xiangqi.Core.csproj) | net9.0/net10.0；没有项目/第三方包引用；不引用 Godot、Steam 或传输实现 |
| [Xiangqi.Transport.csproj](src/Xiangqi.Transport/Xiangqi.Transport.csproj) | net9.0/net10.0；仅引用 Core；三国 TCP、签名握手、连接管理 |
| [Xiangqi.sln](Xiangqi.sln) | 上述三个工程与两个控制台测试工程 |
| [project.godot](project.godot) | 主场景 src/Presentation/Main.tscn；兼容渲染器 |
| [export_presets.cfg](export_presets.cfg) | Windows Desktop/Android 导出配置 |

Steam 托管引用与原生 DLL 受 Windows 目标条件控制；Steam 源码用 GODOT_WINDOWS 隔离。条件构建只证明编译边界，不等于 Android 设备运行或 Steam 实联验收。

### 1.3 每层允许做什么

| 层 | 可以做 | 不应做 |
| --- | --- | --- |
| Domain | 规则验证、占位变化、轮次、收编、胜负、撤销 | 创建节点/材质、读设置、发网络包、启动 AI、按屏幕像素判棋规 |
| Application | 用户意图、协商、身份/序号检查、计时、命令重放、语义输出 | 引用具体控件/Steam 句柄，复制棋种规则 |
| Infrastructure | 网络/进程/平台适配、输入解码、有界队列、身份认证、资源释放 | 跳过领域复验，直接覆盖棋盘，决定棋种走法 |
| Presentation | 布局、点击命中、选中/动画状态、读取快照、展示错误、提交意图 | 自行执行收编/淘汰，改快照，另写一套“合法着法”判断 |

UI 根据“轮到谁、是否协商、是否结束”关闭按钮属于交互反馈，但不构成安全或规则验证；提交端仍需重复校验。UI 调用查询获取合法目标不等于 UI 实现棋规。

现有代码的例外必须知晓：经典 GameScreen 同时编排棋钟/网络/AI；三国虽然遵守 Submit 写入口，但 Session/Game 的公开 API 尚未从类型上阻止 UI 绕过控制器。后续新增功能不得扩大这两处技术债。

### 1.4 共享数据流

输入位置 → 视图投影/命中 → 会话或控制器意图 → 领域验证 → 结果/快照/事件 → UI 刷新或播放动画。

- 逻辑坐标与屏幕坐标分开；缩放、翻转、皮肤不改变棋谱。
- 棋规以领域对象为唯一事实来源；动画期间的移动副本只属于视图。
- 揭棋暗子由 BoardView 绘制无文字木质背面；covered→具体棋种的平移与横向翻面共享同一进度、同时完成，不改变领域翻明时点。
- 规则/AI/网络输入统一复验，但两个玩法的实际编排入口不同，见第 2、3 节。
- 快照只用于读取。经典快照使用新数组隔离内部占位，但不是深度不可变集合；三国发布快照会复制并包装数组。会话棋钟/连接列表则是实时只读视图，不是历史快照。

### 1.5 文档路由与抽象索引

阅读顺序：本文总览 → 目标玩法文档 → 抽象使用手册 → 对应源代码与测试。

| 文档 | 内容与维护边界 |
| --- | --- |
| [抽象层使用手册](docs/abstractions.md) | 接口职责、调用条件、注入点、生命周期、示例、测试与当前限制 |
| [经典象棋架构](docs/architecture.md) | 在原有架构摘要上更新；经典领域、四种房间入口、协议、AI 与视图协作 |
| [三国象棋](docs/three-kingdoms.md) | 已确认规则、拓扑、会话/控制器/传输、迁移重连与使用注意事项 |
| [揭棋玩法](docs/jieqi.md) | 暗/明身份、槽位首步、确定性洗牌、棋谱与联机保密边界 |
| [军旗玩法](docs/junqi.md) | 12×5 图拓扑、布阵/战斗、控制器、可信房主模型、AC2 棋谱与 UI 隐私 |
| [五子棋玩法](docs/gomoku.md) | 通用方格几何、禁手、控制器、可信房主协议、SGF 和 Rapfi AI 边界 |
| [架构检查记录](docs/architecture-review.md) | 2026-09-07 代码证据、分层评估、待改进项与验证范围 |
| [Steam 集成](docs/steam-integration.md) | 平台配置、开发 App ID、验收；三国细节路由到对应玩法文档 |
| [Godot 4.7 / .NET 10 迁移记录](docs/godot-4.7-migration.md) | 引擎与运行时配置、API 取舍、兼容依据、验证证据与剩余风险 |
| [坐标方案](docs/three-kingdoms-coordinate-proposal.md) | 三国历史设计依据；已确认实现以 three-kingdoms.md 和代码为准 |
| [AGENTS.md](AGENTS.md) | 全仓库 Agent 开发与交付约束 |
| [XML 主题包与 shader 接口](docs/theme-package-proposal.md) | 已实现的 schema v1、双目录、安全边界、atlas/uniform 契约与当前限制 |

现有主要抽象：

| 变化点 | 经典 | 三国 | 真实可替换范围 |
| --- | --- | --- | --- |
| 棋种规则 | IReadOnlyBoard / IPieceRule / PieceRuleRegistry | IReadOnlyThreeBoard / IThreePieceRule / ThreePieceRuleRegistry | 领域支持注册棋种；正式房间的配置注入仍有限 |
| 空间结构 | BoardPosition / BoardState（固定 9×10） | BoardCellId / IBoardTopology / KingdomSquare | 三国规则读取拓扑端口，但聚合仍固定 135 点与具体拓扑 |
| UI 对局入口 | GameSession + NetworkGameCoordinator / AiGameCoordinator | IThreeMatchController | 经典没有统一房间控制器；三国查询面尚未收窄 |
| 传输 | IMatchTransport（经典类型化消息）/ ITwoPlayerPacketTransport（新玩法原始包） | IThreeRoomTransport（固定三席、原始应用包） | 双人后端可复用，但玩法协议/权威模型不可混用 |
| AI | IXiangqiAiEngine | 未接入 | 当前端口面向经典 GameSnapshot/ChessMove |
| 特殊局面提示 | IPositionPatternDetector / PresentationCue | 未接入同一 detector 流水线 | detector 是经典坐标语义，不是通用三国接口 |
| 视觉反馈 | BoardFeedbackLayer / PieceFeedback shader | 同左，并复用移动插值/选中浮动语义 | 接收像素标记与主题色；动画只持有视图快照 |
| 展示主题 | IBoardTheme / IBoardThemeCatalog / ThemePackageCatalog | 同左 | XML 双目录、字符串 ID、手动重扫；资源只在 Godot 主线程由视图租约创建 |
| 投影 | BoardView 内部计算 | IBoardProjection / ThreeBoardProjection | 接口已定义，ThreeBoardView 仍直接构造具体投影 |
| 房间消息 | RoomChatManager | 同左 | 共享有界消息板、目标可见性与验证；各玩法协议仍分别适配 |

## 2. 经典象棋架构

详细说明与协议布局见 [经典象棋架构](docs/architecture.md)，具体接口用法见 [抽象层使用手册](docs/abstractions.md)。

### 2.1 领域模型与扩展

- BoardPosition(File, Rank)：9 路 × 10 行，红方底线逻辑 Rank=0，前进 +1；黑方底线 Rank=9，前进 -1。
- ChessPiece：PieceInstanceId + PieceSide + PieceKind。PieceKind 是字符串值对象，便于添加棋种；PieceSide 及 Opponent() 仅适用于双人。
- BoardState 拥有占位；棋种只接收 IReadOnlyBoard 查询。Move/Add/Clone 的可变能力留在 Core 内部。
- IPieceRule.CanMove 处理局部几何/阻挡，ChessGame 统一处理轮次、友子占位、不能送将、将帅照面、终局与历史撤销。
- ChessGame 构造接收局面和注册表；但 GameSession 的创建/重开均直接调用 CreateStandard，不能声称 UI 已能注入任意规则集。
- GameSnapshot 是展示/分析输入；修改快照不会改变权威占位，也不允许靠修改快照制造“落子”。

长将、长捉、重复局面等赛事裁决尚未实现；新增前需确认规则版本。

### 2.2 应用编排与四种入口

| 入口 | 权威写入路径 | 当前编排位置 |
| --- | --- | --- |
| 本地对决 | GameScreen → GameSession.TryMove → ChessGame | GameScreen 管选中、棋钟、重开/悔棋按钮 |
| LAN | GameScreen → NetworkGameCoordinator → GameSession；对端同样复验 | GameScreen + EnetMatchTransport（Godot Node） |
| Steam 好友 | 与 LAN 相同协调器/协议 | Main / SteamLobbyService 构造 SteamMatchTransport |
| 人机对决 | 人类经 SubmitHumanMove；AI 经 PlayAiTurnAsync → GameSession | AiGameCoordinator 校验搜索结果，GameScreen 调度时机 |

GameSession 发出 StateChanged 与 CueRaised；DoubleChariotMateDetector 通过 IPositionPatternDetector 接入，只观察局面、发出语义，不裁决着法。

MatchClock 是 Core 内的纯 C# 双人棋钟，但启动/暂停、主机超时权限、约 0.25 秒时钟同步和 AI 动画后调度仍在 GameScreen。这是“棋规分离已实现、房间编排分离尚不完整”的具体原因。

### 2.3 网络与 AI 边界

经典 IMatchTransport 搬运类型化消息，EnetMatchTransport 与 SteamMatchTransport 共享 MatchWireProtocol v6（小端、封闭消息类型、最大 16600 字节）；v6 配置帧增加通用棋盘尺寸与规则标志，供组合根路由五子棋。两适配器同时实现 `ITwoPlayerPacketTransport`，为新玩法搬运有界不透明包，但不解释 Seed、私密布阵或棋规。经典协调器仍验证序号、己方身份、申请 ID/局面手数，再调用同一个 GameSession。

经典 ENet 默认 UDP 27888，只允许一个远端；Steam 只接受确定的对手。断线结束当前经典网络流程，不具备三国的房主迁移/增量重连。

IXiangqiAiEngine 只接受局面和搜索参数，返回 BestMove/候选变化；UciProcessAiEngine 使用 shell-free `.NET Process`，工作目录固定为可执行文件目录，后台 stdout/stderr 只进入有界队列并由 Godot `_Process` 解析。AiGameCoordinator 检查局面手数、行棋方与结束状态，再复验着法；UndoHumanTurn 撤销人类最近一轮决策。当前思考期间不允许该悔棋入口，不能描述成已经支持任意时刻取消搜索并悔棋。

“向模型询问”当前是 UCI MultiPV 局面分析，不是自由文本对话。新的远程后端需要另外规划配置、鉴权/取消等；当前 Gallery 配置要求可执行文件路径，GameScreen 还要求引擎适配器是 Node，仅实现端口不足以直接替换正式 UI 后端。

## 3. 三国象棋架构

完整规则、连接协议细节及测试入口见 [三国象棋](docs/three-kingdoms.md)。本节聚焦各层如何协作。

### 3.1 拓扑、地域与领域聚合

- 三个 9×5 国家区域，通过 KingdomSquare(Region, File, Rank) 映射 BoardCellId：45×国家编号 + 9×Rank + File。
- ThreeKingdomsTopology 预计算 135 点、243 普通边、108 四边形与中央三角形，以及连续射线、四边形对角、马腿/象眼候选。
- IBoardTopology 不包含 Godot、像素、颜色或占位；规则把候选路径与 IReadOnlyThreeBoard 的占位/国土查询组合。
- ThreeKingdomsGame 拥有棋子数组、存活国家、国土所有权、轮次、收编/淘汰和单手 Memento；不借用经典的红黑 Opponent()。
- KingdomPiece 区分 BirthRegion、Owner、HasLeftBirthRegion。Owner=null 是中立障碍，不能捕获；显示色只读取 Owner，不反向参与规则。
- IThreePieceRule / ThreePieceRuleRegistry 可扩展局部走法；收编、全体无着和棋等全局副作用仍在聚合。
- 将被吃与认输/超时/断线负是不同路径：前者收编，后者移除将并留下障碍。两种士策略通过 AdvisorConquestPolicy 配置。

注意：地域换算和存储仍绑定 3×9×5；不能仅提供另一 IBoardTopology 就把棋盘换成任意形状。扩展新棋盘需同时设计地域查询、占位容量、开局与投影注入，详见抽象手册。

### 3.2 会话与统一控制器

ThreeMatchSession 组合 Game、三组棋钟、连接标记和 KingdomOffer。Move、Resign、RequestOffer、Vote、ForfeitDisconnected 等方法组织应用用例。普通协商冻结走子与计时；断线等待不冻结；悔棋不返还耗时。

IThreeMatchController 的两种实现：

- LocalThreeMatchController：本地热座，LocalKingdom=null；Submit 走 ThreeCommands.Apply，Tick 推进会话。
- ThreeNetworkController：绑定认证席位、等待满三人、管理提交队列/日志/迁移，再重放相同命令路径。

ThreeGameScreen 只发送玩家 ThreeCommand、读取状态和查询目标。网络 Submit 返回 null 表示已受理/排队，不保证已落子；必须等 Changed/快照变化更新棋盘，异步拒绝通过 Message 显示。Tick/Connection 是内部命令，UI 不得发送。

IThreeMatchController.Session 暴露具体会话，Session.Game 又暴露可变聚合：当前 UI 没有直接调用这些写方法，但接口只读边界是约定而非编译器保证。新增 UI 必须继续用 Submit，未来再将其收窄为只读查询端口。

### 3.3 三人同步、迁移与恢复

IThreeRoomTransport 提供 LocalId、固定名册、Connected、Send/Received、Poll/Dispose。满三席后名册不补位，离线不删除身份。LAN 与 Steam 复用 ThreeNetworkController v2，不各写棋规、房管消息或迁移逻辑。

- LAN：Xiangqi.Transport 中的 TCP 全连接，默认房主端口 48028，加入者也有独立监听端口；ECDSA P-256 临时身份，带连接 nonce 的签名帧。后台 I/O 事件经 Poll 回到调用线程。
- Steam：三席 Lobby、独立消息频道 48028、可靠消息及断会话重启；复用唯一 SteamAPI 回调泵，不重复初始化 Steam。
- 同步：有界 JSON 固定 DTO，最大 128 KiB，每页 128 条，日志上限 200000 条；SHA-256 链及领域重放验证。
- 提交：权威先在会话副本执行同一规则校验，至少另一端复验、存储并确认后，权威才提交自己的正式会话。日志副本与权威提交存在短暂在途差异，不是全端原子提交。
- 迁移：失去房主且仍有另一端可达时，由在线存活的最低席位接管；等待状态交换并补齐一致历史，旧房主重连不抢回权威。
- 孤立：仅一台在线不产生新裁决，累计耗时并暂存判负请求；另一台恢复同步后统一处理。队列满不提前清除待扣耗时，超过单包限额的时间分段入队。
- 冲突：不同历史不强行覆盖，进入同步错误状态；不提供恶意房主仲裁或 Raft/Paxos 级共识。

重连只限游戏进程/房间仍保留的网络恢复，不支持关闭后重启续局；跨设备 LAN 和 Steam 三账号实联仍待验收。

### 3.4 三国视图

ThreeBoardProjection 使用六片双线性投影，绘制/点击共享点缓存；基础几何仍按魏/吴/蜀三域生成，再围绕中心旋转，使联网本地席位或热座当前行棋国位于正下方。旋转只重算像素点和命中缓存，不改 CellId、拓扑或网络坐标。ThreeBoardView 扩大可用六边形占比，增加多层厚边框、中央暗纹、角饰与国号水印，参考大幅木雕棋盘的气势而不采用参考图错误拓扑。它绘制网格、九宫、路数与棋子，不判断合法性。双线性投影下九宫斜线显式分段并共同落在逻辑中心点；15 条跨国桥接边继续隐藏并显示“秦岭”“江淮”“三峡”。

RoomChatManager 是两种玩法共同的引擎无关房管消息板：验证席位/目标/UTF-8 大小、保留最多 200 条、按 ID 去重并同步发布事件。三国聊天独立于棋谱日志，房间未满即可发送；每个在线副本保留消息，周期握手同步历史，所以原房主或目标玩家断线不阻止其余人发言，重连/后加入者能读取可见历史。定向消息在可信房间副本间复制、由 UI 按发送者/目标过滤，属于目标可见性而非端到端加密。经典协调器复用相同 Manager，但经典仍无断线恢复协议。

ThreeBoardView 比较相邻的不可变快照，在 Ply 连续且 LastMove 可验证时播放与经典一致的平滑位移、弧形抬升和吃子淡出；选中棋子使用同样的呼吸浮动，BoardFeedbackLayer 继续负责 shader 光环。首次加载、撤销、重同步或跳跃多个 Ply 时直接显示新快照，不伪造动画。动画状态不回写 Session；动画期间 UI 忽略新的棋盘点击。

IBoardProjection 是引擎侧接口，因为它使用 Vector2；不能移动到无引擎依赖的 Core。当前 ThreeBoardView 直接 new ThreeBoardProjection，并读取其 Outline/Center/Vertex 与具体拓扑；该视图还不是投影可插拔的通用棋盘控件。

## 4. 跨玩法的展示与基础设施

### 4.1 页面路由与组合根

```text
MainMenuScreen
  ├── 玩法画廊：ModeGalleryScreen（经典/三国/揭棋/军旗横向图片卡片）
  │   ├── 经典卡片四组折叠菜单 → 联机 / 记录 / 导入 / 人机
  │   ├── 三国卡片两组折叠菜单 → ThreeGalleryScreen 对应本地/LAN/Steam 设置 → ThreeGameScreen
  │   ├── 揭棋卡片两组折叠菜单 → 本地/LAN/Steam 对局 / 最近与保存记录
  │   └── 军旗卡片两组折叠菜单 → 本地/LAN/Steam 对局 / AC2 最近与保存记录
  │   └── 五子棋卡片四组折叠菜单 → 本地/LAN/Steam、SGF 记录/导入、Rapfi 人机对决/设置
  └── 全局设置：SettingsScreen
房间内设置：InGameSettingsDialog
```

Main.cs / Main.ThreeKingdoms.cs 负责创建平台服务、适配器和页面，Action 回调承担导航。ModeGalleryScreen 展示五张横向排列、可横向滚动的玩法卡片，三十三个叶节点经 Action 回调进入设置、记录或对局页；路由值不携带配置，也不创建房间。卡片背景只负责装饰并忽略输入。SwitchTo 移除并 QueueFree 旧页面；共享 Texture2D 由 Godot 资源缓存管理，卡片节点不手动 Dispose。房间负责释放交给它的控制器/适配器。经典会话和协调器仍在 GameScreen 内创建，并非全部对象都已移至组合根。

Godot 基准视口为 1100×820，`canvas_items + expand` 在全屏时扩展逻辑视口以填满显示器，不使用 keep 造成宽高比黑边。经典 LAN 房间为右侧消息栏保留 300px 最小宽度，棋盘列负责弹性伸缩；`--smoke-ui` 在首次布局后检查消息栏四边均在视口内，不再依赖进入试下所触发的二次排版。

### 4.2 设置、皮肤与反馈

AppSettings 通过 ConfigFile 保存 user://settings.cfg，包含显示/特效、音量、资料、字符串 ThemeId、ShowBoardCoordinates，以及经典 UCI 与五子棋 Rapfi 的引擎路径、执子和思考参数。两个人机设置页可选择已有文件，也可在许可确认后调用 `EnginePackageInstaller` 将固定官方版本下载到 `user://engines`；只有哈希、解压和必要文件检查全部成功才保存路径。旧 `board/skin` 数值仍读取/写入一个兼容周期，首次加载映射为 `classic`/`spring`。全局/房间/人机设置先编辑副本，确认后更新、保存并应用，不进入领域棋规或网络棋谱；引擎路径仅在本机使用，不写入棋谱或网络。

IBoardTheme / IBoardThemeCatalog 把稳定字符串 ID 解析为不可变调色板、分模式棋盘资源、semantic-v1 棋子 atlas 和语义特效描述；经典与三国棋盘均实际注入并消费。ThemePackageCatalog 扫描 `assets/themes` 与 `user://themes`，ThemeManifestReader 使用系统 XmlReader 禁用 DTD/实体并限制大小、深度、路径和资源类型，ThemeResourceLease 只在 Godot 主线程加载图片/shader并由视图幂等释放。主题不进入规则、快照或网络。内置春日 XML 使用更浅底色、低装饰强度以及动态柳叶、桃花和春燕。

默认 XML 目录提供经典与春日主题，未知/损坏持久化 ID 回退经典。全局设置从目录动态生成选项并可手动重扫；房间设置复用已发布目录，对局中不热重载。主题作者接口、atlas 排布、shader uniform、安全边界与限制见 [XML 主题包与 shader 接口](docs/theme-package-proposal.md)。

BoardFeedbackLayer 只接受选中/起点/终点像素位置、尺寸与主题语义，内部小型 ColorRect 使用主题 shader 或 PieceFeedback 回退负责旋转/闪烁。ThemePieceLayer 接收投影后的 atlas 绘制命令，两种棋盘共用 7×5 语义排布；无 atlas 时程序绘制。棋盘自身负责选中抬升、移动插值及最近着法生命周期；动态特效关闭后保留静态标记。两种棋盘不伪造通用领域坐标。

CueEffectLayer、GameplayAudioPlayer、IPositionPatternDetector 当前主要服务经典玩法，不能因复用了白环就认为三国已接入经典全部结算/音效事件。

### 4.3 玩家资料与 Steam 生命周期

经典 MatchPlayerProfiles 拥有本局资料和临时 RGBA8，GameScreen 创建并释放 ImageTexture。头像文件路径留在本机；协议只发有界 ID、昵称、像素，不传本机路径或平台句柄。Steam64/LAN 指纹继续只做认证与内部名册键；玩家卡片、聊天和三国提示显示 Steam Persona Name/房间昵称，不再把认证 ID 当默认可见名字。三国仍不具备经典同等头像交换流水线。

SteamLobbyService 是唯一 SteamAPI.InitEx/RunCallbacks/Shutdown 所有者。经典使用双人 Lobby 路径；三国由 SteamThreeLobbyService 处理三席配置和模式路由，再交给 SteamThreeRoomTransport。两类 transport 只释放自己拥有的频道/回调/房间，不自行关闭全局 SteamAPI。

### 4.4 生命周期、线程与性能

- Domain/Application 会话不是线程安全对象。Godot 入口、控制器 Tick 与回调在主线程串行执行；后台 TCP I/O 必须经队列交给 Poll，不能直接触发 UI 更新。
- 控制器可在初次联网配置时替换 Session；UI 应在刷新时重新读取，不永久缓存旧 Session 引用。
- Node、回调、套接字、管道、材质、头像像素和纹理应记录谁创建、谁持有、谁释放；Dispose/DisposeAsync 必须可重复调用。
- 解绑事件再释放底层资源；不要在 Steam 原生回调执行栈内销毁正在调用的回调，既有路径使用延迟处理。
- UCI 离开房间需取消活动操作、尝试 quit、必要时结束子进程、释放管道；Node.QueueFree 不能代替适配器释放。
- 静止棋盘不为光环逐帧 QueueRedraw，shader 使用 TIME；拓扑预计算、投影按尺寸缓存。三国房间仍逐帧格式化标签，不等于 UI 热路径已全部优化。
- 不把皮肤、头像或调试字符串写入权威规则；应用层目前仍输出中文提示，结构化错误/本地化端口属于后续改进。

### 4.5 棋谱、残局与试下分支

经典玩法以文本 PGN 为容器，`Format=ICCS` 保存四字符坐标着法，以 Xiangqi FEN 保存非标准初始局面。选择它是因为 PGN 能承载对局元数据、结果与扩展标签，ICCS 与项目现有 UCI 坐标一一对应且没有中文纵线同子消歧歧义，FEN 又是外部引擎最常用的局面交换形式。WXF 是正式着法记谱规则，但不是当前引擎端的无歧义坐标协议；XQF 是版本相关二进制格式，因此首版不作为写出格式。`XiangqiPgnCodec` 导入只取主线，忽略注释、NAG 与变例，并逐手经 `ChessGame.TryMove` 复验；当前不直接解析 WXF/中文着法。

三国 135 点拓扑没有可宣称兼容的行业棋谱标准。`ThreeKingdomsPgnCodec` 只复用 PGN 标签/主线外壳，强制声明 `Variant=Xiangqi-ThreeKingdoms`、`Format=CellId`，并用 `TKFEN` 扩展保存完整初始状态；经典软件可安全忽略未知变体，但不保证理解其着法。两类文本均限制为 4 MiB，外部局面先验证枚举、点位、重复占位/ID，再由领域聚合重放。

`GameSession.CreateRecord`、`ThreeMatchSession.CreateRecord`、`JieqiGameSession.CreateRecord` 与 `JunqiGameRecord` 返回不可变主线；表现层分别在 `user://records/classic`、`user://records/three`、`user://records/jieqi`、`user://records/junqi` 下持有文件。`recent` 每玩法按修改时间仅保留十局，`saved` 不自动淘汰；写入失败只提示/记录，不反向改变权威胜负。揭棋 PGN 扩展额外保存版本化 Seed/Shuffle，直播禁止建谱，赛后才释放 Seed。军旗以 GB2312 写出 AC2 `INFO[1.0]`，包含双方布阵、着法、战果、亮旗和赛果，导入后逐手重放复验。

五种玩法的复盘页均保留上一步/下一步按钮；经典、三国、揭棋与军旗还提供带刻度的 HSlider，五子棋首版仅按步导航。

`ClassicTrialSession` 与 `ThreeTrialSession` 是无网络、无棋钟、无合并操作的临时领域副本。界面开启试下后把点击送给副本并允许依次模拟各方；经典试下通过 `GameSession.DescribeMove` 复用正常着法的表现语义，播放移动/吃子动画、提示、音效和落子反馈。权威会话仍接收网络事件，退出即丢弃副本并显示最新真实局面；试下着法不发给 AI/transport，也不写入正式棋谱。

经典 `.pgn.meta.json` sidecar 保存列表展示所需的对局时间、方式、双方昵称、我方标记及最多 64×64 RGBA8 头像；System.Text.Json 负责通用编解码，读取限制 256 KiB 并复验字段/像素长度。PGN 仍可脱离 sidecar 交换，旧记录缺少 sidecar 时从 PGN 标签和默认色块回退。记录列表按已批准布局显示日期时间、双方头像/名称、我方胜败和对局方式；视图拥有临时 ImageTexture 并在退出时释放。

若 AppSettings 中存在有效 UCI 路径，经典复盘创建 `ReplayAnalysisCoordinator`。选择某步会取消旧扫描，优先计算当前局面，再串行补齐附近局面；引擎最佳着法再次经 `ChessGame.TryMove` 验证，评分按 UCI 行棋方视角归一为红方分值。`ReplayEvaluationGraph` 只绘制分数折线和当前步基准线，`BoardView.SetAnalysisMove` 只绘制箭头。Screen 拥有协调器和 UCI 引擎，退出时取消并幂等释放进程。

## 5. 扩展与维护流程

具体步骤、代码示例与测试矩阵统一维护在 [抽象层使用手册](docs/abstractions.md)，不要为每个玩法复制一套相互矛盾的接口说明。

1. 先说明变化点及其所属层，寻找已有端口；只有真实替换需求才新增接口。
2. 写明输入输出、权限/不变量、失败方式、线程、所有权、兼容性和注入链。
3. 将规则和应用策略放入可无 UI 测试的类；UI 保留交互/表现状态。
4. 测试正常、拒绝、边界、状态转换及至少一种替代实现；涉及网络增加重放、鉴权和恢复。
5. 在定义处写 XML 注释，在对应 Markdown 解释使用步骤、限制、实例与测试入口，再更新本文路由。
6. 未批准的棋规、坐标或主观特效先向用户确认，不借“抽象重构”改变现有玩法。

## 6. 验证与当前边界

### 6.1 常用命令

```powershell
dotnet build .\Xiangqi.sln
dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj
dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj
dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=windows
dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=android
```

测试工程是自运行控制台程序，不能以 dotnet test 没报错替代实际用例执行。不同 GodotTargetPlatform 构建共享输出，需串行；运行桌面 Godot 前最后构建 Windows 目标。

Godot 4.7.2 路径由 `GODOT_NEW_PATH` 提供；旧版 `GODOT_PATH` 不再用于构建或验收。Debug 参数在 `--` 后传入：

| 参数 | 验证目标 |
| --- | --- |
| --smoke-ui / --capture-ui | 五张横向玩法卡片、三十三个叶子入口、五子棋/Junqi 页面构造、经典 LAN 消息栏边界、设置页与春季皮肤；可选画廊截图 |
| --smoke-gomoku-ai | Windows 下读取 `XIANGQI_GOMOKU_AI_PATH`，以 15 路禁手局验证 Rapfi/Piskvork 探测、人类首着和一手合法回复 |
| --smoke-engine-install-rapfi / --smoke-engine-install-pikafish | 下载固定官方 7z、核对 SHA-256、安全解压、选择 AVX2/SSE 后端，并实际完成 Piskvork 握手或 UCI+NNUE 搜索；临时安装随后删除 |
| --smoke-feedback / --capture-feedback | 经典主题注入/回退、棋子与反馈配色、选中/落地/回复/撤销/翻转/空闲反馈；可选图形截图 |
| --smoke-three / --capture-three | 三国三方朝下视角、三条命名国界留白、九宫中心、移动插值/落点、主题应用、405 次旋转命中往返、243 拓扑边、面积铺满与设置 |
| --smoke-profile-ui | 经典资料卡与临时头像 |
| --smoke-lan-host/client | 经典双进程 LAN |
| --smoke-timeout-host/client | 经典双端棋钟与超时 |
| --smoke-steam-init / --smoke-steam-lobby | Steam 初始化与 Lobby 生命周期，不覆盖完整好友实联 |

### 6.2 证据与未完成项

2026-09-07 本次架构检查重新运行 38 项核心测试与三端网络测试；具体结果见 [检查记录](docs/architecture-review.md)。此前 Windows/Android 条件构建及 Godot smoke 已通过，本次文档维护不将历史记录冒充新实机验收。

2026-09-08 本轮体验更新串行通过 39 项核心测试、三人模拟/真实 TCP 测试、Windows 目标构建，以及 `--smoke-three`、`--smoke-ui`、`--smoke-feedback`。三方朝下视角均生成 GPU 截图；仍不等于跨设备 LAN、Steam 三账号或 Android 实机验收。

2026-09-08 本轮记录/复盘更新通过 45 项核心测试、三人网络测试和 Godot C# 编译；本机 Godot 4.5.1 Mono 的本轮 `--smoke-ui` headless 进程原生 signal 11，因此十四入口、记录头像、复盘曲线/箭头及菜单 Tween 尚未获得新的运行时视觉验收，不能沿用上一轮截图代替。

2026-09-09 揭棋 LAN/Steam 房主权威脱敏、Seed 承诺/赛后验证和直播棋谱阻止已实现；军旗领域、网络控制器、独立棋盘、热座遮罩、四卡二十四叶画廊及 AC2 记录/复盘已实现，并通过 58 项核心测试和 Godot C# 编译。真实双机 LAN、Steam 好友实联、Android 触控及军旗/画廊人工视觉验收仍未完成；揭棋长打与 120 步无吃子裁决仍未实现。

2026-09-10 五子棋领域、可配方格几何、本地/LAN/Steam 控制器、SGF 记录/导入和五卡三十三叶画廊已实现；新增可替换 `IGomokuAiEngine`、主线程轮询协调器和桌面 Rapfi/Piskvork 适配器，官方 2025-06-15 Windows SSE 包的 freestyle/renju 协议实测通过且未进入仓库。Android AI、真实设备/账号及复杂禁手棋库对拍尚未验收。全屏 expand、经典 LAN 消息栏和试下完整动效亦已修复。

2026-09-09 暗子背面/翻面修复通过 48 项核心回归、Android 与最终 Windows 构建（均 0 警告/错误）。揭子平移和翻面现共用同一时间线，不再在落点后追加翻面阶段。`--smoke-feedback` 已增加无字背面、covered→明子翻面和动画释放断言；本机 Godot 4.7.2 headless 可初始化 .NET，但多种启动参数均在主场景 `_Ready` 前以 0 退出、没有 PASS/FAIL，因此该 smoke 和实际翻面观感仍记为未验收。

2026-09-09 Godot 4.7.2 迁移复核使用机器级 `GODOT_NEW_PATH` 的官方 .NET 发行版完成：解决方案、Android/Windows 条件构建均为 0 警告/错误，48 项核心测试和三人网络测试通过，`--smoke-ui`、`--smoke-feedback`、`--smoke-three` 均输出 PASS。随后经维护者批准，桌面主工程和测试迁移到 `net10.0`，Core/Transport 提供 `net9.0;net10.0`；Android 因 Godot 4.7.2 预编译导出模板限制暂留 `net9.0`。升级后的验证结果与平台边界见 [迁移记录](docs/godot-4.7-migration.md)。

仍未实现或未验收：

- 统一经典房间控制器、只读三国查询门面、完整拓扑/投影/规则集注入及自动架构约束测试。
- 可视化摆子残局编辑器、观战、经典断线重连、进程退出后的三国恢复。
- UCI 之外的自然语言对话后端与完整远程 AI 配置。
- 赛事重复局面/长将长捉规则。
- Steam 双/三账号端到端验收、跨设备 LAN 防火墙/NAT 验收、Android 设备验收。开发 App ID 480 不用于正式发行。

.git 工作区可能已有用户修改；不得清除无关改动或导出文件。.godot、bin、obj、build、export、日志、崩溃转储和本地 steam_appid.txt 为生成物，不应提交。
