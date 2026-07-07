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

        /// <summary>居中卡片（模态弹窗用）：overlay 透传点击、卡片本身拦截；返回卡片内容容器。</summary>
        public static VisualElement Card(VisualElement root, string title)
        {
            var overlay = new VisualElement();
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;
            overlay.pickingMode = PickingMode.Ignore;
            root.Add(overlay);

            var card = new VisualElement();
            card.style.maxWidth = 520;
            card.style.paddingTop = 18; card.style.paddingBottom = 18; card.style.paddingLeft = 24; card.style.paddingRight = 24;
            card.style.backgroundColor = new Color(0.10f, 0.13f, 0.17f, 0.98f);
            float r = 12;
            card.style.borderTopLeftRadius = r; card.style.borderTopRightRadius = r;
            card.style.borderBottomLeftRadius = r; card.style.borderBottomRightRadius = r;
            float w = 2;
            card.style.borderTopWidth = w; card.style.borderBottomWidth = w; card.style.borderLeftWidth = w; card.style.borderRightWidth = w;
            card.style.borderTopColor = Accent; card.style.borderBottomColor = Accent;
            card.style.borderLeftColor = Accent; card.style.borderRightColor = Accent;
            overlay.Add(card);

            var head = new Label(title) { enableRichText = false };
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.fontSize = 20;
            head.style.color = Accent;
            head.style.marginBottom = 14;
            card.Add(head);
            return card;
        }

        /// <summary>一条"看点"：橙色小标题 + 灰色说明（一句话把游戏现象接到框架能力上）。</summary>
        public static void Bullet(VisualElement parent, string topic, string detail)
        {
            var row = new VisualElement();
            row.style.marginBottom = 9;
            parent.Add(row);

            var t = new Label("▸ " + topic) { enableRichText = false };
            t.style.color = Accent;
            t.style.fontSize = 13;
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(t);

            var d = new Label(detail) { enableRichText = false };
            d.style.color = new Color(0.72f, 0.77f, 0.85f);
            d.style.fontSize = 12;
            d.style.marginLeft = 16;
            d.style.whiteSpace = WhiteSpace.Normal;
            row.Add(d);
        }

        /// <summary>脚注小灰字（如指向文档路径）。</summary>
        public static Label Hint(VisualElement parent, string text)
        {
            var l = new Label(text) { enableRichText = false };
            l.style.color = new Color(0.55f, 0.60f, 0.68f);
            l.style.fontSize = 11;
            l.style.marginTop = 6;
            l.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(l);
            return l;
        }
    }
}
