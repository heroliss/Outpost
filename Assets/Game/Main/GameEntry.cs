using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.System;
using R3;
using Sirenix.Serialization;
using UnityEngine;
using DisposableBag = Game.Framework.DisposableBag; // 与 R3.DisposableBag 同名,显式指向框架版
using Object = UnityEngine.Object;

namespace Game.Main
{
    /// <summary>
    /// 游戏入口——<c>HotUpdateLauncher</c> 加载完全部热更 DLL 后反射调用（约定：公共静态无参方法 <see cref="Enter"/>）。
    /// 这里是业务的「main」：创建全局 Context、初始化框架资源系统、从 bundle 加载首场景都从这往下走。
    ///
    /// 程序集与目录按领域命名（Main / 模块 / DLC），**不按是否热更命名**——热更与否是热更构建配置
    /// （FrameworkHotUpdateProfile 列表）里的部署决策，与代码组织无关（ADR-0008）。
    ///
    /// 当前内容是 IL2CPP 真机框架自检（ADR-0008 §6 的验证重点）：在热更世界里把框架重反射重泛型的链路
    /// 各跑一遍——DI 容器、RP&lt;T&gt; + R3 订阅、struct Command 双泛型零装箱分发、class Command 注入、
    /// 事件总线、UniTask 异步命令、Odin 对热更类型的序列化往返——结果屏显 + 落日志。
    /// 业务项目把自检替换为真正的启动编排。
    /// </summary>
    public static class GameEntry
    {
        /// <summary>热更验证标记：每次热更迭代改这里，肉眼比对玩家包屏显。</summary>
        public const string EntryVersion = "v3-framework-check";

        public static void Enter()
        {
            Debug.Log($"[GameEntry] 入口已执行（{EntryVersion}）——程序集 {typeof(GameEntry).Assembly.GetName().Name} 运行中。");

            var go = new GameObject("Game");
            Object.DontDestroyOnLoad(go);
            var label = go.AddComponent<FrameworkSelfCheck>();
            label.Run();
        }
    }

    /// <summary>
    /// 框架真机自检：每项独立 try/catch，全部跑完汇总（✓/✗ 逐行屏显并落日志）。
    /// 同步项在 <see cref="Run"/> 里立刻出结果，异步项（UniTask 命令）完成后追加。业务项目删掉即可。
    /// </summary>
    public sealed class FrameworkSelfCheck : MonoBehaviour
    {
        private readonly List<string> _results = new();
        private GameContext _ctx;
        private DisposableBag _bag;

        public void Run()
        {
            // 自检专用 Context：自给自足，不依赖全局（玩家包里此刻还没有 GameContext.Main）。
            var model = new CheckModel();
            var system = new CheckSystem();
            var builder = new ContainerBuilder();
            builder.RegisterValue(model, new[] { typeof(IModel), typeof(CheckModel) });
            builder.RegisterValue(system, new[] { typeof(ISystem), typeof(CheckSystem) });
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _ctx = new GameContext(builder.Build(), inheritFromGlobal: false);
            _ctx.Inject(system);
            _ctx.AttachTo(system);
            _bag = new DisposableBag(_ctx);

            Check("DI 容器注册/解析", () =>
            {
                if (!ReferenceEquals(_ctx.GetModel<CheckModel>(), model)) throw new Exception("GetModel 实例不一致");
                if (!ReferenceEquals(_ctx.GetSystem<CheckSystem>(), system)) throw new Exception("GetSystem 实例不一致");
            });

            Check("RP<int> + R3 订阅（跨 AOT 泛型）", () =>
            {
                int received = -1;
                _bag.Subscribe(_ctx.GetModel<CheckModel>().Count, v => received = v);
                if (received != 0) throw new Exception($"订阅未收到初值（got {received}）");
                _ctx.GetModel<CheckModel>().Count.Value = 42;
                if (received != 42) throw new Exception($"订阅未收到推送（got {received}）");
            });

            Check("struct Command 分发", () =>
            {
                _ctx.ExecuteCommand(new AddStructCommand(8));
                if (_ctx.GetModel<CheckModel>().Count.Value != 50) throw new Exception("命令未生效");
            });

            Check("struct Command 双泛型返回值（零装箱）", () =>
            {
                int v = _ctx.ExecuteCommand<ReadCountStructCommand, int>(new ReadCountStructCommand());
                if (v != 50) throw new Exception($"返回值错误（got {v}）");
            });

            Check("class Command [Inject] 注入", () =>
            {
                _ctx.ExecuteCommand(new InjectClassCommand());
                if (_ctx.GetModel<CheckModel>().Count.Value != 51) throw new Exception("注入字段未生效");
            });

            Check("事件总线 SendEvent/Subscribe", () =>
            {
                int got = 0;
                _bag.Subscribe<CountReachedEvent>(e => got = e.Value);
                _ctx.SendEvent(new CountReachedEvent { Value = 7 });
                if (got != 7) throw new Exception($"事件未送达（got {got}）");
            });

            Check("Odin 序列化热更类型往返", () =>
            {
                var src = new OdinPayload { Name = "hot", Table = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 } };
                byte[] bytes = SerializationUtility.SerializeValue(src, DataFormat.Binary);
                var dst = SerializationUtility.DeserializeValue<OdinPayload>(bytes, DataFormat.Binary);
                if (dst == null || dst.Name != "hot" || dst.Table == null || dst.Table["b"] != 2)
                    throw new Exception("往返后数据不一致");
            });

            RunAsyncChecks().Forget(ex =>
            {
                _results.Add($"✗ 异步命令（UniTask）：{ex.GetType().Name} {ex.Message}");
                LogSummary();
            });
        }

        private async UniTask RunAsyncChecks()
        {
            // 解释器下的 async 状态机 + AOT UniTask 跨界：分别跑 struct 异步命令与带返回值异步命令。
            await _ctx.ExecuteCommandAsync(new DelayAddStructCommand(9));
            if (_ctx.GetModel<CheckModel>().Count.Value != 60)
                throw new Exception($"struct 异步命令未生效（Count={_ctx.GetModel<CheckModel>().Count.Value}）");

            int echoed = await _ctx.ExecuteCommandAsync(new EchoAsyncCommand { Input = 123 });
            if (echoed != 123) throw new Exception($"异步返回值错误（got {echoed}）");

            _results.Add("✓ 异步命令（UniTask 解释器状态机）");
            LogSummary();
        }

        private void Check(string name, Action body)
        {
            try
            {
                body();
                _results.Add("✓ " + name);
            }
            catch (Exception e)
            {
                _results.Add($"✗ {name}：{e.GetType().Name} {e.Message}");
            }
        }

        private void LogSummary()
        {
            bool allOk = !_results.Exists(r => r.StartsWith("✗", StringComparison.Ordinal));
            Debug.Log($"[FrameworkSelfCheck] 自检完成 allOk={allOk}\n" + string.Join("\n", _results));
        }

        private void OnGUI()
        {
            GUI.Label(new Rect(10, 70, Screen.width - 20, 24),
                $"GameEntry {GameEntry.EntryVersion} · 框架内核：{typeof(GameContext).Assembly.GetName().Name}");
            for (int i = 0; i < _results.Count; i++)
                GUI.Label(new Rect(10, 98 + i * 22, Screen.width - 20, 22), _results[i]);
        }

        private void OnDestroy()
        {
            _bag?.Dispose();
            _ctx?.Dispose();
        }
    }

    // ───────────── 自检用的最小层与命令(全部是热更类型,业务项目删掉) ─────────────

    public sealed class CheckModel : IModel
    {
        public RP<int> Count { get; } = new(0);
    }

    public sealed class CheckSystem : ISystem
    {
        public void Add(CheckModel model, int delta) => model.Count.Value += delta;
    }

    public readonly struct AddStructCommand : ICommand
    {
        private readonly int _delta;
        public AddStructCommand(int delta) => _delta = delta;

        public void Execute(ICommandContext ctx)
            => ctx.GetSystem<CheckSystem>().Add(ctx.GetModel<CheckModel>(), _delta);
    }

    public readonly struct ReadCountStructCommand : ICommand<int>
    {
        public int Execute(ICommandContext ctx) => ctx.GetModel<CheckModel>().Count.Value;
    }

    public sealed class InjectClassCommand : ICommand
    {
        [Inject] private CheckModel _model;
        [Inject] private CheckSystem _system;

        public void Execute(ICommandContext ctx) => _system.Add(_model, 1);
    }

    public readonly struct DelayAddStructCommand : IAsyncCommand
    {
        private readonly int _delta;
        public DelayAddStructCommand(int delta) => _delta = delta;

        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(100, cancellationToken: cancellationToken);
            ctx.GetSystem<CheckSystem>().Add(ctx.GetModel<CheckModel>(), _delta);
        }
    }

    public sealed class EchoAsyncCommand : IAsyncCommand<int>
    {
        public int Input;

        public async UniTask<int> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(50, cancellationToken: cancellationToken);
            return Input;
        }
    }

    public sealed class CountReachedEvent : IEvent
    {
        public int Value;
    }

    /// <summary>Odin 序列化往返的载荷:验证 AOT 的 Sirenix.Serialization 对热更类型走反射 formatter。</summary>
    public sealed class OdinPayload
    {
        public string Name;
        public Dictionary<string, int> Table;
    }
}
