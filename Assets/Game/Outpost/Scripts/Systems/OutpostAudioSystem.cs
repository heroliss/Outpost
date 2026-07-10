using Cysharp.Threading.Tasks;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.Systems;
using Game.Outpost.Flow;
using UnityEngine;

namespace Game.Outpost.Systems
{
    /// <summary>
    /// 全局 BGM 导演：订 <see cref="FlowChangedEvent"/> 一个事件、按宏观阶段切曲，不侵入各 FlowState（§28 转场表现姿势）。
    /// 标题 / 结算共用氛围垫，战斗切紧张曲——<c>PlayMusic</c> 单通道自动交叉淡变，同曲幂等（结算回标题不重头）。
    /// 挂根 Context 子节点、跨局常驻；战斗内的一次性音效归 <c>BattleDirectorSystem</c> 的事件翻译层（随战斗场景生灭）。
    /// </summary>
    public sealed class OutpostAudioSystem : MonoSystemBase
    {
        // 曲目基础音量（响度对齐用，乘在 Music 组音量之上）：合成垫底噪偏满，压一点留出音效空间。
        private const float TitleVolume = 0.8f;
        private const float BattleVolume = 0.9f;

        private AudioClip _bgmTitle;
        private AudioClip _bgmBattle;

        private void Start() => InitAsync().Forget();

        private async UniTaskVoid InitAsync()
        {
            // clip 经资源系统加载后传入播放（加载与播放的生命周期分开管，§27）；句柄进根 Bag 常驻整局游戏。
            _bgmTitle = await Bag.Load<AudioClip>("bgm_title");
            _bgmBattle = await Bag.Load<AudioClip>("bgm_battle");

            // 先订事件再补一次当前状态：clip 加载的几帧里 Boot→Title 的切换事件可能已经错过。
            Bag.Subscribe<FlowChangedEvent>(e => OnFlowChanged(e.To));
            OnFlowChanged(this.GetUtility<IGameFlow>().Current);
        }

        private void OnFlowChanged(FlowState state)
        {
            var audio = this.GetUtility<IAudioUtility>();
            switch (state)
            {
                case BattleState:
                    audio.PlayMusic(_bgmBattle, fadeSeconds: 0.8f, volume: BattleVolume);
                    break;
                case TitleState or ResultState:
                    audio.PlayMusic(_bgmTitle, fadeSeconds: 1.2f, volume: TitleVolume);
                    break;
                // Boot 等其余阶段不动音乐通道（保持当前曲直到下个明确落点）。
            }
        }
    }
}
