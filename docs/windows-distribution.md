# Windows 离线分发

`Game.Main.Distribution.Editor.OutpostWindowsDistribution.Build(absoluteEmptyDirectory)` 是 Outpost 的 Windows x64 分发配方。在已打开的 Unity `6000.6.0f1` Editor、Windows x64 平台、Edit 模式且全部场景已保存时调用。输出必须是 Assets 之外的绝对空目录；不会覆盖旧包。

## 配方做了什么

- 用工程现有资源构建 Profile 的内存副本构建 `DefaultPackage` 和 `OutpostExpansionPackage`，两包都完整拷入 `StreamingAssets/yoo`，并核对本次版本文件与清单。
- 复制首场景作为暂存场景，只在副本中将资源运行方式设为 `Offline` 并清空 CDN 列表。原教学场景及全局资源 Profile 保持原设置。
- 临时关闭尚未准备好的 HybridCLR 构建钩子，使用 Unity 标准 IL2CPP Windows x64 编译、非 Development Player，并从 Outpost 场景直接启动。
- Player 默认以 1280×720 可调整大小窗口运行；正式包不启用开发用本地排行榜服务。增援电台音频已随包提供，设置中的安装入口仍可用。
- 无论构建成功或失败，分别恢复 HybridCLR、脚本后端、Player 设置与场景，并删除自己创建的暂存场景。输出目录的 `build-receipt.json` 记录资源版本、结果、错误和恢复状态。

需要 Unity 的 Windows IL2CPP 模块、受支持的 Visual Studio C++ 工具链及 Windows SDK。构建操作应通过 Unity Editor 或 Unity MCP 执行；此配方没有另起一个抢占项目的 Editor。

## 同一个正式二进制的显式验收

配方仅对本次 Player 编译增加 `OUTPOST_STANDALONE_VALIDATION`。正常双击启动不创建验收对象，不执行自动演示。只有明确传入 `--validate-outpost` 时才启动验收：

```powershell
.\Outpost.exe --validate-outpost exercise --validate-storage C:\Temp\OutpostValidation\storage --validate-report C:\Temp\OutpostValidation\exercise.json -logFile C:\Temp\OutpostValidation\exercise.log
.\Outpost.exe --validate-outpost reload --validate-storage C:\Temp\OutpostValidation\storage --validate-report C:\Temp\OutpostValidation\reload.json -logFile C:\Temp\OutpostValidation\reload.log
```

`exercise` 通过真实 UI 回调和业务命令完成标题、设置、电台启用、战斗、击杀、波间强化、撤离结算、返回标题。`reload` 从同一独立存档目录重新启动，检查音量、语言、HUD 偏好、电台与历史战绩。验收检查实际 FileStorageProvider 路径和 `.sav` 数据，PNG 由运行中的 Player 原生截图产生。报告终态为 `passed` 或 `failed`，并分别以退出码 0 或 1 结束。

该入口验证的是程序集中的真实运行链和按钮回调，不替代键鼠实机体验评估。发布前还须对完全不带验收参数的同一 exe 进行普通启动、画面与日志检查。

## 分发内容

保留 `Outpost.exe`、`Outpost_Data`、`UnityPlayer.dll`、`GameAssembly.dll` 与构建所需插件/运行文件及许可说明，整体解压后运行，不单独分发 exe。Unity 生成的 `BackUpThisFolder_ButDontShipItWithYourGame`、Burst 调试资料和符号文件不进入玩家 ZIP；原始构建输出另外保留供排错。每个交付 ZIP 应附源版本、资源版本和 SHA-256 清单。

`Assets/ShowcaseSamples/UIBinding` 是展示 SSFramework 节点绑定与生成器的 Editor-only 自制样例，没有进入游戏资源收集或正式 Player。

## 2026-10-24 实际验证

Unity 6000.6.0f1、Windows x64、标准 IL2CPP、非 Development 构建成功。默认资源包与电台扩展包均核对本次清单、完整 bundle 文件和大小；扩展电台 bundle 为 2,465,929 字节。

同一正式 exe 的 `exercise` 与 `reload` 报告均为 `passed`，两轮 Error/Exception/Assert 计数均为 0。实际走通标题 → 设置和电台启用 → Ecs 战斗产生击杀 → 波间三选一 → 下一波 → 撤离结算 → 返回标题；战绩为 1 局、30 分、第 2 波。重启保持同一 callsign、战绩、中文、0.65 主音量、热力图和电台设置。独立存档文件与内存读取结果一致。

同一 exe 不带任何 `--validate-*` 参数另行启动，真实窗口截图确认普通标题页和中文正常，未自动开局；随后通过窗口正常关闭，日志未出现托管异常或游戏 Error。原教学场景无脏改动，HybridCLR 与 Player 设置已恢复。

构建仍有 560 条警告，主要是已有第三方 Sentis shader 的整数取模性能提示，以及新版 Unity 对旧编译指令/API 的兼容提示；未在消费者工程修改 Framework 或第三方源码。此轮验证覆盖本机 Windows 与上述流程，不代表其他设备、平台或所有功能均已验收。

发布配方的临时 Profile 使用 `HideAndDontSave`：SBP 过程中会卸载未持有的非资产对象，若不保持副本到整批结束，后续包会因副本失效而回退默认首包策略。逐包实体文件校验会在 Player 构建前拒绝不完整内置包。
