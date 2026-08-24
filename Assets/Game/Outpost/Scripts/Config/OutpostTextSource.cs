using Game.Framework;
using Game.Framework.Localization;
using OutpostCfg;
using R3;

namespace Game.Outpost
{
    /// <summary>
    /// 本地化文本源：~10 行包自己的 Luban 表 <c>TbL10N</c>（一行一 key、一列一语言——加语言 = 加一列）。
    /// 这是 <see cref="ILocalizedTextSource"/> Seam 的业务典型 Adapter（guide §21）：配置未 Ready 时报告
    /// <see cref="LocalizedTextLookupStatus.Unavailable"/>，Ready 后才能确认 Found / Missing；状态变化同时发失效信号，
    /// 所以标题页无需知道配置加载时序，既有文本绑定会在同一语言下自动重取。
    /// </summary>
    public sealed class OutpostTextSource : ILocalizedTextSource
    {
        private readonly IConfigUtility<Tables> _config;

        public OutpostTextSource(IConfigUtility<Tables> config) => _config = config;

        public Observable<Unit> Invalidated => _config.State.Select(_ => Unit.Default);

        public LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
        {
            text = null;
            if (_config.State.CurrentValue != ConfigInitState.Ready || _config.Tables == null)
                return LocalizedTextLookupStatus.Unavailable;

            var row = _config.Tables.TbL10N.GetOrDefault(key);
            if (row == null) return LocalizedTextLookupStatus.Missing;
            text = locale switch
            {
                OutpostLocales.ChineseSimplified => row.ZhCn,
                OutpostLocales.English => row.En,
                _ => null,
            };
            return string.IsNullOrEmpty(text)
                ? LocalizedTextLookupStatus.Missing
                : LocalizedTextLookupStatus.Found;
        }
    }
}
