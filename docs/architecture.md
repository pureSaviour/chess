# 中国象棋项目架构

## 目标

当前版本提供菜单导航、本地双人和局域网对弈。核心规则不依赖 Godot，便于单元测试、远端复核和未来接入 Steam。

## 分层

```text
Presentation (Godot C#)
    输入、绘制、动效呈现
             |
Application
    GameSession、命令入口、只读快照、领域提示
             |
Domain
    Board、ChessGame、IPieceRule、具体棋子规则
             |
Infrastructure
    ENetMatchTransport、SteamMatchTransport、Steam Lobby、存档
```

- `Domain`：纯 C#，拥有所有棋规和胜负判定。任何 UI 或网络端都不能自行判定合法性。
- `Application`：对一次对局编排操作并发出状态与表现提示。UI 和网络都应调用同一命令入口。
- `Presentation`：Godot 节点仅消费快照、转发用户意图并播放提示，不持有棋规。
- `Infrastructure`：`EnetMatchTransport` 负责局域网，`SteamMatchTransport` 负责 Steam P2P；二者实现同一 `IMatchTransport`，无需修改棋规或 UI 输入模型。

## 扩展点

### 新棋子

实现 `IPieceRule` 并注册到 `PieceRuleRegistry`。规则对象只回答几何/阻挡约束；“不能送将”等全局约束统一由 `ChessGame` 处理。棋子显示名和视觉样式由展示层映射，避免领域层依赖贴图。

### 联机

`NetworkGameCoordinator` 统一协调本地输入和 `IMatchTransport`。`MatchWireProtocol` 为 ENet 和 Steam 提供同一套有明确类型、长度和上限的二进制格式，支持房间配置、走子、聊天、时钟同步、超时裁决、悔棋/和棋协商及自定义玩家资料；不接受对象反序列化或任意棋盘状态。聊天正文限制为 512 个 UTF-8 字节，自定义头像固定转换为最多 64×64 RGBA8，昵称和 ID 也有独立字节上限。双方都用 `ChessGame` 重新校验着法，协商请求绑定发起时的局面手数，避免在变化后的棋盘上应用旧响应。房主是时钟与超时裁决的权威端。后续可扩展状态哈希与断线重连。

### 玩家资料

`MatchPlayerProfiles` 是本局双方资料的所有权边界。平台提供器只产生不依赖 Godot/Steam 句柄的快照；界面再把 RGBA8 转成临时 `ImageTexture`。Steam 默认资料由 Lobby 成员 SteamID 直接向 Steam Friends/Utils 查询，自定义资料则通过公共对局协议交换，因此 ENet 与未来的 Steam 后端行为一致。断线、退出对局或房间关闭时，纹理、像素缓冲和资料缓存都会在同一生命周期内释放。

### 对局协商

`MatchOfferRequest` 和 `MatchOfferResponse` 通过请求编号、申请类型与局面手数配对。协调器在申请待处理期间关闭走子入口，展示层同时暂停棋钟；响应后恢复。悔棋申请还会在发送端和接收端分别验证申请者确为最后行棋方，即只能撤销自己刚走完且对方尚未落子的那一手。悔棋只通过 `GameSession.Undo` 回退最近一个权威快照，和棋只通过 `GameSession.DeclareDraw` 进入 `GameEndReason.Draw`，UI 不直接修改棋盘或胜负状态。并发、越权或过期申请会被拒绝，而不会尝试合并。

### 计时

`MatchClock` 是不依赖 Godot 的局时/步时状态机。零表示不限时。局域网双方都可平滑显示倒计时，但只有房主能发布超时裁决，客户端通过周期 `ClockSyncCommand` 校正显示。

### Steam

`SteamLobbyService` 独占 SteamAPI 生命周期和回调泵，创建双人好友 Lobby、写入版本化对局 metadata、获取 Lobby 成员的 Steam 昵称/SteamID64/头像，并把通过成员校验的对手交给 `SteamMatchTransport`。后者使用 Steam Networking Messages 的可靠通道及 Steam NAT/Relay 能力；规则层和 `GameScreen` 不引用 Steamworks。等待中的房间可显式取消，传输关闭也会反向触发离开 Lobby，避免下一次创建复用失效房间。测试与正式 App ID 的切换要求见 `docs/steam-integration.md`。

### 特效

`PresentationCue` 是规则/应用层发出的语义提示，例如移动、吃子、将军、结束。`CueEffectLayer` 根据提示播放程序化闪光、放射线和标题动画。`DoubleChariotMateDetector` 已作为首个 `IPositionPatternDetector` 接入；识别器不引用 Godot，也不改变棋局合法性。

### 棋盘动画与音频

`BoardView` 只保存选中脉冲和移动插值等短生命周期的视觉状态；权威棋盘始终来自 `GameSnapshot`。`GameScreen` 在收到语义提示后缓存本步的 `ChessMove`，再用前后两个不可变快照启动动画，因此本地与网络着法共用同一表现路径。动画结束事件由 `GameplayAudioPlayer` 映射为普通落子或吃子音效，音频节点不引用棋规，并统一输出到 `SFX` 总线。替换音频只需保持资源语义映射，不需要修改规则层。

## 基础版规则边界

- 红方先行；将帅照面属于被攻击状态。
- 无合法着法时判负：被将军为将死，未被将军为困毙。
- 暂不处理长将、长捉、三次重复等赛事裁决规则；这些需要先确定采用的规则版本。
- 暂不提供 AI 与棋谱记谱；局时、步时、超时判负及 Steam 好友联机已经支持。
