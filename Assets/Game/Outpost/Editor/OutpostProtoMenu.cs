using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Outpost.Editor
{
    /// <summary>
    /// Outpost 网络协议的 protoc 代码生成菜单（Luban 姿势：编辑器菜单调 CLI 工具 + 刷新资产）。
    /// 把 <c>Proto~/outpost_net.proto</c> 经 <c>Tools/Protoc/&lt;平台&gt;/protoc</c> 生成 C# 到 <c>Scripts/Net/Gen/</c>——
    /// 生成类是 Google.Protobuf 的 <c>IMessage</c> partial，配 <see cref="Game.Outpost.Net"/> 的 IEvent partial 与
    /// GoogleProtobufNetworkSerializer 适配器接进框架的 <c>IWebSocketEnvelopeSerializer</c> 接缝。
    /// </summary>
    /// <remarks>
    /// 只生成 .proto → C#（不做 Luban 那样的数据编译）；protoc 是自带二进制（`Tools/Protoc/` 随仓，同 Luban CLI 打包姿势），
    /// 当前随仓只带 Windows x64——mac/linux 开发机把对应 protoc 放进 `Tools/Protoc/<平台>/` 即可（路径见 <see cref="ProtocPath"/>）。
    /// 生成代码目录（`Scripts/Net/Gen/`）被本菜单接管，勿手放文件。
    /// </remarks>
    public static class OutpostProtoMenu
    {
        private const string ProjectRoot = ""; // Application.dataPath 去掉尾部 "Assets" 即项目根
        private const string ProtoDir = "Assets/Game/Outpost/Proto~";
        private const string ProtoFile = "outpost_net.proto";
        private const string OutDir = "Assets/Game/Outpost/Scripts/Net/Gen";

        [MenuItem("SSFramework/Protobuf/生成 Outpost 协议代码", priority = 1)]
        private static void Generate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Protobuf 生成", "Play 模式下不能生成（会改代码触发重编译），请先停止运行。", "好");
                return;
            }

            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string protoc = ProtocPath;
            if (!File.Exists(protoc))
            {
                EditorUtility.DisplayDialog("Protobuf 生成失败",
                    $"找不到 protoc：{protoc}\n把对应平台的 protoc 放进 Tools/Protoc/<平台>/ 后重试。", "好");
                return;
            }

            string outAbs = Path.Combine(root, OutDir);
            Directory.CreateDirectory(outAbs);

            var psi = new ProcessStartInfo(protoc)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"--proto_path={ProtoDir}");
            psi.ArgumentList.Add($"--csharp_out={OutDir}");
            psi.ArgumentList.Add(ProtoFile);

            string stdout, stderr; int exit;
            using (var p = Process.Start(psi)!)
            {
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                exit = p.ExitCode;
            }

            if (exit != 0)
            {
                Debug.LogError($"[OutpostProto] protoc 失败（exit {exit}）：\n{stderr}");
                EditorUtility.DisplayDialog("Protobuf 生成失败", $"protoc 退出码 {exit}：\n{stderr}", "好");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[OutpostProto] 生成完成 → {OutDir}（源 {ProtoDir}/{ProtoFile}）。{stdout}");
            EditorUtility.DisplayDialog("Protobuf 生成完成",
                $"已生成 C# → {OutDir}\n源：{ProtoDir}/{ProtoFile}", "好");
        }

        /// <summary>当前平台的 protoc 可执行文件路径（随仓 Tools/Protoc/&lt;平台&gt;/）。</summary>
        private static string ProtocPath
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)!.FullName;
#if UNITY_EDITOR_WIN
                return Path.Combine(root, "Tools/Protoc/windows_x64/protoc.exe");
#elif UNITY_EDITOR_OSX
                return Path.Combine(root, "Tools/Protoc/macosx_x64/protoc");
#else
                return Path.Combine(root, "Tools/Protoc/linux_x64/protoc");
#endif
            }
        }
    }
}
