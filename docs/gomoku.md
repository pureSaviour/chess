# 五子棋玩法与开发边界

更新：2026-09-10。本文区分已实现的 Rapfi 桌面接入与尚未验收的移动端方案。

## 规则

- 棋盘默认 15×15，创建房间可选 9..25 路；黑先，双方交替在空交叉点落子。
- 无禁手模式中，任一方连成五子或更多获胜。
- 连珠禁手模式中，只限制黑方：长连、双四、双三落子被拒绝且局面不变；黑方只有恰好五子获胜，白方五子或更多获胜。本版不包含交换开局规则。
- 禁手以 RIF 定义为设计基线。测试覆盖长连、双四、双三；赛事级复杂“假三”局面仍需棋库对拍验收。

## 分层与抽象

`GridPoint` 是零基交叉点，`SquareGrid` 只校验边长、越界与有界射线。它不存储棋子、不判规则、不知道 Godot 像素，可作为后续围棋领域对象的几何依赖。当前是方形 3..25 路，不表达军旗图拓扑或三国棋盘。

`GomokuGame` 拥有占位、轮次、历史和终局；`TryPlace` 是唯一写入口，越界、重叠、错误行棋方和禁手均不修改局面。`GomokuSnapshot` 是展示快照，UI 不得反向修改权威聚合。

`IGomokuMatchController` 是 UI 的实际注入边界。本地实现使用同一聚合；联机实现复用 `ITwoPlayerPacketTransport`，但使用独立 v1、16000 字节上限的房主权威协议。房主 hello 发布尺寸、禁手和席位；客户落子携带预期手数；房主发布主线，客户端逐手重放后才接受。未知版本、越界、错误角色、陈旧手数和畸形 JSON 失败关闭。这是可信 P2P 房主模型，尚无断线续局或中立裁判。所有实现还提供 `InitializeAsync` 和 `Poll`：本地/网络为空操作，AI 用前者探测引擎、用后者在 UI 主线程提交后台结果。

事件在 transport 驱动线程同步发布；Godot 适配器在主线程泵入。AI 后台任务不发布 UI 事件，而是写入有界于“每次一个搜索”的完成队列，`GomokuGameScreen._Process` 调用 `Poll` 后才复验和发布。联网房间通过共享 `RawTwoPlayerRoomChat`/`TwoPlayerChatPanel` 发送有界消息，接收者固定为唯一对手且不显示目标选择；聊天不进入五子棋权威主线。Screen 拥有 controller，退出时先解绑聊天/资料事件再幂等释放；controller 再释放 transport 或 AI 引擎。`GomokuBoardView` 只绘制、命中和播放落子动画。

## Rapfi AI 端口与进程适配

已实现 `IGomokuAiEngine` 和 `AiGomokuMatchController`。端口职责仅为初始化指定棋盘/规则并为不可变 `GomokuSnapshot` 返回一个候选 `GridPoint`；它不拥有棋局、不判断胜负、不落子，也不操作 Godot。输入前置条件是 9..25 路、已定义规则和 100..120000 ms 思考预算；同一实现只允许一个搜索。输出始终不可信，协调器检查局面手数和行棋方，再调用 `GomokuGame.TryPlace` 复验越界、占用和禁手；失败只发布消息，不修改权威局面。

`PiskvorkProcessGomokuAiEngine` 是 Presentation/AI 中的基础设施适配器。每次探测或搜索启动一个短生命周期、无 shell、隐藏窗口的外部进程，通过重定向 stdin/stdout 发送 `START`、`INFO rule`、`BEGIN` 或完整 `BOARD`，忽略有界 `MESSAGE/DEBUG` 行，只接受一个零基 `x,y`。无禁手映射 Piskvork rule 0，连珠禁手映射 rule 2。协议错误、提前退出、超长行、非数字和越界坐标均以异常失败关闭；思考预算外另给 10 秒启动/模型加载余量。取消或释放会终止整个子进程树，释放幂等。适配器不会把后台回调送进 UI。

组合根从 `AppSettings` 注入玩家执色、路数、规则、思考时间和外部可执行文件路径。路径只留在本机设置，不进入棋谱或网络；可执行文件、`config.toml` 和权重归用户所有。设置页既可浏览已有文件，也可在 GPLv3 确认后由通用安装器下载固定官方包、核对 SHA-256、安全解压并自动保存路径。项目不提交、不导出 Rapfi 文件。这样可替换成其他 Piskvork 引擎或未来定制 C++ 适配器，而无需修改领域/UI。若将来随产品分发二进制，必须另行落实对应许可证与完整对应源码义务。

当前兼容性：官方 Rapfi 2025-06-15 Windows SSE 包已实测 SHA-256、`START/BEGIN`、freestyle 和 `rule 2 + BOARD`；Windows 外部进程路径已接通。该发布版接受 9..22 路，23..25 路明确报不支持，因此 Rapfi 设置页限制为 9..22，而非 AI 对局仍保持 9..25。15 路连珠使用随包 NNUE；19 路连珠实测可返回着法但报告没有匹配权重、退回非该 NNUE 评估。Android 条件编译保留，但 UI 明确拒绝启动外部进程。Rapfi 源码虽支持 ARM64/NEON，Android 原生封装、进程/沙箱策略、包体和 GPL 分发尚未设计，因此不宣称移动端 AI 可用。

## 棋谱、UI 与限制

`GomokuSgfCodec` 读写 SGF FF[4]/GM[4]，使用 `SZ/RU/AB/AW/PL/B/W` 主线。解析最多 1,000,000 字符，未知属性/变例不进入主线，返回前必须通过领域重放。`GomokuRecordStore` 拥有 `user://records/gomoku`，recent 保留十局，saved 不自动清理。当前小文件 I/O 同步发生在主线程。

画廊提供与经典象棋同构的 9 个叶子：本地/LAN/Steam，最近/保存，SGF/局面导入，人机对决/设置。九个入口均已接通；AI 设置页选择用户持有的 Rapfi/Piskvork 可执行文件。

核心自运行测试覆盖几何、规则、禁手、SGF、fake transport，以及 AI 正常落子、非法输出和幂等释放。真实 Rapfi 协议采用临时官方包烟测；真实双机 LAN、Steam 双账号、Android 触控、多分辨率、Android AI 与复杂禁手棋库对拍尚未验收。
