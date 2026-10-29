# Windows 离线分发

`Game.Main.Distribution.Editor.OutpostWindowsDistribution.Build(absoluteEmptyDirectory)` 是 Outpost 的 Windows x64 分发配方。在已打开的 Unity `6000.6.0f1` Editor、Windows x64 平台、Edit 模式且全部场景已保存时调用。输出必须是 Assets 之外的绝对空目录；不会覆盖旧包。

## 配方做了什么

- 用工程现有资源构建 Profile 的内存副本构建 `DefaultPackage` 和 `OutpostExpansionPackage`，两包都完整拷入 `StreamingAssets/yoo`，并核对本次版本文件与清单。
- 在资源包构建前临时将 Graphics Settings 的 Instancing Variants 设为 `Keep All`，覆盖 YooAsset/SBP 与后续 Player 两个构建阶段；`build-receipt.json` 记录 `instancingVariants: "KeepAll"`，结束时恢复原设置。
- 复制首场景作为暂存场景，只在副本中将资源运行方式设为 `Offline` 并清空 CDN 列表。原教学场景及全局资源 Profile 保持原设置。
- 临时关闭尚未准备好的 HybridCLR 构建钩子，使用 Unity 标准 IL2CPP Windows x64 编译、非 Development Player，并从 Outpost 场景直接启动。
- Player 默认以 1280×720 可调整大小窗口运行；正式包不启用开发用本地排行榜服务。增援电台音频已随包提供，设置中的安装入口仍可用。
- 无论构建成功或失败，分别恢复 HybridCLR、脚本后端、Player 设置与场景，并删除自己创建的暂存场景。输出目录的 `build-receipt.json` 记录资源版本、结果、错误和恢复状态。

需要 Unity 的 Windows IL2CPP 模块、受支持的 Visual Studio C++ 工具链及 Windows SDK。构建操作应通过 Unity Editor 或 Unity MCP 执行；此配方没有另起一个抢占项目的 Editor。

## 从源码冷启动恢复依赖

克隆后先运行 `git submodule update --init --recursive`，再用工程指定的 Unity 版本打开。`Packages/manifest.json` 的 OpenUPM scoped registry 包含 `org.nuget` scope，Unity Package Manager 由此解析 SSFramework 已声明的 `org.nuget.r3`、ObservableCollections、Google.Protobuf 等依赖；不要依赖某台机器残留的 `Library/PackageCache`。

旧 NuGetForUnity Editor 插件和已安装 DLL 保存在 `Tools/LegacyNuGet~/EditorPlugin` 与 `Tools/LegacyNuGet~/InstalledPackages`，原文件及 `.meta` 用于恢复和核对。该目录位于 Assets/Packages 之外，Unity 不会导入这些旧程序集。`Packages/nuget-packages/packages.config` 仍保留原配置，作为历史构建输入，不再驱动活动恢复。不要把归档 DLL 与正式 `org.nuget` 包同时放回导入范围，以免同名程序集重复。此调整使现有依赖声明可以从冷缓存解析，没有修改 SSFramework 源码或子模块版本。

## 同一个正式二进制的显式验收

配方仅对本次 Player 编译增加 `OUTPOST_STANDALONE_VALIDATION`。正常双击启动不创建验收对象，不执行自动演示。只有明确传入 `--validate-outpost` 时才启动验收：

```powershell
.\Outpost.exe --validate-outpost exercise --validate-storage C:\Temp\OutpostValidation\storage --validate-report C:\Temp\OutpostValidation\exercise.json -logFile C:\Temp\OutpostValidation\exercise.log
.\Outpost.exe --validate-outpost reload --validate-storage C:\Temp\OutpostValidation\storage --validate-report C:\Temp\OutpostValidation\reload.json -logFile C:\Temp\OutpostValidation\reload.log
```

`exercise` 通过真实 UI 回调和业务命令完成标题、设置、电台启用、战斗、击杀、波间强化、撤离结算、返回标题。`reload` 从同一独立存档目录重新启动，检查音量、语言、HUD 偏好、电台与历史战绩。验收检查实际 FileStorageProvider 路径和 `.sav` 数据，PNG 由运行中的 Player 原生截图产生。报告终态为 `passed` 或 `failed`，并分别以退出码 0 或 1 结束。

`exercise` 可额外传入 `--validate-backend Ecs` 或 `--validate-backend Reference`，在开始战斗前通过真实业务命令选择 DOTS/ECS 或 OOP 参考后端。两个后端应各用独立验收存档与报告目录运行，同一后端的 `reload` 复用其 `exercise` 存档。未指定时沿用游戏当前设置，普通启动不受验收参数影响。

该入口验证的是程序集中的真实运行链和按钮回调，不替代键鼠实机体验评估。发布前还须对完全不带验收参数的同一 exe 进行普通启动、画面与日志检查。

## 分发内容

保留 `Outpost.exe`、`Outpost_Data`、`UnityPlayer.dll`、`GameAssembly.dll` 与构建所需插件/运行文件及许可说明，整体解压后运行，不单独分发 exe。Unity 生成的 `BackUpThisFolder_ButDontShipItWithYourGame`、Burst 调试资料和符号文件不进入玩家 ZIP；原始构建输出另外保留供排错。每个交付 ZIP 应附源版本、资源版本和 SHA-256 清单。

`Assets/ShowcaseSamples/UIBinding` 是展示 SSFramework 节点绑定与生成器的 Editor-only 自制样例，没有进入游戏资源收集或正式 Player。

## 2026-10-24 实际验证

Unity 6000.6.0f1、Windows x64、标准 IL2CPP、非 Development 构建成功。默认资源包与电台扩展包均核对本次清单、完整 bundle 文件和大小；扩展电台 bundle 为 2,465,929 字节。

正式 exe 的 `exercise` 与 `reload` 验收覆盖标题 → 设置和电台启用 → Ecs 战斗产生击杀 → 波间三选一 → 下一波 → 撤离结算 → 返回标题；战绩为 1 局、30 分、第 2 波。重启检查同一 callsign、战绩、中文、0.65 主音量、热力图和电台设置，独立存档文件与内存读取结果一致。

同一 exe 不带任何 `--validate-*` 参数另行启动，真实窗口截图确认普通标题页和中文正常，未自动开局；随后发送真实窗口 `WM_CLOSE`，独立 Windows 观察器确认退出码 0，日志包含正常关闭清理且未出现托管异常或游戏 Error。原教学场景无脏改动，HybridCLR 与 Player 设置已恢复。

10 月 24 日构建有 565 条警告，主要是已有第三方 Sentis shader 的整数取模性能提示，以及新版 Unity 对旧编译指令/API 的兼容提示；未在消费者工程修改 Framework 或第三方源码。该轮验证覆盖本机 Windows 与上述流程，不代表其他设备、平台或所有功能均已验收。

该轮战斗验收仅等待模拟击杀数大于零，视觉断言和人工审阅主要覆盖炮塔工事，没有断言敌人及弹丸实例化绘制产生像素。因此旧报告的 `passed` 不能作为敌人、子弹显示正常的证明；这项漏查在 10 月 30 日的回归验收中补齐。

发布配方的临时 Profile 使用 `HideAndDontSave`：SBP 过程中会卸载未持有的非资产对象，若不保持副本到整批结束，后续包会因副本失效而回退默认首包策略。逐包实体文件校验会在 Player 构建前拒绝不完整内置包。

## 正式 Player 的画面完整性

炮塔工事使用场景显式持有的 `EmplacementUnlit.mat` 模板，通过资源依赖一并包含所用 URP Unlit shader 和材质配置；运行时只克隆模板，继续自行释放生成材质。避免依赖资源包场景中的 `Shader.Find` 全局名字查找，也无需全局保留全部 URP shader。缺少或不支持模板会明确报错，不能静默退回原始方块炮塔。

10 月 24 日 Player 已实际检查 Foundation、Core、6 个 ArmorBrace、7 顶点底座网格、两个实际材质的 shader 支持情况及真实相机可见性，并用原生截图核对六边形工事与发光核心。同一轮验收中按名字查找仍返回 missing，但显式引用的材质 shader 可用且正确渲染，因此不把炮塔问题的修复原因简化为已证实的全局 shader 剥离。

## 2026-10-30 敌人与弹丸实例化渲染修复

`SwarmRenderer` 通过运行时创建的材质和 `Graphics.DrawMeshInstanced` 绘制敌人、弹丸、残骸及热力图，没有逐敌人的 `MeshRenderer`。即使模拟继续产生击杀，构建阶段若自动剥离所需的 instancing shader variant，仍可能出现战斗对象不显示。发布配方现在在资源包和 Player 构建前保留 instancing variants，再恢复原工程设置；仅修改 Player 构建阶段不足以保证先前生成的 bundle 包含该变体。

新增的显式 Player probe 直接读取真实 `SwarmRenderer` 的运行材质与当帧可见样本，不使用反射。它检查 `Outpost/SwarmUnlit`、`shader.isSupported`、材质 `enableInstancing` 和设备 `supportsInstancing`，随后对敌人、弹丸分别使用实际网格、变换、颜色和游戏相机矩阵进行隔离实例化绘制。每次同时绘制原样本与偏移后的独立对照色实例，要求两种颜色各产生至少 2 个匹配像素；空画面、洋红错误材质或仅返回白色的 fallback 无法通过。报告保存实际模拟数量、两色像素计数以及 `03-enemy-instancing-proof.png`、`03-projectile-instancing-proof.png`。

隔离图证明该运行材质与实例化变体可以绘制真实样本；完整战场仍须审阅普通启动和实际战斗截图，不能只用隔离图替代玩家画面检查。probe 代码受 `OUTPOST_STANDALONE_VALIDATION && !UNITY_EDITOR` 限制，只有传入 `--validate-outpost` 的 Player 才调用，普通运行不创建验收对象。

分发脚本随源码保存在 `Tools/Distribution/`：`summarize_acceptance.py` 汇总实际 Player 报告，`prepare_delivery.py` 保留原有流程、炮塔视觉、二进制一致性、许可与 ZIP 校验门槛，并新增 `KeepAll` 构建回执和上述 Swarm 状态、敌人/弹丸两色像素门槛；`verify_distribution.py` 核对独立解压目录的逐文件 SHA-256、额外文件和可选 ZIP 完整性。

在源码仓库根目录执行以下命令，`$run` 指向本次构建和已完成验收所在目录。先审阅真实战场和两张像素证明 PNG，确认后才传入 `--visually-reviewed`；省略该参数会生成待审阅摘要，打包脚本拒绝使用。若保留旧 bundle 的失败对照，可给汇总命令增加 `--baseline <旧失败报告绝对路径>`。

```powershell
$run = "C:\Temp\OutpostRelease"
python Tools/Distribution/summarize_acceptance.py --build "$run\build" --exercise "$run\ecs\exercise.json" --reload "$run\ecs\reload.json" --reference "$run\reference\exercise.json" --output "$run\acceptance-summary.json" --visually-reviewed
python Tools/Distribution/prepare_delivery.py --build "$run\build" --source (Get-Location).Path --source-sha (git rev-parse HEAD) --acceptance "$run\acceptance-summary.json" --output "$run\delivery"
Expand-Archive -LiteralPath "$run\delivery\Outpost-Windows-x64.zip" -DestinationPath "$run\extracted"
python Tools/Distribution/verify_distribution.py --root "$run\extracted\Outpost-Windows-x64" --archive "$run\delivery\Outpost-Windows-x64.zip" --output "$run\distribution-verification.json"
```

上述校验不启动 Unity，也不替代独立解压后运行同一 Player 的复验。

本次 `build-02` 已用 Unity 6000.6、Windows x64、IL2CPP、非 Development 配方构建成功：`Succeeded`、0 错误、566 条警告、`originalSettingsRestored: true`，资源版本为 `20261029-182245`。资源包和 Player 两阶段均使用 `KeepAll`；完成后 Instancing Stripping 恢复原值 `0`，暂存场景目录已不存在。相关 PlayMode 测试为 **17/17 通过**。

正式 Player 的 Ecs 后端使用默认 DX12，`exercise` 与同存档 `reload` 均为 `passed`、退出码 0；敌人实际/对照颜色像素为 **218/217**，弹丸为 **110/108**。Reference 后端强制 DX11，`exercise` 为 `passed`、退出码 0；敌人为 **209/208**，弹丸为 **109/109**。这两组检查分别覆盖后端及图形 API，未声称覆盖所有组合。

A/B 对照保留同一套新 Player 原生文件，SHA-256 一致，仅替换 25 个 `StreamingAssets` 旧资源文件。旧资源场景中 shader 仍受支持、材质 instancing 仍为 `true`，但敌人实际/对照像素为 **0/0**，验收失败并以退出码 1 结束；换用本次重新构建资源后上述像素检查通过。这组对照将不可见问题定位到旧资源构建结果，不能仅凭 `shader.isSupported` 或模拟击杀数判定渲染正常。

同一 Player 完全不带 `--validate-*` 参数启动后，已用真实鼠标开局、选择强化并进入第二波；`03-projectiles-and-wrecks.jpg` 可清楚辨认 2 枚白色弹丸和 6 处残骸。随后 Alt+F4 正常退出，退出码 0；检查用户存档的 4 个文件，SHA-256 均未变化。以上限于一台 Windows x64 / RTX 4060 主机与所述流程，不代表长期运行或全部波次均已验证。

最终交付包的 `verification/` 保存构建与运行验收摘要；独立解压及公开下载的文件哈希和运行回执另行保留，用于核对实际发布产物。
