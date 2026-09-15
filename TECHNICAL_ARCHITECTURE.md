# 项目技术架构（Agent 与维护者入口）

更新：2026-09-15。

本文是仓库的架构地图，不重复收录每个玩法、协议或 UI 的实现细节。开发时先阅读 [AGENTS.md](AGENTS.md)，再按本文路由进入对应专题文档；代码与文档不一致时，以已验证的代码事实为准并同步修正文档。

## 1. 当前系统概览

项目基于 Godot 4.7.2 Mono 与 C#，当前包含六个彼此独立的玩法领域：

| 玩法 | 已实现入口 | 记录/AI | 详细文档 |
| --- | --- | --- | --- |
| 经典象棋 | 本地双人、LAN、Steam 好友 | PGN/ICCS、UCI/Pikafish | [经典象棋架构](docs/architecture.md) |
| 三国象棋 | 本地三人、TCP LAN、Steam 三席邀请 | 扩展 PGN/CellId | [三国象棋](docs/three-kingdoms.md) |
| 揭棋 | 本地双人、LAN、Steam 好友 | 扩展 PGN、Seed 赛后验证 | [揭棋玩法](docs/jieqi.md) |
| 军旗 | 本地热座、LAN、Steam 好友 | AC2 `INFO[1.0]` | [军旗玩法](docs/junqi.md) |
| 五子棋 | 本地双人、LAN、Steam 好友、人机 | SGF FF[4]/GM[4]、Rapfi/Piskvork | [五子棋玩法](docs/gomoku.md) |
| 围棋 | 本地双人、LAN、Steam 好友 | SGF FF[4]/GM[1]、中国规则数子；AI 暂未接入 | [围棋玩法](docs/go.md) |

这些玩法共享设置、主题、反馈效果、部分记录设施和传输端口，但不共享一个“任意人数、任意棋盘”的通用棋局引擎。经典的红黑双方、9×10 坐标和协议语义不能套用到三国、军旗、五子棋或围棋。

画廊现通过 `IGameModule` / `GameModuleCatalog` 发现六个内置玩法和已启用的声明式玩法，并按稳定 GameId 保存玩家顺序；入口、分组和能力由模块声明，不再由画廊构造函数逐项硬编码。每个玩法的最近/保存对局使用独立记录根。`.xqgame` 已接入本地安装，以及签名 HTTPS 索引、断点下载、包/发布者复验、版本暂存、启动切换、失败隔离和单版本回滚。官方包只有命中宿主固定发布者指纹才可自动下载；第三方包在下载前和安装前分别确认。受限运行时首个 ABI `line-placement-v1` 可从 active 目录实际加入画廊并运行，不加载任何包内代码；编译进宿主的六个玩法仍不能用数据包替换代码。边界见[玩法模块与 DLC](docs/game-modules.md)。

当前架构结论：核心棋规与 Godot UI 已分离；跨玩法抽象已有实际调用链，但经典房间编排、三国只读查询面和拓扑/投影注入仍存在已记录的边界缺口。证据及改进优先级见 [架构检查记录](docs/architecture-review.md)。

## 2. 工程与依赖方向

```text
Xiangqi（Godot 应用）
  ├── Presentation：页面、棋盘、输入、外观、动效、音频
  ├── 基础设施适配：ENet、Steam、外部 AI、玩家资料
  ├── → Xiangqi.GameHost
  ├── → Xiangqi.Core
  └── → Xiangqi.Transport → Xiangqi.Core

Xiangqi.GameHost
  ├── 玩法身份、Manifest、能力、入口与目录顺序（无 Godot 依赖）
  └── 已安装目录复验与 line-placement-v1 受限规则聚合

Xiangqi.Core
  ├── Application：会话、控制器、端口、命令、协议、棋钟
  └── Domain：棋局、规则、坐标/拓扑、结果与快照

Xiangqi.Core.Tests → Xiangqi.Core
Xiangqi.ThreeNetwork.Tests → Xiangqi.Core + Xiangqi.Transport
```

依赖只能朝 Core 方向流动。`Xiangqi.Core` 不引用 Godot、Steamworks.NET、Presentation 或具体 transport；Presentation 中的网络与 AI 类虽然和 UI 同程序集，职责仍是基础设施适配。

| 工程/入口 | 作用 |
| --- | --- |
| [Xiangqi.csproj](Xiangqi.csproj) | Godot 应用；桌面 `net10.0`，Android 条件 `net9.0` |
| [Xiangqi.GameHost.csproj](src/Xiangqi.GameHost/Xiangqi.GameHost.csproj) | 第三方 SDK 的最小无引擎契约面；不引用具体玩法 |
| [Xiangqi.Core.csproj](src/Xiangqi.Core/Xiangqi.Core.csproj) | 无引擎依赖的领域与应用层 |
| [Xiangqi.Transport.csproj](src/Xiangqi.Transport/Xiangqi.Transport.csproj) | 三国 TCP、握手与连接管理，只引用 Core |
| [Xiangqi.sln](Xiangqi.sln) | 应用、GameHost、Core、Transport 与两个控制台测试工程 |
| [project.godot](project.godot) | Godot 主场景与渲染配置 |

Steam 原生引用按 Windows 目标隔离。条件编译成功不能替代 Android 实机、跨设备 LAN 或 Steam 多账号验收；平台现状与构建依据见 [Godot 4.7 / .NET 10 迁移记录](docs/godot-4.7-migration.md)。

## 3. 稳定分层边界

| 层 | 负责 | 不负责 |
| --- | --- | --- |
| Domain | 规则、占位、轮次、胜负、撤销及玩法不变量 | UI、网络、设置、AI 进程 |
| Application | 会话、权限、协商、序号/版本检查、计时和 AI/网络编排 | 像素布局、平台句柄、复制棋规 |
| Infrastructure | transport、进程、平台 API、外部输入解码、资源释放 | 决定合法着法或绕过领域复验 |
| Presentation | 布局、命中、选择、动画、快照展示和意图提交 | 修改权威棋盘或自行实现棋规 |

统一交互方向：

```text
输入位置 → 视图命中/投影 → 会话或控制器意图
		 → 领域验证 → 快照/结果/语义事件 → UI 刷新与动画
```

外部 AI、网络包和导入棋谱均是不可信输入，必须验证来源、长度、枚举、坐标、序号/局面版本，并通过同一领域规则重新执行。动画和推演副本属于表现状态，不得回写权威对局。

接口职责、注入点、错误语义、线程模型、所有权和扩展示例统一维护在 [抽象层使用手册](docs/abstractions.md)，不在本文复制接口级说明。

## 4. 文档路由

推荐阅读顺序：本文 → 目标玩法文档 → 抽象手册 → 相关基础设施文档 → 源码与测试。

### 4.1 按玩法进入

| 修改范围 | 必读文档 | 文档负责的细节 |
| --- | --- | --- |
| 经典象棋 | [docs/architecture.md](docs/architecture.md) | 领域裁决、四种房间、双人协议、棋钟、UCI、视图协作 |
| 三国象棋 | [docs/three-kingdoms.md](docs/three-kingdoms.md) | 三域拓扑、收编/淘汰、控制器、三席同步、迁移与重连 |
| 揭棋 | [docs/jieqi.md](docs/jieqi.md) | 暗子身份、洗牌承诺、权威脱敏协议、记录与翻面边界 |
| 军旗 | [docs/junqi.md](docs/junqi.md) | 图拓扑、布阵/战斗、玩家视图隐私、联机控制器与 AC2 |
| 五子棋 | [docs/gomoku.md](docs/gomoku.md) | 可配棋盘、禁手、控制器、可信房主协议、SGF 与 Rapfi |
| 围棋 | [docs/go.md](docs/go.md) | 气/提子、全同、双方确认数子、可信房主协议、SGF GM[1] 与坐标投影 |
| AI 难度与分析 | [docs/ai-engines.md](docs/ai-engines.md) | 象棋/五子棋五档与高级搜索、五子棋胜率图、军旗/三国评估边界 |

### 4.2 按横切能力进入

| 修改范围 | 文档 |
| --- | --- |
| 新增或变更抽象、控制器、双人/三人传输、AI、主题、棋谱、推演 | [抽象层使用手册](docs/abstractions.md) |
| 玩法目录、顺序、模块包、第三方信任与 DLC | [玩法模块与 DLC](docs/game-modules.md) |
| Steam 初始化、开发 App ID、Lobby 与发布验收 | [Steam 集成](docs/steam-integration.md) |
| XML 主题、atlas、shader、安全解析和资源租约 | [XML 主题包与 shader 接口](docs/theme-package-proposal.md) |
| Godot/.NET 目标框架、条件构建和迁移风险 | [Godot 4.7 / .NET 10 迁移记录](docs/godot-4.7-migration.md) |
| 已知架构债、分层证据与改进优先级 | [架构检查记录](docs/architecture-review.md) |
| 三国坐标的历史设计依据 | [三国坐标方案](docs/three-kingdoms-coordinate-proposal.md)；当前事实仍以玩法文档和代码为准 |

新增玩法或基础设施时，应优先更新对应专题文档；只有系统边界、工程依赖、路由或全局验证入口变化时才更新本文。

## 5. 跨玩法契约索引

以下只列稳定边界，具体类型、包格式和限制由路由文档维护：

- 双人 transport 只搬运有界数据；经典类型化消息与揭棋/军旗/五子棋/围棋的玩法协议不能互相解释。三国使用固定三席端口和独立同步模型。
- 双人房聊天固定发送给唯一对手，不显示接收者选择；经典协调器和揭棋/军旗/五子棋/围棋原始包适配器共用 `RoomChatManager.PostToOpponent`。只有三人及以上房间允许选择全房间或指定席位。
- LAN 与 Steam 可以复用传输适配层，但身份绑定、重放防护、权威模型和断线语义由各玩法应用层负责。
- 经典 AI 使用 `IXiangqiAiEngine` 与 UCI；五子棋使用 `IGomokuAiEngine` 与 Piskvork/Rapfi。两个端口的局面、着法和取消语义不同。两者只共享玩家可见的五档难度枚举，并分别映射时间、深度与引擎参数；详见 [AI 难度与分析](docs/ai-engines.md)。军旗、三国与围棋 AI 尚未实现。
- 一键引擎安装由共享安装端口负责固定版本下载、SHA-256 校验、安全解压和必要文件探测；运行时只从安装器管理目录解析当前固定版本，不接受玩家提供的可执行文件路径。
- 玩法更新索引与玩法包使用两层签名边界：索引密钥授权“服务器推荐哪些版本”，包发布者密钥证明“内容由谁发布”；两者不能互相替代。
- 声明式 DLC 只向宿主提交有界数据；落子、轮次、胜负和记录写入由宿主实现。包不能提供委托、脚本、程序集、Godot 节点或任意文件路径。
- 经典/三国/揭棋/军旗/五子棋/围棋分别使用自身可重放格式；导入后必须逐手复验，不能把文本解析成功当作合法对局。
- Godot 节点、回调、套接字、进程、纹理和临时资源必须有明确所有者及幂等释放路径；后台 I/O 不得直接操作 UI。

## 6. 当前集成状态与证据边界

截至 2026-09-15，本开发线包含六个内置玩法。围棋已实现中国规则的 19 路本地/LAN/Steam 对局、双方确认数子、任意行棋阶段的本地手动数目估算、SGF GM[1] 保存/导出/复盘和局内坐标开关，AI 尚未接入。手动估算是与权威棋局隔离的临时分析，不进入联机协议或棋谱。六种内置玩法的最近对局都可在记录页追加到保存对局；经典、五子棋和围棋记录可分别导出为 PGN/ICCS、SGF GM[4] 和 SGF GM[1]。各玩法记录用 sidecar 固化本局玩家昵称与受限头像；己方资料按字段优先使用自定义值，缺失时回退 Steam 昵称/头像或本地默认值。六种内置对局页均显示玩家昵称与头像；头像由共享玩家卡按比例裁切铺满圆角矩形且不使用阵营底色。有步时的玩法还消费应用层 `MoveClockIndicator`，在头像外显示绿色剩余进度并于最后十秒切为红色。经典与围棋把低频操作收进“功能”菜单，经典 LAN 地址等技术信息只在设置内显示。行为细节以各玩法专题文档为准。

已完成的本机自动验证包括核心控制台测试、三国网络测试、Godot UI/玩法 smoke，以及 Windows/Android 条件构建；引擎安装 smoke 还验证了固定 Rapfi、Pikafish 包的下载、哈希、解压与真实协议握手。历史环境和逐次证据保留在专题文档及 Git 提交中，不在本入口维护逐日流水账。

尚不能宣称完成的范围：跨设备 LAN、防火墙/NAT、Steam 双/三账号端到端、Android 真机触控与性能、复杂禁手棋库对拍、恶意房主仲裁，以及经典断线重连。架构层仍待改进统一经典房间控制器、三国只读查询门面和完整拓扑/投影注入。

## 7. 验证入口

测试项目是自运行控制台程序，不能用 `dotnet test` 的空成功替代：

```powershell
dotnet run --project .\tests\Xiangqi.Core.Tests\Xiangqi.Core.Tests.csproj
dotnet run --project .\tests\Xiangqi.ThreeNetwork.Tests\Xiangqi.ThreeNetwork.Tests.csproj
dotnet build .\Xiangqi.sln
dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=android
dotnet build .\Xiangqi.csproj -p:GodotTargetPlatform=windows
```

Android 与 Windows 目标共享部分输出，应串行构建；运行桌面 Godot 前最后构建 Windows。Godot smoke 的参数、环境变量和断言随对应功能维护在玩法/基础设施文档与测试代码中，其中常用入口包括：

| 参数 | 范围 |
| --- | --- |
| `--smoke-ui` | 画廊路由、设置页、全屏布局和经典 LAN 消息栏 |
| `--smoke-gomoku-ai` | Rapfi/Piskvork 探测、禁手配置与合法回复 |
| `--smoke-engine-install-rapfi` / `--smoke-engine-install-pikafish` | 固定包下载、校验、解压及真实握手 |
| `--smoke-feedback` | 主题、落子/吃子/翻面及推演反馈 |
| `--smoke-three` | 三国投影、拓扑、旋转命中和主题 |
| `--smoke-lan-host` / `--smoke-lan-client` | 经典双进程 LAN |
| `--smoke-steam-init` / `--smoke-steam-lobby` | Steam 初始化与 Lobby 生命周期，不代表好友实联 |

提交前还需执行 `git diff --check`，确认 Markdown 路由指向真实文件，并按变更风险补充目标玩法的协议、边界和资源生命周期测试。
