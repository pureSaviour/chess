# 抽象层使用手册

更新：2026-09-10。总架构与玩法路由见 [根文档](../TECHNICAL_ARCHITECTURE.md)，本次检查证据见 [架构检查](architecture-review.md)。本文说明当前接口如何使用，不声称未完成的注入链已经可替换。

## 1. 先选择正确边界

| 需要改变的内容 | 放在哪里 | 不应放在哪里 |
| --- | --- | --- |
| 单个棋种的局部走法 | IPieceRule / IThreePieceRule | BoardView、网络收包 switch |
| 收编、轮次、胜负、撤销 | 对应领域聚合 | 棋种策略副作用、按钮回调 |
| 协商、棋钟权限、AI/网络调度 | Application 会话/控制器 | 新增 GameScreen 业务分支 |
| LAN/Steam/远程连接 | 玩法对应 transport 适配器 | 领域棋盘 |
| 棋盘邻接与路径 | Core 拓扑 | shader/像素角度 |
| 屏幕坐标与命中 | Presentation 投影 | 领域坐标、网络协议 |
| 棋子字形、控制色、皮肤 | Presentation | PieceKind 走法逻辑 |

抽象是否有效，要看消费者是否依赖契约、组合根能否注入替代实现、测试能否不用真实平台运行，不以接口数量判断。领域聚合、值对象、工厂和不可变快照也是抽象，不必为每个类建立同名接口。

画廊的跨玩法发现使用 `IGameModule` / `GameModuleCatalog`，只统一稳定身份、Manifest、能力、入口和顺序，不统一棋局快照或控制器。`GamePackageReader` 只验证 `.xqgame` 元数据、路径、大小、载荷哈希和发布者签名，不加载内容；`GamePackageInstaller` 在确认后的完整包哈希上复验并以同盘目录移动原子发布，但不会执行模块。`GameUpdateFeedReader` 是无网络纯解析边界：输入索引原始字节、DER 签名和固定 ECDSA 公钥，输出兼容宿主 API 的不可变版本快照，未知字段、重复模块、非 HTTPS 地址或错误签名均失败。`GameModuleUpdateClient` 拥有一次请求/下载操作但不拥有注入的 `HttpClient`；它支持 Range 续传，并在发布临时文件前核对长度、完整包哈希、Manifest 身份/版本和发布者指纹。`GameModuleVersionStore` 独占 `versions.json`，安装后只写 pending，启动时才原子提升为 active，并保留一个 rollback；它不删除包，也不碰各玩法记录。Godot `GameModuleUpdateDriver` 拥有 HttpClient/取消源，在后台执行网络和安装，事件经 `_Process` 回到主线程。当前 active 状态尚未接入动态运行时，因此不能据此执行第三方内容。具体格式、事件顺序、配置和信任边界见[玩法模块与 DLC](game-modules.md)。

## 2. 经典棋种：IReadOnlyBoard / IPieceRule

定义：[BoardState.cs](../src/Xiangqi.Core/Domain/BoardState.cs)、[IPieceRule.cs](../src/Xiangqi.Core/Domain/Rules/IPieceRule.cs)。

### 职责与调用条件

- IReadOnlyBoard 提供 GetPiece、GetPieces、CountPiecesBetweenStraight；不向规则提供 Move/Add。
- IPieceRule.Kind 标识稳定棋种，CanMove 只判断几何和阻挡。正常聚合路径已检查坐标、空起点、轮次和友子目标；单测直接调用策略时应明确测试前提。
- CountPiecesBetweenStraight 要求起终点在同一路/行，返回中间占位数；不能拿它验证非直线或三国分支。
- 不改变局面、不读随机数/系统时间/用户设置，不发事件、不做 I/O。相同输入必须产生相同答案，以支持试走、将军检查与跨端复验。
- 全局“不能送将”和结束判定由 ChessGame 负责；CanMove 返回 true 并不保证最终提交成功。

### 注册与限制

1. 声明新的 PieceKind 字符串，避免复用已有 ID 表达不同规则。
2. 实现 IPieceRule，开局前用 PieceRuleRegistry.Register 注册。
3. 创建包含新棋种的 BoardState，将其与注册表传给 ChessGame。
4. 添加正常/受阻/友子/轮次/送将等领域测试，再添加独立的字形映射。

Register 重复键会抛异常，Get 未知键也会抛异常；经典注册表本身可变，局中不能继续添加/修改规则。策略对象应无状态或不可变。

当前 GameSession 不接受棋局/规则工厂，重开也固定 CreateStandard。若要在正式房间发布新规则，必须先补会话工厂注入以及双方一致的规则版本，不能只在领域测试注册后就宣称网络支持。现有 UCI 记谱也拒绝未知棋种。

## 3. 三国棋种与地域查询

定义：[ThreePieceRules.cs](../src/Xiangqi.Core/Domain/ThreeKingdoms/ThreePieceRules.cs)；现有可运行例子：[ThreeExtensionTests.cs](../tests/Xiangqi.Core.Tests/ThreeExtensionTests.cs)。

IReadOnlyThreeBoard 提供 Topology、AdvisorPolicy、At、Owns、HasConquered。IThreePieceRule.CanMove 接收此只读契约及棋子/起终点；聚合先排除无效位置、原地移动、友子和中立障碍目标。

不要混淆三个概念：BirthRegion 是出生国，Owner 是当前控制者，KingdomSquare.FromCell(...).Region 是该交点的地理区域。收编只改控制权/国土归属，不改出生国；兵的出境标记不能通过当前颜色推断。

ThreePieceRuleRegistry 构造时生成 FrozenDictionary，重复键会被拒绝，TryGet 未知键失败，聚合因此拒绝着法。ThreePieceRule 是委托适配器，复杂规则可改用具名 IThreePieceRule 实现。注意 FrozenDictionary 只冻结映射，不会冻结传入策略内部的可变字段。

以下是独立领域实验示例，不会注册到正式房间，也不是新增玩法规则：

```csharp
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.ThreeKingdoms;

var kind = new PieceKind("example-scout");
var rule = new ThreePieceRule(kind,
    (board, piece, from, to) => board.Topology.Neighbors(from).Contains(to));
var registry = new ThreePieceRuleRegistry(ThreeBuiltInRules.All.Append(rule));
var start = new KingdomSquare(Kingdom.Wei, 4, 2).Cell;
var pieces = new[] {
    new KingdomPlacement(start, new KingdomPiece(1, kind, Kingdom.Wei, Kingdom.Wei))
};
var game = new ThreeKingdomsGame(pieces, Kingdom.Wei, rules: registry);
var targets = game.LegalDestinations(start);
```

此例验证注册与查询，属于最小测试夹具，不是具备三将的正式开局。游戏级合法性和收编/淘汰测试仍需完整局面。

正式 LocalThreeMatchController / ThreeNetworkController 当前按默认会话创建，网络配置只协商时间和士策略，不传自定义规则集。新棋种进入联机前，需设计规则集 ID/版本与开局工厂；不得发送任意可执行策略或 C# 类型名。

## 4. 空间结构：IBoardTopology

定义：[IBoardTopology.cs](../src/Xiangqi.Core/Domain/Topology/IBoardTopology.cs)，实现：[ThreeKingdomsTopology.cs](../src/Xiangqi.Core/Domain/ThreeKingdoms/ThreeKingdomsTopology.cs)。

| 成员 | 契约 | 常见误用 |
| --- | --- | --- |
| Id / Version / Contains / CellCount | 拓扑身份、版本、有效点域；BoardCellId(0) 是有效点而非空值 | 用默认值代表未选中；忘记验证外部 ID |
| Edges / Neighbors | 普通邻接，无向边；不等于九宫对角 | 把士斜线当将/兵的一步 |
| Faces / Diagonals | 面与四边形对角候选；中央三角形不提供四边形对角 | 把屏幕斜向视觉当合法对角 |
| Rays | 不含起点，按路径距离排序；中心分支分别返回完整射线 | 按像素共线、最短路或途中任意转弯 |
| HorseJumps / ElephantJumps | Destination + Leg（马腿/象眼） | 仅验证终点，不检查腿/眼及地域 |

集合应只读，拓扑不含占位、棋钟、控制权、颜色或 Godot Vector2。射线表示候选路线，车炮仍需按路线检查占位/炮架。

当前限制：聚合构造固定 ThreeKingdomsTopology.Instance、数组固定 135，规则用 KingdomSquare.FromCell 解码固定地域，视图也直接引用具体拓扑。Id/Version 并未作为可选择拓扑配置在三人握手中协商。因此新 IBoardTopology 实现不能单独替换正式游戏。

扩展时需先规划拓扑注入、地域查询契约、动态容量、开局和网络版本；不要只把字段改成接口类型就声称通用。必须测试连通/对称、无重复边、路径连续/可逆、跳跃阻挡、中心分支不回环以及 UI 投影覆盖。

## 5. 展示投影：IBoardProjection

定义及实现：[IBoardProjection.cs](../src/Presentation/Boards/IBoardProjection.cs)。Resize 更新缓存，Point 映射有效 CellId，Hit 返回命中点或 null，CellSize 提供表现尺寸。先 Resize，再查询；无效点不应直接索引 Point。

它使用 Godot.Vector2，属于展示端口，不是 Core 的通用几何库。点击与绘制必须使用同一缓存，缩放不能重编号。逻辑来自拓扑，投影不能反向创造走法。

当前 ThreeBoardView 直接构造 ThreeBoardProjection，且使用接口外的 Outline/Center/Radius/Vertex；经典 BoardView 仍使用自身固定矩形投影。若需真正替换投影，先定义轮廓/地域背景需求或独立背景几何端口，再通过构造/工厂注入；不要盲目把所有具体投影字段塞进公共接口。

ThreeBoardProjection.Outline 目前直接暴露数组，消费者应只读；不要改数组来做动画，应由投影变换统一更新，避免点击与显示缓存不一致。Godot ThreeBoardSmoke 测试点往返、边不交叉与面面积；尚无第二投影实现的契约替换测试。

ThreeBoardProjection.SetBottomKingdom 使指定国土朝向屏幕下方，然后对 Outline 与全部 135 个缓存点施加同一个中心旋转。Point、Hit、边、九宫与棋子都读取旋转后的缓存，因此不需要也禁止重编号 CellId。联网视图固定使用 LocalKingdom，本地热座跟随当前行棋国；非法 Kingdom 抛 ArgumentOutOfRangeException。Resize/朝向切换和绘制只在 Godot 主线程调用，不创建可释放资源。Godot smoke 对三种视角分别做 135 次命中往返，并检查本国底线位于中心下方。

## 6. 会话与 UI 端口

### 经典 GameSession 与协调器

GameSession 是具体门面而非接口，其私有 ChessGame 防止 UI 直接写占位。网络用 NetworkGameCoordinator，人机用 AiGameCoordinator，本地直接调用会话。三者不是统一房间控制器，GameScreen 负责选择路径与部分策略。

合法目标与 Snapshot 只读使用；视觉动画不回写会话；事件订阅由视图解绑。新增功能不要跳过协调器的权限/协商，联网不能为了“方便”直接调用 TryMove/Undo。

### 三国 IThreeMatchController

定义：[ThreeMatchController.cs](../src/Xiangqi.Core/Application/ThreeKingdoms/ThreeMatchController.cs)。

- LocalKingdom=null 是热座；联网必须绑定自己的席位。PlayerNames 是显示值，PlayerIds/传输身份用于区别玩家，不以昵称鉴权。
- Submit 只接收玩家命令。Tick/Connection 为内部命令；命令的无关字段保持默认。
- null 返回值在本地可表示已执行，在网络可仅表示排队；等待快照/Changed 后更新局面，Message 也可能报告稍后的拒绝。
- Tick 由同一主线程推进；不要同时在多个控件里重复 Tick。Changed 不保证逐帧发布棋钟，因此时钟显示读取 DisplayedGameTimes/DisplayedStepTimes。
- Connections 是当前观测状态，Session.Connected 是按日志确认的会话状态，暂时不同是可能的；UI 使用控制器展示字段，不能据此自行裁决断线。
- 控制器在开始配置时可替换 Session，不要永久缓存最初 Session 或订阅其事件而不处理替换。
- Screen 拥有注入的控制器并在 _ExitTree 解绑、Dispose；网络控制器再拥有 transport。SteamAPI 是全局服务，不由房间控制器关闭。

目前 Session 返回具体 ThreeMatchSession，Game 又是可变 ThreeKingdomsGame，IsRunning 可公开设置。**只读查询是使用约定，不是类型系统保证。** 不可通过 Session.Game.Move/Undo 或 Session.Advance 绕过计时、投票、日志与鉴权。后续收窄查询面时，应提供快照、状态、目标查询和能力信息，UI 只保留 Submit/Tick，不让网络历史暴露出去。

### 揭棋、军旗与五子棋控制器

`IJieqiMatchController` 与 `IJunqiMatchController` 是新增玩法的引擎无关 UI 边界。两者均只发布脱敏快照、当前本地席位、能力状态、权威合法目标和异步走子意图；本地与网络实现都经过同一接口实际注入对应 Screen。UI 不得持有其权威聚合、修改快照或自行计算规则。

网络实现使用房主权威裁决。揭棋客户端永远不接收直播 Seed/暗子提示，连接时仅接收 SHA-256 Seed 承诺，终局后验证 Seed 与完整重放；军旗房主为每个玩家生成不同的 `JunqiPlayerSnapshot`，双方完整布阵只在终局后释放以建立 AC2 棋谱。P2P 房主在两种玩法中都是可信裁判；端口认证不等于无信任公平性。详细前置条件、事件顺序、错误、资源所有权与限制分别见 [揭棋玩法](jieqi.md) 和 [军旗玩法](junqi.md)。

`IGomokuMatchController` 只发布 `GomokuSnapshot`、本地颜色、就绪/连接/落子能力，并接受 `GridPoint` 意图。本地、网络和 AI 实现是实际注入点；`InitializeAsync` 负责可取消准备，`Poll` 负责把异步完成收敛到调用线程。本地/网络实现的这两个方法为空操作。网络房主复验预期手数与棋规，客户端逐手重放权威主线；AI 协调器复验局面版本、行棋方和领域合法性。完整契约见 [五子棋玩法](gomoku.md)。

## 7. 跨玩法房管消息板：RoomChatManager

定义：[RoomChat.cs](../src/Xiangqi.Core/Application/RoomChat.cs)。RoomChatManager 是纯 C#、有界、进程内的消息板；经典 NetworkGameCoordinator、三国 IThreeMatchController，以及揭棋/军旗/五子棋的双人房适配器都实际使用同一契约。它不拥有 transport、文件、线程或 Godot 节点，也不负责平台认证、加密或决定谁是游戏权威。

RoomChatEntry 使用稳定房间席位而非 Steam64/公钥指纹：SenderSeat 为零基发送席，TargetSeat=null 表示全房间，非空表示指定接收席。Post 验证发送/目标范围、禁止给自己定向、去除首尾空白、拒绝控制字符及超过 512 UTF-8 字节的正文，成功后生成 32 位十六进制消息 ID、先存储再同步触发 MessageAdded。双人房必须调用 `PostToOpponent`，由消息板根据发送席固定计算唯一对手；该入口拒绝三席及以上房间，避免 UI 或玩法控制器重新实现目标选择。失败返回用户可见错误且不触发事件。消息板默认最多保留 200 条，淘汰最旧项；Entries 是实时只读视图，不是历史快照。

AcceptLive 的前置条件是协调器已经用 transport 认证了来源，并传入 authenticatedSenderSeat；消息自述 SenderSeat 不匹配、结构非法或 ID 重复时返回 false。ImportTrustedHistory 只用于已认证房间同伴提供的有界历史页，逐条复验结构并去重，但不会替调用者建立信任。三国协议为保持房主迁移/断线恢复，把消息复制到可信房间副本；IsVisibleTo 只让全房间消息、发送者和目标席呈现定向消息。这是目标可见性，不是端到端加密。

经典协调器通过 MatchWireProtocol v7 发送 RoomChatEntry，并按唯一对手的认证连接绑定远端席位；揭棋、军旗、五子棋由 `RawTwoPlayerRoomChat` 在各自权威玩法包之外复用同一有界 Chat 帧。所有双人房 UI 使用 `TwoPlayerChatPanel`，不显示接收者下拉框且只发送给唯一对手。适配器只拥有包事件订阅，Screen 必须先释放它，再由控制器释放 transport。三国聊天独立于游戏 ThreeCommand/哈希棋谱，SendChat 不要求三席满员、对局开始或当前房主在线；周期 hello 交换 chat-state 历史，后加入者和重连者可以补齐。三人及以上房间才允许 UI 选择全房间或具体席位。所有事件在控制器 Poll/调用所在主线程同步发布；Manager 本身无需 Dispose。

测试：[Program.cs](../tests/Xiangqi.Core.Tests/Program.cs) 覆盖正常/目标/越界/去重/伪造/协议畸形；[RoomChatNetworkTests.cs](../tests/Xiangqi.ThreeNetwork.Tests/RoomChatNetworkTests.cs) 覆盖两席发言、第三席历史、断线目标恢复和原房主离线后继续发言。常见误用包括用昵称鉴权、把 TargetSeat 当加密、把聊天塞回棋谱阻塞走子、让后台 I/O 直接更新控件。

## 8. 双人/三人传输端口

| 契约 | IMatchTransport | IThreeRoomTransport |
| --- | --- | --- |
| 定义 | Application/NetworkingContracts.cs | Application/ThreeKingdoms/IThreeRoomTransport.cs |
| 语义 | 双人类型化命令/事件 | 固定三席认证身份 + 应用字节包 |
| 编解码 | 具体 transport 复用 MatchWireProtocol | ThreeNetworkController 处理应用 JSON/日志 |
| 推进 | 当前 Godot Node 适配器自行帧驱动 | 控制器调用 Poll |
| 释放 | IAsyncDisposable | IDisposable |
| 后端 | ENet、SteamMatchTransport | ThreeLanTransport、SteamThreeRoomTransport |

共同要求：可靠传递、输入有界、合法身份、状态通知、清晰队列/线程/释放语义。类型化消息合法并不代表着法合法；接收端必须继续走协调器与领域。

`ITwoPlayerPacketTransport` 与 `IMatchTransport` 定义在同一文件，是双人新玩法使用的原始可靠包端口：它只传递有界、不透明的 `ReadOnlyMemory<byte>`，报告连接/房主身份并由屏幕退出路径 `DisposeAsync`；它不解析玩法、序号、棋位、Seed 或私密布阵。`EnetMatchTransport` 与 `SteamMatchTransport` 同时实现两个端口，但一次房间只订阅一种接收事件。揭棋和军旗分别在 Core 控制器中拥有独立版本头和最大 16,000 字节 JSON，不能把原始包直接交给 UI。

三人名册满席后固定，离线只改变 Connected，不能补位；Poll 回调在调用线程发出，后台 I/O 只能入队。Send 不携带 UI Node/Steam handle。SetAuthority 是同步平台房主归属的钩子，不允许 transport 自行决定游戏权威。

新增适配器先以 fake 验证正常、拒绝、顺序、重放/丢失恢复，再做真实后端集成。三人支持迁移不代表二人自动拥有迁移；具体协议、大小上限和版本必须按玩法区分。

## 9. AI：IXiangqiAiEngine

定义：[AiContracts.cs](../src/Xiangqi.Core/Application/AiContracts.cs)；适配器：[UciProcessAiEngine.cs](../src/Presentation/AI/UciProcessAiEngine.cs)。

InitializeAsync/NewGameAsync/SearchAsync/DisposeAsync 是后端生命周期；SearchAsync 接收经典快照与搜索参数，返回 BestMove/候选线路，不允许持有会话并落子。实现必须响应取消、限定输出/超时，释放拥有的进程/连接。

AiGameCoordinator 负责单个活动操作、局面版本验证、最终落子复验和人类决策轮撤销。测试用 FakeAiEngine 隔离真实进程；新适配器仍必须验证非法输出、过期结果、取消、释放和重新开局。

端口面向经典棋力分析，不适用于三国，也没有自然语言聊天方法。当前 UI 配置必须有 EnginePath，且 GameScreen 要求实现为 Node。远程实现应先补独立的后端配置与驱动/生命周期注入，不应伪造一个无用本地路径来绕过约束。

### 五子棋：IGomokuAiEngine

定义：[GomokuAi.cs](../src/Xiangqi.Core/Application/Gomoku/GomokuAi.cs)；桌面适配器：[PiskvorkProcessGomokuAiEngine.cs](../src/Presentation/AI/PiskvorkProcessGomokuAiEngine.cs)。

`IGomokuAiEngine` 的输入是不可变局面和有限思考预算，输出只是候选交叉点。它不拥有或写入 `GomokuGame`，也不发布 UI 事件。`AiGomokuMatchController` 是实际消费者和资源所有者：初始化后仅在人机轮次启动一个搜索，后台结果排队，由屏幕主线程 `Poll` 后按预期手数、行棋方和领域规则复验。过期/非法/异常输出保持棋局不变。屏幕退出释放 controller，controller 取消搜索并幂等释放 engine。

新增实现应复用这一端口并说明线程、取消和资源所有权。Piskvork 适配器每次搜索拥有子进程，禁用 shell，限制协议行，取消时终止进程树。常见误用包括：UI 直接采用引擎坐标、把 Rapfi 模型复制进导出包、从后台任务触碰 Godot 节点、或者把 rule 2 当成所有五子棋规则。测试入口为核心自运行测试中的 fake engine；真实适配器另做官方引擎烟测。当前 Android 没有外部进程实现。

### 外部引擎包：EnginePackageInstaller / EngineInstallDriver

定义：[EnginePackageInstaller.cs](../src/Presentation/AI/EnginePackageInstaller.cs) 与 [EngineInstallDriver.cs](../src/Presentation/AI/EngineInstallDriver.cs)。这是本机设置基础设施，不进入 Core，也不拥有 `AppSettings`。调用者传入 `user://engines` 的绝对路径、包类型、进度接收器和取消令牌；输出只是在完整发布目录中的可执行文件路径。设置页负责展示许可、保存结果和导航，安装器不得自行启用 AI 或更改棋局。

安装器固定 Rapfi 250615 与 Pikafish 2026-01-02 官方 URL/SHA-256；限制下载 80 MiB、展开 256 MiB、512 个文件，拒绝空名、绝对/越目录路径、长度不符和缺失必要文件。下载/解压在同盘随机 staging 目录进行，完成后以目录移动发布；失败时删除本次 staging，不覆盖现有成功版本，且不得修改原设置。重复安装会复用完整版本；已损坏同名目录保留，并发布带时间后缀的新目录。CPU 支持 AVX2 时选 AVX2，否则选最低随包 Windows 构建。

`EngineInstallDriver` 是具体 Godot Node：它拥有单次操作的 CTS，后台进度/完成只进入并发队列，`_Process` 才在主线程发布 UI 事件；退出树取消操作。共享 `EnginePackageInstaller` 由 Main 创建和释放。Rapfi/Pikafish 程序均 GPLv3；Pikafish NNUE 另限个人非商业用途免费、商业用途需授权。必须在下载前展示确认，不能把“一键”解释为静默接受许可证。当前只支持 Windows，Android 按钮禁用。SharpCompress 0.50.4（MIT，桌面程序集约 2.35 MiB）只负责读取 7z，并从 Android 依赖图排除；外部引擎包不进入应用导出。

验证入口是两个 `--smoke-engine-install-*`：使用真实官方下载、哈希与解压，随后实际执行 Rapfi 握手或 Pikafish NNUE 搜索，最后删除临时目录。恶意官方同 URL 内容仍需先通过固定哈希，因此未知更新会失败而不是静默升级；升级版本必须同步 URL、哈希、许可说明、包内路径与 smoke。

## 10. 表现语义、反馈与主题

IPositionPatternDetector 只读取经典前后局面和 MoveResult，返回 PresentationCue；只有 UI 决定颜色、音效和动效。新增 detector 先写纯 C# 测试，再注册到 GameSession；不要在检测器中更改棋规或调用 Godot。

BoardFeedbackLayer.SetMarkers 接收局部像素坐标和尺寸，它不拥有棋盘/轮次。父棋盘负责把自己的逻辑标记投影并决定何时清除；layer 忽略鼠标输入，以免截断点选。SetAnimated 关闭动态而保留静态标记，ApplyTheme 按 selection/moveFrom/landing 选择主题 shader，单槽失败回退 PieceFeedback，_ExitTree 解绑并释放私有材质/租约。

三国视图从前后 KingdomSnapshot 验证稳定棋子 ID 与连续 Ply 后建立自己的动画状态。经典、揭棋与三国的移动棋子实际共用无状态 `PieceMotion` 路径/时长计算：先到终点斜上方再加速竖直落下；被吃棋子不参与动画，落地回调携带是否吃子以选择经典音效。揭棋则由 BoardView 比较前后脱敏快照：只有稳定 ID 从 `covered` 变为具体棋种时，平移与横向背/正翻面才共享同一进度并同时完成；背面无文字，复盘身份提示是独立的低透明度覆盖。首次显示、撤销、重同步、跨多个 Ply 或无法匹配棋子 ID 时直接落到新快照；动画不修改权威提交。跨玩法新增提示时要显式设计通用语义，不能给经典 BoardPosition 填造假的三国坐标。

### IBoardTheme / IBoardThemeCatalog

定义：[BoardThemes.cs](../src/Presentation/Settings/BoardThemes.cs)，解析/目录：[ThemePackages.cs](../src/Presentation/Settings/ThemePackages.cs)，资源所有权：[ThemeResourceLease.cs](../src/Presentation/Settings/ThemeResourceLease.cs)。IBoardTheme 使用稳定字符串 Id，组合显示名、不可变 BoardThemePalette、两种棋盘资源、semantic-v1 棋子 atlas 和语义 effect 字典。它只描述已验证包内文件，不加载资源、不持有 Node/Material、不决定投影或棋规。

IBoardThemeCatalog.Resolve 同步解析字符串或旧 BoardSkinKind，未知 ID 必须回退 classic；Themes/Diagnostics 给设置 UI。BoardThemeCatalog 是不可变发布快照和测试 fake。ThemePackageCatalog.Rescan 同步扫描内置/用户双目录，先完整解析/编译继承，再原子替换当前快照；单包失败记录诊断并隔离，用户包不能覆盖内置 ID。BoardView、ThreeBoardView、两个设置面板和 CueEffectLayer 都消费实际注入目录。

调用与生命周期：AppSettings 持久化 ThemeId；全局设置可以手动 Rescan，保存后视图在 Godot 主线程 ApplyAppearance。ThemeManifestReader/编译器只产生普通对象，可脱离棋局运行；ThemeResourceLease 在主线程按需创建 ImageTexture/Shader/ShaderMaterial。视图换主题或退出树时先解绑 Material/Texture，再幂等 Dispose 租约。应用主题只重画展示，不改变选中、最近着法、命中缓存、权威快照或网络状态。

实现/注册：在 `assets/themes/<id>/theme.xml` 发布内置包，玩家把包放到 `user://themes/<id>`，进入全局设置点“重新扫描主题”。导出时 XML 由 export include filter 打入 PCK，运行时物化到独立的 `user://.builtin-themes-schema1` 后解析。OptionButton 从 Themes 动态生成，无需改枚举/UI 代码。对局设置只消费已扫描快照；首版没有 watcher 和对局中热重载。资源必须是包内规范相对路径，禁止绝对路径、`..`、URL、重解析点、脚本和原生库。

错误语义：manifest 256 KiB、shader 256 KiB、包 64 MiB、图片最大 4096²、主题最多 128；DTD/实体、未知 schema/词汇、非法颜色/枚举/时长、继承环、重复 ID、缺资源和越目录路径均拒绝。当前主题的引擎资源加载异常回退 classic 或程序绘制；Godot 驱动异步 shader 编译错误另写引擎日志。完整 XML、atlas 与 uniform 契约见[主题接口文档](theme-package-proposal.md)。

兼容与测试：BoardSkinKind/`board/skin` 保留一个发布周期，映射 0→classic、1→spring；新键是 `board/theme_id`。主题不进入协议/局面哈希。BoardFeedbackSmoke 覆盖双目录、实际第二目录注入、DTD 拒绝、semantic atlas、shader/材质生命周期与经典动效；ThreeBoardSmoke 覆盖三国消费、投影和命中。Android shader/内存仍需真机验收。

## 11. 棋谱与试下抽象

`ClassicGameRecord` / `ThreeGameRecord` / `JieqiGameRecord` / `JunqiGameRecord` / `GomokuGameRecord` 是不可变初始状态、主线着法和标签的快照，不拥有文件，也不允许消费者修改权威会话。`Replay()` 每次创建新聚合并逐手验证；非法着法以 `InvalidDataException` 失败，不返回部分可信结果。五子棋导入入口是 `GomokuSgfCodec.TryParse`，其余保持原有 PGN/AC2 codec；它们同步、无 I/O，调用者负责读取有界文本并展示错误。

经典 PGN 写出固定 `Game=Chinese Chess`、`Variant=Xiangqi`、`Format=ICCS`，非标准开局增加 `SetUp=1` 与 `FEN`。导入支持 PGN 主线、标签、花括号/分号注释、NAG 与跳过 RAV，但只接受 ICCS；不得看到 `.pgn` 后跳过 `Replay()` 校验。三国扩展固定 `Variant=Xiangqi-ThreeKingdoms`，`TKFEN` 是 UTF-8 JSON 的 Base64 表示，保存存活、国土控制、出生国、当前控制者和兵出境状态；这只是本项目的版本 1 扩展，不是 WXF 标准。

`ClassicRecordStore` / `ThreeRecordStore` / `JieqiRecordStore` / `JunqiRecordStore` / `GomokuRecordStore` 是玩法 codec 与落盘时机的所有者；共享 `GameRecordStorage` 把文件限制在 `user://games/<game-id>/records/recent|saved`，负责原子写、唯一命名、路径/重解析点检查和 recent 淘汰。首次访问会把旧 `user://records/<mode>` 的记录与 sidecar 幂等复制到新根且保留源文件。recent 的容量策略为每玩法十局，saved 永久保留。经典/三国/揭棋为 UTF-8 PGN 扩展，军旗按 AC2 规范使用 GB2312 `.ac2`，五子棋使用 SGF。文件 I/O 同步发生在主线程，当前棋谱很小，但批量导入/云同步前应增加后台端口，且后台完成后只能在主线程更新 Godot UI。

揭棋使用 `Variant=Jieqi`、`Format=ICCS`、十六进制 `Seed` 与 `Shuffle=splitmix64-fy-v1`。`JieqiSnapshot.ToPublicPosition` 是直播脱敏边界；`HiddenIdentityHints` 只允许赛后复盘。未知随机算法必须拒绝，联机中不得把 Seed 或 hints 发给非权威端。详细契约见 [揭棋玩法](jieqi.md)。

军旗使用 AC2 `INFO[1.0]`，A-L/0-4 坐标、a-l 棋子码，以及 AA/AB、MA/MB、RA/RB、FA/FB、EA/EB 属性；`JunqiGameRecord.Replay` 复验声明战果和亮旗，不信任文本。`JunqiSnapshot.ForPlayer` 是直播脱敏边界，`ForReplay` 只供赛后。完整映射与限制见 [军旗玩法](junqi.md)。

五子棋使用 SGF FF[4]/GM[4] 的 `SZ/RU/AB/AW/PL/B/W` 主线，必须通过 `GomokuGameRecord.Replay` 重放验证。`GomokuRecordStore` 保存 UTF-8 `.sgf`；详见 [五子棋玩法](gomoku.md)。

所有玩法记录额外使用同名 `.meta.json` sidecar 保存纯展示信息；棋谱仍是独立、可重放的事实来源。`MatchRecordPresentation` 包含 PlayedAt、Mode、本地玩家、1..2 名对手和本地视角结果；玩家名称限制 64 字，头像限制为 64×64 RGBA8，不保存原头像路径、平台句柄或凭证。双人原始包玩法用 `RawMatchProfileExchange` 复用受限 PlayerProfile 帧，三国 v3 以认证席位同步同一载荷；资料交换不拥有 transport，Screen 在控制器前解绑并释放像素。sidecar 损坏时回退棋谱标签/默认席位名。各记录页共用 `RecordEntryRow`，其标题、104px 行高、时间/双方/结果/模式列与经典相同；页面持有所建 `ImageTexture` 并在 `_ExitTree` 释放。

`GameSession.BeginTrial()` 和 `ThreeMatchSession.BeginTrial()` 返回一次性的隔离分支。输入是调用时的只读权威局面，输出是副本快照和经同一棋规验证的 `TryMove`；它们不含玩家权限、时钟、聊天、协商、网络发送或提交/合并。经典界面将试下前后快照与成功 `MoveResult` 交给 `GameSession.DescribeMove`，按权威对局相同顺序获取移动/吃子、将军/终局和注册 detector 提示，再播放平移、淡出、音效与落子效果；`DescribeMove` 不写入任何局面，并拒绝失败结果。分支由界面持有，退出试下后解除引用即可。

`ReplayAnalysisCoordinator` 输入一个 IXiangqiAiEngine、不可变复盘局面序列和 100..10000 ms 搜索时长。`SelectAndSweepAsync(index)` 取消上一代操作、先算 index，再按距离补齐缓存；AnalysisAvailable 在调用上下文连续发布，UI 仍必须在 Godot 主线程调用。BestMove 经临时 ChessGame 重验，非法输出以 InvalidDataException 失败；分数约定为 UCI 行棋方视角并归一为红方视角，mate 映射到有界哨兵值。协调器拥有引擎，DisposeAsync 取消活动操作并释放进程；消费者不得再单独释放同一引擎。

测试入口：核心自运行测试覆盖经典 FEN/PGN 往返、非法主线、两种试下隔离、三国扩展 PGN 重放，以及复盘评分归一/缓存/引擎释放。2026-09-09 使用 `GODOT_NEW_PATH` 下 Godot 4.7.2 .NET 执行 `--smoke-ui`、`--smoke-feedback`、`--smoke-three` 均通过，并检查了兼容渲染器 GPU 截图；真实 Pikafish 长棋谱性能、头像跨重启显示、动画手感和不同分辨率仍需人工视觉验收。

## 12. 新增抽象的验收清单

每次实现都必须留下以下信息，既写 XML 注释，也在相关 Markdown 更新：

1. 为什么需要变化点、哪些职责不在此接口内、实际消费者是谁。
2. 方法前置条件、返回值、同步/异步失败、事件顺序、调用线程。
3. 数据是历史快照还是实时只读视图；谁拥有可变状态/资源，何时取消和释放。
4. 实现/注册/注入示例；现有代码尚不能注入的限制必须写出。
5. 版本兼容与未知输入处理；新规则不兼容时不能静默与旧客户端混局。
6. 正常、拒绝、边界、状态转换、替代策略/fake、资源释放与跨端一致性测试。

| 变更 | 最低验证 |
| --- | --- |
| 棋种/聚合 | 无引擎单测、非法目标、阻挡、所有权/轮次、撤销，跨玩法受影响时分别测 |
| 会话/控制器 | 权限、协商、时钟、事件、错误、替代输入源，不依赖控件启动 |
| 拓扑/投影 | 连通/路径契约；Godot 命中、连续网格、面积/视角、候选替代实现 |
| 传输/协议 | 往返/畸形包、认证、断线/重放、队列与释放；真实 transport 验收 |
| 房间消息 | 文本/目标边界、认证席位、去重/容量、未满员、定向可见、断线历史恢复与房主迁移 |
| AI | fake 下非法/过期/取消/悔棋；真实进程适配 smoke |
| UI/shader | 保持同一权威局面，Godot 加载/交互/截图与空闲状态检查 |

当前测试没有自动强制所有分层依赖和“UI 不写 Session.Game”的架构规则；这些仍需代码审查。不要把待补的架构测试写成已经存在。
