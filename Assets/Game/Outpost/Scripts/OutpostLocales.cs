using UnityEngine;

namespace Game.Outpost
{
    /// <summary>
    /// 语言常量与系统语言映射。locale code 是开放字符串（框架不预置语言列表），
    /// 业务用常量管理——与音频组 / 存储 key 同一「常量管理字符串契约」姿势（guide §21）。
    /// </summary>
    public static class OutpostLocales
    {
        /// <summary>简体中文（源语言：全部文案的完整版本，也是缺英文时的回退目标）。</summary>
        public const string ChineseSimplified = "zh-CN";

        /// <summary>English。</summary>
        public const string English = "en";

        /// <summary>
        /// 首次启动（无设置存档）时按操作系统语言推断初始语言：中文系统进中文，其余进英文。
        /// 玩家在设置窗切换后以存档为准（启动回灌，见 <c>LoadSettingsCommand</c>）。
        /// </summary>
        public static string FromSystem() => Application.systemLanguage switch
        {
            SystemLanguage.Chinese or SystemLanguage.ChineseSimplified or SystemLanguage.ChineseTraditional
                => ChineseSimplified,
            _ => English,
        };
    }
}
