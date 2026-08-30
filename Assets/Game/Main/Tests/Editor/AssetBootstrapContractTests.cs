using System;
using System.IO;
using Game.Framework;
using Game.Framework.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Main.Tests
{
    /// <summary>
    /// 当前业务工程的资源 Profile、入库生成物与首场景入口组合契约。
    /// 这些断言有意留在 Game.Main，而不是让可抽取的 Framework 测试反向硬编码业务目录与包名。
    /// </summary>
    public sealed class AssetBootstrapContractTests
    {
        [Test]
        public void CheckedInGeneratedConstants_MatchEffectiveProfile()
        {
            Assert.That(FrameworkAssetBuildProfile.TryResolve(out var profile), Is.True,
                "本工程应有显式入库的资源构建 Profile。");

            var freshness = AssetPackageConstantsGenerator.ValidateFreshness(profile);

            Assert.That(freshness.ok, Is.True, freshness.message);
        }

        [Test]
        public void GameEntryBootstrap_UsesGeneratedOffsetAndWebGlMode()
        {
            string source = File.ReadAllText(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                "Assets/Game/Main/GameEntry.cs"));
            const string assignment = "FileOffset = AssetPackages.AssetBundleFileOffset";

            Assert.That(source.Split(new[] { assignment }, StringSplitOptions.None).Length - 1, Is.EqualTo(3),
                "EditorSimulate、WebGL Web 与其它 Player Host 三条引导路径都必须使用同一生成 offset。");
            Assert.That(source, Does.Contain("#elif UNITY_WEBGL"));
            Assert.That(source, Does.Contain("AssetPlayMode.Web"));
            Assert.That(source, Does.Contain("RawFile CodePackage"),
                "调用点应明确普通 AssetBundle 格式不能反向污染代码包引导契约。");

            string bootSource = File.ReadAllText(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                "Assets/Game/Framework/Boot/HotUpdateLauncher.cs"));
            Assert.That(bootSource, Does.Contain("return BootPlayMode.Web;"),
                "WebGL 的 AOT Boot 也必须先切到 Web 文件系统，不能只修热更入口后的业务资源栈。");
            Assert.That(bootSource, Does.Contain("new WebPlayModeOptions"));
        }

        [Test]
        public void RawFileOnlyBuild_IsRejectedByOwningModuleBoundary_NotOrdinaryOffsetPreflight()
        {
            var profile = ScriptableObject.CreateInstance<FrameworkAssetBuildProfile>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("_fileOffset").ulongValue = AssetProviderConfig.MaxBuiltInFileOffset + 1UL;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var result = FrameworkAssetBuilder.Build(profile, new[] { "CodePackage" }, "raw-boundary-test");

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("RawFile"));
                Assert.That(result.message, Does.Not.Contain("文件头偏移"),
                    "RawFile-only 请求不消费普通 AssetBundle offset，应先命中所属构建 Module 边界。");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
    }
}
