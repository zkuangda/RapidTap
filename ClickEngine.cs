using System;
using System.Diagnostics;
using System.Threading;

namespace RapidTap
{
    /// <summary>
    /// 连点循环本体。刻意不碰任何 UI，也不直接调 SendInput：
    /// "点一下"和"热键是否还被物理按着"都由外部以委托注入，
    /// 于是这个类在没有窗口、没有真实鼠标键盘的情况下也能被单元测试完整覆盖。
    ///
    /// 线程模型：全程只有一条常驻工作线程，空闲时阻塞在条件变量上不占 CPU。
    ///
    /// 早先的实现是每次 Start 都新起一条线程、Stop 只置个标志就返回，
    /// 结果线程可能正卡在延时里（间隔设成 1000ms 就是睡整整一秒），
    /// 在它醒来之前若又 Start 了一次，旧线程醒来看到标志又变回 true，
    /// 就会和新线程一起点，点击速度凭空翻倍，快速连按还能层层叠加。
    /// 改成常驻单线程之后，"同时存在两条连点线程"在结构上就不可能发生。
    ///
    /// 状态全部用 Monitor 保护，等待一律写成"循环里判断条件"的形式。
    /// 中途试过用 ManualResetEventSlim 做可中断延时，但 Stop 紧接着 Start 时
    /// 事件会被复位回去，工作线程还没被调度到就错过了信号，于是继续睡满剩余间隔——
    /// 典型的丢唤醒。条件变量 + 状态版本号没有这个问题。
    /// </summary>
    internal sealed class ClickEngine
    {
        private readonly Action _click;

        /// <summary>
        /// 物理兜底。返回 false 表示热键其实已经松开了，循环应当自行停止。
        /// 存在的理由见 <see cref="SafetyCheckIntervalMs"/> 上方的注释。
        /// </summary>
        private readonly Func<bool>? _shouldKeepRunning;

        /// <summary>
        /// 兜底检查的最小间隔。间隔设成 0.1ms 时循环每秒要转一万圈，
        /// 没必要每圈都去问一次系统按键状态；50ms 对"停下来"这件事已经足够及时。
        /// </summary>
        private const double SafetyCheckIntervalMs = 50.0;

        /// <summary>所有状态字段的监视器，同时充当条件变量。</summary>
        private readonly object _sync = new object();

        // 以下四个字段一律在 _sync 里读写
        private bool _running;
        private bool _shuttingDown;
        private Thread? _worker;

        /// <summary>
        /// 状态版本号，每次 Start / Stop / 关停都自增。
        /// 延时循环带着自己启动时的版本号等待，版本一变就立刻退出，
        /// 不必等剩余间隔走完——这既让"停止"即时生效，也让"停了马上再开"不掉帧。
        /// </summary>
        private int _epoch;

        /// <summary>间隔以 0.1ms 为单位存整数，理由见 ClickTiming.ToTenths。</summary>
        private volatile int _intervalTenths = ClickTiming.ToTenths(15m);

        private long _clickCount;

        /// <summary>
        /// 因为兜底检查发现热键已松开而自行停止时触发。
        /// 在连点线程上抛出，订阅方需要自己切回 UI 线程。
        /// </summary>
        public event EventHandler? StoppedBySafetyCheck;

        public ClickEngine(Action click, Func<bool>? shouldKeepRunning = null)
        {
            _click = click ?? throw new ArgumentNullException(nameof(click));
            _shouldKeepRunning = shouldKeepRunning;
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _running;
                }
            }
        }

        public long ClickCount => Interlocked.Read(ref _clickCount);

        public decimal IntervalMs
        {
            get => ClickTiming.FromTenths(_intervalTenths);
            set => _intervalTenths = ClickTiming.ToTenths(value);
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_running || _shuttingDown)
                {
                    return;
                }

                Interlocked.Exchange(ref _clickCount, 0);
                _running = true;
                _epoch++;

                EnsureWorkerStartedLocked();
                Monitor.PulseAll(_sync);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                _epoch++;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// 停掉连点并让常驻线程退出，只在关窗时调用；调用之后本实例不可再启动。
        /// 正常的开关连点走 Stop 即可——Stop 从不阻塞调用方，
        /// 这点很重要，因为它是在低级键盘钩子的回调里被调用的，
        /// 那个回调超过约 300ms 不返回，系统就会静默把钩子卸掉。
        /// </summary>
        public void StopAndJoin(int millisecondsTimeout)
        {
            Thread? worker;

            lock (_sync)
            {
                _running = false;
                _shuttingDown = true;
                _epoch++;
                worker = _worker;
                _worker = null;
                Monitor.PulseAll(_sync);
            }

            worker?.Join(millisecondsTimeout);
        }

        private void EnsureWorkerStartedLocked()
        {
            if (_worker != null || _shuttingDown)
            {
                return;
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                // 略高于普通优先级：连点精度依赖及时被调度，但不用 Highest，
                // 以免在低端机上把 UI 线程饿着、连窗口都点不动
                Priority = ThreadPriority.AboveNormal,
                Name = "RapidTap.ClickLoop"
            };
            _worker.Start();
        }

        private void WorkerLoop()
        {
            var safetyClock = Stopwatch.StartNew();
            double lastSafetyCheckMs = 0;

            while (true)
            {
                int epoch;

                lock (_sync)
                {
                    // 没在连点时就阻塞在这里，完全不占 CPU
                    while (!_running && !_shuttingDown)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_shuttingDown)
                    {
                        return;
                    }

                    epoch = _epoch;
                }

                // 兜底检查和点击都是外部注入的委托，绝不能持锁调用：
                // 那会让 Stop 一直阻塞到这一下点完
                if (_shouldKeepRunning != null)
                {
                    double nowMs = safetyClock.Elapsed.TotalMilliseconds;
                    if (nowMs - lastSafetyCheckMs >= SafetyCheckIntervalMs)
                    {
                        lastSafetyCheckMs = nowMs;
                        if (!_shouldKeepRunning())
                        {
                            bool stopped = false;
                            lock (_sync)
                            {
                                if (_running && _epoch == epoch)
                                {
                                    _running = false;
                                    _epoch++;
                                    Monitor.PulseAll(_sync);
                                    stopped = true;
                                }
                            }

                            if (stopped)
                            {
                                StoppedBySafetyCheck?.Invoke(this, EventArgs.Empty);
                            }

                            continue;
                        }
                    }
                }

                _click();
                Interlocked.Increment(ref _clickCount);

                Delay(ClickTiming.FromTenths(_intervalTenths), epoch);
            }
        }

        /// <summary>
        /// 两段式延时：
        /// - 间隔 &gt;= 2ms 时在条件变量上等待，既让出 CPU，又能在状态一变就醒来；
        /// - 间隔 &lt; 2ms 时任何基于内核对象的等待其调度粒度都不够（即使开了 timeBeginPeriod(1)
        ///   也只到 ~1ms），改用 Stopwatch + SpinWait 忙等，靠高精度计时器自旋到点，
        ///   牺牲少量 CPU 换精度；这么短的间隔也不需要可中断，反正马上就轮到下一次检查。
        /// </summary>
        private void Delay(decimal milliseconds, int epoch)
        {
            double total = (double)milliseconds;
            if (total <= 0)
            {
                return;
            }

            if (ClickTiming.UsesSpinWait(total))
            {
                var spin = Stopwatch.StartNew();
                double targetTicks = total / 1000.0 * Stopwatch.Frequency;
                while (spin.ElapsedTicks < targetTicks)
                {
                    Thread.SpinWait(20);
                }

                return;
            }

            var clock = Stopwatch.StartNew();

            lock (_sync)
            {
                while (true)
                {
                    // 条件写在循环里判断：Stop 之后紧接着 Start 也不会丢掉这次唤醒，
                    // 因为判断的是状态本身而不是"有没有收到过信号"
                    if (_shuttingDown || !_running || _epoch != epoch)
                    {
                        return;
                    }

                    double remaining = total - clock.Elapsed.TotalMilliseconds;
                    if (remaining <= 0.5)
                    {
                        return;
                    }

                    Monitor.Wait(_sync, (int)Math.Ceiling(remaining));
                }
            }
        }
    }
}
