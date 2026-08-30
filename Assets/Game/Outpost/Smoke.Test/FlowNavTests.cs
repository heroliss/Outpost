using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Flow;
using Game.Framework.Logging;
using Game.Outpost.Flow;
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

        private void AssertSingleFailure(Exception expected)
        {
            Assert.AreEqual(1, _sink.Entries.Count);
            LogEntry entry = _sink.Entries[0];
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual("OutpostFlow", entry.Category);
            Assert.AreSame(expected, entry.Exception);
            StringAssert.Contains("进入流程状态 '测试状态' 失败", entry.Message);
        }

        private sealed class NamedState : FlowState
        {
            public override string ToString() => "测试状态";
        }

        private sealed class StubFlow : IGameFlow
        {
            public Func<FlowState, UniTask> GoToHandler { get; set; }
            public int GoToCount { get; private set; }
            public FlowState Current => null;
            public bool IsTransitioning => false;
            public bool IsIn<TState>() where TState : FlowState => false;

            public UniTask GoTo(FlowState next)
            {
                GoToCount++;
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
