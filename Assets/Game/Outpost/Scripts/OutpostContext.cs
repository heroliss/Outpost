using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Systems;

namespace Game.Outpost
{
    /// <summary>
    /// Outpost 的全局根上下文——整个游戏唯一的 Context 根。
    /// 继承 <see cref="MonoGlobalContext"/>：自动设 <c>GameContext.Main</c>、DontDestroyOnLoad、重复实例检测。
    /// </summary>
    /// <remarks>
    /// 只有「整局游戏都活着」的服务才注册在这里；阶段私有的东西（战斗模拟、排行连接等）
    /// 进各 <c>FlowState</c> 的子 Context，随阶段退出整棵撤（见 <c>Scripts/Flow/</c>）。
    /// Inspector 可配的服务（UI 入口 / 对象池 / 资源三件套）走场景子节点的 Mono 组件路径，不在这里重复注册。
    /// </remarks>
    public sealed class OutpostContext : MonoGlobalContext
    {
        protected override void InstallBindings(ContainerBuilder builder)
        {
            // 命令分发：LoggingCommandSystem 装饰默认实现——开发期「SSFramework/诊断/框架诊断面板」可看命令流水。
            builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem));

            // 游戏宏观流程（启动/标题/战斗/结算）。RegisterOwned：随本 Context 销毁，连同当前状态子 Context 一并撤。
            builder.RegisterOwned(new GameFlow(), typeof(IGameFlow));
        }
    }
}
