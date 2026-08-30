using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.Systems;
using Game.Outpost.Flow;

namespace Game.Outpost.Systems
{
    /// <summary>
    /// 启动系统：场景就绪后把流程推入 <see cref="BootState"/>——游戏的「第一脚油门」。
    /// 挂在根 Context 子节点；用 Start 而非 Awake，等根 Context 与所有服务注册完成（AGENTS #3）。
    /// </summary>
    public sealed class OutpostBootSystem : MonoSystemBase
    {
        private void Start() => FlowNav.Request(this.GetSystem<IGameFlow>(), new BootState());
    }
}
