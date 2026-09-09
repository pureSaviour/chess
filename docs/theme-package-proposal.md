# XML 主题包与 shader 接口（schema v1）

更新：2026-09-08。四项设计选择均已确认并实现：内置/玩家双目录、允许 canvas_item shader 但禁止脚本/原生代码、仅手动重扫且不在对局中热重载、棋子使用 semantic-v1 atlas。

## 1. 目标与非目标

目标是让一个主题包同时描述经典/三国棋盘、棋子和语义特效，并由 XML 指向图片、字体及 Godot shader。主题只消费展示语义（选中、可走、移动、落地、吃子、将军、胜负等），不得改变逻辑坐标、拓扑、棋规、计时、网络身份或协议。

第一版不允许主题携带 C#、GDScript、原生库、网络地址或进程命令。所谓“自定义特效”是把已知语义槽映射到材质/shader 与受限参数，不允许主题创建新的游戏事件或执行任意业务逻辑。

## 2. 建议目录

```text
assets/themes/
  spring/
    theme.xml
    board/classic.png
    board/three.png
    pieces/red.png
    shaders/board.gdshader
    shaders/selection.gdshader

user://themes/
  my-theme/
    theme.xml
    ...
```

- `assets/themes` 保存随游戏发布、进入 Godot 导入流程的内置主题。
- `user://themes` 保存玩家主题，避免修改安装目录；导入策略需单独实现，不能假设运行时文件自动成为 `res://` 资源。
- 导出物中的 `res://` 位于 PCK；启动时会把有界的只读内置包物化到 `user://.builtin-themes-schema1` 再交给同一 .NET XML 解析链。该缓存不是玩家安装目录，内置 ID 仍受保护。
- XML 内只允许相对于主题根的规范化路径。拒绝绝对路径、`..`、符号链接越界、URL 和大小写碰撞；包不能引用另一个包的私有文件。
- 建议内置与玩家主题使用同一清单模型，但分开资源加载适配器；`IBoardThemeCatalog` 消费解析后的不可变描述，不直接解析 XML。

## 3. 清单结构建议

```xml
<?xml version="1.0" encoding="utf-8"?>
<theme schemaVersion="1" id="spring" displayName="春日" inherits="classic">
  <supports classic="true" threeKingdoms="true" />
  <palette boardFill="#E8D6A8" boardEdge="#563D28" gridInk="#493522"
           pieceFill="#FFF4D2" neutralPieceFill="#C8C1B1"
           classicRed="#B9342D" classicBlack="#283238"
           wei="#367CC6" wu="#B88B12" shu="#358450"
           legalMove="#6AAE58" selection="#E34B45" moveFrom="#EFE5C6" moveTo="#FFF3A0" />
  <board mode="classic" texture="board/classic.png" shader="shaders/board.gdshader" />
  <board mode="threeKingdoms" texture="board/three.png" shader="shaders/board.gdshader" />
  <pieces atlas="pieces/pieces.png" layout="semantic-v1" shader="shaders/piece.gdshader" />
  <effect slot="selection" shader="shaders/selection.gdshader" durationMs="900" loop="true" />
  <effect slot="move" shader="shaders/move.gdshader" durationMs="320" />
  <effect slot="landing" shader="shaders/landing.gdshader" durationMs="420" />
</theme>
```

使用稳定语义枚举，而不是把类名、节点路径或任意字符串回调写进 XML。首版槽位为：`selection`、`legalMove`、`move`、`capture`、`landing`、`moveFrom`、`moveTo`、`check`、`victory`、`defeat`。模式可覆盖共同默认值；缺项按继承链回退，最终必须落到内置 `classic`。

`semantic-v1` atlas 固定为 7 列 × 5 行。列顺序：`general, advisor, elephant, horse, chariot, cannon, soldier`；行顺序：经典红、经典黑、魏、吴、蜀。图片宽度必须可被 7 整除，高度必须可被 5 整除。透明背景由图片 alpha 控制；没有 atlas 或装载失败时回退现有程序绘制棋子，中立障碍继续程序绘制。

棋子图片建议采用“语义 atlas + 显式布局版本”，避免按文件名猜棋种。若需要逐棋子文件，可在后续 schema 版本新增 `<piece kind="chariot" side="wei" .../>`，不要在 v1 同时支持两套含糊优先级。

## 4. 解析、验证与错误语义

实现使用 .NET 自带 `System.Xml.Linq` + 安全配置的 `XmlReader`，没有自写 XML parser，也没有为已有能力引入第三方依赖。读取时禁止 DTD 和外部实体，限制文件字节数、元素深度、元素/属性数量、文本长度与主题数量；颜色、枚举、有限数值、时长、纹理尺寸及路径逐项验证。schema v1 当前采用严格词汇/语义验证而非单独 XSD 文件。

建议调用链：

```text
ThemePackageScanner
  → 安全枚举候选目录
ThemeManifestReader（XmlReader + XSD/语义校验）
  → ThemeManifestDto / ThemeDiagnostic[]
ThemePackageCompiler
  → 解析继承、语义槽、资源描述
  → ImmutableThemeDefinition
ThemeCatalogAdapter
  → IBoardThemeCatalog / 设置列表 / 两种棋盘与反馈层
```

- `ThemeManifestReader` 只解析与报告，不加载 Godot 资源。
- `ThemePackageCompiler` 检查 ID 唯一、继承无环、必需槽齐全和兼容模式，不创建节点或材质。
- Godot 资源加载器只在主线程按需创建 Texture/Shader/Material；主题定义不拥有这些资源。
- 单包失败隔离为诊断并跳过该包；当前正在使用的主题加载失败时回退 `classic`，保留可见错误，不让半加载材质留在棋盘。
- 主题切换事件顺序建议为：解析完成 → 资源预检 → 原子替换只读目录 → 设置刷新 → 视图应用 → 旧的视图私有材质解绑并幂等释放。

## 5. shader 与资源安全/所有权

- 自定义 `.gdshader` 视为玩家主动安装的本地可信内容，但仍只交给 Godot shader 编译器；不允许脚本、include 越包、屏幕抓取或外部 I/O。
- 第一版只允许在全局设置页点击“重新扫描主题”；对局设置不提供重扫，对局中不热重载。这样动画中的材质不会被异步替换，且没有文件监听线程触碰 Godot UI。
- 每张纹理建议限制到 4096×4096、每包解压后 64 MiB、清单 256 KiB、shader 单文件 256 KiB；确切阈值在实现前通过桌面与 Android 内存预算确认。
- 扫描/解析可在后台线程运行，只产生普通不可变 DTO；Godot `ResourceLoader`、材质创建、设置控件与 `QueueRedraw` 必须回到主线程。
- Catalog/manifest 不需要释放；视图拥有自己的 ShaderMaterial。共享 Texture2D 若走 Godot 缓存由引擎持有；运行时 `user://` ImageTexture 需要主题资源租约计数，最后一个消费者释放。

## 6. 兼容策略

- `schemaVersion` 是主题清单版本；未知主版本拒绝，新增可选字段只在同一主版本内向前兼容。
- `id` 是持久化稳定键，显示名可变。当前 `BoardSkinKind` 配置需迁移为字符串 `theme_id`，首次读取旧数值时映射 `0 → classic`、`1 → spring` 并继续保留旧键回退一个发布周期。
- 主题不进入联机协议。双方可以使用不同主题；任何主题字段都不得参与局面哈希。
- 内置 `classic` 不可被玩家包覆盖；玩家同 ID 包报冲突并禁用，避免静默替换安全回退。

## 7. 测试计划

- 纯 Core/普通 .NET：合法 XML、DTD/实体拒绝、过深/过大、未知字段策略、路径穿越、重复 ID、继承环、缺少槽、非法颜色/NaN/枚举、旧设置迁移、第二目录实际注入。
- Godot smoke：经典/三国分别加载图片与 shader、全部语义槽、缺资源/编译失败回退、连续切换释放、关闭动态效果、旋转/缩放/命中不变。
- 视觉验收：至少经典/春日/一个外部示例包，在 Windows 截图比较棋盘、三方视角、棋子、选中、移动、落地、吃子和终局；Android 只在真实设备验收后标记完成。
- 性能/资源：重复扫描无句柄增长，连续切换无材质泄漏，大图/超限包被拒绝，后台解析取消后不触碰已释放 UI。

## 8. shader 能力与统一参数

Godot `canvas_item` shader 可以用 `TIME`、`UV`、噪声/遮罩、颜色混合和纹理采样实现流动、呼吸、闪烁、溶解、光环、涟漪等动态材质。它不能也不应该自己读取主题文件、加载图片、判断棋规或发现“刚刚落子”；C# 展示适配器负责安全加载资源，并把已知语义槽应用到对应节点。

- 棋盘 shader 运行在棋盘底图节点；若 XML 同时声明纹理，纹理通过 `uniform sampler2D theme_texture` 传入。只声明纹理时使用内置直通 shader。
- 棋子 shader 运行在 semantic atlas 图层，同样可声明 `theme_texture`；shader 应对未设置的可选 uniform 保持可用。
- 反馈/终局 shader 可声明 `uniform vec4 tint` 和 `uniform bool animated`。`selection`、`moveFrom`、`landing/moveTo` 由共享 `BoardFeedbackLayer` 消费；`capture`、`check`、`victory`、`defeat` 由经典模式 `CueEffectLayer` 消费。
- `move`/`capture` 的 `durationMs` 会覆盖两种棋盘的位移动画时长；其余未声明时保持内置时长。主题关闭动态特效后仍保留静态可读反馈。

shader 编译发生在 Godot 渲染器中。当前 API 无法同步取得所有驱动编译错误，因此文件/声明/尺寸先预检，实际编译失败由 Godot 日志报告；资源加载异常会隔离并回退。

## 9. 已确认选择与当前限制

1. 已采用“发布内置 `assets/themes` + 玩家可写 `user://themes`”双目录。
2. 已采用“任意 Godot canvas_item shader，但禁止脚本、原生代码、URL、进程命令与越包 include”的边界。工坊签名/审核尚未实现。
3. 已采用手动重扫/切换；没有后台 watcher，也没有对局中热重载。
4. 已采用 semantic-v1 atlas；逐棋子独立图片留给 schema v2。

已实现：安全解析、包隔离、继承与环检测、内置 ID 防覆盖、字符串设置迁移、两种棋盘底图/atlas、共享选中与落点反馈、经典终局语义、两种棋盘移动时长、私有资源租约和回退。尚未验收：Android 真机内存/驱动、主题工坊与签名；三国模式目前没有独立的全屏终局 `CueEffectLayer`，但棋盘/棋子/选中/落点/位移动画主题均已接入。

确认后再实现，不把本讨论稿中的类型名描述成当前已有接口。
