using System;
using System.Collections.Generic;
using Game.Framework;
using Luban;
using OutpostCfg;

namespace Game.Outpost.Config
{
    /// <summary>
    /// Outpost 的配置表服务：用生成的表根 <see cref="Tables"/> 闭合框架自加载基类。挂在根 OutpostContext——
    /// 配置是全局静态只读数据，各层（含战斗子场景经父链回退）都 <c>GetUtility&lt;IConfigUtility&lt;Tables&gt;&gt;()</c> 直读。
    /// 这是运行时唯一接触 Luban 类型的地方（数值到 <see cref="Sim.BattleSetup"/> 的翻译在 <see cref="BattleSetupFactory"/>）。
    /// </summary>
    public sealed class OutpostConfigUtility : MonoConfigUtilityBase<Tables>
    {
        protected override IReadOnlyList<string> TableFiles => LubanTableManifest.Files;

        protected override Tables CreateTables(Func<string, byte[]> getBytes)
            => new(file => new ByteBuf(getBytes(file)));
    }
}
