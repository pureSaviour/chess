# Steam 好友联机

> 本文主要说明经典双人 Steam 适配与平台配置。三国三席 Lobby、模式路由、全连接消息和房主迁移见 [三国象棋](three-kingdoms.md)；端口/生命周期约束见 [抽象层使用手册](abstractions.md)。全项目关系见 [总架构](../TECHNICAL_ARCHITECTURE.md)。

## 当前状态

Windows x64 版本已经接入 Steamworks SDK 1.65，并使用 Spacewar App ID `480` 进行开发测试。当前已实现：

- SteamAPI 初始化、当前用户与 App ID 读取；
- 创建仅好友可见、最多两人的 Steam Lobby；
- Lobby 对局配置 metadata、好友 Overlay 邀请和邀请回调；
- Steam Networking Messages 可靠 P2P 对局传输；
- Lobby 成员 Steam 昵称、SteamID64 和头像读取；
- 设置页自定义 ID/头像，并通过对局通道临时交换；
- 退出等待房间或断线时离开 Lobby，并释放头像缓存和原生纹理；
- 走子、计时、聊天、悔棋与和棋复用统一的二进制协议。

本机真实 Steam 客户端已验证 API 初始化、用户/App ID 读取，以及退出后再次创建 Lobby。Overlay 接受邀请、远端默认/自定义头像和完整 P2P 对弈仍需两个 Steam 账号和两个同时在线的客户端做双端验收。

## 开发运行

1. 启动并登录 Steam 客户端。
2. Debug 构建会在进程工作目录缺少 `steam_appid.txt` 时自动生成内容 `480`；该文件已被 Git 忽略。
3. 从 Godot 编辑器运行项目，在玩法画廊进入“经典象棋”→“邀请 Steam 好友”，配置计时和先后手后创建房间。三国需进入“三国象棋”对应的 Steam 入口，不能使用双人房间配置。
4. 好友接受邀请后双方自动进入对局；若好友当时未运行游戏，Steam 会使用 `+connect_lobby <LobbyId>` 启动参数，本项目也会自动处理该参数。

可用的 Debug 自检参数：

```text
--smoke-steam-init   验证 SteamAPI、App ID 和登录用户
--smoke-steam-lobby  连续创建、退出并重新创建两个测试 Lobby
```

## 版本与文件

- `steam_api64.dll` 来自环境变量 `STEAMWORKS_SDK_PATH` 指向的官方 SDK 1.65：`sdk/redistributable_bin/win64`。
- `third_party/Steamworks.NET` 固定为与 1.65 严格匹配的上游提交，许可和构建方式见目录内 `NOTICE.md`。
- 托管绑定使用 AnyCPU PE 标记供 Godot .NET 装载，但通过编译常量固定调用 Windows x64 原生库。
- Steam 代码以 `GODOT_WINDOWS` 条件编译；Android 构建不会包含 Steamworks.NET 或 `steam_api64.dll`。
- `steam_appid.txt` 只由 Debug 构建用于本地开发；Release 不打包也不生成它，正式从 Steam 启动时由客户端提供 App ID 上下文。

## 发布前要求

App ID 480 只适用于公开的 Spacewar 测试环境。它可以验证本机集成，但不能作为本游戏正式发布的 App ID。发布或进行本游戏专属配置前，仍须完成 Steamworks/Steam Direct 入驻、取得自己的 App ID，并在 Steamworks 后台配置可执行文件、Depot、Overlay 与网络权限；随后替换开发 App ID 并重新执行双账号测试。

Spacewar 是多人共享环境，因此 Lobby 使用项目标识与协议版本 metadata，客户端会拒绝其他 App 480 程序创建的不兼容房间；P2P 数据也只接受当前双人 Lobby 中的另一位成员。
