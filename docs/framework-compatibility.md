# Framework 兼容记录

- Package：com.liss.ssframework
- 当前本地候选提交：49a2c01d2f3d5d1082b72c410e99780e5e4765b6
- Unity：6000.6.0f1
- YooAsset：3.0.6

升级 Framework 时，先在本项目完成编译、测试和必要的运行时验证，再更新此文件和 submodule 指针。

## 2026-10-23 升级候选

该候选适配 Unity 6000.6 的平台程序集审计，并由 Unity 解析当前包依赖。YooAsset 3.0.6 与 Framework 子模块候选一起进入本地验证。

新增视觉改动前已验证：

- FrameworkModuleAuditTests：EditMode **64/64 passed**，0 failed / 0 skipped，任务 `d9609df0564f`。
- 全部 EditMode：**578/578 passed**，0 failed / 0 skipped，任务 `d9e6d9d7aa3b`。
- 编辑器真实玩家路径冒烟：**1/1 passed**，覆盖标题、战斗、撤离、结算和回标题。

本轮随后增加了 UI Toolkit、战斗 HUD 与战场视觉候选，并完成视觉改动后的最终回归：

- 全部 EditMode：**578/578 passed**，0 failed / 0 skipped，任务 `b636a0a71a98`。
- 编辑器真实玩家路径 PlayMode：**1/1 passed**，0 failed / 0 skipped，任务 `f9f8ef0606cc`，覆盖标题、战斗、撤离、结算和回标题。
- 实际检查 1920×1080、1280×720 与 1600×1200（4:3）画面，完成英文设置、三选一强化面板和结算布局检查；Settings 真实语言切换 / 键盘 Close、Result 键盘 Back 通过，新运行 Console errors 0。
- 临时 Editor 取证脚本移至操作系统垃圾桶并保留外部验证备份，移除后再次编译 0 错误。

上述结果确认当前 Outpost 升级与视觉候选的已执行范围，不代表各目标平台 Player 构建均已验证。最终素材选择仍待主任务完成。

Framework 第二消费方 NomadWorkshop 已完成同一候选的模块审计：任务 `cb37753cc095` **64/64 passed**，0 failed / 0 skipped；正式场景实际 Play 确认 `IsReady=true`、居民 3、Console 0 error。这是其框架审计与场景就绪证据，不代表 Nomad 全量测试已执行。Framework 代码提交 `49a2c01d2f3d5d1082b72c410e99780e5e4765b6` 已推送，主任务已用 `git ls-remote` 核对远端 main 为 `70bba7d08121c088fb5add5ff36ccc1735eb9304`（后一提交仅补充指南）。本消费方保留已验收的 `49a2c01` 固定子模块指针，Outpost 自身升级与视觉改动随后由主任务审阅并提交 / 推送。

