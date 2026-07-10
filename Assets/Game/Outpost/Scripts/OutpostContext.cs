using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Localization;
using Game.Framework.Storage;
using Game.Framework.Systems;
using Game.Outpost.Save;
using OutpostCfg;

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

            // 本地存档（历史战绩）：跨局常驻服务与数据都挂根 Context——战斗子 Context 撤了它们还在。
            // StorageUtility 默认走 persistentDataPath/storage + JSON；RegisterOwned 随根 Context 释放 provider（§26 推荐）。
            builder.RegisterOwned(new StorageUtility(), typeof(IStorageUtility));
            builder.RegisterValue(new PlayerRecordModel(), typeof(PlayerRecordModel));

            // 音频：全局播放编排（BGM 单通道 + 池化音效 + 分组音量）。RegisterOwned：随根 Context 销毁全停（§27 推荐）。
            // 音量初始全 1，启动时 LoadSettingsCommand 从设置存档回灌（持久化归业务，ADR-0022）。
            builder.RegisterOwned(new AudioUtility(), typeof(IAudioUtility));

            // 本地化：文本源 = 业务 adapter 包自己的 Luban 表（TbL10N）。文本源要吃配置表服务（场景组件 Awake 注册），
            // 用 RegisterFactory 让容器解决依赖顺序——首次解析（进标题开窗绑定文本）时配置服务早已注册。
            // 初始语言按系统推断，玩家选过语言则启动时 LoadSettingsCommand 回灌；中文是源语言，作 fallback。
            builder.RegisterFactory(
                c => new LocalizationUtility(
                    new OutpostTextSource((IConfigUtility<Tables>)c.Resolve(typeof(IConfigUtility<Tables>))),
                    initialLocale: OutpostLocales.FromSystem(),
                    fallbackLocale: OutpostLocales.ChineseSimplified),
                typeof(ILocalizationUtility));
        }
    }
}
