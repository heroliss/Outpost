# Outpost

Outpost 是 SSFramework 的独立教程游戏和真实消费示例。它用一个可运行的小型游戏验证 Framework 在场景、模拟、资源、配置与测试中的接入方式；Outpost 的玩法和业务代码只属于本仓库，不回写 Framework。

## 仓库边界

- Unity 工程与场景、Prefab、配置和美术资产：本仓库。
- 纯逻辑模拟：`Assets/Game/Outpost/Sim`，不依赖 Unity 或 Framework。
- Framework：`Packages/com.liss.ssframework` Git submodule，固定到已验证的 commit。
- Outpost 不依赖 FrameworkTutorial 或 NomadWorkshop；跨项目只通过文档链接说明关系。

## 开始使用

需要 Unity `6000.3.22f1`。克隆后在仓库根目录执行：

```powershell
git submodule update --init --recursive
```

然后用 Unity Hub 打开根目录。教程章节、项目规则和验证入口位于 `Assets/Game/Outpost/Documentation~` 与 `docs/`。

## 分支与发布

- `main`：可运行的稳定线。
- `develop`：章节和功能集成线。
- `feature/*`：短期开发分支。
- `vX.Y.Z`：教程游戏的阶段发布标签。

Framework 的版本升级必须提交新的子模块指针，并在 `docs/framework-compatibility.md` 记录兼容性和验证结果。通用安装与升级规则见 [Framework 接入与升级说明](https://github.com/heroliss/SSFramework/blob/main/docs/consuming-framework.md)。

## 验证原则

验证范围根据改动风险选择：纯模拟优先运行纯 C# 测试；场景、Prefab 或 Framework 接入改动需要 Unity 编译、相关测试和实际运行路径。测试通过只证明契约成立，教程体验仍需在运行时检查。

## 相关仓库

- [SSFramework](https://github.com/heroliss/SSFramework)：被本项目消费的框架包。
- [FrameworkTutorial](https://github.com/heroliss/FrameworkTutorial)：框架章节教程，计划更名为 `SSFrameworkTutorial`。
- [NomadWorkshop](https://github.com/heroliss/NomadWorkshop)：独立开发中的正式游戏。
