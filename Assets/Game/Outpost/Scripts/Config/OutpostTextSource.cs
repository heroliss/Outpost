using Game.Framework;
using Game.Framework.Localization;
using OutpostCfg;

namespace Game.Outpost
{
    /// <summary>
    /// 本地化文本源：~10 行包自己的 Luban 表 <c>TbL10N</c>（一行一 key、一列一语言——加语言 = 加一列）。
    /// 这是 <see cref="ILocalizedTextSource"/> 接缝的业务典型实现（guide §21）：查不到只返回 false，
    /// 回退链（当前语言 → fallback → 裸 key 上屏）与缺失警告由 <c>LocalizationUtility</c> 统一处理。
    /// 配置表异步加载，就绪前 <c>Tables</c> 为 null → 一律 false → 裸 key 上屏（可见的「加载中」，启动瞬间即过）。
    /// </summary>
    public sealed class OutpostTextSource : ILocalizedTextSource
    {
        private readonly IConfigUtility<Tables> _config;

        public OutpostTextSource(IConfigUtility<Tables> config) => _config = config;

        public bool TryGet(string locale, string key, out string text)
        {
            text = null;
            var row = _config.Tables?.TbL10N.GetOrDefault(key);
            if (row == null) return false;
            text = locale switch
            {
                OutpostLocales.ChineseSimplified => row.ZhCn,
                OutpostLocales.English => row.En,
                _ => null,
            };
            return !string.IsNullOrEmpty(text);
        }
    }
}
