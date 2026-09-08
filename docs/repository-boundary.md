# 仓库边界

本仓库是独立 Unity 工程，唯一共享源码入口是 com.liss.ssframework。Framework 通过 Packages/com.liss.ssframework/ 子模块固定到具体提交；更新时只提交 gitlink 和兼容性说明。

项目之间没有运行时引用，不能把另一个项目的 Assets/、ProjectSettings/ 或生成目录复制进来。