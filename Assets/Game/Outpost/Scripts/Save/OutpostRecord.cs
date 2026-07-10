using System;

namespace Game.Outpost.Save
{
    /// <summary>
    /// 玩家跨局历史战绩存档——整存整取的持久化数据（§26：一个 <c>[Serializable]</c> 类、只序列化<b>字段</b>、无属性）。
    /// 经 <see cref="Game.Framework.Storage.IStorageUtility"/> 以 <see cref="StorageKeys.Record"/> 为 key 落盘。
    /// </summary>
    /// <remarks>
    /// <b>版本迁移</b>：<see cref="Version"/> 是预留的迁移锚点——本档结构演进（改字段语义 / 拆分）时 Load 后按版本
    /// 链式迁移再回写（§26 姿势）。当前仅一版且 JsonUtility 对新增字段天然宽容（旧档缺字段取默认 0），故暂无迁移逻辑。
    /// </remarks>
    [Serializable]
    public class OutpostRecord
    {
        /// <summary>存档结构版本（迁移锚点）。当前恒为 1。</summary>
        public int Version = 1;

        /// <summary>历史最高得分。</summary>
        public int BestScore;

        /// <summary>历史最高抵达波次。</summary>
        public int BestWave;

        /// <summary>累计对局数（无限模式每局都以失守收场，此值即"打过多少局"）。</summary>
        public int Runs;

        /// <summary>排行榜署名（首次启动自动生成，如 <c>OP-3F7A</c>）。空 = 旧档 / 新档尚未生成，启动载入时补。</summary>
        public string Callsign = "";
    }

    /// <summary>
    /// Outpost 存档 key 常量。key 是持久契约（落成文件名）：显式传、集中管理、<b>只增不改</b>——改 key 等同丢弃旧存档
    /// （见 §26 / <see cref="Game.Framework.Storage.StorageKey"/> 字符集规则）。
    /// </summary>
    public static class StorageKeys
    {
        /// <summary>玩家跨局历史战绩（单槽位；本切片不做多存档槽，<c>/</c> 分段留给将来扩展如 <c>outpost/slot1</c>）。</summary>
        public const string Record = "outpost/record";

        /// <summary>玩家设置（音量 / 语言，<see cref="OutpostSettings"/>）。</summary>
        public const string Settings = "outpost/settings";
    }
}
