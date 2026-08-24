using System;
using Game.Framework;
using Game.Framework.Audio;
using Game.Framework.Context;
using Game.Framework.Flow;
using Game.Framework.Localization;
using Game.Framework.Network;
using Game.Framework.Storage;
using Game.Framework.Systems;
using Game.Outpost.Net;
using Game.Outpost.Save;
using OutpostCfg;
using UnityEngine;

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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Header("排行榜对端（仅 Editor / Development Build 生效）")]
        [Tooltip("独立真后端（Server~/OutpostServer）的 HTTP 基地址，如 http://127.0.0.1:5080。\n留空 = 起进程内 dev server（零配置默认）。")]
        [SerializeField] private string _remoteHttpBaseUrl = "";

        [Tooltip("独立真后端的 WebSocket 地址，如 ws://127.0.0.1:5080/ws。\n与 HTTP 基地址要么都填、要么都留空。")]
        [SerializeField] private string _remoteWsUrl = "";
#endif

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

            // 战斗偏好（模拟后端选择，ADR-0030 的双后端切换入口）：跨局常驻挂根，设置窗写、导演每局开局采样。
            builder.RegisterValue(new Battle.BattlePrefsModel(), typeof(Battle.BattlePrefsModel));

            // 音频：全局播放编排（BGM 单通道 + 池化音效 + 分组音量）。RegisterOwned：随根 Context 销毁全停（§27 推荐）。
            // 音量初始全 1，启动时 LoadSettingsCommand 从设置存档回灌（持久化归业务，ADR-0022）。
            builder.RegisterOwned(new AudioUtility(), typeof(IAudioUtility));

            // 本地化：文本源 = 业务 adapter 包自己的 Luban 表（TbL10N）。文本源要吃配置表服务（场景组件 Awake 注册），
            // 用 RegisterOwnedFactory 让容器解决依赖顺序——首次解析（进标题开窗绑定文本）时配置服务早已注册；
            // LocalizationUtility 实现 IDisposable，owned factory 保证它仍随根 Context 释放，不因懒构造漏掉所有权。
            // 初始语言按系统推断，玩家选过语言则启动时 LoadSettingsCommand 回灌；中文是源语言，作 fallback。
            builder.RegisterOwnedFactory(
                c => new LocalizationUtility(
                    new OutpostTextSource((IConfigUtility<Tables>)c.Resolve(typeof(IConfigUtility<Tables>))),
                    initialLocale: OutpostLocales.FromSystem(),
                    fallbackLocale: OutpostLocales.ChineseSimplified),
                typeof(ILocalizationUtility));

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 网络排行（仅 dev 环境）：对端二选一——Inspector 留空 = 进程内 dev server（构造即监听、随本
            // Context Dispose 停）；填了地址 = 直连独立真后端（Server~/OutpostServer），不起 dev server。
            // 两侧共享同一份 .proto 契约、wire 兼容，客户端栈零差异——「换端只改地址」的接缝就落在这里。
            // HTTP / WS 客户端栈全程 Protobuf（两个 utility 各建一份序列化器）。HTTP 无状态挂根全局共享；
            // WS 单条长连接也挂根——新纪录广播要跨阶段可见（连接维持在 OutpostNetSystem）。§32 / ADR-0028。
            OutpostNetEndpoint endpoint;
            if (string.IsNullOrEmpty(_remoteHttpBaseUrl))
            {
                var devServer = new OutpostDevServer();
                builder.RegisterOwned(devServer, typeof(OutpostDevServer));
                endpoint = new OutpostNetEndpoint(devServer.HttpBaseUrl, devServer.WsUrl);
            }
            else
            {
                // 半配置 fail-fast（§22）：只填 HTTP 会让 WS 维持循环拿空地址无限退避，把配置错误拖成"连不上"疑难。
                if (string.IsNullOrEmpty(_remoteWsUrl))
                    throw new InvalidOperationException(
                        "OutpostContext：配置了真后端 HTTP 基地址但缺 WS 地址——两个地址要么都填（连独立服务端），要么都留空（进程内 dev server）。");
                endpoint = new OutpostNetEndpoint(_remoteHttpBaseUrl.TrimEnd('/'), _remoteWsUrl);
            }
            builder.RegisterValue(endpoint, typeof(OutpostNetEndpoint));
            builder.RegisterOwned(
                new HttpUtility(endpoint.HttpBaseUrl, serializer: OutpostNet.CreateSerializer()),
                typeof(IHttpUtility));
            builder.RegisterOwned(
                new WebSocketUtility(serializer: OutpostNet.CreateSerializer()),
                typeof(IWebSocketUtility));
#endif
        }
    }
}
