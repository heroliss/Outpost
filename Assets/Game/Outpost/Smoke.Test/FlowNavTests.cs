using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using Game.Framework.Logging;
using Game.Outpost.Commands;
using Game.Outpost.Flow;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Systems;
using NUnit.Framework;

namespace Game.Outpost.Smoke.Test
{
    /// <summary>
    /// 锁定项目导航 Adapter 对 <see cref="IGameFlow.GoTo"/> 三种终态的观察策略：成功和顶替取消静默，真实失败只记录一次。
    /// </summary>
    public sealed class FlowNavTests
    {
        private List<ILogSink> _previousSinks;
        private LogLevel _previousMinLevel;
        private CapturingSink _sink;

        [SetUp]
        public void SetUp()
        {
            _previousSinks = new List<ILogSink>(Log.Sinks);
            _previousMinLevel = Log.MinLevel;
            _sink = new CapturingSink();
            Log.ClearSinks();
            Log.AddSink(_sink);
            Log.MinLevel = LogLevel.Trace;
        }

        [TearDown]
        public void TearDown()
        {
            Log.ClearSinks();
            foreach (var sink in _previousSinks) Log.AddSink(sink);
            Log.MinLevel = _previousMinLevel;
        }

        [Test]
        public void Request_Succeeds_DoesNotLog()
        {
            var flow = new StubFlow { GoToHandler = _ => UniTask.CompletedTask };

            FlowNav.Request(flow, new NamedState());

            Assert.AreEqual(1, flow.GoToCount);
            Assert.IsEmpty(_sink.Entries);
        }

        [Test]
        public void Request_IsCanceled_DoesNotLog()
        {
            var canceled = new CancellationToken(canceled: true);
            var flow = new StubFlow { GoToHandler = _ => UniTask.FromCanceled(canceled) };

            FlowNav.Request(flow, new NamedState());

            Assert.AreEqual(1, flow.GoToCount);
            Assert.IsEmpty(_sink.Entries, "最新意图胜造成的顶替取消是正常控制流，不应制造错误噪音");
        }

        [Test]
        public void Request_TaskFaults_LogsOriginalExceptionExactlyOnce()
        {
            var expected = new InvalidOperationException("enter-failed");
            var flow = new StubFlow { GoToHandler = _ => UniTask.FromException(expected) };

            FlowNav.Request(flow, new NamedState());

            AssertSingleFailure(expected);
        }

        [Test]
        public void Request_GoToThrowsSynchronously_LogsOriginalExceptionExactlyOnce()
        {
            var expected = new ArgumentException("invalid-state");
            var flow = new StubFlow { GoToHandler = _ => throw expected };

            FlowNav.Request(flow, new NamedState());

            AssertSingleFailure(expected);
        }

        [Test]
        public void Request_TaskCompletesAfterReturn_RemainsObservedWithoutLog()
        {
            var completion = new UniTaskCompletionSource();
            var flow = new StubFlow { GoToHandler = _ => completion.Task };

            FlowNav.Request(flow, new NamedState());
            Assert.IsEmpty(_sink.Entries);

            completion.TrySetResult();

            Assert.AreEqual(1, flow.GoToCount);
            Assert.IsEmpty(_sink.Entries);
        }

        [Test]
        public void Request_TaskCancelsAfterReturn_RemainsObservedWithoutLog()
        {
            var completion = new UniTaskCompletionSource();
            var flow = new StubFlow { GoToHandler = _ => completion.Task };

            FlowNav.Request(flow, new NamedState());
            completion.TrySetCanceled();

            Assert.AreEqual(1, flow.GoToCount);
            Assert.IsEmpty(_sink.Entries, "异步到达的顶替取消也应由 Adapter 静默收口");
        }

        [Test]
        public void Request_TaskFaultsAfterReturn_LogsOriginalExceptionExactlyOnce()
        {
            var completion = new UniTaskCompletionSource();
            var expected = new InvalidOperationException("deferred-enter-failed");
            var flow = new StubFlow { GoToHandler = _ => completion.Task };

            FlowNav.Request(flow, new NamedState());
            Assert.IsEmpty(_sink.Entries, "物理任务尚未完成时不能预先报告失败");

            completion.TrySetException(expected);

            Assert.AreEqual(1, flow.GoToCount);
            AssertSingleFailure(expected);
        }

        [Test]
        public void Request_StateToStringThrows_StillLogsGoToFailureExactlyOnce()
        {
            var expected = new InvalidOperationException("root-enter-failure");
            var flow = new StubFlow { GoToHandler = _ => UniTask.FromException(expected) };

            FlowNav.Request(flow, new ThrowingNameState());

            AssertSingleFailure(expected, nameof(ThrowingNameState));
        }

        [Test]
        public void StartBattle_PreCanceledTokenRejectsBeforeSubmittingIntent()
        {
            var flow = new StubFlow { GoToHandler = _ => UniTask.CompletedTask };
            using var context = CreateCommandContext(flow);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            UniTask task = context.ExecuteCommandAsync(new StartBattleCommand(), canceled.Token);

            Assert.AreEqual(UniTaskStatus.Canceled, task.Status);
            Assert.Zero(flow.GoToCount);
            Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        }

        [Test]
        public void StartBattle_ViewTokenCanceledAfterSubmissionReportsActualFlowOutcome()
        {
            var completion = new UniTaskCompletionSource();
            var flow = new StubFlow { GoToHandler = _ => completion.Task };
            using var context = CreateCommandContext(flow);
            using var viewLifetime = new CancellationTokenSource();

            UniTask task = context.ExecuteCommandAsync(new StartBattleCommand(), viewLifetime.Token);
            viewLifetime.Cancel();

            Assert.AreEqual(UniTaskStatus.Pending, task.Status,
                "意图已经被 GameFlow 接受后，旧 View token 只能结束反馈，不能篡改流程 task 的真实结局。");
            completion.TrySetResult();

            Assert.AreEqual(UniTaskStatus.Succeeded, task.Status);
            Assert.DoesNotThrow(() => task.GetAwaiter().GetResult());
            Assert.AreEqual(1, flow.GoToCount);
        }

        [Test]
        public void StartBattle_StartupFailureRestoresTitleAndPreservesOriginalFailure()
        {
            var expected = new InvalidOperationException("battle-startup-failed");
            var flow = new StubFlow
            {
                GoToHandler = state => state is BattleState
                    ? UniTask.FromException(expected)
                    : UniTask.CompletedTask,
            };
            using var context = CreateCommandContext(flow);

            UniTask task = context.ExecuteCommandAsync(new StartBattleCommand());

            Assert.AreEqual(UniTaskStatus.Faulted, task.Status);
            var actual = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            Assert.AreSame(expected, actual);
            Assert.AreEqual(2, flow.Requests.Count);
            Assert.IsInstanceOf<BattleState>(flow.Requests[0]);
            Assert.IsInstanceOf<TitleState>(flow.Requests[1]);
        }

        [Test]
        public void StartBattle_StartupFailureDoesNotOverrideNewerCurrentState()
        {
            var newer = new NamedState();
            var flow = new StubFlow
            {
                CurrentState = newer,
                GoToHandler = _ => UniTask.FromException(new InvalidOperationException("stale-battle-failure")),
            };
            using var context = CreateCommandContext(flow);

            UniTask task = context.ExecuteCommandAsync(new StartBattleCommand());

            Assert.AreEqual(UniTaskStatus.Faulted, task.Status);
            Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            Assert.AreEqual(1, flow.Requests.Count,
                "已有更新状态接手时，旧 Battle 失败不能再发一个 Title 覆盖它。");
            Assert.AreSame(newer, flow.Current);
        }

        private void AssertSingleFailure(Exception expected, string expectedState = "测试状态")
        {
            Assert.AreEqual(1, _sink.Entries.Count);
            LogEntry entry = _sink.Entries[0];
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual("OutpostFlow", entry.Category);
            Assert.AreSame(expected, entry.Exception);
            StringAssert.Contains($"进入流程状态 '{expectedState}' 失败", entry.Message);
        }

        private static GameContext CreateCommandContext(IGameFlow flow)
        {
            using var builder = new ContainerBuilder();
            builder.RegisterValue(flow, typeof(IGameFlow));
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            return new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        private sealed class NamedState : FlowState
        {
            public override string ToString() => "测试状态";
        }

        private sealed class ThrowingNameState : FlowState
        {
            public override string ToString() => throw new InvalidOperationException("diagnostic-name-failure");
        }

        private sealed class StubFlow : IGameFlow
        {
            public Func<FlowState, UniTask> GoToHandler { get; set; }
            public int GoToCount { get; private set; }
            public List<FlowState> Requests { get; } = new();
            public FlowState CurrentState { get; set; }
            public bool Transitioning { get; set; }
            public FlowState Current => CurrentState;
            public bool IsTransitioning => Transitioning;
            public bool IsIn<TState>() where TState : FlowState => false;

            public UniTask GoTo(FlowState next)
            {
                GoToCount++;
                Requests.Add(next);
                return GoToHandler(next);
            }
        }

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }
    }
}
