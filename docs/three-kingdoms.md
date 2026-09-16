# 三国象棋：已确认规则与实现边界

更新：2026-09-13。本文与根目录 `TECHNICAL_ARCHITECTURE.md` 是当前实现事实；坐标提案保留为历史设计依据，不再作为待确认事项列表。

AI 状态：尚无支持本项目 135 点三域拓扑、三方轮次及收编/淘汰规则的现成引擎，也未定义三国 AI 端口。后续若批准自研，应使用独立 `IThreeKingdomsAiEngine` 与 Max-N/paranoid/MCTS 评估，不得套用经典红黑 UCI；详细边界见 [AI 难度与分析](ai-engines.md)。

## 玩法

- 正六边形基础投影为魏在上、吴在右下、蜀在左下，实际玩家视角再旋转到本国朝下；轮次魏→吴→蜀。每国 9 路 × 5 行、16 子，共 135 交点 / 48 子。
- 国内网格、跨国桥接、中央三角形采用已批准的[坐标方案](three-kingdoms-coordinate-proposal.md)。车炮沿带进入方向的射线移动，中路可选择另两国之一，进入后不得绕中央三角再次转弯。
- 将只沿本国九宫普通邻接一步；无“王不见王”、禁止送将或强制解将规则。将被吃后该国淘汰，征服者获得其余棋子、国土；控制色转为征服者颜色，出生国不变。收编不增加额外轮次。
- 兵在出生国内只能按原方向前进走吃，首次出境后沿普通邻接一步移动，不能回出生国。所有兵均不能从境外进入当前控制者已收编的国土；本来就在被收编国土内的兵仍可向原前方移动直至出境。
- 士支持两种策略，房间默认“共享国土”：征服后己方与收编的士解除九宫限制，可在己方/被收编国土内沿四边形对角一步移动和跨友方河界。另一策略“共生死”移除被收编方的士，保留征服者原士的九宫限制。
- 象走两段连续四边形对角，象眼必须空且位于己方国土；目的点可在己方/收编国土，不能进存活敌国。征服者原有的象也获得跨友方国土权限。马腿、象眼不依据屏幕角度推断；中央三角形不提供四边形对角。
- 认输、超时、断线判负移除该控制者的将，其他棋子变成不可移动、不可捕获的中立障碍；仍阻挡线路/马腿/象眼并可作为炮架。
- 无合法着法跳过该国，所有存活国家都不能走则和棋；仅剩一个国家则获胜。联网必须三席到齐才开始。
- 悔棋只撤申请者刚落的一手，且下一人尚未落子；需所有其他存活玩家同意，不返还任何局时/步时。撤销吃将会一并恢复被收编阵营与所有权。和棋同样需要所有其他存活者同意。普通协商期间冻结走子与计时，拒绝即取消，断线会取消未完成协商。
- 断线默认等待重连，其回合继续计时。任一其他存活玩家可立即判其负，不需要一致投票；已判负的玩家重连不会复活。聊天显示国号与玩家昵称，可发送给全房间或指定另一国。
- 最后确认的孤立端边界：仅一台设备在线时继续累计应扣时间、暂存判负请求，不单机产生最终裁决；至少另一台恢复通信并同步后统一结算。界面显示累计倒计时及等待状态，阻止孤立端提交新走子。

## 分层及扩展

| 层 | 入口 | 职责 |
| --- | --- | --- |
| Domain | `IBoardTopology` / `ThreeKingdomsTopology` | 交点、普通边、面、射线、马腿/象眼候选预计算 |
| Domain | `IReadOnlyThreeBoard` / `IThreePieceRule` / `ThreePieceRuleRegistry` | 棋种只读查询和可注册几何/阻挡规则 |
| Domain | `ThreeKingdomsGame` | 权威占位、所有权、收编、轮次、淘汰、单步 Memento |
| Application | `ThreeMatchSession` | 棋钟、活跃席位协商和断线判负 |
| Application | `RoomChatManager` | 跨玩法房管消息板、目标可见性、验证、去重与有界历史 |
| Application | `IThreeMatchController` | 本地热座/网络共用的 UI 命令端口 |
| Application | `ThreeNetworkController` | 命令鉴权、日志同步、权威迁移、受验证重放 |
| Infrastructure | `IThreeRoomTransport` / `Xiangqi.Transport` | 引擎无关 LAN TCP 全连接适配器 |
| Infrastructure | `SteamThreeLobbyService` / `SteamThreeRoomTransport` | Steam 三席邀请、平台 ID、全连接消息与平台房主转交 |
| Presentation | `IBoardProjection` / `ThreeBoardProjection` | 六片双线性投影与点击命中共享缓存 |
| Presentation | `ThreeBoardView` / `ThreeGameScreen` | 棋盘/房间/设置/投票/聊天；不修改规则状态 |

经典象棋仍使用已有 `ChessGame` / `GameSession` / `IMatchTransport`，没有把三国状态塞入红黑阵营的 `Opponent()`。两种棋盘共用可注入的 `IBoardThemeCatalog`、XML 主题包、`ThemePieceLayer`、`BoardFeedbackLayer` 与主题 shader；三国还从连续快照复用经典的移动插值、弧形抬升、吃子淡出与选中呼吸语义。主题只改变投影后的显示，不改变 135 点拓扑或网络坐标。三国棋种扩展注册 `IThreePieceRule`，不要再给棋局主体加棋种 switch。

## 抽象层使用与当前限制

完整接口契约、注册示例、生命周期和测试要求见 [抽象层使用手册](abstractions.md)；检查证据及改进计划见 [架构检查](architecture-review.md)。以下是本玩法调用时必须特别注意的边界：

- UI 写操作只能走 IThreeMatchController.Submit；不能通过公开 Session.Game 直接 Move/Undo/Eliminate，也不能直接设置 Session.IsRunning 或推进棋钟。当前接口仍暴露这些可变能力，分离依赖调用约定，尚未收窄为只读 UI 门面。
- 网络 Submit 返回 null 可能只代表命令进入队列；不要马上移动视图棋子或宣布判负，等待权威快照/Changed。随后拒绝通过 Message 显示。
- UI 显示控制器 Connections/DisplayedGameTimes/DisplayedStepTimes；会话 Connected 和 GameTimes 是已确认日志对应的状态，可能与当前连接观测及孤立端累计显示暂时不同。
- 首次联网配置可替换 Session，视图应每次刷新重新读取控制器，不永久缓存最初会话。
- IBoardTopology 已隔离像素，但聚合仍固定 ThreeKingdomsTopology.Instance 和 135 点，地域规则依赖 KingdomSquare；不能只实现新拓扑就替换正式棋盘。
- IBoardProjection 已定义，但 ThreeBoardView 仍直接构造 ThreeBoardProjection，并依赖 Outline/Center/Vertex 等具体成员及具体拓扑；经典 BoardView 也未使用此接口。当前不是通用可插拔棋盘 View。
- 领域 ThreePieceRuleRegistry 可注入新棋种（见 ThreeExtensionTests）；正式本地/网络控制器仍创建默认规则集。新增正式联机规则需同步开局、规则版本与兼容检查，不能把策略对象序列化给远端。
- BirthRegion、Owner、交点 Region 分别代表出生、控制与地理，不能混用。策略不得直接收编或修改棋钟；聚合与会话分别拥有这些副作用。
- Screen 退出时解绑并 Dispose 控制器，网络控制器再释放 transport；后台 TCP I/O 只能通过 Poll 在调用线程发布事件。Steam 全局 API 不由此房间关闭。
- LAN/Steam 加入者在异步握手完成前可能看到空或部分名册；控制器仍固定投影魏、吴、蜀三个展示席位，缺席项使用“等待玩家”和空身份，名册到达后刷新。UI 不得直接按当前 transport 名册长度构造席位，以免等待阶段越界并中断 Steam 邀请回调。
- 三国九宫斜线在双线性投影下分成四段并显式汇合到逻辑 `(file=4, rank=1)`，避免仅连接四角时视觉交点偏离中心；该修复只影响绘制，不新增拓扑边或走法。
- 魏蜀、魏吴、蜀吴之间的 15 条跨国桥接边仍属于拓扑与合法走法，但绘制时隐藏这些国界网格线，形成类似楚河汉界的留白带，并分别用深粗水平文字标记“秦岭”“江淮”“三峡”。地名是展示语义，不写入快照或网络协议。
- 三国移动动画只在相邻 Ply、LastMove 与稳定棋子 ID 能由前后快照验证时播放；撤销、首次加载和重同步直接刷新。它复用了春季主题和选中/最近着法反馈，但没有自动接入经典的完整 CueEffectLayer/GameplayAudioPlayer 流水线。扩展结算/音效时需显式设计事件，而非复制经典坐标类型。
- ThreeBoardProjection 根据联网本地席位旋转整套绘制/命中缓存，让自己的国土位于正下方；本地热座跟随当前行棋国。旋转不改变 135 个逻辑编号、桥接边或棋谱。棋盘扩大六边形占比并增加厚木框式层次、角饰、中央暗纹和国号水印，仅参考附图的宏大木雕风格，不采用其错误拓扑。
- 三国聊天已从 ThreeCommand/棋谱日志拆出。ThreeNetworkController 的独立 RoomChatManager 在未满三席时也接收消息；全连接副本复制至多 200 条历史并按消息 ID 去重，后加入/重连者经周期握手补齐。原房主离线时其他直连席位仍可发言。定向消息只由发送者和目标在 UI 呈现，但内容会复制到可信房间副本以支持迁移，因此不等同加密私聊。
- 本地三人控制器可由组合根注入魏席的受限玩家资料，使最近/保存记录的己方昵称和头像与双人玩法一致；这不改变热座的 `LocalKingdom=null` 或棋局权限。局内三席均复用共享玩家卡显示昵称和头像，并由控制器的 `DisplayedStepTimes` 生成头像外步时进度；最后十秒红色警示只属于展示投影，不参与超时裁决。所有最近记录都可在列表中追加到 saved，原 recent 保留。

## 网络与重连

LAN 与 Steam 共用 `ThreeNetworkController` 协议 v3（与经典二进制 v7 相互独立），最大应用包 128 KiB；Steam 房间模式键仍为 `three-kingdoms-v2`，它是平台路由键而非线协议版本。名册顺序即魏/吴/蜀，满三席后禁止补位。命令 Actor、实时聊天 SenderSeat 和资料包来源必须匹配 transport 认证身份；移动仍由同一领域聚合校验。v3 资料包同步受限昵称与最多 64×64 RGBA8 头像，供本局记录 sidecar 使用，不进入棋谱哈希或权威棋局。Steam64/公钥指纹只作内部身份键，界面名称使用资料昵称、Steam Persona Name 或 LAN 房间昵称。

LAN 默认 **TCP 48028**。每位玩家都持有独立监听端口，加入者用系统分配的空闲 TCP 端口；所有设备需要允许本游戏在私有网络上的入站连接，且路由器不能开启客户端隔离。每位加入者先接房主，随后根据名册建立另外一条直连；因此初始房主失联不会切断另外两人的联系。ECDSA P-256 临时私钥只留本机，公钥指纹是本局身份，每帧签名并绑定连接 nonce。不是把经典双人 ENet 的远端数量简单改成二。

Steam 仍由已有 `SteamLobbyService` 唯一初始化/关闭 SteamAPI、驱动 callbacks。`xq_mode=three-kingdoms-v2` 将邀请路由到三人服务；配置键 `xq3_game` / `xq3_step` / `xq3_policy` / `xq3_roster`。使用独立消息频道 48028、可靠发送及 `AutoRestartBrokenSession`；按照最终名册过滤 Steam ID。游戏权威迁移也会申请将平台 Lobby owner 转给对应存活玩家。

各端保存相同的有序 SHA-256 命令链（包括棋钟增量），每页最多补齐 128 条，恢复不接受任意棋盘覆盖。心跳约 5 秒判断失联，接管/连接变化后等待约 2 秒交换状态；未结算的计时增量在同步后继续扣除。新权威选当前在线、存活的最低席位，旧房主恢复后不会抢回权威。

权威先在独立会话副本上复用领域执行路径校验，再提议该命令；至少另一端复验、记入日志并确认后，权威才更新自己的正式会话。未确认提议可重发，不在孤立旧房主上独自形成已提交分支。新权威在至少两端互通时选举，接管前取得可达副本的最长一致历史。只有一台在线时按已确认规则暂存请求和耗时，恢复通信后处理；暂存目标若已经重连或申请者已超时淘汰，仍按届时真实规则重新校验。

边界必须明确：

- 当前重连指**保留房间且游戏进程仍在运行时的网络恢复**。退出房间、关闭/崩溃后重启的恢复存档尚未实现，LAN 临时私钥不会持久化。
- 这是好友/局域网点对点房间，不是可抵抗恶意房主的竞技仲裁系统。长时间网络分区若产生不同的已提交分支，会停止行棋并报告同步冲突，不猜测合并或覆盖对方棋谱。没有宣称实现 Raft/Paxos 等多数派共识。
- 日志安全上限 200,000 条；达到上限停止接收动作。普通对局远低于该限额，后续可用受验证 checkpoint 压缩。
- Steam 三账号实联、跨路由器/真实掉线环境仍需验收；不能将 Windows 条件构建通过当作已完成 Steam 联机验收。

## 验证

```powershell
dotnet build .\Xiangqi.sln
dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj
dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj
dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=windows
```

Core 测试包含拓扑旋转/连通/可逆射线、标准开局、收编/撤销、士双策略、兵禁入、中心车炮、断线判负、计时、全员协商、无着跳过/和棋、允许不解将以及自定义棋种注册。

网络测试同时使用可控模拟网络和真实 TCP 三端连接，覆盖席位鉴权、房主持续运行但网络隔离、房主心跳失联、接管、旧房主自动重连、棋谱补齐、三端棋钟、悔棋和单人断线判负；另测仅一端在线的累计耗时、禁止提交走子、暂存判负、恢复通信后统一结算，以及队列拥堵与超过单包限额的离线累计耗时不丢失。真实 TCP 测试使用同机回环接口，不替代多台设备防火墙/NAT 验收。

Godot Debug 参数 `--smoke-three` 验证三种朝下视角、三条地名映射与 15 条国界线过滤、九宫四段汇合、移动落点生命周期、三种视角共 405 次交点往返命中、243 条权威拓扑边、面面积完全覆盖正六边形、反馈和房间设置。三人网络控制台另覆盖未满员发言、后加入历史、定向可见、目标断线恢复及原房主离线聊天。带 `--capture-three` 时保存画面至忽略目录 `build/three-qa`。

官方 API 参考：[Godot 自定义二维绘制](https://docs.godotengine.org/en/stable/tutorials/2d/custom_drawing_in_2d.html)、[Steam Networking Messages](https://partner.steamgames.com/doc/api/ISteamNetworkingMessages)、[Steam Lobby 所有权](https://partner.steamgames.com/doc/api/ISteamMatchmaking#GetLobbyOwner)。
