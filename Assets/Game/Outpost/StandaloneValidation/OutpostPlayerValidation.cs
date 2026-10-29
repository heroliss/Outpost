#if OUTPOST_STANDALONE_VALIDATION && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Flow;
using Game.Framework.Localization;
using Game.Framework.Storage;
using Game.Framework.UI;
using Game.Outpost.Battle;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using Game.Outpost.Net;
using Game.Outpost.Save;
using Game.Outpost.Windows;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.Outpost.StandaloneValidation
{
    /// <summary>同一个正式二进制的显式验收入口；无 --validate-outpost 参数时不创建任何对象。</summary>
    public sealed class OutpostPlayerValidation : MonoBehaviour
    {
        [Serializable]
        public sealed class Receipt
        {
            public string status;
            public string mode;
            public string startedUtc;
            public string completedUtc;
            public string unityVersion;
            public string storageDirectory;
            public string actualStorageDirectory;
            public bool developmentBuild;
            public int errorCount;
            public string error;
            public int runs;
            public int score;
            public int wave;
            public string callsign;
            public string backend;
            public string emplacementShader;
            public string shaderFindResult;
            public bool turretFoundation;
            public bool turretCore;
            public int armorBraceCount;
            public int foundationVertexCount;
            public string swarmShader;
            public bool swarmShaderSupported;
            public bool swarmMaterialInstancing;
            public bool systemSupportsInstancing;
            public int enemySnapshotCount;
            public int projectileSnapshotCount;
            public int enemyRenderedPixels;
            public int enemyControlColorPixels;
            public int projectileRenderedPixels;
            public int projectileControlColorPixels;
            public string locale;
            public float masterVolume;
            public bool heatmap;
            public bool expansionInstalled;
            public bool expansionBgm;
            public int chosenUpgrade;
            public List<string> checks = new();
            public List<string> screenshots = new();
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static bool Enabled => Argument("--validate-outpost") != null;
        private static string _actualStorageDirectory;

        public static StorageUtility CreateStorage()
        {
            if (!Enabled) return new StorageUtility();
            string folder = Argument("--validate-storage");
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                throw new ArgumentException("显式验收必须指定绝对 --validate-storage 路径。");
            var provider = new FileStorageProvider(Path.GetFullPath(folder));
            _actualStorageDirectory = provider.RootPath;
            return new StorageUtility(provider);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!Enabled) return;
            var host = new GameObject("Outpost explicit Player validation");
            DontDestroyOnLoad(host);
            host.AddComponent<OutpostPlayerValidation>();
        }

        private Receipt _receipt;
        private string _reportPath;
        private string _captures;
        private int _errors;

        private void Awake() => Application.logMessageReceived += ObserveLog;

        private void Start() => Run().Forget(OnUnexpectedFailure);

        private async UniTask Run()
        {
            _reportPath = Argument("--validate-report");
            if (string.IsNullOrWhiteSpace(_reportPath) || !Path.IsPathFullyQualified(_reportPath))
                throw new ArgumentException("显式验收必须指定绝对 --validate-report 路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(_reportPath));
            _captures = Path.Combine(Path.GetDirectoryName(_reportPath), Path.GetFileNameWithoutExtension(_reportPath) + "-screenshots");
            Directory.CreateDirectory(_captures);
            _receipt = new Receipt
            {
                status = "running",
                mode = Argument("--validate-outpost"),
                startedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                storageDirectory = Argument("--validate-storage"),
                developmentBuild = Debug.isDebugBuild,
            };
            Application.runInBackground = true;
            Save();
            try
            {
                OutpostContext root = null;
                await Wait(() => (root = UnityEngine.Object.FindFirstObjectByType<OutpostContext>()) != null,
                    "真实首场景 Context", 60f);
                var flow = root.GetSystem<IGameFlow>();
                var ui = root.GetUtility<IUIUtility>();
                await Wait(() => flow.Current is TitleState && !flow.IsTransitioning && ui.IsOpen<TitleWindow>(),
                    "离线资源启动到标题", 90f);
                Require(!Debug.isDebugBuild, "非 Development 正式 Player");
                Require(!OutpostNet.Available, "正式包不启动本地排行榜服务");
                Require(root.GetUtility<IAssetUtility>().CurrentPlayMode == AssetPlayMode.Offline, "运行时实际使用 Offline");
                var storage = root.GetUtility<IStorageUtility>();
                _receipt.actualStorageDirectory = _actualStorageDirectory;
                Require(string.Equals(Path.GetFullPath(_receipt.storageDirectory), _actualStorageDirectory,
                    StringComparison.OrdinalIgnoreCase), "实际 FileStorageProvider 使用独立验收目录");

                if (_receipt.mode == "reload")
                {
                    await CheckPersistence(root, storage);
                    Require(_receipt.runs >= 1, "重启载入已完成对局");
                    await Capture("08-reloaded-title.png");
                }
                else if (_receipt.mode == "exercise")
                {
                    int runsBefore = root.ExecuteCommand(new GetPlayerRecordCommand()).Runs.CurrentValue;
                    Submit(ui.Get<TitleWindow>().Root.Q<Button>("settings"));
                    await Wait(() => ui.IsOpen<SettingsWindow>(), "标题页设置按钮打开真实窗口", 30f);
                    var settingsWindow = ui.Get<SettingsWindow>();
                    settingsWindow.Root.Q<Slider>("master").value = 0.65f;
                    Submit(settingsWindow.Root.Q<Button>("lang-zh"));
                    root.ExecuteCommand(new SetWreckHeatmapCommand(true));
                    Submit(settingsWindow.Root.Q<Button>("exp-download"));
                    var assets = root.GetUtility<IAssetUtility>();
                    await Wait(() => assets.GetInitState(Game.Main.AssetPackages.OutpostExpansionPackage).CurrentValue == AssetInitState.Ready &&
                        settingsWindow.Root.Q<VisualElement>("exp-bgm-row").resolvedStyle.display != DisplayStyle.None,
                        "增援电台真实设置入口离线启用", 45f);
                    Require(assets.CreateAllDownloader(Game.Main.AssetPackages.OutpostExpansionPackage).TotalCount == 0,
                        "增援电台完整内置，无需下载");
                    Submit(settingsWindow.Root.Q<Button>("exp-bgm-on"));
                    await root.ExecuteCommandAsync(new SaveSettingsCommand());
                    await CheckPersistence(root, storage);
                    await Capture("02-settings.png");
                    Submit(settingsWindow.Root.Q<Button>("close"));
                    await Wait(() => !ui.IsOpen<SettingsWindow>(), "设置页关闭按钮", 30f);
                    await Capture("01-title.png");
                    string requestedBackend = Argument("--validate-backend");
                    if (requestedBackend != null)
                    {
                        if (!Enum.TryParse<BattleSimBackend>(requestedBackend, true, out var backend) ||
                            !Enum.IsDefined(typeof(BattleSimBackend), backend))
                            throw new ArgumentException("--validate-backend 仅支持 Ecs 或 Reference。");
                        root.ExecuteCommand(new SetBattleBackendCommand(backend));
                    }
                    Submit(ui.Get<TitleWindow>().Root.Q<Button>("start"));
                    BattleContext battle = null;
                    await Wait(() => flow.Current is BattleState &&
                        (battle = UnityEngine.Object.FindFirstObjectByType<BattleContext>()) != null &&
                        battle.ExecuteCommand(new GetBattleReadModelCommand()).IsReady.CurrentValue,
                        "真实战斗场景和导演就绪", 90f);
                    await ValidateTurretVisuals();
                    battle.ExecuteCommand(new SetAutoManageCommand(false));
                    await ValidateSwarmVisuals();
                    root.ExecuteCommand(new SetSimSpeedCommand(4f));
                    await Wait(() => battle.ExecuteCommand(new GetBattleReadModelCommand()).Kills.CurrentValue > 0,
                        "真实模拟产生击杀", 90f);
                    var state = battle.ExecuteCommand(new GetBattleReadModelCommand());
                    _receipt.backend = state.Backend.CurrentValue;
                    await Capture("03-battle.png");
                    await Wait(() => battle.ExecuteCommand(new GetUpgradeChoiceCommand()).IsChoosing.CurrentValue,
                        "正常清波出现强化选择", 180f);
                    root.ExecuteCommand(new SetSimSpeedCommand(1f));
                    var choice = battle.ExecuteCommand(new GetUpgradeChoiceCommand());
                    Require(choice.Choices.Count == 3, "真实三选一强化候选");
                    _receipt.chosenUpgrade = choice.Choices[0].Id;
                    await Capture("04-upgrade.png");
                    var choiceView = UnityEngine.Object.FindFirstObjectByType<UpgradeChoiceView>();
                    var cards = choiceView.GetComponentsInChildren<UnityEngine.UI.Button>(false);
                    Require(cards.Length == 3 && cards[0].interactable, "真实强化卡片按钮可交互");
                    cards[0].onClick.Invoke();
                    await Wait(() => !choice.IsChoosing.CurrentValue && state.Wave.CurrentValue >= 2,
                        "选择强化后进入下一波", 30f);
                    await Capture("05-next-wave.png");
                    var retreat = battle.GetComponentsInChildren<UnityEngine.UI.Button>(false)
                        .Single(button => button.name == "RetreatButton");
                    Require(retreat.interactable, "真实撤离按钮可交互");
                    retreat.onClick.Invoke();
                    await Wait(() => flow.Current is ResultState && !flow.IsTransitioning &&
                        ui.IsOpen<ResultWindow>() && !SceneManager.GetSceneByName("OutpostBattle").isLoaded,
                        "撤离结算并卸载战斗场景", 45f);
                    Require(UnityEngine.Object.FindFirstObjectByType<BattleContext>() == null, "战斗 Context 已释放");
                    Require(Mathf.Abs(Time.timeScale - 1f) < 0.001f, "离场恢复全局时间倍率");
                    await CheckPersistence(root, storage);
                    Require(_receipt.runs == runsBefore + 1, "真实结算只提交一次战绩");
                    Require(_receipt.score > 0 && _receipt.wave >= 2, "实际战绩含得分与波次");
                    await Capture("06-result.png");
                    Submit(ui.Get<ResultWindow>().Root.Q<Button>("back"));
                    await Wait(() => flow.Current is TitleState && ui.IsOpen<TitleWindow>(), "返回标题", 30f);
                    await Capture("07-returned-title.png");
                }
                else throw new ArgumentException("--validate-outpost 仅支持 exercise 或 reload。");

                Require(_errors == 0, "验收过程无 Error/Exception/Assert");
                _receipt.status = "passed";
                _receipt.completedUtc = DateTime.UtcNow.ToString("O");
                Save();
                Application.Quit(0);
            }
            catch (Exception exception)
            {
                _receipt.status = "failed";
                _receipt.error = exception.ToString();
                _receipt.completedUtc = DateTime.UtcNow.ToString("O");
                Save();
                Debug.LogException(exception);
                Application.Quit(1);
            }
        }

        private async UniTask ValidateTurretVisuals()
        {
            var turret = UnityEngine.Object.FindFirstObjectByType<TurretView>();
            Require(turret != null, "真实炮塔表现组件存在");
            var foundation = turret.transform.Find("Foundation");
            var core = turret.transform.Find("Core");
            _receipt.turretFoundation = foundation != null;
            _receipt.turretCore = core != null;
            _receipt.armorBraceCount = turret.transform.Cast<Transform>()
                .Count(child => child.name == "ArmorBrace");
            var found = Shader.Find("Universal Render Pipeline/Unlit");
            _receipt.shaderFindResult = found != null ? found.name : "missing";
            Require(_receipt.turretFoundation && _receipt.turretCore && _receipt.armorBraceCount == 6,
                "正式 Player 实际创建工事底座、核心和六根支撑");
            var foundationRenderer = foundation.GetComponent<MeshRenderer>();
            var coreRenderer = core.GetComponent<MeshRenderer>();
            var mesh = foundation.GetComponent<MeshFilter>()?.sharedMesh;
            Require(mesh != null, "工事实际使用程序网格");
            _receipt.foundationVertexCount = mesh.vertexCount;
            Require(mesh.vertexCount >= 6, "工事底座包含六边形几何");
            _receipt.emplacementShader = foundationRenderer.sharedMaterial.shader.name;
            Require(_receipt.emplacementShader == "Universal Render Pipeline/Unlit" &&
                foundationRenderer.sharedMaterial.shader.isSupported &&
                coreRenderer.sharedMaterial.shader.name == _receipt.emplacementShader &&
                coreRenderer.sharedMaterial.shader.isSupported,
                "工事与核心实际材质使用受支持的 URP Unlit shader");
            await Wait(() => foundationRenderer.isVisible && coreRenderer.isVisible,
                "工事底座与核心进入真实相机可见集合", 10f);
            Save();
        }

        private async UniTask ValidateSwarmVisuals()
        {
            var swarm = UnityEngine.Object.FindFirstObjectByType<SwarmRenderer>();
            Require(swarm != null, "真实战斗 SwarmRenderer 存在");
            await Wait(() => swarm.ValidationMaterial != null, "实例化战斗材质已初始化", 15f);
            var material = swarm.ValidationMaterial;
            _receipt.swarmShader = material.shader != null ? material.shader.name : "missing";
            _receipt.swarmShaderSupported = material.shader != null && material.shader.isSupported;
            _receipt.swarmMaterialInstancing = material.enableInstancing;
            _receipt.systemSupportsInstancing = SystemInfo.supportsInstancing;
            Save();
            Require(_receipt.swarmShader == "Outpost/SwarmUnlit" && _receipt.swarmShaderSupported,
                "敌人与弹丸实际材质使用受支持的 Outpost/SwarmUnlit shader");
            Require(_receipt.systemSupportsInstancing && _receipt.swarmMaterialInstancing,
                "设备与真实战斗材质均启用 GPU instancing");

            swarm.ValidationSamplingEnabled = true;
            try
            {
                await ValidateSwarmLayer(swarm, false);
                await ValidateSwarmLayer(swarm, true);
            }
            finally { swarm.ValidationSamplingEnabled = false; }
        }

        private async UniTask ValidateSwarmLayer(SwarmRenderer swarm, bool projectile)
        {
            Mesh mesh = null;
            Matrix4x4 matrix = Matrix4x4.identity;
            Vector4 color = Vector4.zero;
            Camera camera = null;
            string label = projectile ? "弹丸" : "敌人";
            await Wait(() => (camera = Camera.main) != null &&
                (projectile ? swarm.ValidationProjectileCount : swarm.ValidationEnemyCount) > 0 &&
                swarm.TryGetValidationSample(projectile, out mesh, out matrix, out color) &&
                SampleIsVisible(camera, mesh, matrix),
                "真实模拟和渲染路径产生相机可见的" + label + "样本", 60f);
            if (projectile) _receipt.projectileSnapshotCount = swarm.ValidationProjectileCount;
            else _receipt.enemySnapshotCount = swarm.ValidationEnemyCount;

            // First instance keeps the actual runtime mesh, transform and color. A second,
            // offset copy has a distinct control color: missing INSTANCING_ON variants or
            // a plain white fallback cannot satisfy both independent pixel assertions.
            var controlColor = new Vector4(0.13f, 0.79f, 0.31f, 1f);
            var center = camera.WorldToViewportPoint(matrix.MultiplyPoint3x4(mesh.bounds.center));
            float offset = center.x > 0.5f ? -1.8f : 1.8f;
            var matrices = new[] { matrix, Matrix4x4.Translate(camera.transform.right * offset) * matrix };
            var colors = new[] { color, controlColor };
            int width = Mathf.Clamp(camera.pixelWidth, 640, 1920);
            int height = Mathf.Clamp(camera.pixelHeight, 360, 1080);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear) { name = "Outpost explicit " + label + " pixel proof" };
            var pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var previousTarget = RenderTexture.active;
            try
            {
                target.Create();
                var properties = new MaterialPropertyBlock();
                properties.SetVectorArray(Shader.PropertyToID("_BaseColor"), colors);
                using (var commands = new CommandBuffer { name = "Outpost explicit " + label + " instancing proof" })
                {
                    commands.SetRenderTarget(target);
                    commands.ClearRenderTarget(true, true, Color.black);
                    commands.SetViewport(new Rect(0, 0, width, height));
                    commands.SetViewProjectionMatrices(camera.worldToCameraMatrix,
                        GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
                    commands.DrawMeshInstanced(mesh, 0, swarm.ValidationMaterial, 0, matrices, 2, properties);
                    Graphics.ExecuteCommandBuffer(commands);
                }
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                pixels.Apply(false, false);
                string filename = projectile ? "03-projectile-instancing-proof.png" : "03-enemy-instancing-proof.png";
                string path = Path.Combine(_captures, filename);
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                _receipt.screenshots.Add(path);
                int actualPixels = CountColorPixels(pixels.GetPixels32(), color);
                int controlPixels = CountColorPixels(pixels.GetPixels32(), controlColor);
                if (projectile)
                {
                    _receipt.projectileRenderedPixels = actualPixels;
                    _receipt.projectileControlColorPixels = controlPixels;
                }
                else
                {
                    _receipt.enemyRenderedPixels = actualPixels;
                    _receipt.enemyControlColorPixels = controlPixels;
                }
                Save();
                Require(actualPixels >= 2 && controlPixels >= 2,
                    label + "真实材质/网格/相机矩阵实例化输出实际颜色与独立对照颜色像素");
            }
            finally
            {
                RenderTexture.active = previousTarget;
                target.Release();
                Destroy(target);
                Destroy(pixels);
            }
        }

        private static bool SampleIsVisible(Camera camera, Mesh mesh, Matrix4x4 matrix)
        {
            if (mesh == null || matrix.lossyScale.sqrMagnitude < 0.001f) return false;
            var viewport = camera.WorldToViewportPoint(matrix.MultiplyPoint3x4(mesh.bounds.center));
            return viewport.z > camera.nearClipPlane && viewport.z < camera.farClipPlane &&
                viewport.x > 0.06f && viewport.x < 0.94f && viewport.y > 0.06f && viewport.y < 0.94f;
        }

        private static int CountColorPixels(Color32[] pixels, Vector4 expected)
        {
            float red = Mathf.Clamp01(expected.x) * 255f;
            float green = Mathf.Clamp01(expected.y) * 255f;
            float blue = Mathf.Clamp01(expected.z) * 255f;
            int count = 0;
            foreach (var pixel in pixels)
                if (Mathf.Abs(pixel.r - red) <= 16f && Mathf.Abs(pixel.g - green) <= 16f &&
                    Mathf.Abs(pixel.b - blue) <= 16f)
                    count++;
            return count;
        }

        private async UniTask CheckPersistence(OutpostContext root, IStorageUtility storage)
        {
            var settings = await storage.Load<OutpostSettings>(StorageKeys.Settings);
            var record = await storage.Load<OutpostRecord>(StorageKeys.Record);
            Require(settings != null && record != null, "设置与战绩真实写入磁盘");
            _receipt.locale = root.GetUtility<ILocalizationUtility>().Locale.CurrentValue;
            _receipt.masterVolume = root.GetUtility<IAudioUtility>().MasterVolume;
            _receipt.heatmap = root.ExecuteCommand(new GetWreckHeatmapCommand()).CurrentValue;
            Require(settings.Locale == OutpostLocales.ChineseSimplified && _receipt.locale == settings.Locale,
                "语言设置保存并回灌");
            Require(Mathf.Abs(settings.MasterVolume - 0.65f) < 0.001f &&
                Mathf.Abs(_receipt.masterVolume - settings.MasterVolume) < 0.001f, "音量设置保存并回灌");
            Require(settings.WreckHeatmap && _receipt.heatmap, "HUD 偏好保存并回灌");
            await Wait(() => root.GetUtility<IAssetUtility>().GetInitState(Game.Main.AssetPackages.OutpostExpansionPackage).CurrentValue == AssetInitState.Ready,
                "已安装扩展包在当前会话就绪", 45f);
            _receipt.expansionInstalled = settings.ExpansionInstalled;
            _receipt.expansionBgm = root.ExecuteCommand(new GetExpansionBgmCommand()).CurrentValue;
            Require(settings.ExpansionInstalled && settings.ExpansionBgm && _receipt.expansionBgm,
                "扩展包安装与电台曲设置保存并回灌");
            var view = root.ExecuteCommand(new GetPlayerRecordCommand());
            _receipt.runs = view.Runs.CurrentValue;
            _receipt.score = view.BestScore.CurrentValue;
            _receipt.wave = view.BestWave.CurrentValue;
            _receipt.callsign = view.Callsign.CurrentValue;
            Require(record.Runs == _receipt.runs && record.BestScore == _receipt.score &&
                record.BestWave == _receipt.wave && record.Callsign == _receipt.callsign,
                "内存战绩与真实存档一致");
            string settingsPath = Path.Combine(_actualStorageDirectory, StorageKeys.Settings + ".sav");
            string recordPath = Path.Combine(_actualStorageDirectory, StorageKeys.Record + ".sav");
            Require(File.Exists(settingsPath) && File.Exists(recordPath), "独立目录存在实际设置与战绩文件");
            var diskRecord = JsonUtility.FromJson<OutpostRecord>(File.ReadAllText(recordPath));
            Require(diskRecord.Runs == record.Runs && diskRecord.Callsign == record.Callsign &&
                diskRecord.BestScore == record.BestScore, "独立目录存档与实际读取数据一致");
            Save();
        }

        private async UniTask Capture(string name)
        {
            await UniTask.Delay(250, ignoreTimeScale: true);
            string path = Path.Combine(_captures, name);
            ScreenCapture.CaptureScreenshot(path);
            await Wait(() => File.Exists(path) && new FileInfo(path).Length > 1000, "截图已写入 " + name, 15f);
            _receipt.screenshots.Add(path);
            Save();
        }

        private async UniTask Wait(Func<bool> predicate, string label, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup > end) throw new TimeoutException(label);
                await UniTask.Yield();
            }
            _receipt.checks.Add(label);
            Save();
        }

        private void Require(bool passed, string label)
        {
            if (!passed) throw new InvalidOperationException(label);
            _receipt.checks.Add(label);
        }

        private void Submit(Button button)
        {
            Require(button != null && button.enabledInHierarchy && button.panel != null, "UI Toolkit 按钮可交互");
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }

        private void ObserveLog(string message, string stack, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert) _errors++;
        }

        private void Save()
        {
            if (_receipt == null || string.IsNullOrEmpty(_reportPath)) return;
            _receipt.errorCount = _errors;
            File.WriteAllText(_reportPath, JsonUtility.ToJson(_receipt, true));
        }

        private void OnUnexpectedFailure(Exception exception)
        {
            Debug.LogException(exception);
            if (_receipt != null)
            {
                _receipt.status = "failed";
                _receipt.error = exception.ToString();
                _receipt.completedUtc = DateTime.UtcNow.ToString("O");
                Save();
            }
            Application.Quit(1);
        }

        private void OnDestroy() => Application.logMessageReceived -= ObserveLog;
    }
}
#endif
