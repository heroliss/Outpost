using Game.Framework.Diagnostics;
using UnityEngine;

namespace Game.Main
{
    /// <summary>
    /// 游戏入口——业务的「main」。<c>HotUpdateLauncher</c> 加载完全部热更 DLL 后反射调用（约定：公共静态无参方法
    /// <see cref="Enter"/>）；纯 AOT / 不启用热更的项目可绕开 Launcher，直接从随包场景调本方法（见框架手册 §15）。
    /// 这里是真正启动编排的起点：创建全局 Context、初始化框架资源系统、从 bundle 加载首场景都从这往下走。
    ///
    /// <para>程序集与目录按领域命名（Main / 模块 / DLC），**不按是否热更命名**——热更与否是热更构建配置
    /// （FrameworkHotUpdateProfile 列表）里的部署决策，与代码组织无关（ADR-0008）。</para>
    ///
    /// <para>当前是**模板**：仅挂一个框架自检（<see cref="FrameworkSelfCheck"/>）验证当前构建下框架基元端到端可用
    /// （IL2CPP / 热更链路冒烟，ADR-0008 §6）。正式项目把这行替换为真正的启动编排，上线前移除自检。</para>
    /// </summary>
    public static class GameEntry
    {
        /// <summary>热更验证标记：每次热更迭代改这里，肉眼比对玩家包屏显跑的是哪一版。</summary>
        public const string EntryVersion = "v3-framework-check";

        public static void Enter()
        {
            Debug.Log($"[GameEntry] 入口已执行（{EntryVersion}）——程序集 {typeof(GameEntry).Assembly.GetName().Name} 运行中。");

            // TODO(业务): 替换为真正的启动编排——new MonoGlobalContext → 初始化资源系统 → 从 bundle 加载首场景。
            // 接入设备/热更链路阶段保留下面这段做一次性自检，上线前删掉。
            var go = new GameObject("FrameworkSelfCheck");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<FrameworkSelfCheck>().Caption = $"GameEntry {EntryVersion}";
        }
    }
}
