# 玩法画廊图片

这些资源作为 `ModeGalleryScreen` 的纯装饰背景。图片不承载按钮文字、房间配置或玩法规则；交互与可访问文本始终由 Godot 原生控件提供。

- `classic-xiangqi-card.png`：经典象棋，暖木棋盘、红黑对阵、山水背景；下半部保留暗色按钮区域。
- `three-kingdoms-card.png`：三国象棋，三向六边形棋盘、蓝绿金三方与山河意象；下半部保留暗色按钮区域。

共同生成约束：竖向玩法卡片构图；不生成文字、数字、Logo、水印、人物、UI 控件或西洋棋子。Godot 以 `TextureRect.KeepAspectCovered` 裁切，背景控件忽略鼠标输入。
`jieqi-card.png`：揭棋卡片背景，暗子与翻明棋子的写实场景；由内置图像生成工具生成。提示词强调深色胡桃木棋盘、雕纹暗子、中央翻明红子、无 UI/Logo/水印。

`junqi-card.svg`：本仓库内直接创作的军旗棋盘矢量背景，使用原创几何、配色与无文字军用符号，无第三方素材；由 Godot 作为 Texture2D 导入。
