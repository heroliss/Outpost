using UnityEngine;

namespace Game.Main
{
    /// <summary>
    /// 游戏入口——<c>HotUpdateLauncher</c> 加载完全部热更 DLL 后反射调用（约定：公共静态无参方法 <see cref="Enter"/>）。
    /// 这里是业务的「main」：创建全局 Context、初始化框架资源系统、从 bundle 加载首场景都从这往下走。
    ///
    /// 程序集与目录按领域命名（Main / 模块 / DLC），**不按是否热更命名**——热更与否是热更构建配置
    /// （FrameworkHotUpdateProfile 列表）里的部署决策，与代码组织无关（ADR-0008）。
    ///
    /// 当前内容是端到端验证样例：打日志 + 屏显版本标记。改 <see cref="EntryVersion"/> 后只重打代码包（不重出安装包），
    /// 重启玩家包看到新值即证明热更链路生效。业务项目把样例逻辑替换为真正的启动编排。
    /// </summary>
    public static class GameEntry
    {
        /// <summary>热更验证标记：每次热更迭代改这里，肉眼比对玩家包屏显。</summary>
        public const string EntryVersion = "v2";

        public static void Enter()
        {
            Debug.Log($"[GameEntry] 入口已执行（{EntryVersion}）——程序集 {typeof(GameEntry).Assembly.GetName().Name} 运行中。");

            var go = new GameObject("Game");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<HotUpdateSmokeLabel>();
        }
    }

    /// <summary>端到端验证用屏显标记（热更版本 + 框架内核程序集名，证明热更世界里框架类型可用）。业务项目删掉即可。</summary>
    public sealed class HotUpdateSmokeLabel : MonoBehaviour
    {
        private void OnGUI()
        {
            GUI.Label(new Rect(10, 70, Screen.width - 20, 30),
                $"GameEntry {GameEntry.EntryVersion} · 热更程序集运行中 · " +
                $"框架内核：{typeof(Game.Framework.Context.GameContext).Assembly.GetName().Name}");
        }
    }
}
