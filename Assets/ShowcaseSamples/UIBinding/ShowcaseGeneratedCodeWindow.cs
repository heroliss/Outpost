using System.IO;
using UnityEditor;
using UnityEngine;

namespace Showcase.SelfMade.UI
{
    /// <summary>展示磁盘上的真实生成文件；输入框编辑不会回写源文件。</summary>
    public sealed class ShowcaseGeneratedCodeWindow : EditorWindow
    {
        private const string Source =
            "Assets/ShowcaseSamples/UIBinding/Generated/ShowcaseBindingWindow.nodes.g.cs";
        private Vector2 _scroll;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("SSFramework · 实际生成文件", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Source, EditorStyles.miniLabel);
            EditorGUILayout.Space(6);
            var style = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = 14,
                wordWrap = false,
            };
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            string text = File.Exists(Source) ? File.ReadAllText(Source) : "文件尚未生成";
            EditorGUILayout.TextArea(text, style,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndScrollView();
        }
    }
}
