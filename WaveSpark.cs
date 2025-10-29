// WaveSpark.cs  — 直立長條 VU（非波形）
// 會依 SetLevel(0..1) 畫出自下而上的長條；超過門檻用 ActiveColor，否則用 SilentColor。

#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace MxfPlayer
{
    public static class WaveSparkExt
    {
        public static void SetEnabledVisual(this WaveSpark sp, bool on)
        {
            if (sp == null) return;
            sp.ActiveColor    = on ? Color.Lime : Color.FromArgb(70, 70, 70);
            sp.SilentColor    = on ? Color.FromArgb(40, 40, 40) : Color.FromArgb(30, 30, 30);
            sp.BarBorderColor = on ? Color.DimGray : Color.FromArgb(50, 50, 50);
            sp.Invalidate();
        }
    }

    public sealed class WaveSpark : Control
    {
        // === 設計器/序列化友善的公開屬性（給你現有的初始化語句用；不影響行為） ===
        [Browsable(true), DefaultValue(2.5f)]
        public float HistorySeconds { get; set; } = 2.5f;   // 兼容舊參數，實際不使用

        [Browsable(true), DefaultValue(30)]
        public int Fps { get; set; } = 30;                  // 兼容舊參數，實際不使用

        [Browsable(true), DefaultValue(-50f)]
        public float SilenceThresholdDb { get; set; } = -50f; // 以 dB 表示的靜音門檻（預設 -50 dB）

        [Browsable(true), DefaultValue(typeof(Color), "Lime")]
        public Color ActiveColor { get; set; } = Color.Lime;  // 有訊號時的顏色

        [Browsable(true), DefaultValue(typeof(Color), "Gray")]
        public Color SilentColor { get; set; } = Color.Gray;  // 無訊號時的顏色

        [Browsable(true), DefaultValue(typeof(Color), "Black")]
        public Color BarBorderColor { get; set; } = Color.Black; // 外框

        [Browsable(true), DefaultValue(3)]
        public int BarPadding { get; set; } = 3;               // 內邊距（避免貼邊）

        // === 內部狀態 ===
        private float _level;          // 0..1
        private float _peakHold;       // 簡易 peak hold（小尖頭效果）
        private DateTime _lastDecay = DateTime.UtcNow;

        public WaveSpark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 20, 20);
            MinimumSize = new Size(14, 24);
        }

        /// <summary>更新音量（0..1）。</summary>
        public void SetLevel(float value)
        {
            var v = Math.Clamp(value, 0f, 1f);
            _level = v;

            // 小小的 peak hold：上升立刻跟上，下降緩慢衰減
            if (v > _peakHold) _peakHold = v;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);

            // 轉換 dB 門檻為線性值（dBFS 假定 0 dB = 1.0）
            float thrLin = (float)Math.Pow(10.0, SilenceThresholdDb / 20.0);
            bool hasSignal = _level >= thrLin;

            var rc = ClientRectangle;
            if (rc.Width <= 0 || rc.Height <= 0) return;

            // 內縮，避免貼邊
            rc.Inflate(-BarPadding, -BarPadding);
            if (rc.Width <= 2 || rc.Height <= 2) return;

            // 外框
            using (var pen = new Pen(BarBorderColor))
                g.DrawRectangle(pen, rc);

            // 長條高度（由底部往上填色）
            int fillH = (int)Math.Round(rc.Height * _level);
            if (fillH < 0) fillH = 0;
            if (fillH > rc.Height) fillH = rc.Height;

            var barRect = new Rectangle(rc.X + 1,
                                        rc.Bottom - fillH,
                                        rc.Width - 2,
                                        fillH);

            using (var br = new SolidBrush(hasSignal ? ActiveColor : SilentColor))
                g.FillRectangle(br, barRect);

            // 簡易 peak hold（1 秒內以每秒 0.8 的比例衰減）
            DecayPeak();
            int peakY = rc.Bottom - (int)Math.Round(rc.Height * _peakHold);
            peakY = Math.Clamp(peakY, rc.Top, rc.Bottom - 1);
            using (var penPeak = new Pen(Color.FromArgb(220, 255, 255, 255)))
                g.DrawLine(penPeak, rc.Left + 1, peakY, rc.Right - 1, peakY);
        }

        private void DecayPeak()
        {
            var now = DateTime.UtcNow;
            double dt = (now - _lastDecay).TotalSeconds;
            if (dt <= 0) return;
            _lastDecay = now;

            // 緩慢衰減（每秒乘上 0.8），並且不低於目前 level
            float decay = (float)Math.Pow(0.8, dt);
            _peakHold = Math.Max(_level, _peakHold * decay);
        }
    }
}
