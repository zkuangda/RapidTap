using System;
using System.Threading;
using RapidTap;
using Xunit;

namespace RapidTap.Tests
{
    public class ClickEngineTests
    {
        [Fact]
        public void 启动后会持续点击()
        {
            int count = 0;
            var engine = new ClickEngine(() => Interlocked.Increment(ref count));

            engine.IntervalMs = 2m;
            engine.Start();
            Assert.True(engine.IsRunning);

            WaitUntil(() => Volatile.Read(ref count) >= 3, 2000);
            engine.StopAndJoin(1000);

            Assert.True(Volatile.Read(ref count) >= 3);
            Assert.False(engine.IsRunning);
        }

        [Fact]
        public void 停止之后不再产生新的点击()
        {
            int count = 0;
            var engine = new ClickEngine(() => Interlocked.Increment(ref count));

            engine.IntervalMs = 2m;
            engine.Start();
            WaitUntil(() => Volatile.Read(ref count) >= 2, 2000);
            engine.StopAndJoin(1000);

            int afterStop = Volatile.Read(ref count);
            Thread.Sleep(120);

            Assert.Equal(afterStop, Volatile.Read(ref count));
        }

        /// <summary>
        /// 这是这套测试存在的主要理由。
        ///
        /// 旧实现里 Stop 只置标志、不等线程退出，而线程可能正卡在长间隔的休眠里。
        /// 若在它醒来之前又 Start，旧线程醒来时看到标志已被新的 Start 置回 true，
        /// 就会和新线程一起点，点击速度凭空翻倍。
        ///
        /// 这里用一个"当前并发进入点击回调的线程数"的计数器来断言：任何时刻都只能有一个。
        /// </summary>
        [Fact]
        public void 快速反复开关不会叠加出多个连点线程()
        {
            int concurrent = 0;
            int maxConcurrent = 0;
            int total = 0;

            var engine = new ClickEngine(() =>
            {
                int now = Interlocked.Increment(ref concurrent);

                // 记录历史最大并发数
                int snapshot;
                while (now > (snapshot = Volatile.Read(ref maxConcurrent)))
                {
                    Interlocked.CompareExchange(ref maxConcurrent, now, snapshot);
                }

                Interlocked.Increment(ref total);

                // 停留一会儿，放大并发窗口——真实实现里 SendInput 也是要花时间的
                Thread.Sleep(5);
                Interlocked.Decrement(ref concurrent);
            });

            // 间隔故意设得比开关节奏长：旧线程一定还在休眠里，正是竞态的触发条件
            engine.IntervalMs = 200m;

            for (int i = 0; i < 40; i++)
            {
                engine.Start();
                Thread.Sleep(3);
                engine.Stop();
                Thread.Sleep(3);
            }

            engine.StopAndJoin(1000);
            Thread.Sleep(400); // 让任何残留线程有机会醒来作乱

            Assert.Equal(1, Volatile.Read(ref maxConcurrent));
            Assert.True(Volatile.Read(ref total) > 0, "至少应该点出来过几下，否则这个测试没测到东西");
        }

        [Fact]
        public void 停止后残留的旧线程不会再多点一下()
        {
            int count = 0;
            var engine = new ClickEngine(() => Interlocked.Increment(ref count));

            // 长间隔：Start 之后马上 Stop，线程此刻必然睡在 PreciseDelay 里
            engine.IntervalMs = 300m;
            engine.Start();
            WaitUntil(() => Volatile.Read(ref count) >= 1, 1000);
            engine.Stop();

            int afterStop = Volatile.Read(ref count);
            Thread.Sleep(500); // 远超那 300ms 的休眠，旧线程一定已经醒过

            Assert.Equal(afterStop, Volatile.Read(ref count));
        }

        [Fact]
        public void 兜底回调返回否时会自行停止并发出通知()
        {
            int count = 0;
            bool keepRunning = true;
            var stopped = new ManualResetEventSlim(false);

            var engine = new ClickEngine(
                () => Interlocked.Increment(ref count),
                () => Volatile.Read(ref keepRunning));

            engine.StoppedBySafetyCheck += (s, e) => stopped.Set();

            engine.IntervalMs = 2m;
            engine.Start();
            WaitUntil(() => Volatile.Read(ref count) >= 1, 1000);

            Volatile.Write(ref keepRunning, false);

            // 兜底检查有 50ms 的节流，给它足够的余量
            Assert.True(stopped.Wait(2000), "兜底检查应当在热键松开后及时停下连点");
            Assert.False(engine.IsRunning);
        }

        [Fact]
        public void 重复调用_Start_不会起第二个线程()
        {
            int concurrent = 0;
            int maxConcurrent = 0;

            var engine = new ClickEngine(() =>
            {
                int now = Interlocked.Increment(ref concurrent);
                int snapshot;
                while (now > (snapshot = Volatile.Read(ref maxConcurrent)))
                {
                    Interlocked.CompareExchange(ref maxConcurrent, now, snapshot);
                }

                Thread.Sleep(5);
                Interlocked.Decrement(ref concurrent);
            });

            engine.IntervalMs = 2m;

            // 按住热键时系统会持续重发 KEYDOWN，Start 会被反复调用
            for (int i = 0; i < 20; i++)
            {
                engine.Start();
            }

            Thread.Sleep(200);
            engine.StopAndJoin(1000);

            Assert.Equal(1, Volatile.Read(ref maxConcurrent));
        }

        [Fact]
        public void 间隔设置会被夹进合法区间()
        {
            var engine = new ClickEngine(() => { });

            engine.IntervalMs = 5000m;
            Assert.Equal(ClickTiming.MaxIntervalMs, engine.IntervalMs);

            engine.IntervalMs = 0m;
            Assert.Equal(ClickTiming.MinIntervalMs, engine.IntervalMs);
        }

        [Fact]
        public void 每次启动都会把点击计数清零()
        {
            var engine = new ClickEngine(() => { });
            engine.IntervalMs = 2m;

            engine.Start();
            WaitUntil(() => engine.ClickCount >= 3, 2000);
            engine.Stop();
            Assert.True(engine.ClickCount >= 3);

            engine.Start();
            Assert.True(engine.ClickCount < 3);
            engine.StopAndJoin(1000);
        }

        /// <summary>
        /// 延时用的是可中断的事件等待而不是 Thread.Sleep，所以停止和重启都应当立刻生效。
        /// 若退回 Thread.Sleep，间隔设成 800ms 时最长要等将近一秒才重新开始点，
        /// 用户会觉得"热键按了没反应"。
        /// </summary>
        [Fact]
        public void 长间隔下停止与重启都不必等待剩余的间隔()
        {
            int count = 0;
            var engine = new ClickEngine(() => Interlocked.Increment(ref count));

            engine.IntervalMs = 800m;
            engine.Start();

            // 第一下点完之后，线程就进了那 800ms 的延时
            WaitUntil(() => Volatile.Read(ref count) >= 1, 1000);
            Assert.Equal(1, Volatile.Read(ref count));

            engine.Stop();
            engine.Start();

            // 如果延时不可中断，这里要等到 800ms 之后才会有第二下
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WaitUntil(() => Volatile.Read(ref count) >= 2, 1000);
            sw.Stop();

            engine.StopAndJoin(1000);

            Assert.True(Volatile.Read(ref count) >= 2, "重启后应当立刻点出第二下");
            Assert.True(sw.ElapsedMilliseconds < 300,
                $"重启后第二下用了 {sw.ElapsedMilliseconds}ms，说明延时没有被 Stop 打断");
        }

        /// <summary>
        /// 回归："按下热键后要等半秒才开始点"。
        ///
        /// 低级键盘钩子跑在系统更新按键状态之前——钩子里（以及被它唤醒的连点线程里）
        /// GetAsyncKeyState 对刚按下的那个键仍然返回"松开"，实测约 2ms 后才翻成"按下"。
        /// 兜底检查的节流时刻是跨轮次保留的，隔一会儿再按热键必然已超过 50ms 节流窗口，
        /// 于是新一轮的第一圈就去做检查，一查一个准地把整轮连点否掉，一下都没点就停了；
        /// 用户要等键盘自动重复补发的 KEYDOWN 才真的开始连点。
        ///
        /// 所以这里必须连按多次：第一次按下时兜底计时恰好是从零起算的，反而不会出问题，
        /// 只测第一次是测不出这个缺陷的。
        /// </summary>
        [Fact]
        public void 热键刚按下时兜底检查不应否掉新一轮连点()
        {
            int count = 0;
            long pressedAtTicks = 0;
            bool held = false;
            int safetyStops = 0;

            // 模拟 GetAsyncKeyState：手一直按着，但按下后 2ms 内系统仍报"松开"
            Func<bool> keyStateSaysHeld = () =>
            {
                if (!Volatile.Read(ref held))
                {
                    return false;
                }

                long since = System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref pressedAtTicks);
                return since * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 2.0;
            };

            var engine = new ClickEngine(() => Interlocked.Increment(ref count), keyStateSaysHeld);
            engine.StoppedBySafetyCheck += (s, e) => Interlocked.Increment(ref safetyStops);
            engine.IntervalMs = 2m;

            for (int press = 1; press <= 3; press++)
            {
                Interlocked.Exchange(ref pressedAtTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                Volatile.Write(ref held, true);
                Interlocked.Exchange(ref count, 0);

                engine.Start();
                WaitUntil(() => Volatile.Read(ref count) >= 1, 500);

                int clicked = Volatile.Read(ref count);
                int stops = Volatile.Read(ref safetyStops);

                engine.Stop();
                Volatile.Write(ref held, false);

                Assert.True(clicked >= 1,
                    $"第 {press} 次按下热键后一下都没点出来（兜底自停 {stops} 次）");
                Assert.Equal(0, stops);

                // 两次按键之间拉开距离，超过兜底检查那 50ms 的节流窗口
                Thread.Sleep(80);
            }

            engine.StopAndJoin(1000);
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }
        }
    }
}
