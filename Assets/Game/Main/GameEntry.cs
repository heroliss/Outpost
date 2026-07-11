using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Context;
using UnityEngine;

namespace Game.Main
{
    /// <summary>
    /// 游戏入口——业务的「main」。<c>HotUpdateLauncher</c> 加载完全部热更 DLL 后反射调用（约定：公共静态无参方法
    /// <see cref="Enter"/>）；纯 AOT / 不启用热更的项目可绕开 Launcher，直接从随包场景调本方法（见框架手册 §15）。
    ///
    /// <para>这里做的事只有一件：搭一个最小的引导资源栈，把首场景（<c>OutpostGame</c>）从 bundle 拉起来。
    /// Boot 场景是 AOT 世界、挂不了任何热更组件（框架组件也是热更的），所以资源三件套没法摆在随包场景里——
    /// 由本方法用代码搭（Context + <see cref="AssetUtility"/>），初始化默认包后加载首场景，然后销毁引导物体交棒：
    /// 首场景根 Context（游戏真正的全局 Context）与其场景内三件套接管一切，provider 对已初始化的包按名复用、不重复拉清单。</para>
    ///
    /// <para>程序集与目录按领域命名（Main / 模块 / DLC），**不按是否热更命名**——热更与否是热更构建配置
    /// （FrameworkHotUpdateProfile 列表）里的部署决策，与代码组织无关（ADR-0008）。</para>
    /// </summary>
    public static class GameEntry
    {
        /// <summary>热更验证标记：每次热更迭代改这里，肉眼比对玩家包日志跑的是哪一版。</summary>
        public const string EntryVersion = "v4.1-outpost-hotfix";

        /// <summary>首场景 location（默认包内按文件名寻址）。</summary>
        private const string FirstSceneLocation = "OutpostGame";

#if !UNITY_EDITOR
        /// <summary>
        /// 资源包 CDN 根地址（第一条主、其余备），与代码包同一套部署结构 {CDN}/{包名}/{文件}。
        /// 当前指向本地联调服务（构建 profile 的 LocalServePort）；正式部署换成真实 CDN。
        /// </summary>
        private static readonly string[] CdnUrls = { "http://127.0.0.1:8080/" };
#endif

        public static void Enter()
        {
            Debug.Log($"[GameEntry] 入口已执行（{EntryVersion}）——程序集 {typeof(GameEntry).Assembly.GetName().Name} 运行中。");
            Boot().Forget(static ex => Debug.LogException(ex));
        }

        private static async UniTask Boot()
        {
            // 引导物体挂 DDOL：Single 场景切换会清掉 Boot 场景的一切，引导栈要活到交棒完成。
            // Context 在前、Utility 在后（AddComponent 即 Awake，Utility 沿父链找到同物体上的 Context 注册）。
            var go = new GameObject("GameEntryBoot");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MonoGameContextBase>();
            var assets = go.AddComponent<AssetUtility>();

            // 引导期只认默认包；编辑器从 BootScene 进 Play 走模拟模式（免打包），玩家包走 Host（内置首包 + CDN 热更）。
            // 首场景内 AssetSystemConfigModel 的配置（含扩展包策略、玩家包模式）在场景起来后由场景三件套接管。
#if UNITY_EDITOR
            assets.Configure(AssetPackages.DefaultPackage, new AssetProviderConfig(), AssetPlayMode.EditorSimulate);
#else
            assets.Configure(
                AssetPackages.DefaultPackage,
                new AssetProviderConfig { CdnUrls = CdnUrls },
                AssetPlayMode.Host);
#endif
            await assets.Initialize();
            await assets.LoadScene(FirstSceneLocation);

            // 交棒完成：首场景根 Context 已就位，引导栈退场（包在 YooAssets 全局注册表按进程级单例存活，不受影响）。
            Object.Destroy(go);
            Debug.Log($"[GameEntry] 首场景 {FirstSceneLocation} 已拉起，引导栈退场。");
        }
    }
}
