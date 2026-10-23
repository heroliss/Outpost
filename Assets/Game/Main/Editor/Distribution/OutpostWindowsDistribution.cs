using System;
using System.IO;
using System.Linq;
using Game.Framework;
using Game.Framework.Build;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;
using YooAsset.Editor;

namespace Game.Main.Distribution.Editor
{
    /// <summary>Outpost 的离线 Windows 分发配方；不会改写教学场景与热更配置。</summary>
    public static class OutpostWindowsDistribution
    {
        private const string SourceScene = "Assets/Game/Outpost/Scenes/OutpostGame.unity";
        private const string StagingRoot = "Assets/__OutpostDistributionStaging";
        private const string StagedScene = StagingRoot + "/OutpostGame.unity";

        [Serializable]
        public sealed class BundleFile { public string FileName; public long FileSize; }
        [Serializable] public sealed class BundleReport { public BundleFile[] BundleInfos; }

        [Serializable]
        public sealed class Receipt
        {
            public string status;
            public string startedUtc;
            public string completedUtc;
            public string unityVersion;
            public string outputDirectory;
            public string executable;
            public string resourceVersion;
            public string resourceMessage;
            public string result;
            public string error;
            public ulong totalBytes;
            public int errors;
            public int warnings;
            public bool originalSettingsRestored;
            public string[] packages;
        }

        /// <summary>在 Edit 模式、Windows64 平台执行；目标必须为空目录，产物不会覆盖旧包。</summary>
        public static void Build(string destinationDirectory)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("构建前必须处于无编译的 Edit 模式。");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                throw new InvalidOperationException("请先通过 Editor 切换到 Windows x64，再执行此配方。");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("存在未保存场景；请先明确保存后再构建。");

            string output = Path.GetFullPath(destinationDirectory);
            if (!Path.IsPathFullyQualified(destinationDirectory) ||
                output.StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出必须是 Assets 之外的绝对路径。", nameof(destinationDirectory));
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException("拒绝覆盖非空输出目录：" + output);
            if (AssetDatabase.IsValidFolder(StagingRoot))
                throw new IOException("发现残留构建场景，请先核对并清理：" + StagingRoot);

            Directory.CreateDirectory(output);
            var receipt = new Receipt
            {
                status = "preparing",
                startedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                outputDirectory = output,
                executable = Path.Combine(output, "Outpost.exe"),
                resourceVersion = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
                packages = new[] { "DefaultPackage", "OutpostExpansionPackage" },
            };
            string receiptPath = Path.Combine(output, "build-receipt.json");
            void Save() => File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true));
            Save();

            var scenes = EditorSceneManager.GetSceneManagerSetup();
            bool hybridEnabled = HybridCLRSettings.Instance.enable;
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            string company = PlayerSettings.companyName;
            bool previousDevelopment = EditorUserBuildSettings.development;
            bool previousBackground = PlayerSettings.runInBackground;
            bool previousResizable = PlayerSettings.resizableWindow;
            bool previousNativeResolution = PlayerSettings.defaultIsNativeResolution;
            int width = PlayerSettings.defaultScreenWidth;
            int height = PlayerSettings.defaultScreenHeight;
            var screenMode = PlayerSettings.fullScreenMode;
            FrameworkAssetBuildProfile copy = null;
            try
            {
                HybridCLRSettings.Instance.enable = false;
                HybridCLRSettings.Save();
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
                EditorUserBuildSettings.development = false;
                PlayerSettings.companyName = "Liss";
                PlayerSettings.runInBackground = true;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.defaultIsNativeResolution = false;
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 720;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

                if (!FrameworkAssetBuildProfile.TryResolve(out var profile))
                    throw new InvalidOperationException("工程缺少资源构建 Profile。");
                copy = UnityEngine.Object.Instantiate(profile);
                // SBP 会卸载未被资产系统持有的对象；内存配置必须显式保持到整批构建结束。
                copy.hideFlags = HideFlags.HideAndDontSave;
                // 分发包把可选电台也放进首包，设置页的启用流程同样不依赖本机 CDN。
                var serializedProfile = new SerializedObject(copy);
                var packages = serializedProfile.FindProperty("_packages");
                for (int i = 0; i < packages.arraySize; i++)
                {
                    var entry = packages.GetArrayElementAtIndex(i);
                    bool included = receipt.packages.Contains(entry.FindPropertyRelative("_packageName").stringValue);
                    entry.FindPropertyRelative("_buildEnabled").boolValue = included;
                    if (included)
                        entry.FindPropertyRelative("_builtinCopy").intValue = (int)EBundledCopyOption.ClearAndCopyAll;
                }
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                receipt.status = "building-resources";
                Save();
                var assets = FrameworkAssetBuilder.Build(copy, receipt.packages, receipt.resourceVersion);
                if (copy == null) throw new InvalidOperationException("临时资源 Profile 在构建中被卸载。");
                receipt.resourceMessage = assets.message;
                if (!assets.ok) throw new InvalidOperationException("资源构建失败：" + assets.message);
                foreach (string package in receipt.packages)
                {
                    string directory = Path.Combine(Application.streamingAssetsPath, "yoo", package);
                    string versionFile = Path.Combine(directory, YooAssetConfiguration.GetPackageVersionFileName(package));
                    if (!File.Exists(versionFile) || File.ReadAllText(versionFile).Trim() != receipt.resourceVersion)
                        throw new IOException(package + " 未内置本次构建的版本文件。");
                    if (!File.Exists(Path.Combine(directory, YooAssetConfiguration.GetManifestBinaryFileName(package, receipt.resourceVersion))))
                        throw new IOException(package + " 未内置本次构建的清单。");
                    string reportFile = Path.Combine(AssetBuildLayout.BundlesRoot, EditorUserBuildSettings.activeBuildTarget.ToString(), package, receipt.resourceVersion, YooAssetConfiguration.GetBuildReportFileName(package, receipt.resourceVersion));
                    var bundles = JsonUtility.FromJson<BundleReport>(File.ReadAllText(reportFile)).BundleInfos;
                    if (bundles == null || bundles.Length == 0) throw new IOException(package + " 没有实际资源。");
                    foreach (var bundle in bundles)
                    {
                        string file = Path.Combine(directory, bundle.FileName);
                        if (!File.Exists(file) || new FileInfo(file).Length != bundle.FileSize)
                            throw new IOException(package + " 内置资源缺失或大小不符：" + bundle.FileName);
                    }
                }

                AssetDatabase.CreateFolder("Assets", "__OutpostDistributionStaging");
                if (!AssetDatabase.CopyAsset(SourceScene, StagedScene))
                    throw new IOException("无法创建独立分发场景副本。");
                var staged = EditorSceneManager.OpenScene(StagedScene, OpenSceneMode.Additive);
                var utilities = staged.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<AssetUtility>(true)).ToArray();
                if (utilities.Length != 1)
                    throw new InvalidOperationException("分发首场景必须恰有一个 AssetUtility。");
                var utility = new SerializedObject(utilities[0]);
                utility.FindProperty("_settings._playerPlayMode").intValue = (int)AssetPlayMode.Offline;
                utility.FindProperty("_settings._cdnUrls").arraySize = 0;
                utility.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(staged);
                if (!EditorSceneManager.SaveScene(staged)) throw new IOException("无法保存分发场景副本。");
                EditorSceneManager.CloseScene(staged, true);
                AssetDatabase.SaveAssets();

                receipt.status = "building-player";
                Save();
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { StagedScene },
                    locationPathName = receipt.executable,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                    extraScriptingDefines = new[] { "OUTPOST_STANDALONE_VALIDATION" },
                });
                receipt.result = report.summary.result.ToString();
                receipt.totalBytes = report.summary.totalSize;
                receipt.errors = report.summary.totalErrors;
                receipt.warnings = report.summary.totalWarnings;
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded || !File.Exists(receipt.executable))
                    throw new InvalidOperationException("Windows Player 构建失败：" + report.summary.result);
                receipt.status = "built";
            }
            catch (Exception exception)
            {
                receipt.status = "failed";
                receipt.error = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                bool restored = true;
                void Restore(Action action)
                {
                    try { action(); }
                    catch (Exception restorationError)
                    {
                        restored = false;
                        receipt.status = "failed";
                        receipt.error += "\n恢复工程失败：" + restorationError;
                        Debug.LogException(restorationError);
                    }
                }
                // 各组恢复相互独立；场景清理失败也不能阻止恢复热更和 Player 设置。
                Restore(() => { HybridCLRSettings.Instance.enable = hybridEnabled; HybridCLRSettings.Save(); });
                Restore(() => PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend));
                Restore(() => {
                    PlayerSettings.companyName = company;
                    PlayerSettings.runInBackground = previousBackground;
                    PlayerSettings.resizableWindow = previousResizable;
                    PlayerSettings.defaultIsNativeResolution = previousNativeResolution;
                    PlayerSettings.defaultScreenWidth = width;
                    PlayerSettings.defaultScreenHeight = height;
                    PlayerSettings.fullScreenMode = screenMode;
                    EditorUserBuildSettings.development = previousDevelopment;
                });
                Restore(() => EditorSceneManager.RestoreSceneManagerSetup(scenes));
                Restore(() => { if (AssetDatabase.IsValidFolder(StagingRoot)) AssetDatabase.DeleteAsset(StagingRoot); });
                Restore(AssetDatabase.SaveAssets);
                receipt.originalSettingsRestored = restored;
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                receipt.completedUtc = DateTime.UtcNow.ToString("O");
                Save();
            }
        }
    }
}
