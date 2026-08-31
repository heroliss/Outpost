#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Storage;
using Game.Framework.UI;
using Game.Outpost.Battle;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using Game.Outpost.Save;
using Game.Outpost.Windows;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Outpost.Smoke.Test
{
    /// <summary>
    /// Outpost 最短真实玩家路径：真实场景、Composition Root、Command、FlowState、UI/资源 Adapter 与战斗后端
    /// 一起运行。交互通过稳定业务 Interface 发起，不依赖坐标、动画时长或私有字段。
    /// </summary>
    public sealed class OutpostPlayerPathSmokeTests
    {
        private const string GameScenePath = "Assets/Game/Outpost/Scenes/OutpostGame.unity";
        private const string GameSceneName = "OutpostGame";
        private const string BattleSceneName = "OutpostBattle";

        private string _storagePath;
        private string _storageBackupPath;
        private bool _hadOriginalStorage;
        private bool _storageIsolationActive;
        private bool _previousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 保留编译器生成的外层 IEnumerator。Unity Test Framework 在 EditMode → PlayMode 过渡时会反射
            // IEnumerator 的状态字段做续跑；直接返回 UniTask 自定义 Enumerator 会让包内 PC 恢复器空引用。
            yield return UniTask.ToCoroutine(async () =>
            {
                Time.timeScale = 1f;
                _previousRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                PrepareIsolatedStorage();

                // PlayMode 测试从用户当前场景启动；这里不能用 Single 加载，否则当前场景有未保存改动时 Unity 会
                // 弹保存对话框，把无头测试永远卡在 0/0。只在 Play 会话内撤掉其他场景的 Context 组合根；不能销毁
                // 场景全部根节点，因为 Unity Test Runner 的协程驱动器本身也是 "Code-based tests runner" 根节点。
                // 退出 Play 后这些销毁自动回滚，不保存也不丢弃 Editor 里的场景改动。
                foreach (var context in Object.FindObjectsByType<MonoGameContextBase>(
                             FindObjectsInactive.Include,
                             FindObjectsSortMode.None))
                {
                    if (context == null || context.gameObject.scene.path == GameScenePath) continue;
                    Object.Destroy(context.transform.root.gameObject);
                }
                await UniTask.Yield(PlayerLoopTiming.Update);
                await UniTask.Yield(PlayerLoopTiming.Update);
                Assert.IsNull(GameContext.Main, "冒烟加载前已有资产场景的 MonoGlobalContext 应已随根节点销毁");

                Scene loaded = EditorSceneManager.LoadSceneInPlayMode(
                    GameScenePath,
                    new LoadSceneParameters(LoadSceneMode.Additive));
                Assert.IsTrue(loaded.IsValid(), $"无法加载冒烟场景：{GameScenePath}");

                await WaitUntil(
                    () => FindOne<OutpostContext>() != null && GameContext.Main != null,
                    "Outpost Composition Root 初始化",
                    30f);
            });
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                try
                {
                    Time.timeScale = 1f;

                    foreach (var context in Object.FindObjectsByType<OutpostContext>(
                                 FindObjectsInactive.Include,
                                 FindObjectsSortMode.None))
                        if (context != null)
                            Object.Destroy(context.gameObject);

                    await UniTask.Yield(PlayerLoopTiming.Update);
                    await UniTask.Yield(PlayerLoopTiming.Update);

                    // 用户原场景仍以 Additive 方式留在 Play 会话里，所以可直接撤真实业务场景及可能残留的战斗场景。
                    await UnloadIfLoaded(BattleSceneName);
                    await UnloadIfLoaded(GameSceneName);
                }
                finally
                {
                    try
                    {
                        RestoreStorage();
                    }
                    finally
                    {
                        Application.runInBackground = _previousRunInBackground;
                        Time.timeScale = 1f;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator Title_Battle_Retreat_Result_BackToTitle_CleansTransientModules()
        {
            yield return UniTask.ToCoroutine(async () =>
            {
                var root = FindOne<OutpostContext>();
                Assert.IsNotNull(root);
                var flow = root.GetSystem<IGameFlow>();
                var ui = root.GetUtility<IUIUtility>();

                await WaitUntil(
                    () => flow.Current is TitleState && !flow.IsTransitioning && ui.IsOpen<TitleWindow>(),
                    "Boot → 标题页",
                    60f);

                // 热力图开关来自战斗 HUD，不经过 SettingsWindow。锁定“每次持久化设置变更都安排保存”，
                // 避免把落盘职责重新退化成只有设置窗 OnClose 才会执行。
                bool initialHeatmap = root.ExecuteCommand(new GetWreckHeatmapCommand()).CurrentValue;
                root.ExecuteCommand(new SetWreckHeatmapCommand(!initialHeatmap));
                var storage = root.GetUtility<IStorageUtility>();
                await WaitUntil(
                    () => storage.Exists(StorageKeys.Settings),
                    "未打开设置窗时，HUD 偏好自动落盘",
                    10f);
                var persistedSettings = await storage.Load<OutpostSettings>(StorageKeys.Settings);
                Assert.IsNotNull(persistedSettings);
                Assert.AreEqual(!initialHeatmap, persistedSettings.WreckHeatmap,
                    "HUD 修改的持久化偏好应写入设置快照，不能依赖 SettingsWindow.OnClose");

                int runsBefore = root.ExecuteCommand(new GetPlayerRecordCommand()).Runs.CurrentValue;

                await root.ExecuteCommandAsync(new StartBattleCommand());
                await WaitUntil(
                    () => flow.Current is BattleState &&
                          SceneManager.GetSceneByName(BattleSceneName).isLoaded &&
                          FindOne<BattleContext>() != null,
                    "标题页 → 战斗场景",
                    60f);

                BattleContext battle = null;
                await WaitUntil(
                    () =>
                    {
                        battle = FindOne<BattleContext>();
                        return battle != null &&
                               battle.ExecuteCommand(new GetBattleReadModelCommand()).IsReady.CurrentValue;
                    },
                    "战斗导演完成配置、资源与模拟后端初始化",
                    90f);

                // 走与 HUD 按钮相同的 Command Interface；不反射 _ready，也不直调导演 Implementation。
                battle.ExecuteCommand(new RetreatCommand());

                await WaitUntil(
                    () => flow.Current is ResultState &&
                          !flow.IsTransitioning &&
                          ui.IsOpen<ResultWindow>() &&
                          !SceneManager.GetSceneByName(BattleSceneName).isLoaded,
                    "撤离 → 结算页并卸载战斗 Module",
                    30f);

                Assert.IsNull(FindOne<BattleContext>(), "退出 BattleState 后 BattleContext 应随附加场景卸载");
                Assert.IsNull(FindOne<BattleDirectorSystem>(), "退出 BattleState 后战斗导演不应残留");
                Assert.AreEqual(1f, Time.timeScale, 0.001f, "战斗场景销毁必须还原全局时间倍率");
                Assert.AreEqual(
                    runsBefore + 1,
                    root.ExecuteCommand(new GetPlayerRecordCommand()).Runs.CurrentValue,
                    "结算状态应恰好提交一次本局战绩");

                await root.ExecuteCommandAsync(new GoToTitleCommand());
                await WaitUntil(
                    () => flow.Current is TitleState &&
                          !flow.IsTransitioning &&
                          ui.IsOpen<TitleWindow>() &&
                          !ui.IsOpen<ResultWindow>() &&
                          !SceneManager.GetSceneByName(BattleSceneName).isLoaded,
                    "结算页 → 标题页",
                    30f);
            });
        }

        private static T FindOne<T>() where T : Object
            => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

        private static async UniTask WaitUntil(Func<bool> condition, string milestone, float timeoutSeconds)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            Exception lastException = null;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                try
                {
                    if (condition()) return;
                    lastException = null;
                }
                catch (Exception e)
                {
                    // 场景异步装配期间短暂缺依赖是可预期的；超时时把最后一次异常并入里程碑诊断。
                    lastException = e;
                }
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            string current = GameContext.Main != null &&
                             GameContext.Main.TryResolve(typeof(IGameFlow), out var resolved) &&
                             resolved is IGameFlow flow
                ? $"{flow.Current?.ToString() ?? "null"}（转换中={flow.IsTransitioning}）"
                : "无 IGameFlow";
            string scenes = string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount)
                .Select(i => SceneManager.GetSceneAt(i))
                .Select(s => $"{s.name}[loaded={s.isLoaded}]"));
            Assert.Fail(
                $"Outpost 冒烟等待“{milestone}”超时（{timeoutSeconds:F0}s）。" +
                $" 当前流程={current}；Scenes={scenes}" +
                (lastException == null ? "" : $"；最后异常={lastException}"));
        }

        private static async UniTask UnloadIfLoaded(string sceneName)
        {
            Scene scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() || !scene.isLoaded) return;
            AsyncOperation operation = SceneManager.UnloadSceneAsync(scene);
            if (operation != null) await operation.ToUniTask();
        }

        private void PrepareIsolatedStorage()
        {
            _storagePath = Path.Combine(Application.persistentDataPath, "storage", "outpost");
            _storageBackupPath = Path.Combine(
                Path.GetDirectoryName(_storagePath),
                $".outpost-smoke-backup-{Guid.NewGuid():N}");
            _hadOriginalStorage = Directory.Exists(_storagePath);

            // 同一父目录内重命名是原子操作：成功则原存档完整移出测试路径，失败则原路径保持不动。
            // 这比“复制后删除原目录”更安全，避免复制到一半时 TearDown 用残缺备份覆盖真实存档。
            if (_hadOriginalStorage)
                Directory.Move(_storagePath, _storageBackupPath);

            _storageIsolationActive = true;
        }

        private void RestoreStorage()
        {
            if (!_storageIsolationActive) return;

            if (Directory.Exists(_storagePath))
                Directory.Delete(_storagePath, recursive: true);
            if (_hadOriginalStorage)
            {
                if (!Directory.Exists(_storageBackupPath))
                    throw new DirectoryNotFoundException(
                        $"Outpost 冒烟测试无法恢复原存档，原子备份目录不存在：{_storageBackupPath}");

                Directory.Move(_storageBackupPath, _storagePath);
            }

            _storageIsolationActive = false;
            _storagePath = null;
            _storageBackupPath = null;
            _hadOriginalStorage = false;
        }
    }
}
#endif
