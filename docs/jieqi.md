# 揭棋玩法与实现边界

更新：2026-09-11。总览见 [TECHNICAL_ARCHITECTURE.md](../TECHNICAL_ARCHITECTURE.md)，共享接口见 [抽象层使用手册](abstractions.md)。规则基线参考棋者文章及 Mistboard 揭棋规则；不同平台的长打、暗子被吃后谁可查看等约定并不完全一致，首版固定下述语义，后续变体必须显式版本化。

## 已实现规则

- 红黑将/帅明置原位；每方其余十五枚棋子在本方十五个标准起始槽位中确定性洗牌并暗置，红方先行。
- 暗子首步按槽位棋种走法和限制移动，成功落子后翻明；因此暗士首步仍受九宫限制、暗象首步仍不能过河。
- 明子按实际身份移动；明士只保留斜走一步，明象保留田字与塞象眼，但均不再受九宫/河界限制。
- 将军、自将校验、飞将、吃将、将死和困毙沿用经典语义。当前尚未实现赛事级长将/长捉与 120 步无吃子裁决。

领域入口是 `JieqiGame.Create(ulong seed)`。`JieqiPiece` 同时保存 ActualKind、CoverKind 与 IsRevealed；这些均属权威状态。`JieqiSnapshot.ToPublicPosition()` 将所有暗子投影为 `BuiltInPieceKinds.Covered`，正常 UI 及当前网络协议只能消费该投影。`HiddenIdentityHints()` 只允许赛后复盘使用，不能进入直播、日志提示或远端消息。

## 随机与棋谱

洗牌固定为 `splitmix64-fy-v1`：SplitMix64 产生 64 位序列，Fisher–Yates 对每方十五枚棋种独立洗牌。算法名称和 64 位十六进制 Seed 与 ICCS 着法一起写入 PGN 扩展：`Variant=Jieqi`、`Format=ICCS`、`Shuffle=splitmix64-fy-v1`。选择 PGN/ICCS 是为了和经典记录容器、元数据及外部坐标工具兼容；Seed/Shuffle 是揭棋专有扩展，不宣称普通象棋软件能重建暗子。

`JieqiPgnCodec.TryParse` 限制输入为 4 MiB，验证变体、算法、种子和坐标，并通过 `JieqiGame` 逐手重放。未知算法拒绝导入，避免同一种子在未来悄然产生另一盘棋。`JieqiRecordStore` 通过共享作用域存储原子写入 `user://games/xiangqi.jieqi/records`，recent 最多十局，saved 不淘汰；首次访问只复制并保留旧目录记录。

终局自动落入 recent 后，玩家可在最近记录页将其追加到 saved；Store 会重新校验 Seed/Shuffle 和全部着法，不删除 recent 原件。

## 表现与所有权

`JieqiGameScreen` 通过 `JieqiGameSession` 提交意图；Session 的 Snapshot/StateChanged/MoveCommitted 只发布脱敏 `GameSnapshot`，不把权威 `JieqiSnapshot` 交给直播 UI。页面复用经典 `BoardView` 的选中、合法落点和位移动画。暗子使用双方一致、无文字的木质背面（同心饰环与环形纹点），不占用 semantic-v1 七列正面 atlas。`BoardView.AnimateMove` 只在提交前后同一稳定棋子 ID 从 `covered` 变为具体棋种时，让位置插值与横向翻面共享同一条 0..1 时间线：棋子离开起点时开始收窄，中途切换为正面，并在到达终点时完全展开；普通移动不翻面。揭子移动总时长取普通移动时长和主题 `landing` 时长的较大值，后者限制为 180..420 ms、未配置时为 280 ms，因此不会在落点后追加停顿。动画结束后才发出 `MoveAnimationFinished`。这些状态都由 BoardView 持有，在主线程 `_Process` 推进；SetPosition、重同步或退出节点会丢弃瞬态动画，不写回 Session。

联网界面把本机席位写入 `BoardView.BottomSide`，因此双方设备上自己的初始阵地都显示在棋盘下方；该旋转只影响绘制、坐标标签和命中映射，不改变权威坐标。

`JieqiReplayScreen` 保留上/下一步，并提供带刻度滑杆；棋盘在复盘页使用受限最小高度，为时间轴和底部按钮保留固定布局空间，避免常见窗口高度下被裁切。“隐约显示暗子身份”在无字背面的中心以低透明度叠加实际棋种，仅消费 replay snapshot，不改变正常对局背面。

Session 拥有游戏和正式着法；Screen 拥有 Godot 节点；Store 拥有文件；均在当前主线程同步使用。离开页面后节点释放，纯 C# Session 无非托管资源。

## 联机安全边界（已实现）

LAN 与 Steam 通过 `NetworkJieqiMatchController` 使用房主权威模型：只有房主持有 `JieqiGameSession` 和 Seed，客户端只发送带预期 Ply 的走子意图，并接收 `ToPublicPosition()` 产生的脱敏快照与权威合法落点。底层复用 `ITwoPlayerPacketTransport`，不复用经典双方完整局面的锁步消息。协议限制为 16,000 字节、带独立版本/玩法标记，并验证消息角色、坐标、枚举、序号和局面连续性。

连接建立时房主发送 `SHA-256("jieqi-authority-v1:{seed:X16}")` 承诺；对局结束后才释放 Seed，客户端重新计算承诺并逐手重放。`JieqiGameSession.CreateRecord` 也拒绝未结束的会话，防止 UI 或日志提前泄露。该机制能发现房主赛后更换 Seed，但 P2P 房主仍是实时裁决者，不能阻止恶意房主选择有利初始排列；需要消除此信任时应引入中立服务器或双方提交后联合派生随机数。

当前揭棋协议为 v2。房主拥有 `MatchClock` 并裁决局时/步时超时，按约 250 ms 发布脱敏棋钟；客户端只应用权威同步。认输请求带请求号、预期 Ply 和认证席位，房主裁决后发送终局公开状态，并仅在终局释放 Seed；客户端等待该状态确认。界面提供与经典一致的计时显示和认输入口，未结束时返回画廊会先提交本地认输。房间布局与经典一致：对手资料在棋盘上方，本机资料在下方，均显示本局昵称、头像、席位和棋钟；联网时右侧使用共享双人聊天面板，消息固定发给唯一对手、不提供目标选择。聊天与资料采用共享有界辅助帧，不进入揭棋权威状态或 Seed 承诺。协议仍没有悔棋、和棋协商或断线续局。事件由 Godot transport 轮询所在主线程同步进入控制器；`JieqiGameScreen` 先解绑资料、聊天与 UI 事件，再幂等释放控制器及 transport。

测试入口：`dotnet run --project tests/Xiangqi.Core.Tests/Xiangqi.Core.Tests.csproj`，覆盖确定性排列、公开投影脱敏、首步槽位走法、翻明、Seed/Shuffle 棋谱往返、直播棋谱阻止、房主/客户端同步、计时超时、联机认输和 Seed 不进入直播数据。`--smoke-feedback` 另外断言背面标签为空、covered→明子会安排翻面阶段且最终释放动画状态；真实双机 LAN、Steam 好友实联、断线体验与不同分辨率视觉仍需人工验收。
