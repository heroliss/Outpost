using System;

namespace Game.Outpost.Save
{
    /// <summary>
    /// 玩家设置存档（音量三档 + 语言）。经 <see cref="Game.Framework.Storage.IStorageUtility"/> 以
    /// <see cref="StorageKeys.Settings"/> 为 key 整存整取（§26：不做散装 KV）。
    /// </summary>
    /// <remarks>
    /// 音量与语言的<b>运行时真源分别是 <c>IAudioUtility</c> 与 <c>ILocalizationUtility</c> 自身状态</b>——
    /// 本类只是它们的落盘快照，不做第二份内存状态（设置窗直连两个 Utility，关窗时收集当前值保存）。
    /// 框架刻意把「音量 / 语言选择持久化」留给业务（ADR-0022 / 0024），这里就是那个业务落点。
    /// </remarks>
    [Serializable]
    public class OutpostSettings
    {
        /// <summary>结构版本（迁移锚点，同 <see cref="OutpostRecord.Version"/> 姿势）。</summary>
        public int Version = 1;

        /// <summary>主音量 [0,1]，乘在所有组之上。</summary>
        public float MasterVolume = 1f;

        /// <summary>音乐组音量 [0,1]（<c>AudioGroups.Music</c>）。</summary>
        public float MusicVolume = 1f;

        /// <summary>音效组音量 [0,1]（<c>AudioGroups.Sfx</c>）。</summary>
        public float SfxVolume = 1f;

        /// <summary>语言 code（<see cref="OutpostLocales"/> 常量）。空 = 未选过，按系统语言推断。</summary>
        public string Locale = "";
    }
}
