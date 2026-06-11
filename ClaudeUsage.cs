// ============================================================
//  ClaudeUsage.cs — Claude 사용량 위젯 v2.0 (winexe)
//  ClaudeUsageTray.ps1 v1.4의 C# 재구현. 콘솔 0개 — 터미널 수명과 완전 분리.
//  빌드: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
//        /nologo /target:winexe /codepage:65001 /optimize+
//        /out:ClaudeUsage.exe /r:System.Web.Extensions.dll ClaudeUsage.cs
//  외부 의존성 0 (.NET Framework 내장만 사용). 토큰은 읽기 전용 — 갱신·저장·출력 금지.
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeUsageWidget
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight, int nBottom, int nWidthEllipse, int nHeightEllipse);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    }

    class UsageWindow
    {
        public double Utilization;
        public DateTimeOffset ResetsAt;
    }

    enum FetchStatus { Ok, NoCred, Expired, Backoff, RateLimited, Error }

    class FetchResult
    {
        public FetchStatus Status;
        public int HttpCode;
        public Dictionary<string, UsageWindow> Windows;
    }

    static class TokenReader
    {
        static readonly string CredPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".claude\.credentials.json");

        // 토큰은 메모리에서만 사용. 만료 판정은 expiresAt(epoch ms)이 유일한 진실.
        public static string Read(out bool expired)
        {
            expired = false;
            try
            {
                if (!File.Exists(CredPath)) return null;
                var ser = new JavaScriptSerializer();
                var root = ser.DeserializeObject(File.ReadAllText(CredPath)) as Dictionary<string, object>;
                if (root == null) return null;
                Dictionary<string, object> o = null;
                object oauthObj;
                if (root.TryGetValue("claudeAiOauth", out oauthObj)) o = oauthObj as Dictionary<string, object>;
                if (o == null) o = root;
                object tokObj;
                if (!o.TryGetValue("accessToken", out tokObj) || !(tokObj is string)) return null;
                object expObj;
                if (o.TryGetValue("expiresAt", out expObj) && expObj != null)
                {
                    long ms = Convert.ToInt64(expObj, CultureInfo.InvariantCulture);
                    DateTimeOffset exp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    expired = exp < DateTimeOffset.UtcNow.AddSeconds(60);
                }
                return (string)tokObj;
            }
            catch { return null; }
        }
    }

    static class UsageClient
    {
        const string Url = "https://api.anthropic.com/api/oauth/usage";

        public static FetchResult Fetch()
        {
            bool expired;
            string token = TokenReader.Read(out expired);
            if (token == null) return new FetchResult { Status = FetchStatus.NoCred };
            if (expired) return new FetchResult { Status = FetchStatus.Expired };
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url);
                req.Method = "GET";
                req.Timeout = 15000;
                // 필수 헤더 4종 — User-Agent 누락 시 영구 429 (레이트리밋 버킷 분리 헤더)
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                req.ContentType = "application/json";
                req.UserAgent = "claude-code/2.0.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    var windows = ParseWindows(sr.ReadToEnd());
                    if (windows == null) return new FetchResult { Status = FetchStatus.Error, HttpCode = (int)resp.StatusCode };
                    return new FetchResult { Status = FetchStatus.Ok, Windows = windows };
                }
            }
            catch (WebException ex)
            {
                int code = 0;
                var r = ex.Response as HttpWebResponse;
                if (r != null) { code = (int)r.StatusCode; r.Close(); }
                if (code == 429) return new FetchResult { Status = FetchStatus.RateLimited, HttpCode = code };
                if (code == 401) return new FetchResult { Status = FetchStatus.Expired, HttpCode = code };
                return new FetchResult { Status = FetchStatus.Error, HttpCode = code };
            }
            catch { return new FetchResult { Status = FetchStatus.Error }; }
        }

        // 덕 타이핑 파서: non-null 숫자 utilization + 파싱 가능한 resets_at을 가진 객체만
        // 윈도우로 취급, 모르는 키는 전부 무시 — 스키마 변화에 깨지지 않음.
        static Dictionary<string, UsageWindow> ParseWindows(string json)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return null;
                var result = new Dictionary<string, UsageWindow>();
                foreach (var kv in root)
                {
                    var obj = kv.Value as Dictionary<string, object>;
                    if (obj == null) continue;
                    object u, r;
                    if (!obj.TryGetValue("utilization", out u) || u == null) continue;
                    if (!obj.TryGetValue("resets_at", out r) || !(r is string)) continue;
                    double util;
                    try { util = Convert.ToDouble(u, CultureInfo.InvariantCulture); } catch { continue; }
                    DateTimeOffset reset;
                    if (!DateTimeOffset.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset)) continue;
                    result[kv.Key] = new UsageWindow { Utilization = util, ResetsAt = reset };
                }
                return result;
            }
            catch { return null; }
        }
    }

    class HudForm : Form
    {
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW: Alt-Tab 제외
        }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    class App : ApplicationContext
    {
        const int PollSeconds = 180; // 실측 안전선 — 더 짧게 금지
        static readonly string CfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray.cfg");
        static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };

        NotifyIcon _ni;
        HudForm _hud;
        Label _dot, _txt, _txt2;
        System.Windows.Forms.Timer _pollTimer, _tickTimer, _bootTimer;
        ContextMenuStrip _menu;
        ToolStripMenuItem _miStartup;

        Dictionary<string, UsageWindow> _lastData;
        DateTime _lastOkTime = DateTime.MinValue;
        bool _lastFresh;
        FetchStatus _lastStatus = FetchStatus.Error;
        int _lastHttpCode;
        DateTime _backoffUntil = DateTime.MinValue;
        bool _warned80, _warned95;
        IntPtr _curHicon = IntPtr.Zero;
        bool _dragOn;
        Point _dragOff;
        bool _hudWanted = true, _hudDetail = true;
        bool _polling;

        public App()
        {
            BuildHud();
            BuildMenuAndTray();

            int x, y; bool show, detail;
            LoadCfg(out x, out y, out show, out detail);
            _hud.Location = new Point(x, y);
            IntPtr forceHandle = _hud.Handle; // BeginInvoke 대상 핸들 선생성
            _hudWanted = show;
            SetHudMode(detail);
            if (show) ShowHud();

            UpdateTray(); // 데이터 도착 전 회색 '?' 아이콘

            _bootTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _bootTimer.Tick += delegate { _bootTimer.Stop(); if (_hudWanted) ShowHud(); };
            _bootTimer.Start();

            PollAsync();
            _pollTimer = new System.Windows.Forms.Timer { Interval = PollSeconds * 1000 };
            _pollTimer.Tick += delegate { PollAsync(); };
            _pollTimer.Start();

            _tickTimer = new System.Windows.Forms.Timer { Interval = 30000 }; // 카운트다운 로컬 재계산 (API 호출 없음)
            _tickTimer.Tick += delegate { RenderHud(); };
            _tickTimer.Start();
        }

        // ---- 폴링 (백그라운드 스레드 — UI 멈춤 방지) ----
        void PollAsync()
        {
            if (_polling) return;
            if (DateTime.Now < _backoffUntil)
            {
                _lastFresh = false; _lastStatus = FetchStatus.Backoff;
                UpdateTray();
                return;
            }
            _polling = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                FetchResult res;
                try { res = UsageClient.Fetch(); }
                catch { res = new FetchResult { Status = FetchStatus.Error }; }
                try
                {
                    _hud.BeginInvoke((Action)delegate { _polling = false; ApplyResult(res); });
                }
                catch { _polling = false; }
            });
        }

        void ApplyResult(FetchResult res)
        {
            if (res.Status == FetchStatus.Ok)
            {
                _lastData = res.Windows;
                _lastOkTime = DateTime.Now;
                _backoffUntil = DateTime.MinValue;
            }
            else if (res.Status == FetchStatus.RateLimited)
            {
                _backoffUntil = DateTime.Now.AddMinutes(10); // 백오프 중 마지막 데이터 유지·표시
            }
            _lastFresh = (res.Status == FetchStatus.Ok);
            _lastStatus = res.Status;
            _lastHttpCode = res.HttpCode;
            UpdateTray();
        }

        // ---- 데이터 헬퍼 ----
        UsageWindow Get(string key)
        {
            if (_lastData == null) return null;
            UsageWindow w;
            return _lastData.TryGetValue(key, out w) ? w : null;
        }

        int GetPct(string key)
        {
            UsageWindow w = Get(key);
            return w == null ? -1 : (int)Math.Round(w.Utilization);
        }

        static Color StateColor(int pct)
        {
            if (pct < 0) return Color.FromArgb(125, 125, 125);
            if (pct >= 90) return Color.FromArgb(235, 90, 90);
            if (pct >= 70) return Color.FromArgb(240, 160, 50);
            return Color.FromArgb(80, 200, 120);
        }

        static string Countdown(UsageWindow w)
        {
            if (w == null) return "?";
            TimeSpan ts = w.ResetsAt.ToLocalTime() - DateTimeOffset.Now;
            if (ts.TotalMinutes <= 0) return "곧";
            if (ts.TotalHours >= 24) return string.Format("{0}일 {1}시간 후", (int)Math.Floor(ts.TotalDays), ts.Hours);
            if (ts.TotalHours >= 1) return string.Format("{0}시간 {1}분 후", (int)Math.Floor(ts.TotalHours), ts.Minutes);
            return string.Format("{0}분 후", (int)Math.Ceiling(ts.TotalMinutes));
        }

        static string ResetStamp(UsageWindow w)
        {
            if (w == null) return "?";
            DateTimeOffset r = w.ResetsAt.ToLocalTime();
            return string.Format("{0}/{1}({2}) {3:HH:mm}", r.Month, r.Day, DayNames[(int)r.DayOfWeek], r.DateTime);
        }

        // ---- 트레이 ----
        void SetTrayIcon(int pct, bool fresh)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    Color bg = StateColor(pct);
                    if (!fresh && pct >= 0) bg = Color.FromArgb(120, bg); // stale → 반투명
                    using (var brush = new SolidBrush(bg)) g.FillEllipse(brush, 0, 0, 15, 15);
                    string txt = pct < 0 ? "?" : Math.Min(pct, 99).ToString();
                    using (var font = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(txt, font, Brushes.White, new RectangleF(0, 0, 16, 16), sf);
                }
                IntPtr h = bmp.GetHicon();
                _ni.Icon = Icon.FromHandle(h);
                if (_curHicon != IntPtr.Zero) Native.DestroyIcon(_curHicon); // GDI 누수 방지
                _curHicon = h;
            }
        }

        void UpdateTray()
        {
            int pct = GetPct("five_hour");
            SetTrayIcon(pct, _lastFresh);
            RenderHud();

            string tip = "Claude";
            if (_lastData != null)
            {
                var parts = new List<string>();
                if (pct >= 0) parts.Add("세션 " + pct + "%");
                int w = GetPct("seven_day"); if (w >= 0) parts.Add("주간 " + w + "%");
                int s = GetPct("seven_day_sonnet"); if (s >= 0) parts.Add("S " + s + "%");
                int o = GetPct("seven_day_opus"); if (o >= 0) parts.Add("O " + o + "%");
                if (parts.Count > 0) tip = "Claude " + string.Join(" | ", parts);
            }
            switch (_lastStatus)
            {
                case FetchStatus.NoCred: tip = "Claude: 로그인 필요 (claude 1회 실행)"; break;
                case FetchStatus.Expired: tip += " (토큰만료-CC 열면 갱신)"; break;
                case FetchStatus.RateLimited: tip += " (요청대기)"; break;
                case FetchStatus.Backoff: tip += " (요청대기)"; break;
                case FetchStatus.Error: tip += " (오류 HTTP " + _lastHttpCode + ")"; break;
            }
            if (tip.Length > 63) tip = tip.Substring(0, 63); // NotifyIcon 63자 제한
            _ni.Text = tip;

            if (_lastFresh && pct >= 0)
            {
                if (pct >= 95 && !_warned95)
                {
                    _ni.ShowBalloonTip(10000, "Claude 사용량 경고", "세션 한도 " + pct + "% 도달! 곧 제한됩니다.", ToolTipIcon.Warning);
                    _warned95 = true; _warned80 = true;
                }
                else if (pct >= 80 && !_warned80)
                {
                    _ni.ShowBalloonTip(10000, "Claude 사용량 주의", "세션 한도 " + pct + "% 사용 중", ToolTipIcon.Warning);
                    _warned80 = true;
                }
                else if (pct < 80) { _warned80 = false; _warned95 = false; }
            }
        }

        void ShowDetail()
        {
            if (_lastData == null)
            {
                _ni.ShowBalloonTip(6000, "Claude 사용량", "아직 데이터가 없습니다.", ToolTipIcon.Info);
                return;
            }
            var L = new List<string>();
            UsageWindow fh = Get("five_hour");
            if (fh != null) L.Add(string.Format("세션(5h): {0}%  리셋 {1:HH:mm} ({2})", GetPct("five_hour"), fh.ResetsAt.ToLocalTime().DateTime, Countdown(fh)));
            UsageWindow sd = Get("seven_day");
            if (sd != null) L.Add(string.Format("주간 전체: {0}%  리셋 {1} ({2})", GetPct("seven_day"), ResetStamp(sd), Countdown(sd)));
            UsageWindow ss = Get("seven_day_sonnet");
            if (ss != null) L.Add(string.Format("주간 Sonnet: {0}%  리셋 {1}", GetPct("seven_day_sonnet"), ResetStamp(ss)));
            UsageWindow so = Get("seven_day_opus");
            if (so != null) L.Add(string.Format("주간 Opus: {0}%  리셋 {1}", GetPct("seven_day_opus"), ResetStamp(so)));
            if (_lastOkTime != DateTime.MinValue) L.Add("마지막 갱신: " + _lastOkTime.ToString("HH:mm:ss"));
            _ni.ShowBalloonTip(10000, "Claude 사용량", string.Join("\n", L), ToolTipIcon.Info);
        }

        // ---- HUD ----
        void BuildHud()
        {
            _hud = new HudForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(30, 30, 34),
                Size = new Size(240, 46),
                Opacity = 0.93
            };
            _dot = new Label { Text = "●", AutoSize = true, Location = new Point(8, 12), BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
            _txt = new Label { Text = "...", AutoSize = true, Location = new Point(26, 5), BackColor = Color.Transparent, ForeColor = Color.White, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            _txt2 = new Label { Text = "", AutoSize = true, Location = new Point(26, 25), BackColor = Color.Transparent, ForeColor = Color.FromArgb(200, 200, 205), Font = new Font("Segoe UI", 8.5f) };
            _hud.Controls.Add(_dot);
            _hud.Controls.Add(_txt);
            _hud.Controls.Add(_txt2);

            Control[] dragTargets = { _hud, _dot, _txt, _txt2 };
            foreach (Control c in dragTargets)
            {
                c.MouseDown += HudMouseDown;
                c.MouseMove += HudMouseMove;
                c.MouseUp += HudMouseUp;
                c.DoubleClick += delegate { SetHudMode(!_hudDetail); };
                c.Cursor = Cursors.SizeAll;
            }
        }

        void HudMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragOn = true;
                Point p = Cursor.Position;
                _dragOff = new Point(p.X - _hud.Left, p.Y - _hud.Top);
            }
        }

        void HudMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragOn)
            {
                Point p = Cursor.Position;
                _hud.Location = new Point(p.X - _dragOff.X, p.Y - _dragOff.Y);
            }
        }

        void HudMouseUp(object sender, MouseEventArgs e) { _dragOn = false; SaveCfg(); }

        void SetHudShape()
        {
            IntPtr hrgn = Native.CreateRoundRectRgn(0, 0, _hud.Width, _hud.Height, 16, 16);
            Region old = _hud.Region;
            _hud.Region = Region.FromHrgn(hrgn);
            Native.DeleteObject(hrgn); // FromHrgn은 복사본 사용 — 원본 HRGN 해제
            if (old != null) old.Dispose();
        }

        void ShowHud()
        {
            _hudWanted = true;
            _hud.Show();
            // TopMost 재보장: SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE|SWP_SHOWWINDOW
            Native.SetWindowPos(_hud.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x53);
            SaveCfg();
        }

        void HideHud()
        {
            _hudWanted = false;
            _hud.Hide();
            SaveCfg();
        }

        void SetHudMode(bool detail)
        {
            _hudDetail = detail;
            if (detail)
            {
                _hud.Height = 46;
                _dot.Location = new Point(8, 12);
                _txt.Location = new Point(26, 5);
                _txt2.Visible = true;
            }
            else
            {
                _hud.Height = 32;
                _dot.Location = new Point(8, 4);
                _txt.Location = new Point(26, 6);
                _txt2.Visible = false;
            }
            SetHudShape();
            RenderHud();
            SaveCfg();
        }

        void RenderHud()
        {
            if (_hud == null || _hud.IsDisposed) return;
            if (_hudWanted && !_hud.Visible) ShowHud();
            int pct = GetPct("five_hour");
            _dot.ForeColor = StateColor(pct);
            if (_hudDetail)
            {
                string l1 = pct >= 0
                    ? string.Format("세션 {0}%  ·  {1} 리셋", pct, Countdown(Get("five_hour")))
                    : "세션 --  데이터 대기중";
                var parts = new List<string>();
                int w = GetPct("seven_day"); if (w >= 0) parts.Add(string.Format("주간 {0}%", w));
                int s = GetPct("seven_day_sonnet"); if (s >= 0) parts.Add(string.Format("S {0}%", s));
                int o = GetPct("seven_day_opus"); if (o >= 0) parts.Add(string.Format("O {0}%", o));
                string l2 = parts.Count > 0 ? string.Join(" · ", parts) : "주간 --";
                UsageWindow sd = Get("seven_day");
                if (sd != null) l2 += "   리셋 " + ResetStamp(sd);
                _txt.Text = l1;
                _txt2.Text = l2;
            }
            else
            {
                string t = pct >= 0 ? pct + "%" : "--";
                int w = GetPct("seven_day"); if (w >= 0) t += string.Format("   주 {0}%", w);
                int s = GetPct("seven_day_sonnet"); if (s >= 0) t += string.Format("   S {0}%", s);
                _txt.Text = t;
            }
            _txt.ForeColor = _lastFresh ? Color.White : Color.FromArgb(150, 150, 150);
            _txt2.ForeColor = _lastFresh ? Color.FromArgb(200, 200, 205) : Color.FromArgb(130, 130, 130);
            int rt = _txt.Right;
            if (_hudDetail) rt = Math.Max(rt, _txt2.Right);
            int newW = rt + 14;
            if (_hud.Width != newW) { _hud.Width = newW; SetHudShape(); }
        }

        // ---- cfg 영속 (기존 v1.4 CSV 포맷 그대로: L,T,visible,detail) ----
        void LoadCfg(out int x, out int y, out bool show, out bool detail)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            x = wa.Right - _hud.Width - 12;
            y = wa.Bottom - _hud.Height - 8;
            show = true; detail = true;
            try
            {
                if (!File.Exists(CfgPath)) return;
                string[] parts = File.ReadAllText(CfgPath).Trim().Split(',');
                if (parts.Length >= 3)
                {
                    int cx = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                    int cy = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                    bool onScreen = false;
                    foreach (Screen sc in Screen.AllScreens)
                        if (sc.Bounds.Contains(cx, cy)) { onScreen = true; break; }
                    if (onScreen) { x = cx; y = cy; } // 모니터 밖이면 기본 위치(주 모니터 우하단) 유지
                    show = parts[2].Trim() == "1";
                }
                if (parts.Length >= 4) detail = parts[3].Trim() == "1";
            }
            catch { }
        }

        void SaveCfg()
        {
            try
            {
                File.WriteAllText(CfgPath, string.Format("{0},{1},{2},{3}",
                    _hud.Left, _hud.Top, _hudWanted ? 1 : 0, _hudDetail ? 1 : 0));
            }
            catch { }
        }

        // ---- 메뉴 + 트레이 ----
        void BuildMenuAndTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add("지금 새로고침", null, delegate { PollAsync(); });
            _menu.Items.Add("상세 보기(풍선)", null, delegate { ShowDetail(); });
            _menu.Items.Add("HUD 자세히/간단히 전환", null, delegate { SetHudMode(!_hudDetail); });
            _menu.Items.Add("작은 창 표시/숨기기", null, delegate { if (_hud.Visible) HideHud(); else ShowHud(); });
            _menu.Items.Add("claude.ai 사용량 페이지", null, delegate
            {
                try { System.Diagnostics.Process.Start("https://claude.ai/settings/usage"); } catch { }
            });
            _menu.Items.Add(new ToolStripSeparator());
            _miStartup = new ToolStripMenuItem("시작프로그램 등록");
            _miStartup.Click += delegate { ToggleStartup(); };
            _menu.Items.Add(_miStartup);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("종료", null, delegate { StopApp(); });
            _menu.Opening += delegate { _miStartup.Checked = IsStartupRegistered(); };

            _ni = new NotifyIcon();
            _ni.ContextMenuStrip = _menu;
            _ni.DoubleClick += delegate { ShowDetail(); };
            _hud.ContextMenuStrip = _menu;
            _ni.Visible = true;
        }

        // ---- 시작프로그램 (shell:startup 바로가기) ----
        static string StartupLnkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ClaudeUsage.lnk"); }
        }

        static bool IsStartupRegistered()
        {
            try { return File.Exists(StartupLnkPath); } catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                if (IsStartupRegistered())
                {
                    File.Delete(StartupLnkPath);
                    _ni.ShowBalloonTip(4000, "ClaudeUsage", "시작프로그램에서 해제했습니다.", ToolTipIcon.Info);
                }
                else
                {
                    CreateStartupShortcut();
                    _ni.ShowBalloonTip(4000, "ClaudeUsage", "시작프로그램에 등록했습니다.", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                _ni.ShowBalloonTip(6000, "ClaudeUsage", "시작프로그램 변경 실패: " + ex.Message, ToolTipIcon.Error);
            }
        }

        static void CreateStartupShortcut()
        {
            // WScript.Shell COM 후기 바인딩 — 외부 패키지 0개 유지
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartupLnkPath });
                try
                {
                    Type st = sc.GetType();
                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Claude 사용량 위젯" });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                }
                finally { Marshal.ReleaseComObject(sc); }
            }
            finally { Marshal.ReleaseComObject(shell); }
        }

        // ---- 종료 ----
        void StopApp()
        {
            SaveCfg();
            if (_pollTimer != null) _pollTimer.Stop();
            if (_tickTimer != null) _tickTimer.Stop();
            if (_bootTimer != null) _bootTimer.Stop();
            _ni.Visible = false;
            _ni.Dispose();
            _hud.Close();
            _hud.Dispose();
            if (_curHicon != IntPtr.Zero) { Native.DestroyIcon(_curHicon); _curHicon = IntPtr.Zero; }
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(false, "ClaudeUsageTrayMutex", out created)) // PS v1.4와 동일 이름 — 신구 동시 실행 차단
            {
                if (!created)
                {
                    MessageBox.Show("Claude 사용량 위젯이 이미 실행 중입니다.", "ClaudeUsage");
                    return;
                }
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }
}
