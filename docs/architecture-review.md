# 架构检查记录：抽象层与逻辑/UI 分离

检查日期：2026-09-07。范围为当前工作区（包含尚未提交的经典 AI/外观与三国实现），不是仅检查 Git HEAD。此次只修改文档与 Agent 约束，不实施运行逻辑重构。

## 1. 结论

**核心棋规已经与 Godot UI 分离，但完整房间流程的抽象和可替换性尚未做完。**

| 检查项 | 结论 | 依据 |
| --- | --- | --- |
| Core 对引擎/平台隔离 | 已做到 | Core 工程无外部项目/包引用；源码未发现 Godot/Steam/Presentation/Transport 反向引用 |
| 棋规唯一入口 | 已做到主要调用链 | 经典由 ChessGame；三国由 ThreeKingdomsGame；UI 查询合法目标而非计算棋规 |
| 棋种策略抽象 | 已具备 | 两套只读棋盘/规则契约与注册表；三国有注册新棋种测试 |
| 跨平台传输替换 | 已具备，但端口分玩法 | 经典 ENet/Steam 共用 IMatchTransport；三国 TCP/Steam 共用 IThreeRoomTransport |
| 经典完整房间与 UI 分离 | 部分做到 | GameScreen 仍创建会话/协调器，调度棋钟、配置和 AI |
| 三国统一 UI 命令入口 | 已使用但未类型封闭 | ThreeGameScreen 经 Submit 写入，但 Session/Game 仍公开可变能力 |
| 通用拓扑/投影注入 | 尚未完成 | 接口存在，聚合/视图仍固定具体类、135 点与地域换算 |
| 替代实现与架构约束测试 | 部分做到 | fake 网络/AI、自定义三国棋种已覆盖；缺专用依赖/可变 API/注入契约测试 |

这不意味着当前玩法不能运行，也不意味着要把所有类改成接口。优先处理真实阻碍 UI 替换、测试或新玩法扩展的边界。

## 2. 具体待改进项

### A. 经典 GameScreen 混合房间策略与界面（优先）

证据：[GameScreen.cs](../src/Presentation/Screens/GameScreen.cs) 的字段初始化、构造函数、_Process、ConfigureNetworkMatch、OnMatchOfferPendingChanged、ScheduleAiMoveIfReady。

- 会话在 Screen 内创建；计时开始/暂停、房主超时权限、发送时钟同步也在 Screen。
- 本地/联网/AI 三种操作路径由控件类分支选择，AI 的开始时机依赖此类。
- 因此替换整套 UI 时必须一并搬迁这些业务策略，单测 Core 无法覆盖完整 UI 编排。

建议下一次重构时规划经典房间控制器/只读状态门面，迁出时钟、协商和 AI 策略；Main 注入控制器，Screen 只处理交互/动画。动画完成可以作为显式输入事件，不应让应用层引用 BoardView。以上是建议，当前并不存在该统一接口。

验收应覆盖本地、LAN、Steam fake、AI fake 的同一房间用例，验证协商暂停、超时权限、重开/悔棋、动画就绪与释放。

### B. 三国控制器暴露可变会话/聚合（优先）

证据：[IThreeMatchController](../src/Xiangqi.Core/Application/ThreeKingdoms/ThreeMatchController.cs) 返回 ThreeMatchSession；[ThreeMatchSession](../src/Xiangqi.Core/Application/ThreeKingdoms/ThreeMatchSession.cs) 公开 Game 和可写 IsRunning。

当前 [ThreeGameScreen](../src/Presentation/Screens/ThreeGameScreen.cs) 没有调用 Game.Move/Undo/Eliminate，也没有直接推进 Session 棋钟；写操作实际走 Submit，因此未发现正在绕过规则的 UI 路径。问题是新 UI 仍能在编译通过的情况下绕过投票、日志和身份验证。

建议将 UI 查询面收窄为只读局面、时钟、连接、协商、能力与合法目标查询，保留 Submit/Tick；可变会话只由控制器持有。迁移时需考虑网络初次配置可替换 Session，以及 Changed 不逐帧发布棋钟的现有契约。

### C. 拓扑/投影只有局部抽象，没有完整可插拔链（中期）

证据：[ThreeKingdomsGame.cs](../src/Xiangqi.Core/Domain/ThreeKingdoms/ThreeKingdomsGame.cs) 固定 135 元素、ThreeKingdomsTopology.Instance；[ThreePieceRules.cs](../src/Xiangqi.Core/Domain/ThreeKingdoms/ThreePieceRules.cs) 使用 KingdomSquare.FromCell；[ThreeBoardView.cs](../src/Presentation/Boards/ThreeBoardView.cs) 直接 new ThreeBoardProjection 并引用具体拓扑；[IBoardProjection.cs](../src/Presentation/Boards/IBoardProjection.cs) 之外还有轮廓/顶点依赖。

现有分离的价值是逻辑不受屏幕变形影响，不是任意拓扑即插即用。建议只有在确有新棋盘/视角需求时，再一起规划地域查询、拓扑/投影工厂、存储容量与开局配置；增加第二实现替换测试。不能仅改字段类型而保留所有硬编码。

### D. 领域规则注册与正式房间配置之间存在缺口（中期）

证据：[GameSession.cs](../src/Xiangqi.Core/Application/GameSession.cs) 构造/重开固定 CreateStandard；三国本地/网络控制器创建默认会话；三人配置包协商时间与士策略，不协商自定义棋种或拓扑。

领域注册表示可测试、可构造自定义棋局，不表示自定义玩法已经能从 Gallery 开局或跨端协商。建议按需要增加规则集/开局工厂和稳定版本标识，确保重开不退回标准规则、网络双方拒绝不兼容配置。

### E. 经典端口被 Screen 附加 Node 要求（中期）

证据：[GameScreen.cs](../src/Presentation/Screens/GameScreen.cs) 构造时对 IMatchTransport、IXiangqiAiEngine 执行 is not Node 检查；[AiContracts.cs](../src/Xiangqi.Core/Application/AiContracts.cs) 的房间配置要求 EnginePath。

当前 Godot 适配器这样管理生命周期有实际作用，但 Core 的 fake 或普通远程 C# 实现不能仅实现接口就注入正式界面。应将平台驱动/节点所有权放在组合/宿主层，另行规划后端配置，保留 Core 端口无引擎约束；不应通过伪造路径或强制所有未来后端继承 Node 掩盖此限制。

### F. 自动架构约束尚缺，部分“只读”只是约定（持续改进）

证据：经典 ChessGame.Snapshot 返回新数组；ThreeBoardProjection.Outline 直接公开数组；transport 的 Connected 返回集合接口。它们并非一律深度不可变。当前测试工程以业务回归为主，没有自动限制项目依赖或 UI 写入可变 Session 的专用检查。

这些消费者目前按只读使用，不能将潜在修改能力等同于已发生状态破坏。后续可逐步包装可变集合、限制会话公开面，并增加依赖和契约测试。只用命名空间 grep 不能证明所有分层规则；也不要为了把扫描结果变绿而隐藏真实依赖。

## 3. 已确认良好的边界，应继续保留

- Core 项目与无 Godot 的 TCP 项目有明确编译引用方向。
- 棋种规则只判断局部走法；收编/将军/终局等由对应聚合执行，不在 UI 中复制。
- 三国拓扑与屏幕投影分离，网格、线路与命中有专门测试。
- AI 最终着法经会话/领域复验；fake 使测试不依赖真实引擎进程。
- 三人身份/同步/恢复在控制器，传输只负责认证与搬运；LAN 与 Steam 没有各自复制棋规。
- BoardFeedbackLayer 接收像素标记，shader 动画跨两种棋盘复用，不改变逻辑坐标。

## 4. 本次验证与证据边界

本次执行：

- 项目引用与 Core 源码反向依赖检查；阅读两个聚合、规则注册/查询接口、会话/控制器、AI/网络端口、两个房间/棋盘的调用链。
- dotnet run --project tests/Xiangqi.Core.Tests/Xiangqi.Core.Tests.csproj：38/38 通过。
- dotnet run --project tests/Xiangqi.ThreeNetwork.Tests/Xiangqi.ThreeNetwork.Tests.csproj：模拟席位鉴权/走子/协商/迁移/重连、孤立端统一结算、队列/长离线计时、真实 TCP 三端测试均通过。
- 文档检查：本地链接、符号/方法名与示例来源；Git diff 空白检查。

本次未重新启动 Godot 图形 smoke，未做 Steam 多账号/跨设备 LAN/Android 设备验收，未添加自动架构测试，也未修改运行代码。上一轮构建/图形 smoke 记录保留于玩法文档，不作为本次的新执行结果。

测试通过说明现有受测行为正确，不能反证上述抽象限制不存在。建议修复顺序为 A/B → 与真实扩展需求相符的 C/D/E → 持续补 F；任何行为或主观效果调整仍需按用户确认的规则进行。
