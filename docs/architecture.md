# 经典象棋架构与运行流程

更新：2026-09-11。本文件由原经典架构摘要原地整理，不是全项目总架构。总览见 [TECHNICAL_ARCHITECTURE.md](../TECHNICAL_ARCHITECTURE.md)；接口契约、扩展示例和注意事项见 [抽象层使用手册](abstractions.md)；三人玩法见 [三国象棋](three-kingdoms.md)。

## 1. 层次与职责

依赖方向为 Godot 展示/平台适配器 → Core.Application → Core.Domain。基础设施实现应用端口，而不是让 Domain 依赖基础设施。

| 对象 | 层 | 职责 |
| --- | --- | --- |
| BoardPosition / ChessPiece / BoardState | Domain | 逻辑坐标、稳定棋子身份与占位 |
| IReadOnlyBoard / IPieceRule / PieceRuleRegistry | Domain | 只读查询、局部走法、棋种注册 |
| ChessGame | Domain | 唯一棋规与胜负裁决、历史撤销 |
| GameSession | Application | 意图门面、状态事件、表现语义 |
| MatchClock | Application | 纯 C# 局时/步时状态机 |
| NetworkGameCoordinator / IMatchTransport | Application | 双人同步、协商、类型化传输端口 |
| RoomChatManager | Application | 跨玩法有界消息板、目标席位、验证与去重 |
| AiGameCoordinator / IXiangqiAiEngine | Application | AI 决策回合、搜索结果验证、搜索端口 |
| EnetMatchTransport / SteamMatchTransport / UciProcessAiEngine | 基础设施（在 Godot 工程内） | ENet、Steam、UCI 适配与生命周期 |
| GameScreen / BoardView | Presentation | 房间交互/显示、棋盘输入/绘制/动画 |

当前 GameScreen 还承担棋钟、网络配置、AI 调度和对象组合，不能称为纯 View。相应改进项见 [架构检查](architecture-review.md)。

## 2. 领域模型与唯一裁决

BoardPosition 为 File 0..8 / Rank 0..9，红方 Rank=0，前进 +1；黑方 Rank=9，前进 -1。棋盘翻转只影响 BoardView.BottomSide。

ChessPiece 组合 PieceInstanceId、PieceSide、PieceKind；后者为稳定字符串值对象。PieceRuleRegistry.Register 拒绝重复键，Get 遇到未知棋种抛异常，注册表在开局前配置，运行中不要修改。

BoardState 构造占位，ChessGame 构造时克隆棋盘。规则通过 IReadOnlyBoard 查询；聚合内部写棋盘。快照 Pieces 是新生成的数组，但公开类型为 IReadOnlyList 不代表数组深度不可变；消费者只能读取，不能转换为数组再改写。

着法处理顺序：

1. 检查终局、坐标、起点、轮次与友子占位。
2. 查询对应 IPieceRule.CanMove 验证几何和阻挡。
3. 试走克隆局面，拒绝使己方将帅受攻击的走法（含将帅照面）。
4. 保存 Memento、提交落子/吃子、增加 PlyNumber。
5. 处理将被吃的结束分支，或切换阵营后计算将军与合法着法。
6. 无合法着法：受将为将死，否则困毙；均判负。

IPieceRule 不能处理轮次、网络身份、撤销或音效。棋种扩展与测试示例见 [抽象手册](abstractions.md)。当前 GameSession 创建/重开都固定 ChessGame.CreateStandard；领域可配置不等于 Gallery 已能配置自定义棋种。

规则边界：无赛事长将、长捉、重复局面裁决；不得擅自选定规则版本。

## 3. 会话、提示与视图事件

GameSession 包含私有 ChessGame；提供 Snapshot、CanUndo、GetLegalDestinations、TryMove、Undo、NewGame、DeclareTimeout、DeclareDraw。UI/协调器只通过这些用例修改经典棋局。

合法着法时，GameSession 先保存 before，执行聚合，再发布移动/吃子、将军/结束等 CueRaised，随后运行已注册 IPositionPatternDetector，最后发布 StateChanged。失败着法只发出拒绝提示，不产生新权威局面。

GameScreen 在 CueRaised 缓存本手视觉信息，在 StateChanged 获取 after，与之前显示的 before 一起调用 BoardView.AnimateMove。领域此时已提交 after，动画只是表现；动画结束后播放经典落子/吃子音效。选中不播放音效；吃子不为被吃棋子建立淡出/飞行动画，也不再向中央特效层显示“吃”字，只保留吃子音效。经典、揭棋与三国共用 `PieceMotion`：以较短的距离相关时长先移到终点斜上方，再沿竖直方向加速落下；它只计算表现坐标，不修改棋位。

- IPositionPatternDetector 目前为经典接口，参数是 GameSnapshot/MoveResult。
- DoubleChariotMateDetector 只识别“双车错”语义，不影响合法性。
- CueEffectLayer 决定提示如何呈现；不要在 detector 内加载 shader/纹理。
- 订阅会话/传输/AI/棋盘事件要在 _ExitTree 配对解绑；会话事件顺序改变会影响动画，应补回归。

## 4. 四种房间的编排

玩法画廊通过 `IGameModule` / `GameModuleCatalog` 枚举经典、三国、揭棋、军旗、五子棋五张横向卡片，宽度不足时横向滚动，并允许玩家调整持久化顺序。每次只展开一组，裁剪容器通过 Tween 改变高度和透明度。经典与五子棋均含四组菜单且 AI 已接通。揭棋、军旗和五子棋的 LAN/Steam 均由各自权威控制器接入共享双人原始包端口。当前五个模块仍由主程序集静态注册；下载与第三方信任边界见[玩法模块与 DLC](game-modules.md)。

### 4.1 本地双人

Main 从 Gallery 的本地房间设置创建 GameScreen；Screen 创建 GameSession 和 MatchClock。点击转换为 BoardPosition，目标查询来自会话；TryMove 提交规则。选中、合法点标记、动画进度、按钮可用状态留在展示层。

本地 Undo 直接调用 GameSession.Undo；重开调用 NewGame 并重置棋钟。本地不走联网投票。不要将网络“申请者/对手同意”限制直接塞入所有玩法共用的 ChessGame.Undo。

### 4.2 LAN 与 Steam

Main 创建具体 IMatchTransport，GameScreen 在配置就绪后创建 NetworkGameCoordinator；两种传输共用同一协调器和 MatchWireProtocol。

```text
本端点击 → SubmitLocalMoveAsync → GameSession.TryMove
        → 成功后发送 MoveCommand(sequence, move)
对端收包 → 解码/身份校验 → OnMoveReceived
        → 序号、协商、阵营校验 → 同一 GameSession.TryMove
双方 StateChanged → 同一棋盘动画入口
```

本地先应用后发送；发送失败通过同步错误处理，不是事务回滚或离线续局协议。经典不使用三国哈希日志、重连和迁移。

EnetMatchTransport 默认 UDP 27888，CreateServer 只允许一个远端，使用可靠包并绑定唯一 PeerId。SteamMatchTransport 绑定确定的对手 SteamID，使用专用可靠消息通道；不能接受任意 Lobby 成员的包。

经典网络聊天由 NetworkGameCoordinator 的 RoomChatManager 管理，与其他玩法共用正文/席位/目标/去重规则。双人界面不显示目标选择，`SendChatAsync` 通过 `PostToOpponent` 固定发送给唯一对手；远端 SenderSeat 必须匹配唯一认证对手。三人及以上房间才可选择全房间或指定席位。经典 transport 断线即结束流程，当前不具备三国的历史补齐能力。

GameScreen 当前要求 IMatchTransport 实例同时为 Godot Node 并将其 AddChild。Core 协调器可用 FakeTransport 测试，但一个普通 C# transport 不能直接注入现有 GameScreen，需显式生命周期适配或后续拆分驱动职责。

### 4.3 时钟与协商

MatchClock.Advance/BeginTurn/Apply 是引擎无关算法；0 为不限时。GameScreen._Process 推进时钟，房主才向协调器提交超时裁决，并约每 0.25 秒发送 ClockSync；客户端平滑显示并按相同 PlyNumber 校正。

悔棋/和棋由 NetworkGameCoordinator 管理申请 ID、类型、PlyNumber、发送/接收票据。仅本人最后落子且对方未走时可申请悔棋；双方复验申请资格与响应绑定。接受悔棋走 Session.Undo，和棋走 DeclareDraw。

协商期间协调器拒绝走子，GameScreen 根据 HasPendingOffer 事件暂停棋钟。这项暂停策略尚未封装为统一无 UI 控制器，因此单测协调器并不能完全验证真实 UI 中的计时编排。

### 4.4 人机与局面分析

Main 创建 UciProcessAiEngine，Screen 创建 AiGameCoordinator 并把引擎 Node 加入树。UCI 进程以可执行文件目录为工作目录，stdout/stderr 后台回调只写有界队列，`_Process` 在 Godot 主线程解析。流程如下：

1. InitializeAsync 完成 UCI 握手与就绪检查。
2. SubmitHumanMove 使用同一 GameSession，记录人类落子后的 ply。
3. Screen 在轮到 AI 且没有动画/活动搜索时调用 PlayAiTurnAsync。
4. 引擎接收 GameSnapshot + AiSearchRequest，返回 AiSearchResult。
5. Coordinator 验证返回时 ply、阵营、终局仍匹配，再通过 TryMove 复验 BestMove。
6. StateChanged 驱动棋盘，不由引擎写棋局。

XiangqiUciNotation 输出 FEN（从 Rank=9 向 Rank=0）及 a0..i9 坐标；它只支持内置经典棋种，未知 PieceKind 会被拒绝，不是任意扩展棋种的序列化器。

当前聊天询问触发 AnalyzeAsync/MultiPV，返回候选线路、深度和评分，不将聊天自由文本发送给棋力引擎。分析期间变化的局面按版本拒绝。

复盘使用独立 ReplayAnalysisCoordinator，不复用 AiGameCoordinator 的落子职责。它以当前步优先顺序串行搜索、缓存分数与最佳着法，取消旧选择的扫描，把 UCI 的行棋方分数换算为红方视角，并通过临时 ChessGame 拒绝非法 BestMove。Replay Screen 将结果显示为右侧折线、随步数移动的竖线、分数文字和棋盘箭头；上/下一步按钮与带刻度 HSlider 共同修改同一个当前步索引，SetValueNoSignal 防止渲染反向触发跳转。这些表现状态不写回记录或会话。

UndoHumanTurn 可撤销人类最近一手，若 AI 已回复则一起撤回回复；CanUndoHumanTurn 在 IsThinking 时为 false。NewGame/Dispose 会取消当前操作，不能把这一点描述成“思考中已支持悔棋”。GameScreen 还依赖引擎为 Node、AiMatchConfiguration 要求 EnginePath；远程/自然语言模型尚需配置和生命周期抽象。

人机设置可手动选择 UCI 程序，也可确认 GPLv3 与 NNUE 非商业许可后，一键下载固定 Pikafish 2026-01-02 官方 7z。共享安装器校验发布 SHA-256、安全解压到 `user://engines/pikafish`，按 CPU 选择 AVX2 或 SSE4.1/POPCNT，并将 `pikafish.nnue` 与 exe 同目录以兼容 Unicode 用户目录。只有真实 UCI 搜索可用后才视为 smoke 通过；应用自身不打包该引擎或权重。

## 5. 双人协议与兼容性

[MatchWireProtocol.cs](../src/Xiangqi.Core/Application/MatchWireProtocol.cs) 是经典类型化消息的唯一编解码事实来源。当前 v7，小端二进制帧；配置帧含玩法模式、棋盘尺寸和规则标志以供组合根路由。v7 新增带当前 Ply 与败方的认输消息；协调器用已认证连接绑定远端一方，并在领域会话中复验终局。揭棋、军旗与五子棋另在各自控制器中编解码原始包，不进入经典消息枚举。

| 消息 | 类型值 | Payload 与主要约束 |
| --- | ---: | --- |
| Move | 1 | sequence:int64 + 起终点各两个 int32；26 字节；坐标合法、序号 >0 |
| Configuration | 2 | 局时/步时 int32 + hostSide/mode byte + boardSize/ruleFlags int32；20 字节 |
| Timeout | 3 | ply:int64 + loser byte；11 字节 |
| ClockSync | 4 | ply:int64 + 三个 double + 当前方 byte；35 字节；有限非负 |
| Chat | 5 | 32 字节消息 ID + sender/target 席位 + UTF-8 长度与正文；目标可空，非空、无控制字符、最多 512 UTF-8 字节 |
| MatchOffer | 6 | 请求 ID、ply、申请类型；19 字节 |
| MatchOfferResponse | 7 | 同上 + accepted bool；20 字节；必须匹配待处理申请 |
| PlayerProfile | 8 | ID、昵称、尺寸、RGBA8；文字各 ≤96 UTF-8 字节，头像 ≤64×64 |
| Resignation | 9 | 当前 Ply、认输方；接收端复验席位与局面版本 |

适配器先验证来源/长度并调用 TryDecode，再发布类型化事件；协调器验证状态序号/权限，再进入领域。不能只在 UI 或发送端校验，也不能用任意对象反序列化覆盖棋盘。

修改不兼容布局时必须提升 Version，同步所有 transport dispatch、Lobby 版本检查、FakeTransport 和往返/畸形包测试。不得改经典 v6 而遗漏 Steam 适配器，或把新玩法 JSON 混入经典类型化消息流。

## 6. 资料、外观与生命周期

MatchPlayerProfiles 持有本局双方 PlayerProfileSnapshot/PlayerAvatarPixels；平台或自定义提供器只产出名称/ID/像素。Steam Persona Name 是玩家可见名称，Steam64 仍保留为内部身份字段但玩家卡不再默认显示它。GameScreen 创建 ImageTexture，释放时先断开控件纹理再 Dispose；本机头像路径不发给对手。当前 UI 断线后释放资料并显示释放状态。

BoardView.BottomSide 只改变投影。路数按当前朝向显示，红方中文、黑方阿拉伯数字。IBoardThemeCatalog 由 BoardView 构造注入，主题统一控制棋盘、棋子、合法点与 BoardFeedbackLayer；默认春日主题使用更浅底色、较低装饰强度和更多春季细节。完整契约见抽象手册。BoardView 仍是固定经典棋盘，不实现三国的 IBoardProjection。

选中红环/悬浮及最近落点白环/起点角标与棋规无关。新着法开始隐藏旧标记，动画落地显示新标记；同局面刷新保留，悔棋/重开清除。关闭动态特效保留静态提示。

SteamLobbyService 独占 SteamAPI 全局生命周期；具体 transport 只拥有频道和局部回调。UCI 适配器拥有子进程和管道。释放边界及线程契约必须随新实现一起说明，不能只依赖 QueueFree 或 GC。

## 7. 验证与扩展限制

- 核心测试：tests/Xiangqi.Core.Tests/Program.cs（棋规、协议、协商、AI fake）。
- 引擎反馈测试：src/Presentation/Diagnostics/BoardFeedbackSmoke.cs。
- 经典 LAN/计时/Steam smoke 的入口仍有部分位于 Main/GameScreen。
- 部分接口已有 fake 验证，但没有覆盖全部架构约束的自动测试；“Core 测试通过”不代表 UI 计时/生命周期全覆盖。
- 新 UI 应复用会话/协调器，不复制走法；要获得真正可替换的完整房间 UI，优先规划经典统一控制器并迁出调度策略，而不是给 GameScreen 再加平台分支。
