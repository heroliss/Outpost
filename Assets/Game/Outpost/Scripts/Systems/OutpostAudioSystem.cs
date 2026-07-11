using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Flow;
using Game.Framework.Systems;
using Game.Main;
using Game.Outpost.Flow;
using UnityEngine;

namespace Game.Outpost.Systems
{
    /// <summary>
    /// 全局 BGM 导演：订 <see cref="FlowChangedEvent"/> 一个事件、按宏观阶段切曲，不侵入各 FlowState（§28 转场表现姿势）。
    /// 标题 / 结算共用氛围垫，战斗切紧张曲——<c>PlayMusic</c> 单通道自动交叉淡变，同曲幂等（结算回标题不重头）。
    /// 挂根 Context 子节点、跨局常驻；战斗内的一次性音效归 <c>BattleDirectorSystem</c> 的事件翻译层（随战斗场景生灭）。
    /// 战斗曲有扩展包变体（「增援电台」，OutpostExpansionPackage）：包就绪即懒加载替换，未安装 / 未就绪回落默认曲。
    /// </summary>
    public sealed class OutpostAudioSystem : MonoSystemBase
    {
        // 曲目基础音量（响度对齐用，乘在 Music 组音量之上）：合成垫底噪偏满，压一点留出音效空间。
        private const float TitleVolume = 0.8f;
        private const float BattleVolume = 0.9f;

        private AudioClip _bgmTitle;
        private AudioClip _bgmBattle;
        private AudioClip _bgmBattleAlt; // 扩展包变体：首次战斗时按包状态懒加载，载到后整局复用

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
                    PlayBattleMusic(audio).Forget();
                    break;
                case TitleState or ResultState:
                    audio.PlayMusic(_bgmTitle, fadeSeconds: 1.2f, volume: TitleVolume);
                    break;
                // Boot 等其余阶段不动音乐通道（保持当前曲直到下个明确落点）。
            }
        }

        // 战斗曲选择：扩展包 Ready 且变体能载到 → 播变体，否则默认曲。跨包加载失败返回 null（包 Ready 后
        // 资源级问题不抛，§13 失败语义），天然容错「初始化了但内容没下完」；下载完成后的下一场战斗自动接上。
        private async UniTaskVoid PlayBattleMusic(IAudioUtility audio)
        {
            if (_bgmBattleAlt == null
                && this.GetUtility<IAssetUtility>()
                       .GetInitState(AssetPackages.OutpostExpansionPackage).CurrentValue == AssetInitState.Ready)
            {
                _bgmBattleAlt = await Bag.Load<AudioClip>(AssetPackages.OutpostExpansionPackage, "bgm_battle_alt");
                // 异步间隙里阶段可能已切走（秒退结算）：不再抢占音乐通道，交给新阶段的落点。
                if (this.GetUtility<IGameFlow>().Current is not BattleState) return;
            }

            var clip = _bgmBattleAlt != null ? _bgmBattleAlt : _bgmBattle;
            audio.PlayMusic(clip, fadeSeconds: 0.8f, volume: BattleVolume);
        }
    }
}
