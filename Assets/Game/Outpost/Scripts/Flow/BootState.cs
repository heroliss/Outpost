using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Outpost.Save;
using OutpostCfg;

namespace Game.Outpost.Flow
{
    /// <summary>
    /// 启动阶段：基础设施就绪后进标题。
    /// M0 窗口全部纯代码搭建、无资源可等，直接流转；M1 起在这里 await 资源包初始化与配置表就绪；M3 起先载入历史存档。
    /// </summary>
    public sealed class BootState : FlowState
    {
        public override string ToString() => "启动";

        protected override async UniTask OnEnter(CancellationToken ct)
        {
            // 配置表就绪是标题页的硬前置：UI 文案的本地化源是 TbL10N（配置表），而 BindLocalizedText 的刷新
            // 信号只有 Locale——绑定发生在配置就绪前会裸 key 上屏、且配置后到不会触发重绑（ADR-0024 把
            // 「文本源就绪时序」留给业务，这里就是业务的答案：进标题前等表加载完，Failed 也放行——裸 key
            // 上屏是可见的缺失报告，好过卡死在启动）。
            var config = Context.GetUtility<IConfigUtility<Tables>>();
            await UniTask.WaitUntil(
                () => config.State.CurrentValue is ConfigInitState.Ready or ConfigInitState.Failed,
                cancellationToken: ct);

            // 历史战绩载入 Model，标题页开时即有值可展示（命令内已兜住载入失败，按新档继续）；
            // 再回灌玩家设置（音量进音频服务、语言进本地化服务）——标题页开窗时绑定即取到正确语言。
            await Context.ExecuteCommandAsync(new LoadPlayerRecordCommand(), ct);
            await Context.ExecuteCommandAsync(new LoadSettingsCommand(), ct);

            // OnEnter 里转向别处：调 GoTo 后直接 return、不要 await 它（互等死锁，guide §20）。
            FlowNav.Go(Context.GetUtility<IGameFlow>(), new TitleState());
        }
    }
}
