# 玩法模块、画廊目录与 DLC 边界

更新：2026-09-12。本文区分已经接通的模块目录/本地及在线安装链路与尚未实现的执行机制。玩法规则仍分别维护在各玩法文档中；本文不定义一个任意棋盘、任意人数的通用棋局。

## 1. 目标与非目标

玩法模块边界解决四个宿主问题：按稳定身份发现玩法、以数据生成画廊入口、保存玩家排序，以及为独立安装包提供兼容锚点。它不统一棋盘坐标、快照、着法、权威模型、记录格式或网络 payload。

经典、三国、揭棋、军旗、五子棋和围棋可以分别使用自己的领域聚合与控制器。公共库只承载经过多个玩法验证的能力，例如传输端口、聊天、资料、主题/反馈和安全文件操作；不能为了减少类型数量而把双人 `Opponent()`、9×10 坐标或完整公开局面强加给其他玩法。

## 2. 已实现：目录契约与内置模块适配

独立的 `src/Xiangqi.GameHost/GameModuleContracts.cs` 契约程序集定义：

- `GameModuleId`：持久化身份，最长 96 个字符，只允许 ASCII 字母、数字、点和连字符，规范为小写。显示名称、程序集名和目录位置都不能代替它。
- `GameModuleManifest`：版本、名称、简介、卡片资源、能力与默认顺序的不可变描述。
- `GameModuleRoute`：模块自己的稳定入口 ID、画廊分组、标签、能力和组内顺序。
- `IGameModule`：只读发现接口；没有 Godot Node、transport、平台句柄或可变棋局。
- `GameModuleCatalog`：构造时验证模块/入口唯一性和能力一致性；之后为不可变、可跨线程读取、无需释放。`Order` 忽略损坏、重复或当前未安装的偏好并补齐新模块；`Move` 仅接受 -1/+1，未知模块失败。

`Presentation/GameModules/GameModuleRegistration.cs` 是当前 Godot 宿主的组合适配。它将已经验证的 route ID 绑定到主线程页面动作；回调本身不拥有页面、网络或引擎资源。`Main.GameModules.cs` 注册六个内置模块，`ModeGalleryScreen` 只遍历模块和入口，不再接收每个玩法的一长串委托。卡片左右箭头修改稳定 ID 顺序，设置保存到 `gallery/module_order`。

当前注册仍由主程序集静态创建，因此“内置模块已统一发现”和“玩法包可验证并落盘”是已实现事实，“下载玩法已出现在画廊或可动态执行”不是。

## 3. 调用、错误、线程和所有权

1. `Main` 在 Godot 主线程构造内置注册，`GameModuleCatalog` 先验证全部元数据。
2. 设置中的顺序只作为偏好输入；目录输出总是当前已安装模块的完整唯一序列。
3. 画廊按输出顺序创建卡片。点击 route 后，`GameModuleRegistration.Activate` 同步调用现有组合根。
4. 组合根创建的 Screen 继续拥有其事件订阅、控制器和局部资源，并按原玩法约定释放；目录不延长 Screen 生命周期。
5. 重复 GameId、重复 route、空入口、未在 Manifest 声明的能力属于包/开发错误，在目录构造时抛出。陈旧用户顺序属于可恢复输入，不阻止应用启动。

设置文件是宿主所有；模块不得直接写其他模块的顺序。排序变化不会改变进行中的对局、网络协议或记录路径。

## 4. 已实现：`.xqgame` 包扫描与原子安装

`.xqgame` 是 ZIP 容器，版本一包含：

```text
manifest.json
signature.sig            # 声明发布者公钥时必需
content/...              # 文件名可自定，但必须逐项列入 manifest
```

`manifest.json` 必须使用 schema 1，声明稳定模块 ID/版本、画廊资料和入口、最低宿主 API、执行模式、权限、发布者、公钥及每个载荷的相对路径、字节数和 SHA-256。解析器拒绝未知字段、重复字段/枚举/路径、绝对路径、反斜杠、`..`、符号链接/重解析点、未列出的载荷、大小或压缩比越限、载荷哈希不符和过新的宿主 API。纯资源包只接受当前白名单中的图片、音频、字体和文本扩展名；白名单不等于文件解析器绝对安全，因此仍需确认。

存在公钥时，它必须是 base64 编码的 SubjectPublicKeyInfo ECDSA P-256 公钥；`signature.sig` 是对 **manifest 原始字节** 的 ECDSA/SHA-256 DER 签名。发布者指纹由宿主对公钥字节计算 SHA-256，不能由 Manifest 自报。清单覆盖全部载荷哈希，完整 `.xqgame` 再计算一个包 SHA-256，用户批准绑定后者。签名不合法直接拒绝；未签名包保留 `SignatureValid=false` 交给混合信任策略决定。

`GamePackageReader.Inspect` 只读扫描且不提取/执行内容。画廊的“安装玩法包”通过 `GamePackageInstallDriver` 在工作线程扫描，Godot 主线程只接收不可变结果。确认页展示名称/ID/版本、发布者、签名和密钥指纹、执行模式、请求权限、文件数、完整包哈希及相应风险；取消时不写入。`GamePackageInstaller.InstallConfirmed` 在禁止写共享的文件句柄上重新计算确认过的包哈希、重新验证全部输入、在安装根的随机 `.staging` 目录逐文件复验哈希，然后将完整目录移动至 `user://modules/<module-id>/<version>`。失败不会留下可见版本，也不会改变启用目录。

安装器再次执行不可绕过的信任策略。确认哈希由上层 UI 传入，不能取包内自报值。有效签名且满足 `line-placement-v1` 的 sandboxed 包会写入 pending，并在下一次启动提升为 active；其余包只安装，不执行。禁用、卸载和活动模块保护 UI 仍需后续实现。

## 5. 已实现：签名索引、独立下载与版本切换状态

服务器发布一个 HTTPS JSON 索引，并在同 URL 后附加 `.sig` 文件。索引 schema 1 示例：

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-09-12T10:00:00Z",
  "modules": [{
    "id": "xiangqi.classic",
    "version": "1.1.0",
    "minimumHostApi": "1.0",
    "packageUrl": "https://games.example.com/modules/xiangqi.classic/1.1.0.xqgame",
    "packageBytes": 1234567,
    "packageSha256": "64 个十六进制字符",
    "publisherKeyFingerprint": "64 个十六进制字符",
    "official": true
  }]
}
```

`.sig` 是索引**原始 JSON 字节**的 ECDSA P-256/SHA-256 DER 签名。宿主中的索引公钥是 base64 SubjectPublicKeyInfo；它授权服务器推荐列表，但不替代每个 `.xqgame` 自身的发布者签名。`official=true` 也不是信任证据：只有包发布者指纹同时命中宿主 `official_publisher_fingerprints` 固定集合，才自动下载；指纹不匹配的“官方”条目整次检查失败。非官方条目下载前必须展示 URL、大小、发布者指纹和包哈希供用户确认，下载及完整扫描后再展示实际 Manifest、权限、执行模式与签名，进行第二次安装确认。

发布配置位于 `project.godot`：

```ini
[game_modules]
update_index_url="https://games.example.com/index.json"
update_feed_public_key="Base64-SPKI"
official_publisher_fingerprints="sha256-hex-1,sha256-hex-2"
```

仓库默认留空，因此开发构建不会联系未知服务器；画廊“检查更新”会说明未配置。配置有效时，启动后静默检查官方更新，画廊也可手动检查。`GameModuleUpdateClient` 只接受无凭据/片段的 HTTPS URL，索引上限 1 MiB、签名 16 KiB、模块 256 个、单包 128 MiB。下载写入 `user://modules/.downloads`，已有匹配临时文件使用 HTTP Range 续传；服务端返回完整 200 时从头覆盖，206 必须具有一致 Content-Range。长度、索引包哈希、包内身份/版本和发布者指纹任一不符即拒绝，完成后才把 `.partial` 提升为 `.xqgame`。

成功安装到 `user://modules/<id>/<version>` 并通过当前受限 ABI 验证后，`GameModuleVersionStore` 原子写 `user://modules/versions.json`：当前 active 不变，新版本进入 pending；只有下次应用启动才将 pending 提升为 active，并保留此前 active 作为 rollback。`RequestRollback` 同样只把回滚版本安排为 pending，下一次启动交换，避免运行中的资源被替换。启动复验或运行时构造失败时，失败版本从 active 移除、记入 failed，并安排上一个已知正常版本为下次 pending；failed 仍参与版本比较，服务器不会无限重装同一个坏版本。状态 schema 2 可读取首版 schema 1。版本目录不自动删除，各玩法 `user://games/<id>` 记录更不会随升级或回滚删除。

错误语义与事件顺序：一次驱动只允许一个操作；网络、签名、磁盘或策略错误产生失败事件且不启用部分内容；下载进度可多次出现，第三方下载完成事件之后必须显式调用安装，安装成功才产生 staged，检查完成最后产生 checked。`GameModuleUpdateDriver` 拥有 HttpClient 和取消源，退出树取消；GameHost 解析/下载类不操作 Godot UI，注入 HttpClient 的所有权仍属于调用者。

## 6. 已实现：声明式受限运行时 v1

active 目录在加入画廊前由 `GamePackageReader.InspectInstalledDirectory` 再次扫描：根与全部后代不能是符号链接/重解析点，文件集合必须与 Manifest 完全一致，长度、SHA-256 和 Manifest ECDSA 签名必须仍有效，并与版本日志中的版本、执行模式、权限和发布者指纹相同。下载模块 ID 不得覆盖六个编译进宿主的内置模块。验证失败的模块本次启动不可见，并进入上述 failed/rollback 流程。

当前唯一 ABI 是 `content/runtime.json`：

```json
{
  "schemaVersion": 1,
  "kind": "line-placement-v1",
  "boardWidth": 15,
  "boardHeight": 15,
  "winLength": 5,
  "firstPlayerLabel": "黑方",
  "secondPlayerLabel": "白方",
  "firstPlayer": 0
}
```

对应包必须满足：`executionMode="sandboxed"`；权限只能为空或 `module-storage`；能力严格为 `local-play`；只声明一个本地入口；棋盘宽高分别为 3..25；胜利长度至少 3 且不超过较长边；玩家标签最多 24 字且无控制字符。卡片只接受不超过 8 MiB 的 PNG/JPEG/WebP，解码后边长不超过 4096。Manifest 还必须逐项列出 runtime 与卡片文件。

玩法进入画廊后由宿主的 `LinePlacementGame` 裁决空点落子、两方轮次、横/竖/双斜线胜利与满盘和棋，包无法提供 C# 委托、Godot Node、脚本、shader、网络请求、进程或文件路径。UI 只向聚合提交行列并显示防御性快照；如果授予 `module-storage`，终局以有界 JSON 写入该模块独立的 recent 目录并保留最近十局。外部卡片在后台验证后以字节快照交给主线程解码，画廊退出时释放所建纹理。

这使第三方能够在不更新整包的情况下发布和更新“双方轮流落子、连线获胜”类玩法，并在 Windows/Android 共用同一纯托管规则运行时。它不是任意棋类脚本沙箱：当前不支持自定义走子、隐藏信息、LAN/Steam、AI、撤销、计时、记录浏览或内置五玩法的代码热替换。新增 ABI 必须由宿主发版并增加独立领域验证，不能让包上传表达式或反射类型来绕过边界。

## 7. 第三方玩法的信任模型

用户确认是知情授权，不是沙箱。下载模块分为两类：

已实现的 `GamePackageTrustPolicy` 是安装前的纯决策门：批准绑定完整包 SHA-256；包内容、版本或权限改变后必须重新确认。沙箱包请求外部进程、原生代码或任意文件系统会直接拒绝；未签名完全信任包在普通模式下直接拒绝，仅开发者模式可进入强警告确认。包解析、载荷哈希、ECDSA 签名、本地/在线来源、两阶段确认、原子安装和版本状态已经实现；证书信任链/撤销、开发者模式 UI 和实际运行时尚未实现。

### 7.1 进程内完全信任模块

第三方 C# 代码若进入主进程，原则上可获得与游戏相同的文件、网络、进程和平台权限。安装页必须展示发布者、签名状态、哈希、来源、版本、请求能力和“可访问本机同等权限”的明确警告；未知/变化的签名必须再次确认。未签名包只能通过显式的开发者模式安装，并持续显示风险状态。

签名证明包来自对应密钥且未被修改，不证明代码没有恶意。允许列表、撤销列表和商店审核只能降低风险，不能改写这一语义。

### 7.2 受限模块

需要技术隔离的第三方玩法不得加载任意进程内程序集。后续应选择一个受限执行方案，例如宿主定义的声明式规则/资源 API，或独立子进程/WASM 运行时，通过有界、版本化消息请求渲染语义、存储和网络能力。宿主逐项授予能力，外部输出仍按不可信输入验证。

`line-placement-v1` 是已经实现的数据型受限运行时，但不接受包代码。更通用的 WASM/独立进程方案尚未选型。任意代码 DLC 必须在 Windows 原型、Android 真机、签名和发布渠道验证后再声明支持。

## 8. 公共能力与玩法专属能力

计划中的 `IGameHostServices` 不作为服务定位器暴露整个应用，而由包的权限/依赖声明获得窄端口：

| 能力 | 可共享内容 | 玩法仍负责 |
| --- | --- | --- |
| 存储 | 玩法作用域、安全路径、原子写、配额、recent 淘汰 | codec、重放验证、隐私与何时落盘 |
| 双人网络 | transport 工厂、认证来源、有界帧、聊天、资料 | payload、协议版本、序号、权威和断线语义 |
| 多人网络 | 身份/连接基础设施中可验证的共性 | 三国固定三席、日志、迁移和孤立端规则 |
| 象棋表现 | 棋子层、主题、语义反馈、音效资源 | 玩法事件到语义提示的映射 |
| AI | 进程/下载生命周期中可验证的共性 | 局面格式、候选着法、复验和许可 |

网络匹配最终必须携带 `GameModuleId + gameplay protocol id/version + configuration hash`。仅安装同名模块并不代表可混局；未知或不兼容版本必须在创建棋局前拒绝。

## 9. 已实现：独立记录目录

记录根现为 `user://games/<game-id>/records/recent|saved`。无引擎的 `GameRecordStorage` 提供模块作用域、立即子文件验证、重解析点拒绝、同目录原子写、唯一命名和 recent 淘汰；各模块保留 PGN、扩展 PGN、AC2、SGF 等自己的格式与重放验证。卸载默认保留用户记录；删除记录是独立、明确且可预览的操作。

首次访问时会把现有 `user://records/classic|three|jieqi|junqi|gomoku` 的匹配记录和 sidecar 原子复制到新根，写入迁移标记后不再重复；旧文件始终保留，不会因迁移或安装/卸载模块被静默删除。文件 I/O 当前仍同步，批量导入或云同步前需增加后台驱动，完成回调只能在主线程更新 UI。

## 10. 兼容与测试

目录契约使用测试 fake，不启动 Godot。最低覆盖默认顺序、陈旧/重复偏好、移动边界、重复 ID、入口重复、能力不匹配和数量上限。Godot smoke 继续验证六张卡片、38 个入口、排序后的完整性与点击路由，并实例化围棋页面与坐标开关。

`--smoke-ui` 的声明式测试模块由 `Main.CreateDeclarativeUiSmokeModule` 在主线程构造；卡片通过 `GD.Load<Texture2D>` 读取已导入资源，再用 `GetImage` / `SavePngToBuffer` 生成独立 PNG 字节，兼容编辑器目录与导出的 PCK。临时 `Image` 在方法退出时释放，资源缓存中的纹理不由测试夹具释放；加载或编码失败使 smoke 失败。不要用 `File.ReadAllBytes(ProjectSettings.GlobalizePath("res://..."))` 读取已导入图片，导出后源图片未必是磁盘文件。回归需同时运行编辑器版和实际 Windows Debug 导出程序的 `--smoke-ui`，不能靠把源 PNG 额外复制到导出目录通过测试。

包层测试已覆盖正常扫描和安装、确认哈希变化、重复版本、载荷哈希/签名失败、越目录路径及纯资源代码扩展名。更新测试覆盖索引签名/篡改、语义版本筛选、官方固定指纹策略、HTTP Range 续传后的整包复验，以及 pending→active→rollback 的启动状态转换与持久化。声明式运行时测试覆盖安装目录复验、合法/重复/越界落子、胜利、终局拒绝、载荷篡改、failed 隔离、自动回滚和 schema 1 状态兼容；Godot UI smoke 物化外部卡片并实例化声明式对局页。还需补齐真实 HTTPS 服务器、Range 被代理改写、磁盘耗尽、索引密钥轮换/撤销、Android 真机图像/触控、记录浏览和权限变化 UI。真实第三方代码执行在隔离方案完成前不得描述成安全。

## 11. 下一步

1. 将六个玩法组合根从 `Main` 迁入独立模块程序集，同时保持现有行为。
2. 把 Steam/LAN 路由从全局玩法枚举迁移为稳定协议注册。
3. 为声明式模块增加记录浏览、显式禁用/回滚 UI，以及第二种可替换规则 ABI。
4. 分别原型验证完全信任 DLL 和 WASM/独立进程运行时，再确定第三方代码 SDK；在隔离与跨平台验收前不执行包代码。
