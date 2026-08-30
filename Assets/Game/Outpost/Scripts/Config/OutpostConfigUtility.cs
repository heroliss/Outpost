using System;
using System.Collections.Generic;
using Game.Framework;
using Luban;
using OutpostCfg;

namespace Game.Outpost.Config
{
    /// <summary>
    /// Outpost 的配置表服务：用生成的表根 <see cref="Tables"/> 闭合框架自加载基类。挂在根 OutpostContext——
    /// 配置是 Context 内稳定的只读数据，各层（含战斗子场景经父链回退）通常经
    /// <c>GetConfig&lt;Tables&gt;()</c> / <c>EnsureConfig&lt;Tables&gt;(token)</c> 读取；它不是进程级静态全局。
    /// 这是运行时唯一接触 Luban 类型的地方（数值到 <see cref="Sim.BattleSetup"/> 的翻译在 <see cref="BattleSetupFactory"/>）。
    /// </summary>
    public sealed class OutpostConfigUtility : MonoConfigUtilityBase<Tables>
    {
        protected override IReadOnlyList<string> TableFiles => LubanTableManifest.Files;

        protected override Tables CreateTables(Func<string, byte[]> getBytes)
            => new(file => new ByteBuf(getBytes(file)));
    }
}
