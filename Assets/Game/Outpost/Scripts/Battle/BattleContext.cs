using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Pool;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 战斗子场景的根 Context。<see cref="OutpostBattle"/> 场景以此为根节点：作为附加加载的场景，它没有 Transform
    /// 父链连到主场景，靠 <c>Inherit From Global</c> 回退到 <see cref="Game.Framework.Context.GameContext.Main"/>
    /// （根 OutpostContext）解析 IConfigUtility / IGameFlow 等全局服务；战斗私有的 <see cref="BattleModel"/> 与
    /// 敌人/飘字对象池本地注册，随场景卸载整棵撤（下一局全新一份，无跨局残留）。
    /// </summary>
    public sealed class BattleContext : MonoGameContextBase
    {
        protected override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterModel(new BattleModel());
            builder.RegisterModel(new UpgradeModel());
            builder.RegisterOwnedUtility(new PoolUtility());
        }
    }
}
