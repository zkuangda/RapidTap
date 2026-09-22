using RapidTap;
using Xunit;

namespace RapidTap.Tests
{
    public class ClickTimingTests
    {
        [Theory]
        [InlineData(15.0, 150)]
        [InlineData(1.0, 10)]
        [InlineData(1000.0, 10000)]
        [InlineData(2.5, 25)]
        public void ToTenths_把毫秒换成十分之一毫秒的整数(double intervalMs, int expected)
        {
            Assert.Equal(expected, ClickTiming.ToTenths((decimal)intervalMs));
        }

        [Fact]
        public void ToTenths_会先把超范围的值夹回区间()
        {
            Assert.Equal(10, ClickTiming.ToTenths(0.001m));
            Assert.Equal(10000, ClickTiming.ToTenths(99999m));
        }

        [Theory]
        [InlineData(150, 15.0)]
        [InlineData(1, 0.1)]
        public void FromTenths_是_ToTenths_的逆运算(int tenths, double expected)
        {
            Assert.Equal((decimal)expected, ClickTiming.FromTenths(tenths));
        }

        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(15.0, 15.0)]
        [InlineData(1000.0, 1000.0)]
        [InlineData(-5.0, 1.0)]
        [InlineData(5000.0, 1000.0)]
        public void ClampInterval_把值夹进合法区间(double input, double expected)
        {
            Assert.Equal((decimal)expected, ClickTiming.ClampInterval((decimal)input));
        }

        [Theory]
        [InlineData(10.0, 100.0)]
        [InlineData(1000.0, 1.0)]
        [InlineData(1.0, 1000.0)]
        [InlineData(15.0, 67.0)]
        public void IntervalToCps_按每秒次数换算(double intervalMs, double expectedCps)
        {
            Assert.Equal((decimal)expectedCps, ClickTiming.IntervalToCps((decimal)intervalMs));
        }

        [Theory]
        [InlineData(100.0, 10.0)]
        [InlineData(1.0, 1000.0)]
        [InlineData(5000.0, 1.0)]
        [InlineData(67.0, 15.0)]
        public void CpsToInterval_按间隔换算(double cps, double expectedInterval)
        {
            Assert.Equal((decimal)expectedInterval, ClickTiming.CpsToInterval((decimal)cps));
        }

        [Fact]
        public void 间隔与速度来回换算不会漂移()
        {
            // 界面上两个输入框是双向联动的，来回换算若不稳定就会自己抖起来
            foreach (decimal interval in new[] { 1m, 2m, 3m, 7m, 10m, 15m, 50m, 100m, 1000m })
            {
                decimal cps = ClickTiming.IntervalToCps(interval);
                decimal back = ClickTiming.CpsToInterval(cps);
                decimal again = ClickTiming.IntervalToCps(back);

                Assert.Equal(cps, again);
            }
        }

        [Theory]
        [InlineData(0.1, true)]
        [InlineData(1.9, true)]
        [InlineData(2.0, false)]
        [InlineData(15.0, false)]
        [InlineData(0.0, false)]
        [InlineData(-1.0, false)]
        public void UsesSpinWait_只在低于两毫秒时才忙等(double ms, bool expected)
        {
            Assert.Equal(expected, ClickTiming.UsesSpinWait(ms));
        }

        [Theory]
        [InlineData(20, 0.2, 100.0)]
        [InlineData(0, 0.2, 0.0)]
        [InlineData(13, 1.0, 13.0)]
        public void ComputeCps_由采样增量算出每秒次数(long delta, double seconds, double expected)
        {
            Assert.Equal(expected, ClickTiming.ComputeCps(delta, seconds), 3);
        }

        [Fact]
        public void ComputeCps_采样窗口非法时返回零而不是除零()
        {
            Assert.Equal(0.0, ClickTiming.ComputeCps(10, 0));
            Assert.Equal(0.0, ClickTiming.ComputeCps(10, -1));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(3.0)]
        [InlineData(7.0)]
        [InlineData(15.0)]
        [InlineData(999.0)]
        public void 两个方向的换算结果都是整数(double intervalMs)
        {
            // 界面上两个输入框都只显示整数，换算若带小数，显示的值和实际值就对不上
            decimal cps = ClickTiming.IntervalToCps((decimal)intervalMs);
            Assert.Equal(decimal.Truncate(cps), cps);
            Assert.Equal(decimal.Truncate(ClickTiming.CpsToInterval(cps)), ClickTiming.CpsToInterval(cps));
        }

        [Theory]
        [InlineData(7.5, 8.0)]
        [InlineData(7.4, 7.0)]
        [InlineData(0.1, 1.0)]
        [InlineData(-3.0, 1.0)]
        [InlineData(99999.0, 1000.0)]
        public void RoundInterval_四舍五入到整数并夹进区间(double input, double expected)
        {
            Assert.Equal((decimal)expected, ClickTiming.RoundInterval((decimal)input));
        }

        [Theory]
        [InlineData(66.7, 67.0)]
        [InlineData(66.4, 66.0)]
        [InlineData(0.2, 1.0)]
        [InlineData(99999.0, 1000.0)]
        public void RoundCps_四舍五入到整数并夹进区间(double input, double expected)
        {
            Assert.Equal((decimal)expected, ClickTiming.RoundCps((decimal)input));
        }

        [Fact]
        public void AdjustStep_上下箭头一次走五()
        {
            Assert.Equal(5m, ClickTiming.AdjustStep);

            // 10 毫秒按一下上箭头就是 15 毫秒
            Assert.Equal(15m, ClickTiming.RoundInterval(10m + ClickTiming.AdjustStep));
        }
    }
}
