using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Outpost.Save;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 启动阶段：载入历史存档与设置后进标题。配置表可以继续异步加载：本地化文本源会把 Unavailable → Ready
    /// 作为独立失效信号推给既有绑定；真正依赖战斗配置的 BattleDirectorSystem 在自己的初始化入口等待。
    /// </summary>
    public sealed class BootState : FlowState
    {
        public override string ToString() => "启动";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            // 历史战绩载入 Model，标题页开时即有值可展示（命令内已兜住载入失败，按新档继续）；
            // 再回灌玩家设置（音量进音频服务、语言进本地化服务）——标题页开窗时绑定即取到正确语言。
            await Context.ExecuteCommandAsync(new LoadPlayerRecordCommand(), ct);
            await Context.ExecuteCommandAsync(new LoadSettingsCommand(), ct);

            // OnEnter 里转向别处：调 GoTo 后直接 return、不要 await 它（互等死锁，guide §20）。
            FlowNav.Request(Context.GetSystem<IGameFlow>(), new TitleState());
        }
    }
}
