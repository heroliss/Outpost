using Game.Framework.Model;
using ObservableCollections;
using R3;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 波间三选一升级的一个候选项——已从配置表（<c>OutpostCfg.Upgrade</c>）翻译成纯展示数据，是 <c>Bag.BindList</c> 的项类型。
    /// <see cref="Id"/> 回指配置主键，玩家选定后经它取回配置行、映射成 <c>PlayerModifier</c> 应用到模拟。
    /// </summary>
    public readonly struct UpgradeOption
    {
        public readonly int Id;
        public readonly string Title;
        public readonly string Desc;

        public UpgradeOption(int id, string title, string desc)
        {
            Id = id;
            Title = title;
            Desc = desc;
        }
    }

    /// <summary>
    /// 波间升级抉择的展示状态：当前三选一候选（<see cref="Choices"/>，会整批换、用 <see cref="ObservableList{T}"/> 让 UI 增量绑定）
    /// + 是否正在等待玩家抉择（<see cref="IsChoosing"/>，控制升级面板显隐）。由 <see cref="BattleDirectorSystem"/> 单向写入
    /// （波清空时填充 / 玩家选定后清空），升级面板 View 只读订阅——读写分离同 <see cref="BattleModel"/>。
    /// </summary>
    public sealed class UpgradeModel : IModel
    {
        public readonly ObservableList<UpgradeOption> Choices = new();
        public readonly RP<bool> IsChoosing = new(false);

        /// <summary>
        /// 托管模式开关：开启后波间三选一由 <see cref="BattleDirectorSystem"/> 按优先级自动选定，玩家进入纯观战；可随时开关。
        /// 同样由导演单向写入（经开关命令中转），HUD 托管按钮只读订阅回显——读写分离同上。
        /// </summary>
        public readonly RP<bool> AutoManaged = new(false);
    }
}
