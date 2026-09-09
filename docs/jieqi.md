# 揭棋玩法与实现边界

更新：2026-09-09。总览见 [TECHNICAL_ARCHITECTURE.md](../TECHNICAL_ARCHITECTURE.md)，共享接口见 [抽象层使用手册](abstractions.md)。规则基线参考棋者文章及 Mistboard 揭棋规则；不同平台的长打、暗子被吃后谁可查看等约定并不完全一致，首版固定下述语义，后续变体必须显式版本化。

## 已实现规则

- 红黑将/帅明置原位；每方其余十五枚棋子在本方十五个标准起始槽位中确定性洗牌并暗置，红方先行。
- 暗子首步按槽位棋种走法和限制移动，成功落子后翻明；因此暗士首步仍受九宫限制、暗象首步仍不能过河。
- 明子按实际身份移动；明士只保留斜走一步，明象保留田字与塞象眼，但均不再受九宫/河界限制。
- 将军、自将校验、飞将、吃将、将死和困毙沿用经典语义。当前尚未实现赛事级长将/长捉与 120 步无吃子裁决。

领域入口是 `JieqiGame.Create(ulong seed)`。`JieqiPiece` 同时保存 ActualKind、CoverKind 与 IsRevealed；这些均属权威状态。`JieqiSnapshot.ToPublicPosition()` 将所有暗子投影为 `BuiltInPieceKinds.Covered`，正常 UI 及未来网络协议只能消费该投影。`HiddenIdentityHints()` 只允许赛后复盘使用，不能进入直播、日志提示或远端消息。

## 随机与棋谱

洗牌固定为 `splitmix64-fy-v1`：SplitMix64 产生 64 位序列，Fisher–Yates 对每方十五枚棋种独立洗牌。算法名称和 64 位十六进制 Seed 与 ICCS 着法一起写入 PGN 扩展：`Variant=Jieqi`、`Format=ICCS`、`Shuffle=splitmix64-fy-v1`。选择 PGN/ICCS 是为了和经典记录容器、元数据及外部坐标工具兼容；Seed/Shuffle 是揭棋专有扩展，不宣称普通象棋软件能重建暗子。

`JieqiPgnCodec.TryParse` 限制输入为 4 MiB，验证变体、算法、种子和坐标，并通过 `JieqiGame` 逐手重放。未知算法拒绝导入，避免同一种子在未来悄然产生另一盘棋。`JieqiRecordStore` 原子写入 `user://records/jieqi`，recent 最多十局，saved 不淘汰。

## 表现与所有权

`JieqiGameScreen` 通过 `JieqiGameSession` 提交意图；Session 的 Snapshot/StateChanged/MoveCommitted 只发布脱敏 `GameSnapshot`，不把权威 `JieqiSnapshot` 交给直播 UI。页面复用经典 `BoardView` 的选中、合法落点和位移动画。暗子使用双方一致、无文字的木质背面（同心饰环与环形纹点），不占用 semantic-v1 七列正面 atlas。`BoardView.AnimateMove` 只在提交前后同一稳定棋子 ID 从 `covered` 变为具体棋种时，让位置插值与横向翻面共享同一条 0..1 时间线：棋子离开起点时开始收窄，中途切换为正面，并在到达终点时完全展开；普通移动不翻面。揭子移动总时长取普通移动时长和主题 `landing` 时长的较大值，后者限制为 180..420 ms、未配置时为 280 ms，因此不会在落点后追加停顿。动画结束后才发出 `MoveAnimationFinished`。这些状态都由 BoardView 持有，在主线程 `_Process` 推进；SetPosition、重同步或退出节点会丢弃瞬态动画，不写回 Session。

`JieqiReplayScreen` 保留上/下一步，并提供带刻度滑杆；“隐约显示暗子身份”在无字背面的中心以低透明度叠加实际棋种，仅消费 replay snapshot，不改变正常对局背面。

Session 拥有游戏和正式着法；Screen 拥有 Godot 节点；Store 拥有文件；均在当前主线程同步使用。离开页面后节点释放，纯 C# Session 无非托管资源。

## 尚未实现 / 不得误称已完成

揭棋 LAN 与 Steam 入口已在画廊预留，但尚未开放。经典协议是双方持有完整局面的锁步模型；若广播 Seed，会让对手算出所有暗子。后续必须在 `IMatchTransport` 之上新增房主权威命令与脱敏快照/揭子事件，验证来源、序号、局面版本和重连历史，且 Seed 只在赛后记录中披露。不能为了复用现有协议牺牲暗子保密性。

测试入口：`dotnet run --project tests/Xiangqi.Core.Tests/Xiangqi.Core.Tests.csproj`，覆盖确定性排列、公开投影脱敏、首步槽位走法、翻明、Seed/Shuffle 棋谱往返和未知算法拒绝。`--smoke-feedback` 另外断言背面标签为空、covered→明子会安排翻面阶段且最终释放动画状态；不同分辨率下的背纹、翻面观感与淡字可读性仍需截图/人工验收。
