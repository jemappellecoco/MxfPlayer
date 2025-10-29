#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Concurrent;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Timer = System.Windows.Forms.Timer;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Linq;
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
        // private readonly ProgressBar[] _vuBars;
        // private readonly Label[] _vuLabels;


        private readonly Panel _rightPanel = new() { Dock = DockStyle.Right, Width = 90, Padding = new Padding(6) };
        private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private readonly WaveSpark[] _sparks = new WaveSpark[MAX_TRACKS];
        private readonly CheckBox[] _checks = new CheckBox[MAX_TRACKS];
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

        // 解析到的有效音軌（LibVLC 的 trackId）與柱子索引對應
        private readonly List<int> _trackIds = new();
        private readonly Dictionary<int,int> _id2col = new();

        // 每條音軌一個「只取電平」的 tap
        private sealed class MeterTap
        {
            public int TrackId;
            public MediaPlayer Player;
            public MeterTap(int id, MediaPlayer p) { TrackId = id; Player = p; }
        }
        private readonly List<MeterTap> _meterTaps = new();

        // === 右側小勾勾／底部清單 同步用旗標 ===
        private bool _suppressMiniChecks = false;
        private bool _suppressLbChecks = false;

        // 右側第 idx 條小勾勾（不觸發反向事件）
        private void SetMiniCheck(int idx, bool val)
        {
            if (idx < 0 || idx >= _checks.Length) return;
            if (_checks[idx] == null) return;
            _suppressMiniChecks = true;
            try { _checks[idx].Checked = val; }
            finally { _suppressMiniChecks = false; }
        }

        // 依 trackId 勾/取消底部 CheckedListBox（不觸發反向事件）
        private void SetLbCheckByTrackId(int trackId, bool val)
        {
            if (trackId < 0) return;
            _suppressLbChecks = true;
            try
            {
                for (int i = 0; i < _lbAudioTracks.Items.Count; i++)
                {
                    if (_lbAudioTracks.Items[i] is TrackItem it && it.Id == trackId)
                    {
                        _lbAudioTracks.SetItemChecked(i, val);
                        break;
                    }
                }
            }
            finally { _suppressLbChecks = false; }
        }


       private void ToggleTrack(int i, bool on)
        {
            if (_suppressMiniChecks) return;

            // 右側第 i 條對應的 trackId
            int trackId = -1;
            if (i >= 0 && i < _checks.Length && _checks[i].Tag is int tid) trackId = tid;
            if (trackId < 0) return;

            // 同步到底下 CheckedListBox
            SetLbCheckByTrackId(trackId, on);

            // 視覺高亮這條 spark
            var sp = _sparks[i];
            if (sp != null) sp.SetEnabledVisual(on);

            // 套用混音（以底下清單為主去收集）
            ApplyTrackSelection();
        }
        public Form1()
        {
            InitializeComponent();

            // 先建立 VideoView
            _video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
            Controls.Add(_video);

            // 再建立右側八根 VU
            InitRightWaveArea();

            Text = "MXF Player — 音軌 / 倍速 / 8 聲道電平圖（多選混音）";
            ClientSize = new Size(1200, 700);
            MinimumSize = new Size(1000, 600);

            // // === 視訊區 ===
            // _video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
            // Controls.Add(_video);

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

        
            // === 綁定事件 ===
            _btnOpen.Click  += OnOpen;
            _btnPlay.Click  += (s, e) => _mp?.Play();
            _btnPause.Click += (s, e) => _mp?.Pause();
            _btnStop.Click  += (s, e) => _mp?.Stop();
            _btnBack.Click  += (s, e) => { if (_mp != null) _mp.Time -= 5000; };
            _btnFwd.Click   += (s, e) => { if (_mp != null) _mp.Time += 5000; };
            _rateBar.ValueChanged += (s, e) =>
            {
                float r = _rateBar.Value / 100f;
                _mp?.SetRate(r);
                _lblRate.Text = $"Speed: {r:0.00}×";

                // 讓所有電平 tap 也跟著倍速
                foreach (var t in _meterTaps)
                {
                    try { t.Player.SetRate(r); } catch { }
                }
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

                        // 讓所有電平 tap 跟到相同時間點
                        foreach (var t in _meterTaps)
                        {
                            try { t.Player.Time = target; } catch { }
                        }
                    }
                }
                _isSeeking = false;};
            _lbAudioTracks.ItemCheck += (s, e) =>
            {
                // 等 CheckedListBox 實際更新完成後再讀取勾選結果
                if (_suppressLbChecks) return;
                BeginInvoke(new Action(ApplyTrackSelection));
            };

            // 時間條互動
            _posBar.MouseDown += (s, e) =>
            {
                _isSeeking = true;
                _wasPlayingBeforeSeek = _mp?.IsPlaying ?? false;
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

      private void InitRightWaveArea()
        {
            // 右側主 Panel：跟影片等高，固定寬度
            _rightPanel.Dock = DockStyle.Right;
            _rightPanel.Width = 260;                   // 可依需要再微調
            _rightPanel.Padding = new Padding(4, 0, 4, 0);
            _rightPanel.BackColor = Color.Black;
            Controls.Add(_rightPanel);
            _rightPanel.BringToFront();

            // 橫向排列八根
            _flow.Dock = DockStyle.Fill;
            _flow.FlowDirection = FlowDirection.LeftToRight;
            _flow.WrapContents = false;
            _flow.Padding = new Padding(0, 20, 0, 20); // 讓上下不貼邊
            _flow.AutoScroll = false;
            _flow.BackColor = Color.Black;
            _rightPanel.Controls.Add(_flow);

            // 這裡每根柱子細高、與影片同高
            for (int i = 0; i < MAX_TRACKS; i++)
            {
                var holder = new Panel
                {
                    Width  = 26,
                    Height = _video?.Height ?? 300,   // ← 容錯
                    Margin = new Padding(2, 0, 2, 0),
                    BackColor = Color.Black
                };

                var spark = new WaveSpark
                {
                    Dock = DockStyle.Fill,
                    ActiveColor = Color.Lime,
                    SilentColor = Color.FromArgb(40, 40, 40),
                    BarBorderColor = Color.DimGray
                };

                var cb = new CheckBox
                {
                    Dock = DockStyle.Bottom,
                    Text = $"音{i + 1}",
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.White,
                    Font = new Font("微軟正黑體", 7f, FontStyle.Regular),
                    Checked = (i == 0)
                };
                cb.CheckedChanged += (s, e) => ToggleTrack(i, cb.Checked);

                _sparks[i] = spark;
                _checks[i] = cb;

                holder.Controls.Add(spark);
                holder.Controls.Add(cb);
                _flow.Controls.Add(holder);
            }

            // 綁定影片尺寸變化 → 同步調整高度
            _video.SizeChanged += (s, e) =>
            {
                foreach (Control c in _flow.Controls)
                    c.Height = _video.Height;
            };
        }

        private void RebuildAllMeterTaps()
            {
                // 關掉舊的
                foreach (var t in _meterTaps)
                {
                    try { t.Player.Stop(); } catch { }
                    try { t.Player.Dispose(); } catch { }
                }
                _meterTaps.Clear();

                if (_vlc == null || string.IsNullOrEmpty(_currentPath)) return;

                foreach (var tid in _trackIds)
                {
                    var p = new MediaPlayer(_vlc);
                    // 用 2ch float32 取樣來抓峰值（夠用、計算簡單）
                    p.SetAudioFormat("f32l", 48000, 2);
                    p.SetAudioCallbacks(
                        (opaque, samples, count, pts) => TapAudioCb(tid, samples, count),
                        PauseCb, ResumeCb, FlushCb, DrainCb);

                    var m = new Media(_vlc, _currentPath, FromType.FromPath);
                    m.AddOption(":no-video");
                    m.AddOption(":vout=dummy");
                    m.AddOption(":video-title-show=0");
                    // m.AddOption(":aout=dummy");            // 不出聲，只進 callback
                    m.AddOption($":audio-track={tid}");    // 鎖定該音軌
                    p.Play(m);

                    _meterTaps.Add(new MeterTap(tid, p));
                }
            }

        private void TapAudioCb(int tid, IntPtr samples, uint count)
            {
                if (samples == IntPtr.Zero || count == 0) return;

                const int ch = 2;                         // 上面 SetAudioFormat 設的 2 聲道
                int total = (int)(count * ch);
                if (total <= 0) return;

                var arr = new float[total];
                Marshal.Copy(samples, arr, 0, total);

                float peak = 0f;
                for (int i = 0; i < total; i++)
                {
                    float a = Math.Abs(arr[i]);
                    if (a > peak) peak = a;
                }

                if (_id2col.TryGetValue(tid, out int col))
                {
                    lock (_lock)
                    {
                        _peaks[col] = Math.Max(peak, _peaks[col] * 0.9f); // 加點衰減
                        _lastUpdate = DateTime.UtcNow;
                    }
                }
            }


            
        


       private void InitLibVLC()
        {
            Core.Initialize();
            _vlc = new LibVLC("--aout=directsound");   // ✅ 指定 DirectSound（或改 "--aout=wasapi" 測試）
            _mp = new MediaPlayer(_vlc) { Volume = 100, Mute = false };  // ✅ 確認不是靜音
            _video.MediaPlayer = _mp;

            // _tap = new MediaPlayer(_vlc);
            // _tap.SetAudioFormat("f32l", 48000, (uint)MAX_TRACKS);
            // _tap.SetAudioCallbacks(AudioCb, PauseCb, ResumeCb, FlushCb, DrainCb);

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
            m.AddOption(":aout=dummy");             // 不出聲，只觸發 AudioCb
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
            BuildAudioTrackList();   // 解析出所有有效音軌 id，對應到 0..7 的柱子
            RebuildAllMeterTaps();   // 每條音軌各開一個 dummy player，只抓電平
        }
        private void BuildAudioTrackList()
        {
            _trackIds.Clear();
            _id2col.Clear();

            if (_mp == null) return;
            var desc = _mp.AudioTrackDescription;
            if (desc == null) return;

            int col = 0;
            foreach (var d in desc)
            {
                if (d.Id >= 0 && col < MAX_TRACKS)
                {
                    _trackIds.Add(d.Id);
                    _id2col[d.Id] = col++;
                }
            }

            // 固定 8 條都顯示；有的填入名稱與 Tag；沒有的填 "—"
            for (int i = 0; i < MAX_TRACKS; i++)
            {
                if (_flow.Controls.Count > i) _flow.Controls[i].Visible = true;

                _peaks[i] = 0f;

                var chk = _checks[i];
                if (chk != null)
                {
                    if (i < _trackIds.Count)
                    {
                        int tid = _trackIds[i];
                        var d = desc.First(dd => dd.Id == tid);
                        chk.Text = $"音{i+1}  {d.Name}";
                        chk.Tag = tid;           // ★ 右側小勾勾對應 trackId
                        chk.Enabled = true;
                    }
                    else
                    {
                        chk.Text = $"音{i+1}  —";
                        chk.Tag = -1;            // 無對應音軌
                        chk.Enabled = false;
                    }
                }

                // 讓 spark 用不同外觀凸顯「是否被勾選混音」
                var sp = _sparks[i];
                if (sp != null)
                {
                    bool selected = (i < _trackIds.Count) && IsTrackIdChecked(_trackIds[i]);
                    sp.SetEnabledVisual(selected); // 下面會加這個延伸方法
                }
            }
        }
        private bool IsTrackIdChecked(int trackId)
            {
                foreach (var obj in _lbAudioTracks.CheckedItems)
                {
                    var it = (TrackItem)obj;
                    if (it.Id == trackId) return true;
                }
                return false;
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

            // 1) 收集所有勾選的有效 trackId
            var selectedIds = _lbAudioTracks.CheckedItems
                                .Cast<TrackItem>()
                                .Where(t => t.Id >= 0)
                                .Select(t => t.Id)
                                .ToList();

            if (selectedIds.Count == 0)
            {
                // 至少勾第一條
                ForceSelectFirstValidAudioTrack();
                selectedIds = _lbAudioTracks.CheckedItems
                                .Cast<TrackItem>()
                                .Where(t => t.Id >= 0)
                                .Select(t => t.Id)
                                .ToList();
            }

            // 2) 同步右側小勾勾與 spark 視覺
            for (int i = 0; i < MAX_TRACKS; i++)
            {
                bool on = false;
                int tid = -1;
                if (i < _trackIds.Count) { tid = _trackIds[i]; on = selectedIds.Contains(tid); }

                SetMiniCheck(i, on);

                var sp = _sparks[i];
                if (sp != null) sp.SetEnabledVisual(on);
            }

            // 3) 主播放器播第一條被勾選的音軌；其它用隱藏 taps 混音
            try { _mp.SetAudioTrack(selectedIds[0]); } catch { }

            BuildOrRebuildMixTaps(selectedIds);
        }





        // 建立/重建混音 taps（隱藏 players）
        private void BuildOrRebuildMixTaps(List<int> selectedIds)
        {
            if (_vlc == null || _mp == null || string.IsNullOrEmpty(_currentPath)) return;

            // 主播放器設成第一條，被當作「畫面 & 時間軸基準」
            try { _mp.SetAudioTrack(selectedIds[0]); } catch { }

            StopAndDisposeMixTaps();

            foreach (var tid in _trackIds)
                {
                    var p = new MediaPlayer(_vlc);

                    // 每條音軌各自 2 聲道取樣，用來抓峰值；不需要 8 聲道，計算簡單就好
                    p.SetAudioFormat("f32l", 48000, 2);
                    p.SetAudioCallbacks(
                        (opaque, samples, count, pts) => TapAudioCb(tid, samples, count),
                        PauseCb, ResumeCb, FlushCb, DrainCb);

                    var m = new Media(_vlc, _currentPath, FromType.FromPath);
                    m.AddOption(":no-video");
                    m.AddOption(":vout=dummy");
                    m.AddOption(":video-title-show=0");

                    m.AddOption(":aout=dummy");         // ✅ 關閉 VLC 自身輸出，這個 tap 只觸發 AudioCallbacks
                    m.AddOption($":audio-track={tid}");  // ✅ 鎖定這條音軌

                    p.Play(m);

                    _meterTaps.Add(new MeterTap(tid, p));
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

        // private void RefreshMeters()
        // {
        //     float[] copy;
        //     bool stale;
        //     lock (_lock)
        //     {
        //         copy = _peaks.ToArray();
        //         stale = (DateTime.UtcNow - _lastUpdate).TotalMilliseconds > 200;
        //         if (stale)
        //         {
        //             for (int i = 0; i < MAX_TRACKS; i++)
        //                 _peaks[i] *= 0.9f;
        //         }
        //     }

        //     for (int i = 0; i < MAX_TRACKS; i++)
        //     {
        //         int v = (int)(Math.Clamp(copy[i], 0f, 1f) * 1000);
        //         _vuBars[i].Value = Math.Max(_vuBars[i].Minimum, Math.Min(_vuBars[i].Maximum, v));
        //         _vuBars[i].Visible = _vuLabels[i].Visible = true;
        //     }
        // }
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
                        _peaks[i] *= 0.9f;   // 無聲時慢慢掉
                }
            }

            for (int i = 0; i < MAX_TRACKS; i++)
            {
                var v = Math.Clamp(copy[i], 0f, 1f);
                if (_sparks[i] != null && _sparks[i].IsHandleCreated)
                    _sparks[i].SetLevel(v);  // ← 餵到右側小波形
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
