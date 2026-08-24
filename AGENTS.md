# AGENTS.md — 项目协作入口

本文件只放需要 AI **始终记住、无法靠代码结构自然约束**的规则。场景化知识按目录或 Skill 加载：

- 使用框架 API：`Assets/Game/AGENTS.md`
- 修改框架源码：`Assets/Game/Framework/AGENTS.md`
- 编写 Demo 章节：`Assets/Game/Framework/Demo/Scripts/Modules/AGENTS.md`
- 方案原理与工具差异：`docs/ai-collaboration-guide.md`
- Unity MCP 项目要点：`docs/unity-mcp-tips.md`
- Claude Code 专属配置：`.claude/`

## 不可绕过的协作边界

### 1. 第三方库不直接修改

`Library/PackageCache/` 下的 YooAsset / UniTask / R3 等先用 Adapter、扩展或自定义 Interface 隔离。确需修改时先沟通，并记录补丁原因与升级影响；包更新会覆盖直接修改。

### 2. 场景与 Prefab 只经 Unity 编辑器修改

- `Assets/**/*.unity`、`*.prefab` **不得手改 YAML**；读取、搜索可用文件工具，写入必须用 Unity MCP 的 `unity_*` 工具。
- 动手前确认 Editor 不在 Play / 即将进入 Play；在 Play 就先停止。修改后调用 `unity_scene_save` 落盘。
- 经 MCP 启动 PlayMode 测试前，必须先执行菜单 `SSFramework/诊断/AI 自动化/PlayMode 测试预检（保存脏场景）`；它保存已有路径的脏场景并拒绝未命名场景，避免原生保存弹窗阻塞 MCP 队列。完整流程见 `docs/unity-mcp-tips.md`。
- 原因：直接写 YAML 容易破坏 GUID/fileID，绕过刷新/Undo，并制造难合并冲突。

### 3. 扩权、冲突与不可逆操作先沟通

以下情况停止并说明证据、选项与建议，不替用户拍板：

- 需求存在会显著改变结果的歧义，或技术选型尚未确定；
- 删除文件、推送代码、修改共享环境/权限等难恢复操作；
- 工具连续失败，或发现现有设计与请求冲突；
- 完成目标需要超出用户已授权范围的外部协调。

用户明确授予的自主权可覆盖“是否逐项确认”，但不扩大任务边界，也不取消上述安全检查。

### 4. 设置变更必须可追溯

修改 `.claude/settings.json` 或 `~/.claude/settings.json` 后，立即在同目录 `SETTINGS_LOG.md` 追加“日期 + 改了什么 + 为什么”。其他会改变 AI 行为的项目配置也应在对应协作文档说明。

## 代码与注释

### XML doc 泛型写法

| 场景 | 写法 | 示例 |
|---|---|---|
| `cref` 属性 | `{T}` | `<see cref="Subscribe{T}"/>` |
| 正文短引用 | `&lt;T&gt;` | `<c>GetSystem&lt;T&gt;()</c>` |
| 多行 `<code>` | CDATA | `<code><![CDATA[ var x = new List<int>(); ]]></code>` |

正文裸 `<T>` 会破坏 XML；只在 `<code>` 块使用 CDATA，因为其中的 `<see>` 不再可跳转。

### 注释写给维护者看

- 公共/受保护 API、关键生命周期入口、复杂时序、异步/取消/释放/缓存、反射与第三方 Adapter 边界，应说明职责、调用语义、所有权、失败行为和“为什么”。
- 删除逐行翻译式注释；注释描述当前设计，不写改动历史或已删除方案。
- 第三方集成优先记录框架侧约束与踩坑边界。

## 协作姿态

把 AI 当有判断力的同事：发现用户判断可能有偏差、存在更高 Leverage 的实现或潜在风险时，按“观察 → 建议 → 理由”主动说明；最终决策权交给用户。先给可验证证据，不做无依据迎合。

## 执行方式选择

默认单 Agent 直接完成。只有下面场景才提议切换，并由用户决定。

### Plan Mode

适合不可逆操作、跨多目录结构性大改、不熟悉代码库的初始调研、或需要先对齐多方案的模糊需求。先提议“建议 Shift+Tab 进入 Plan Mode 审完方案再动手”；用户拒绝则在授权范围内继续。

单文件局部修改、明确的小任务、同一迭代的延续不提议，避免流程噪音。

### Subagent

仅用于：

- 跨多目录的大范围只读探索；
- 范围模糊的大设计初始建模；
- 真正独立、可并行且不会争写同一文件的子任务。

先说明 Agent 类型、边界和收益，用户同意后再启动。不要把交互式开发机械拆成 plan → impl → review 流水线。

非琐碎 feature 完成、改公共 API/架构、或准备合并时，可提议只读 reviewer。给 reviewer 代码与关注点，不灌输设计理由；关注边界条件、错误处理、命名一致性、测试缺口和本目录规则。

## 持续改进

规则和 Skill 是底线，不是上限。发现下列信号时，先按“观察 → 提议 → 为什么”告知用户，再决定是否落地：

- 同类错误被反复纠正，规则与代码脱节/冲突，或同一多步流程反复手工指导；
- 常驻 `AGENTS.md` 接近 200 行或自动加载链逼近工具上下文上限；
- 出现必须在固定时机执行的检查，适合 Hook/测试而非自然语言提醒；
- 重复工作流缺少 Skill，现有 Skill 触发不准/过时，或约束已被代码结构取代；
- 架构、目录、协作方案重大调整（同步 `docs/ai-collaboration-guide.md`）。

归类与落地格式见 Skill `propose-rule-evolution`；截图流程见 `unity-screenshot`。若当前工具没有对应项目 Skill，先读取 `.claude/skills/<name>/SKILL.md` 的同名说明或明确告知缺口，不得假装已加载。

## 规则放置原则

| 内容 | 去向 |
|---|---|
| 始终影响安全/协作判断 | 根 `AGENTS.md` |
| 使用 Framework 的业务约束 | `Assets/Game/AGENTS.md` |
| Framework 内部实现约束 | `Assets/Game/Framework/AGENTS.md` |
| Demo 教学章节约束 | Demo Modules 的 `AGENTS.md` |
| 可触发的多步流程 | Project Skill |
| 确定性、必须执行的门禁 | 测试 / Hook / 编译器约束 |
| 完整教程、原理、历史决策 | guide / ADR / 专题文档 |
