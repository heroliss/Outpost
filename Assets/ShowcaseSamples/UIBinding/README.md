# SSFramework UI binding sample

这是为展示框架真实 Hierarchy 节点绑定与代码生成流程制作的独立样例，并非 Outpost 游戏 UI。

- Prefab 上 6 个绑定由框架原生菜单标记，生成器创建 Generated/*.nodes.g.cs 与 Logic/*.cs。
- 目录配置只作用于本目录，未改全局 Profile。
- 本目录程序集仅在 Editor 编译，Prefab 未加入资源收集器。
- ShowcaseGeneratedCodeWindow 只读取实际生成文件用于编辑器预览，不修改磁盘内容。
