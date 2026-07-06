using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Outpost.Windows
{
    /// <summary>
    /// Outpost 窗口的最小代码搭建件（全屏页 / 标签 / 按钮）。
    /// M0-M2 阶段窗口纯代码搭建（UIWindow.Asset 留空），后续视需要再换 UXML + USS。
    /// </summary>
    internal static class OutpostUiKit
    {
        public static readonly Color Accent = new(1f, 0.62f, 0.25f); // 哨站主题色：警戒橙

        /// <summary>全屏页：铺满、居中内容、盖住下层。返回内容容器。</summary>
        public static VisualElement FullPage(VisualElement root, string title, Color bg)
        {
            var page = new VisualElement();
            page.style.position = Position.Absolute;
            page.style.left = 0; page.style.top = 0; page.style.right = 0; page.style.bottom = 0;
            page.style.backgroundColor = bg;
            page.style.justifyContent = Justify.Center;
            page.style.alignItems = Align.Center;
            root.Add(page);

            var head = new Label(title) { enableRichText = false };
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.fontSize = 34;
            head.style.color = Accent;
            head.style.letterSpacing = 4;
            head.style.marginBottom = 18;
            page.Add(head);
            return page;
        }

        public static Label Lbl(VisualElement parent, string text)
        {
            var l = new Label(text) { enableRichText = false };
            l.style.color = new Color(0.85f, 0.88f, 0.95f);
            l.style.fontSize = 14;
            l.style.marginBottom = 10;
            parent.Add(l);
            return l;
        }

        public static Button Btn(VisualElement parent, string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.marginTop = 8;
            b.style.minWidth = 220;
            b.style.height = 36;
            b.style.fontSize = 14;
            parent.Add(b);
            return b;
        }
    }
}
