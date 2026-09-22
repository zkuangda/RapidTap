using System;

namespace RapidTap
{
    /// <summary>
    /// 连点的时序换算，全部是纯函数，不依赖窗体、线程和任何 Win32 调用，
    /// 因此可以被单元测试完整覆盖（见 tests/RapidTap.Tests/ClickTimingTests.cs）。
    /// </summary>
    internal static class ClickTiming
    {
        /// <summary>
        /// 间隔下限。界面上间隔与速度都按整数显示，1ms 就是整数能表达的最小一档；
        /// 再小下去 SendInput 本身的开销就成了瓶颈，调它也没有意义。
        /// </summary>
        public const decimal MinIntervalMs = 1m;

        /// <summary>间隔上限，1 秒一次，比这更慢的场景手点就行了。</summary>
        public const decimal MaxIntervalMs = 1000m;

        /// <summary>
        /// 界面上两个数字框点一下上下箭头走的步数。1 这种步长要按半天才看得出差别，
        /// 5 一档正好：10ms 按一下就是 15ms。
        /// </summary>
        public const decimal AdjustStep = 5m;

        /// <summary>
        /// 低于这个间隔就不能用 Thread.Sleep：即使开了 timeBeginPeriod(1)，
        /// 操作系统的调度粒度也只能到约 1ms，误差会把设定值整个淹没。
        /// </summary>
        public const double SpinWaitThresholdMs = 2.0;

        public static decimal MinCps => Math.Round(1000m / MaxIntervalMs, 0, MidpointRounding.AwayFromZero);

        public static decimal MaxCps => Math.Round(1000m / MinIntervalMs, 0, MidpointRounding.AwayFromZero);

        /// <summary>
        /// 间隔以 0.1ms 为单位存成整数在线程间传递。
        /// 用 int 是为了能安全地 volatile 读写——double 不支持 volatile，
        /// decimal 更是 16 字节、连原子读写都保证不了。
        /// </summary>
        public static int ToTenths(decimal intervalMs)
        {
            return (int)Math.Round(ClampInterval(intervalMs) * 10m, MidpointRounding.AwayFromZero);
        }

        public static decimal FromTenths(int tenths)
        {
            return tenths / 10m;
        }

        public static decimal ClampInterval(decimal intervalMs)
        {
            if (intervalMs < MinIntervalMs)
            {
                return MinIntervalMs;
            }

            return intervalMs > MaxIntervalMs ? MaxIntervalMs : intervalMs;
        }

        public static decimal ClampCps(decimal cps)
        {
            if (cps < MinCps)
            {
                return MinCps;
            }

            return cps > MaxCps ? MaxCps : cps;
        }

        /// <summary>
        /// 把任意来源的间隔（旧配置文件里可能存着 7.5）归整成界面能显示的整数毫秒。
        /// 凡是要写回输入框的值都先过这一道，否则控件显示的是四舍五入后的整数、
        /// 内部却还留着小数，存回配置时又原样写出去，对不上。
        /// </summary>
        public static decimal RoundInterval(decimal intervalMs)
        {
            return ClampInterval(Math.Round(intervalMs, 0, MidpointRounding.AwayFromZero));
        }

        /// <summary>同 <see cref="RoundInterval"/>，把速度归整成界面能显示的整数。</summary>
        public static decimal RoundCps(decimal cps)
        {
            return ClampCps(Math.Round(cps, 0, MidpointRounding.AwayFromZero));
        }

        /// <summary>间隔（毫秒）换算成每秒点击次数，取整到个位与界面输入框一致。</summary>
        public static decimal IntervalToCps(decimal intervalMs)
        {
            decimal clamped = ClampInterval(intervalMs);
            return ClampCps(Math.Round(1000m / clamped, 0, MidpointRounding.AwayFromZero));
        }

        /// <summary>每秒点击次数换算回间隔（毫秒），同样取整到个位。</summary>
        public static decimal CpsToInterval(decimal cps)
        {
            decimal clamped = ClampCps(cps);
            return ClampInterval(Math.Round(1000m / clamped, 0, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// 该用忙等还是 Thread.Sleep。抽成独立判断是为了能直接断言边界，
        /// 而不必真的去睡一觉再测量耗时（那种测试既慢又不稳定）。
        /// </summary>
        public static bool UsesSpinWait(double intervalMs)
        {
            return intervalMs > 0 && intervalMs < SpinWaitThresholdMs;
        }

        /// <summary>由采样窗口内的点击增量算出每秒点击次数。</summary>
        public static double ComputeCps(long delta, double seconds)
        {
            if (seconds <= 0 || delta <= 0)
            {
                return 0;
            }

            return delta / seconds;
        }
    }
}
