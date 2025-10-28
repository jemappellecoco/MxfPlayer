#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Concurrent;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using NAudio.Wave;
using Timer = System.Windows.Forms.Timer;

namespace MxfPlayer
{
    public partial class Form1 : Form
    {
        private LibVLC? _vlc;
        private MediaPlayer? _mp;              // 主播放器（負責畫面與時間軸）
        private MediaPlayer? _tap;
        // === 控制項 ===
        private readonly VideoView _video;
        private readonly Button _btnOpen, _btnPlay, _btnPause, _btnStop, _btnBack, _btnFwd;
        private readonly TrackBar _rateBar;
        private readonly Label _lblRate;

        // 音軌清單：多選＝混音；單選＝只播該軌
        private readonly CheckedListBox _lbAudioTracks;

        // === 時間條與時間標籤 ===
        private readonly TrackBar _posBar;
        private readonly Label _lblTime;
        private bool _isSeeking = false;           // 拖曳中避免 Timer 覆寫
        private bool _wasPlayingBeforeSeek = false;

        // === VU ===
        private readonly ProgressBar[] _vuBars;
        private readonly Label[] _vuLabels;

        // === 定時 UI 更新 ===
        private readonly Timer _uiTimer;

        // === 音訊狀態 ===
        private const int MAX_TRACKS = 8;          // 取樣以 8 聲道為主（MXF 常見）
        private readonly float[] _peaks = new float[MAX_TRACKS];
        private readonly object _lock = new();
        private DateTime _lastUpdate = DateTime.UtcNow;

        // === NAudio 輸出（統一輸出：立體聲） ===
        private IWavePlayer? _waveOut;
        private BufferedWaveProvider? _buffer;

        // === 混音用的隱藏 players（每個一條音軌） ===
        private sealed class TrackTap
        {
            public MediaPlayer Player;
            public int TrackId;
            public ConcurrentQueue<float[]> Queue = new();
            public TrackTap(MediaPlayer p, int id) { Player = p; TrackId = id; }
        }
        private readonly List<TrackTap> _mixTaps = new();     // 不含主播放器
        private readonly object _mixLock = new();

        // 目前檔案路徑（建隱藏 players 用）
        private string? _currentPath;

        public Form1()
        {
            InitializeComponent();
            Text = "MXF Player — 音軌 / 倍速 / 8 聲道電平圖（多選混音）";
            ClientSize = new Size(1200, 700);
            MinimumSize = new Size(1000, 600);

            // === 視訊區 ===
            _video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
            Controls.Add(_video);

            // === 底部控制區 ===
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 220, Padding = new Padding(8) };
            Controls.Add(bottom);

            // === 按鈕 ===
            _btnOpen  = new Button { Text = "開檔(MXF…)", Width = 110 };
            _btnPlay  = new Button { Text = "▶ Play",     Width = 70  };
            _btnPause = new Button { Text = "⏸ Pause",    Width = 80  };
            _btnStop  = new Button { Text = "■ Stop",     Width = 70  };
            _btnBack  = new Button { Text = "⏪ -5s",      Width = 70  };
            _btnFwd   = new Button { Text = "⏩ +5s",      Width = 70  };

            // === 倍速控制 ===
            _rateBar = new TrackBar
            {
                Minimum = 25,
                Maximum = 400,
                Value = 100,
                TickFrequency = 25,
                Width = 240
            };
            _lblRate = new Label { Text = "Speed: 1.00×", AutoSize = true };

            // === 音軌清單（多選） ===
            _lbAudioTracks = new CheckedListBox { Width = 280, Height = 64, CheckOnClick = true };

            // === 控制列排版（上方） ===
            var ctrlRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 64,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            ctrlRow.Controls.AddRange(new Control[]
            {
                _btnOpen, _btnPlay, _btnPause, _btnStop, _btnBack, _btnFwd,
                new Label { Text = "音軌（可多選）：", AutoSize = true, Padding = new Padding(8,8,0,0) }, _lbAudioTracks,
                new Label { Text = "倍速：", AutoSize = true, Padding = new Padding(12,8,0,0) }, _rateBar, _lblRate
            });
            bottom.Controls.Add(ctrlRow);

            // === 時間列（中間，含時間條與時間標籤） ===
            _posBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 1000,    // 0–1000 對應 0–100%
                Value = 0,
                TickFrequency = 50,
                Width = 860
            };
            _lblTime = new Label
            {
                Text = "00:00:00 / 00:00:00",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 10, 0, 0)
            };
            var timeRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            timeRow.Controls.Add(_posBar);
            timeRow.Controls.Add(_lblTime);
            bottom.Controls.Add(timeRow);

            // === 電平圖 (8 聲道) ===
            var vuRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            _vuBars = Enumerable.Range(0, MAX_TRACKS)
                .Select(_ => new ProgressBar { Width = 60, Height = 110, Minimum = 0, Maximum = 1000 })
                .ToArray();
            _vuLabels = Enumerable.Range(0, MAX_TRACKS)
                .Select(i => new Label { Text = $"Ch {i + 1}", Width = 60, TextAlign = ContentAlignment.MiddleCenter })
                .ToArray();

            for (int i = 0; i < MAX_TRACKS; i++)
            {
                var col = new TableLayoutPanel { Width = 64, Height = 150 };
                col.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
                col.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
                col.Controls.Add(_vuBars[i], 0, 0);
                col.Controls.Add(_vuLabels[i], 0, 1);
                vuRow.Controls.Add(col);
            }
            bottom.Controls.Add(vuRow);

            // === 綁定事件 ===
            _btnOpen.Click  += OnOpen;
            _btnPlay.Click  += (s, e) => _mp?.Play();
            _btnPause.Click += (s, e) => _mp?.Pause();
            _btnStop.Click  += (s, e) => _mp?.Stop();
            _btnBack.Click  += (s, e) => { if (_mp != null) _mp.Time -= 5000; };
            _btnFwd.Click   += (s, e) => { if (_mp != null) _mp.Time += 5000; };
            // if (_tap != null) { try { _tap.Time = _mp.Time; } catch { } }
            _rateBar.ValueChanged += (s, e) =>
            {
                float r = _rateBar.Value / 100f;
                if (_mp != null) _mp.SetRate(r);
                _lblRate.Text = $"Speed: {r:0.00}×";
                 _tap?.SetRate(r);  
            };

            _lbAudioTracks.ItemCheck += (s, e) =>
            {
                // 等 CheckedListBox 實際更新完成後再讀取勾選結果
                BeginInvoke(new Action(ApplyTrackSelection));
            };

            // 時間條互動
            _posBar.MouseDown += (s, e) =>
            {
                _isSeeking = true;
                _wasPlayingBeforeSeek = _mp?.IsPlaying ?? false;
            };
            _posBar.MouseUp += (s, e) =>
            {
                if (_mp != null)
                {
                    long len = _mp.Length; // ms
                    if (len > 0)
                    {
                        double ratio = _posBar.Value / 1000.0;
                        long target = (long)Math.Round(len * ratio);
                        try { _mp.Time = target; } catch { }
                    }
                }
                _isSeeking = false;
            };
            _posBar.Scroll += (s, e) =>
            {
                if (_mp == null) return;
                long len = _mp.Length;
                if (len <= 0) return;
                double ratio = _posBar.Value / 1000.0;
                long preview = (long)Math.Round(len * ratio);
                _lblTime.Text = $"{FmtTime(preview)} / {FmtTime(len)}";
            };

            // === 定時更新 ===
            _uiTimer = new Timer { Interval = 80 };
            _uiTimer.Tick += (s, e) =>
            {
                RefreshMeters();
                RefreshAudioTrackList();
                // ForceSelectFirstValidAudioTrack(); 
                RefreshTimebar();
            };
            _uiTimer.Start();

            this.Load += (_, __) => InitLibVLC();
        }

        
       private void InitLibVLC()
        {
            Core.Initialize();
            _vlc = new LibVLC("--aout=directsound");   // ✅ 指定 DirectSound（或改 "--aout=wasapi" 測試）
            _mp = new MediaPlayer(_vlc) { Volume = 100, Mute = false };  // ✅ 確認不是靜音
            _video.MediaPlayer = _mp;

            _tap = new MediaPlayer(_vlc);
            _tap.SetAudioFormat("f32l", 48000, (uint)MAX_TRACKS);
            _tap.SetAudioCallbacks(AudioCb, PauseCb, ResumeCb, FlushCb, DrainCb);

            _buffer = null;      // 我們不自己播聲音
            _waveOut = null;
        }


        private void StartTapForTrack(string path, int trackId)
        {
            if (_vlc == null || _tap == null) return;
            try { _tap.Stop(); } catch { }

            var m = new Media(_vlc, path, FromType.FromPath);
            m.AddOption(":no-video");
            m.AddOption(":vout=dummy");
            m.AddOption(":video-title-show=0");
            m.AddOption(":aout=dummy");              // 不出聲，只觸發 AudioCb
            if (trackId >= 0) m.AddOption($":audio-track={trackId}");
            _tap.Play(m);
        }
        private void OnOpen(object? sender, EventArgs e)
        {
            if (_vlc == null || _mp == null) return;

            using var ofd = new OpenFileDialog
            {
                Title = "選擇影片",
                Filter = "Video|*.mxf;*.mp4;*.mkv;*.mov;*.avi|All|*.*"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            // 關閉舊的混音 taps
            StopAndDisposeMixTaps();

            _mp.Stop();
            using var media = new Media(_vlc, ofd.FileName, FromType.FromPath);
            media.Parse(MediaParseOptions.ParseLocal);
            _mp.Play(media);
            _currentPath = ofd.FileName;                 // ✅ 提前
            RefreshAudioTrackList();
            ForceSelectFirstValidAudioTrack();
            ApplyTrackSelection(); 
            _currentPath = ofd.FileName;

            lock (_lock)
                for (int i = 0; i < MAX_TRACKS; i++)
                    _peaks[i] = 0f;

            // 開檔時重置時間條
            _posBar.Value = 0;
            _lblTime.Text = "00:00:00 / 00:00:00";
        }

        // === 音軌清單（CheckedListBox）動態更新 ===
        private void RefreshAudioTrackList()
            {
                if (_mp == null) return;
                var desc = _mp.AudioTrackDescription;
                if (desc == null) return;

                var items = desc.Select(d => new TrackItem(d.Id, d.Name)).ToArray();

                if (_lbAudioTracks.Items.Count != items.Length)
                {
                    // 記住原本已勾的有效音軌（排除 Disable）
                    var checkedIds = _lbAudioTracks.CheckedItems.Cast<TrackItem>()
                                        .Where(t => t.Id >= 0)
                                        .Select(t => t.Id).ToHashSet();

                    _lbAudioTracks.BeginUpdate();
                    _lbAudioTracks.Items.Clear();
                    foreach (var it in items)
                    {
                        int idx = _lbAudioTracks.Items.Add(it);
                        if (checkedIds.Contains(it.Id))
                            _lbAudioTracks.SetItemChecked(idx, true);
                    }
                    _lbAudioTracks.EndUpdate();

                    // 若沒有任何有效音軌被勾，就自動勾第一條有效音軌
                    if (_lbAudioTracks.CheckedItems.Count == 0)
                        ForceSelectFirstValidAudioTrack();
                }
            }

        // 套用音軌勾選：單選＝只播該軌；多選＝建立混音 taps
        private void ApplyTrackSelection()
        {
            if (_mp == null) return;

            // 若尚未勾選任何有效音軌，先強制勾第一條有效音軌
            if (_lbAudioTracks.CheckedItems.Cast<TrackItem>().All(t => t.Id < 0))
                ForceSelectFirstValidAudioTrack();

            int tid = _lbAudioTracks.CheckedItems.Cast<TrackItem>()
                        .Where(t => t.Id >= 0)
                        .Select(t => t.Id)
                        .FirstOrDefault();

            // 沒有任何有效音軌就不處理（只會靜音）
            if (tid < 0) return;

            try { _mp.SetAudioTrack(tid); } catch { }

            if (!string.IsNullOrEmpty(_currentPath))
                StartTapForTrack(_currentPath!, tid);
        }




        // 建立/重建混音 taps（隱藏 players）
        private void BuildOrRebuildMixTaps(List<int> selectedIds)
        {
            if (_vlc == null || _mp == null || string.IsNullOrEmpty(_currentPath)) return;

            // 主播放器設成第一條，被當作「畫面 & 時間軸基準」
            try { _mp.SetAudioTrack(selectedIds[0]); } catch { }

            StopAndDisposeMixTaps();

            foreach (var tid in selectedIds.Skip(1))
            {
                var p = new MediaPlayer(_vlc);

                // 只要音訊回呼，不要任何視訊輸出
                p.SetAudioFormat("f32l", 48000, (uint)MAX_TRACKS);
                p.SetAudioCallbacks(
                    (opaque, samples, count, pts) => MixTapAudioCb(tid, samples, count),
                    PauseCb, ResumeCb, FlushCb, DrainCb);

                var m = new Media(_vlc, _currentPath, FromType.FromPath);

                // ★ 關鍵：禁止視訊輸出與 OSD 視窗
                m.AddOption(":no-video");             // 不輸出影像
                m.AddOption(":vout=dummy");           // 視訊輸出用 dummy
                m.AddOption(":video-title-show=0");   // 不顯示標題浮窗
                m.AddOption(":no-xlib");              //（可選）避免某些平台啟動 Xlib 視窗
                // 若素材含字幕、也可關掉： m.AddOption(":subsdec-disable");

                // 音訊輸出也走 dummy，因為我們要靠 callbacks 自行播
                m.AddOption(":aout=dummy");

                // 指定這個 tap 要鎖定的音軌
                m.AddOption($":audio-track={tid}");

                p.Play(m);

                _mixTaps.Add(new TrackTap(p, tid));
            }
        }


        // 隱藏 player 的 callback：把 8ch interleaved float 塞進佇列
        private void MixTapAudioCb(int trackId, IntPtr samples, uint count)
        {
            if (samples == IntPtr.Zero || count == 0) return;
            int ch = MAX_TRACKS;
            int total = (int)(count * (uint)ch);
            var arr = new float[total];
            Marshal.Copy(samples, arr, 0, arr.Length);

            lock (_mixLock)
            {
                var tap = _mixTaps.FirstOrDefault(t => t.TrackId == trackId);
                tap?.Queue.Enqueue(arr);
            }
        }

        // 主播放器的音訊回呼：更新 VU + 與其它 tap 混音 → NAudio
        private void AudioCb(IntPtr opaque, IntPtr samples, uint count, long pts)
        {
            if (samples == IntPtr.Zero || count == 0) return;

            int ch = MAX_TRACKS;
            int total = (int)(count * (uint)ch);
            if (total <= 0) return;

            var arr = new float[total];
            Marshal.Copy(samples, arr, 0, arr.Length);

            // === 1) 計算各聲道峰值 ===
            float[] peaks = new float[ch];
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < ch; c++, idx++)
                {
                    if (idx >= arr.Length) break;
                    float a = Math.Abs(arr[idx]);
                    if (a > peaks[c]) peaks[c] = a;
                }
            }
            lock (_lock)
            {
                for (int c = 0; c < ch; c++)
                    _peaks[c] = Math.Max(peaks[c], _peaks[c] * 0.9f);
                _lastUpdate = DateTime.UtcNow;
            }

            // === 2) 與其它 taps 混音，並 downmix 成 2ch 輸出到 NAudio ===
            if (_buffer == null) return;

            // 主播放器 8ch → 2ch
            var mixL = new float[count];
            var mixR = new float[count];
            Downmix8to2(arr, count, mixL, mixR);

            // 其它勾選的軌也混進來
            lock (_mixLock)
            {
                foreach (var tap in _mixTaps)
                {
                    if (!tap.Queue.TryDequeue(out var other)) continue;
                    int otherFrames = Math.Min((int)(other.Length / ch), (int)count);
                    if (otherFrames <= 0) continue;

                    Downmix8to2(other, (uint)otherFrames, mixL, mixR, accumulate: true);
                }
            }

            ApplySoftLimit(mixL);
            ApplySoftLimit(mixR);

            // 立體聲 interleaved → byte[]
            var outBytes = new byte[mixL.Length * 2 * sizeof(float)];
            int w = 0;
            for (int i = 0; i < mixL.Length; i++)
            {
                var l = mixL[i];
                var r = mixR[i];
                Buffer.BlockCopy(BitConverter.GetBytes(l), 0, outBytes, w, 4); w += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(r), 0, outBytes, w, 4); w += 4;
            }
            try { _buffer.AddSamples(outBytes, 0, outBytes.Length); } catch { }
        }

        private void PauseCb(IntPtr opaque, long pts) { }
        private void ResumeCb(IntPtr opaque, long pts) { }
        private void FlushCb(IntPtr opaque, long pts) { }
        private void DrainCb(IntPtr opaque) { }

        private void RefreshMeters()
        {
            float[] copy;
            bool stale;
            lock (_lock)
            {
                copy = _peaks.ToArray();
                stale = (DateTime.UtcNow - _lastUpdate).TotalMilliseconds > 200;
                if (stale)
                {
                    for (int i = 0; i < MAX_TRACKS; i++)
                        _peaks[i] *= 0.9f;
                }
            }

            for (int i = 0; i < MAX_TRACKS; i++)
            {
                int v = (int)(Math.Clamp(copy[i], 0f, 1f) * 1000);
                _vuBars[i].Value = Math.Max(_vuBars[i].Minimum, Math.Min(_vuBars[i].Maximum, v));
                _vuBars[i].Visible = _vuLabels[i].Visible = true;
            }
        }

        private void RefreshTimebar()
        {
            if (_mp == null) return;

            long len;
            long cur;
            try
            {
                len = _mp.Length; // ms
                cur = _mp.Time;   // ms
            }
            catch { return; }

            if (len <= 0)
            {
                _posBar.Enabled = false;
                _lblTime.Text = "00:00:00 / 00:00:00";
                return;
            }

            _posBar.Enabled = true;

            if (!_isSeeking)
            {
                double ratio = Math.Clamp(len > 0 ? (double)cur / len : 0.0, 0.0, 1.0);
                int val = (int)Math.Round(ratio * 1000.0);
                val = Math.Max(_posBar.Minimum, Math.Min(_posBar.Maximum, val));
                if (_posBar.Value != val)
                {
                    try { _posBar.Value = val; } catch { }
                }
                _lblTime.Text = $"{FmtTime(cur)} / {FmtTime(len)}";
            }
        }

        // 8ch → 2ch downmix（簡單：偶數→L，奇數→R；並平均）
        private static void Downmix8to2(float[] interleaved8, uint frames, float[] L, float[] R, bool accumulate = false)
        {
            int ch = 8;
            int idx = 0;
            for (int i = 0; i < frames; i++)
            {
                float l = 0f, r = 0f;
                for (int c = 0; c < ch; c++, idx++)
                {
                    float v = interleaved8[idx];
                    if ((c & 1) == 0) l += v; else r += v;
                }
                l /= (ch / 2f);
                r /= (ch / 2f);

                if (accumulate) { L[i] += l; R[i] += r; }
                else { L[i] = l; R[i] = r; }
            }
        }

        // 輕量軟限幅避免爆音
        private static void ApplySoftLimit(float[] x)
        {
            for (int i = 0; i < x.Length; i++)
            {
                float v = x[i];
                x[i] = v / (1f + Math.Abs(v));
            }
        }
        private void ForceSelectFirstValidAudioTrack()
        {
            if (_lbAudioTracks.Items.Count == 0) return;

            // 先清掉所有勾選
            for (int i = 0; i < _lbAudioTracks.Items.Count; i++)
                _lbAudioTracks.SetItemChecked(i, false);

            // 勾第一條 Id >= 0 的音軌（跳過 -1: Disable）
            for (int i = 0; i < _lbAudioTracks.Items.Count; i++)
            {
                var it = (TrackItem)_lbAudioTracks.Items[i];
                if (it.Id >= 0)
                {
                    _lbAudioTracks.SetItemChecked(i, true);
                    break;
                }
            }
        }
        // 時間字串：HH:mm:ss
        private static string FmtTime(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        private sealed record TrackItem(int Id, string Name)
        {
            public override string ToString() => $"{Id}: {Name}";
        }

        private void StopAndDisposeMixTaps()
        {
            lock (_mixLock)
            {
                foreach (var t in _mixTaps)
                {
                    try { t.Player.Stop(); } catch { }
                    try { t.Player.Dispose(); } catch { }
                }
                _mixTaps.Clear();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _uiTimer.Stop();
            try { _mp?.Stop(); } catch { }
            _mp?.Dispose();
            _vlc?.Dispose();

            StopAndDisposeMixTaps();

            try { _waveOut?.Stop(); } catch { }
            _waveOut?.Dispose();
        }
    }
}
