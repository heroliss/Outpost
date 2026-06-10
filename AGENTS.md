# AGENTS.md — 项目协作指南

项目级 AI 协作入口，所有遵循 AGENTS.md 约定的工具通用（Claude Code / Codex / Cursor 等）。

- **框架使用规则**：`Assets/Game/AGENTS.md`（目录就近自动加载，写业务代码时进上下文）
- **框架内部编码规则**：`Assets/Game/Framework/AGENTS.md`（目录就近自动加载，仅改框架源码时进上下文）
- **本套方案设计原理 / 跨工具差异 / 用户级配置**：`docs/ai-collaboration-guide.md`
- **Claude Code 专属配置**：`.claude/`（settings / skills / agents / hooks）

> 本文件**只放需要 AI 主动观察、无关键词触发的常驻规则**。场景化规则（编码、截图、改场景等）按触发模式拆到嵌套 AGENTS.md / Skill / Hook。

---

## 协作规则（常驻）

### 1. 第三方库不直接修改
`Library/PackageCache/` 下的 YooAsset / UniTask / R3 等先尝试包装、扩展或自定义接口绕过。确需修改先沟通并加注释。
**Why:** 包更新会覆盖修改；这是动手前的判断，skill 触发不可靠，必须常驻。

### 2. 不直接编辑 `.unity` / `.prefab` YAML 文件
场景与 Prefab 必须通过 Unity MCP 的 `unity_*` 工具修改（`unity_gameobject_*` / `unity_component_*` / `unity_scene_*` / `unity_asset_create_prefab` 等），不要手改 YAML。
**Why:** 直接改 YAML 会导致 GUID/fileID 断链、Editor 不会自动刷新（需手动 Reimport）、版本控制冲突极难合并。项目侧调用要点见 `docs/unity-mcp-tips.md`。
**How to apply:** 任何 `Assets/**/*.unity` 或 `*.prefab` 的改动一律走 MCP；只有读取/搜索 YAML 内容（Grep 定位）才能用文件工具。
⚠ **动手前先确认编辑器不在 Play 模式**（查 editor state 或 `EditorApplication.isPlayingOrWillChangePlaymode`）——Play 下的场景修改停止运行即全部回滚（工具返回 success 也是白做），且节点路径解析可能异常；在 Play 就先停掉再改，改完 `unity_scene_save` 落盘。

### 3. 不确定时先沟通
以下情况停下来问用户，不擅自推进：
- 需求歧义、技术选型未定
- 不可逆操作（删文件、推代码、改共享配置）
- 工具反复失败
- 发现现有设计的潜在冲突

### 4. 设置变更同步记录
改 `.claude/settings.json` 或 `~/.claude/settings.json` 时，把变更追加到同目录的 `SETTINGS_LOG.md`。
**Why:** settings 改动直接影响 AI 行为（权限、hook、env），无记录则后续问题难以归因到具体变更。
**How to apply:** Edit/Write 这两个文件之后立刻 append 一行变更摘要到 `SETTINGS_LOG.md`，含日期 + 改了什么 + 为什么。

### 5. XML doc 泛型尖括号处理（按场景三分）
C# XML doc 按真 XML 规范解析，正文裸 `<T>` 会被当成未知元素 → 破坏整段 summary 结构 → IDE 鼠标悬浮空白（可能伴随 CS1570 警告）。处理分三种场景：

| 场景 | 写法 | 示例 |
|---|---|---|
| **cref 属性内** | `{T}` | `<see cref="Subscribe{T}"/>` |
| **正文短引用**（一两处尖括号） | `&lt;T&gt;` | `调用 <c>GetSystem&lt;T&gt;()</c>` |
| **多行 `<code>` 代码示例** | `<code><![CDATA[ ... ]]></code>` | 见下 |

`<code>` 多行块用 CDATA 把多处尖括号一次性括起，比每个都转义干净：

```csharp
/// <code><![CDATA[
/// Subs.Subscribe<T>(...);
/// var list = new List<int>();
/// ]]></code>
```

**Why 分三套：** cref 是 cref 专属语法（`{}` 代表 `<>`），跟正文是两套规则；正文短引用 `&lt;T&gt;` 比 `<![CDATA[<T>]]>` 短一半（8 vs 15 字符）；`<code>` 多行块里尖括号密集，CDATA 一次包整段反而最干净，且 `<code>` 内 `<see>` 本来就不常用，CDATA 让出可读性是赚的。

**How to apply:**
- 写注释时按场景选写法
- 改既有文件碰到正文裸 `<T>` 改 `&lt;T&gt;`；碰到 `<code>` 多行块用了 `&lt;` 转义的，改成 CDATA
- ⚠️ CDATA 内 `<see cref="..."/>` 会变纯文本失去跳转，所以**只在 `<code>` 块用 CDATA**，summary 主体仍用 `&lt;`

### 6. 注释写给维护者看
注释不是越少越好，也不是逐行翻译代码。公共类型、公共/受保护 API、关键生命周期入口、复杂逻辑块、第三方库适配边界、异步/取消/释放/缓存/时序等容易误解的位置，都应补充通俗易懂的 XML doc 或内联注释。

**How to apply:**
- 类和接口说明“它在架构里负责什么、谁应该用、哪些细节被刻意隐藏”
- 方法说明“调用语义、生命周期归属、取消/异常/释放约定”，不要只复述方法名
- 复杂逻辑块说明“为什么这样做、规避什么坑、依赖第三方库什么行为”
- 删除“这是 getter / 设置字段 / 循环列表”这类无信息注释
- 注释只讲“当前代码是什么 / 为什么这样”，不写改动缘由、也不提已删除的替代方案（如“免得再点被遮住的按钮”）——历史 / 对比信息属于 commit message，不属于源码
- 改第三方库集成（YooAsset / UniTask / R3 等）时，优先记录框架侧约束和踩坑边界

---

## 协作姿态

把 AI 当**有判断力的同事**，不是无脑执行工具：

- 用户判断可能有偏差、需求不清、有更优方案时 → **主动指出 + 给出理由**
- 不盲从、不在错误方向上沉默执行
- 最终决策权交回用户，但要让用户**知情** —— "我观察到 X，建议 Y，理由 Z，你觉得呢？"

---

## 执行模式选择

默认**单 agent 直连执行**——以下场景才显式切换工具。判断准则：30 秒说不清方向就考虑 Plan Mode；任务符合下表才考虑 subagent；其余一律主 agent 干。

### Plan Mode（Shift+Tab 进入）
适合：不可逆操作（删文件 / 推代码 / 改共享配置 / 跨多文件结构性大改）、不熟悉代码库需先探查、需求模糊需多方案对齐。
**Why:** harness 层在 Plan 阶段门控掉写操作，强制"先看后动"，比口头约定可靠；同一上下文连续，零信息损失。
**主动提议（关键）**：识别到上述场景时，**先提议**："这次涉及 X，建议 Shift+Tab 进 Plan Mode 让你审完方案再动手，要进吗？"——用户拒绝就直接干，但不能默不作声。
**不要提议**：单文件局部修改、用户已给明确指令的小任务、迭代延续中——否则变成"每件事都问"的噪音。
**How to apply:** 适合场景一律先提议；用户进了之后配合调研、出方案、等 `ExitPlanMode` 批准。

### Subagent（`Agent` 工具）
默认**不开**——冷启动 + 重 ingest 上下文的代价通常超过收益。仅以下场景开：

| 场景 | agent type |
|---|---|
| 跨多目录大范围只读搜索（关键词分散、需多轮探查） | `Explore` |
| 范围模糊的大初始任务做架构设计 | `Plan` |
| 真正并行的独立子任务（同时调研 N 个方案） | 同消息多个 `Agent` 并行 |

**禁止**：把交互式开发拆成 plan → impl × N → review 流水线——subagent 不能反向问主 agent，碰到歧义只能猜，对"边写边调"的协作方式是负优化。

**主动提议（关键）**：AI 识别到任务符合上表时，**主动告诉用户**："这个任务跨 X 个目录 / 是 Y 类大设计 / 有 N 个独立分支，开 `<agent_type>` 更合适，理由是 Z——开吗？"——让用户拍板，不擅自 spawn。**沉默错过机会和擅自 spawn 一样是错**。

**不要提议**：单文件改动、targeted 搜索（Grep 一两条就够）、迭代延续中的子任务——不在三场景表内的都不提，避免噪音。

**How to apply:** 任务符合上表先**提议**；用户同意才 spawn；不在表内主 agent 直接做。

### 客观评审：评审 subagent（`/ultrareview` 替代方案）
本环境无 `/ultrareview`，feature 锁定可用 `general-purpose` subagent 做轻量评审。能力上限是"junior dev 二审"级别，不替代真正多角度评审。

**触发**（满足任一）：
- 非琐碎 feature 完成（多文件改动 / 改公共 API / 影响架构走向）
- 用户说"准备 PR / 准备合并 / 这块写完了"

**Why:** 单人开发缺少另一双眼睛；评审 subagent 独立上下文，对刚做的决策不带情感投入，能发现主 agent 习惯性忽略的疑点。

**How to apply:**
- 满足触发条件时主动提议："这次改动涉及 X，建议开评审 subagent 复查找疑漏，开吗？"
- spawn 时**只给代码 + 关注点列表，不给设计理由**——否则 reviewer 被偏见污染，等于白开
- 关注点要具体：边界条件 / 错误处理 / 命名一致性 / 测试覆盖缺口 / 是否符合 AGENTS.md 既有约定（指路径，不指自然语言总结）
- 用户审完 reviewer 输出，决定哪些采纳

**不要提议**：单文件修复、小 bug、迭代讨论中、规则/文档微调

---

## 持续改进 / Self-Evolution

规则和 skill 是协作的**底线，不是上限**。主动运用自身判断，不必等到触发信号才开口：发现更优实现路径、潜在风险、或协作体系本身可以改善的地方，随时直接提出。

识别到以下信号时，**告诉用户**（不擅自落地），按"观察到什么 / 提议什么 / 为什么"格式提议。具体归类与落地规范见 skill `propose-rule-evolution`。

**问题信号（被动发现）：**
- 用户**连续多次纠正同一类错误**
- 规则**与代码现状脱节、自相矛盾**
- AGENTS.md **超过 200 行影响遵循度**
- 同样的**多步流程被反复手动指引**
- 某操作**必须**在特定时机发生
- 用户提到**个人偏好 / 外部资源引用 / 非显然判断依据**
- 架构、目录、协作方案有**重大调整**（需同步 `docs/ai-collaboration-guide.md`）

**优化机会（主动识别）：**
- 发现重复模式/工作流，但**无对应规则或 skill** 承接 → 建议新增
- 现有规则/skill **触发词不准、内容过时或与实际用法偏差** → 建议修改
- 规则已被代码结构或工具自然约束，**不再需要 AI 显式记忆** → 建议删除
- skill **长期未触发**且对应场景已消失 → 建议归档或删除

---

## 场景化规则去向

| 场景 | 去向 |
|---|---|
| 使用框架 API（Command / Model / System / 注入 / 事件等） | `Assets/Game/AGENTS.md`（目录就近自动加载） |
| 改框架源码（接口依赖、XML doc、注释风格等内部约定） | `Assets/Game/Framework/AGENTS.md`（目录就近自动加载） |
| Unity 场景 / GameObject / 组件 / Prefab 操作的 MCP 工具用法 | `docs/unity-mcp-tips.md`（项目侧要点）+ MCP 自带工具描述 |
| UI / Scene 截图（项目专属约定） | skill `unity-screenshot` |
| 提议规则演进的归类与落地格式 | skill `propose-rule-evolution` |
