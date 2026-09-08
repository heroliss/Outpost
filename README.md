# Outpost

Outpost 是 SSFramework 的独立教程游戏，用来验证真实游戏如何消费框架能力。它拥有自己的场景、模拟、资源、配置、网络样例和测试，不向 Framework 回写游戏逻辑。

## 依赖边界

- Unity：6000.3.22f1
- Framework：`com.liss.ssframework`
- Framework 以 Git submodule 固定在 `Packages/com.liss.ssframework/`
- Outpost 的纯逻辑模拟位于 `Assets/Game/Outpost/Sim`，不依赖 Unity 或 Framework

Outpost 不依赖 FrameworkTutorial 或 NomadWorkshop。升级 Framework 时必须提交新的 submodule 指针，并同步 `docs/framework-compatibility.md`。

## 分支

`main` 保持可运行稳定版本，`develop` 用于集成，功能使用短期 `feature/*` 分支。发布使用仓库内的 `vX.Y.Z` Tag。

## 打开与验证

使用 Unity 6000.3.22f1 打开本仓库根目录，并先执行：

```text
git submodule update --init --recursive
```

测试范围、教程章节和项目约束见 `Assets/Game/Outpost/Documentation~` 与 `docs/`。