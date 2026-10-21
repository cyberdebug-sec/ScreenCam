using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal enum CaptureMode { Fullscreen, Region, Window, Game, AroundMouse, Device, AudioOnly }

    internal sealed class MainForm : Form
    {
        const int WM_HOTKEY = 0x0312, HOTKEY_RECORD = 0x51, HOTKEY_IMAGE = 0x52;
        const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDBLCLK = 0x00A3;
        const uint MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("dwmapi.dll", PreserveSig = true)] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int valueSize);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        readonly Dictionary<string, Image> skinImages = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        readonly Color navy = Color.FromArgb(37, 53, 73), navy2 = Color.FromArgb(47, 66, 90), canvas = Color.FromArgb(31, 44, 61), tile = Color.FromArgb(48, 68, 94), red = Color.FromArgb(229, 72, 77), pale = Color.FromArgb(225, 233, 242), muted = Color.FromArgb(183, 195, 208), blue = Color.FromArgb(115, 183, 255);
        Panel titleBar, toolbar, nav, tabs, content, footer;
        Label timerLabel, statusLabel, navText;
        Button recButton, pauseButton;
        System.Windows.Forms.Timer clockTimer, scheduleTimer;
        Process encoder;
        DateTime started;
        string activeFile;
        string outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenStudio");
        string ffmpegPath = "ffmpeg.exe", audioDevice = "Microphone (Realtek(R) Audio)", videoDevice = "";
        int fps = 30, crf = 23;
        bool recordMic, drawCursor = true, scheduleArmed;
        CaptureMode selectedMode = CaptureMode.Fullscreen;
        Rectangle captureRect;
        string selectedWindow;
        IntPtr selectedWindowHandle = IntPtr.Zero;
        string currentPage = "Começar";
        ComboBox micCombo, cameraCombo, fpsCombo, qualityCombo, formatCombo;
        CheckBox micCheck, cursorCheck;
        TextBox folderBox;
        DateTimePicker scheduleDate;
        NumericUpDown scheduleDuration;
        ListView fileList;
        PictureBox mediaPreview;
        Label mediaInfo, mediaPath;
        string mediaFilter = "video";
        ImageList mediaIcons;

        public MainForm()
        {
            Text = "ScreenCam";
            ClientSize = new Size(650, 600);
            MinimumSize = new Size(650, 600);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = canvas;
            ForeColor = pale;
            Font = new Font("Segoe UI", 9);
            KeyPreview = true;
            BuildShell();
            ShowHome();
            clockTimer = new System.Windows.Forms.Timer(); clockTimer.Interval = 500; clockTimer.Tick += OnClock;
            scheduleTimer = new System.Windows.Forms.Timer(); scheduleTimer.Interval = 1000; scheduleTimer.Tick += OnSchedule;
            Shown += delegate { ApplyDarkTitleBar(); RegisterHotKey(Handle, HOTKEY_RECORD, MOD_NOREPEAT, (uint)Keys.F12); RegisterHotKey(Handle, HOTKEY_IMAGE, MOD_NOREPEAT, (uint)Keys.F11); };
            FormClosing += delegate { UnregisterHotKey(Handle, HOTKEY_RECORD); UnregisterHotKey(Handle, HOTKEY_IMAGE); StopEncoder(true); };
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState != FormWindowState.Maximized)
            {
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point(unchecked((short)(lp & 0xffff)), unchecked((short)((lp >> 16) & 0xffff))));
                const int edge = 6;
                bool left = p.X < edge, right = p.X >= ClientSize.Width - edge, top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                if (top && left) { m.Result = (IntPtr)13; return; }
                if (top && right) { m.Result = (IntPtr)14; return; }
                if (bottom && left) { m.Result = (IntPtr)16; return; }
                if (bottom && right) { m.Result = (IntPtr)17; return; }
                if (left) { m.Result = (IntPtr)10; return; }
                if (right) { m.Result = (IntPtr)11; return; }
                if (top) { m.Result = (IntPtr)12; return; }
                if (bottom) { m.Result = (IntPtr)15; return; }
                if (p.Y < titleBar.Height) { m.Result = (IntPtr)2; return; }
            }
            if (m.Msg == WM_NCLBUTTONDBLCLK && m.WParam.ToInt32() == 2) { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return; }
            if (m.Msg == WM_HOTKEY) { if (m.WParam.ToInt32() == HOTKEY_RECORD) ToggleRecording(); else if (m.WParam.ToInt32() == HOTKEY_IMAGE) TakeScreenshot(); }
            base.WndProc(ref m);
        }

        void ApplyDarkTitleBar()
        {
            try { int enabled = 1; if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int)); } catch { }
        }

        void BuildShell()
        {
            titleBar = new Panel { Location = new Point(0, 0), Size = new Size(650, 36), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.FromArgb(51, 65, 85) }; Controls.Add(titleBar);
            Label logoWhite = new Label { Text = "SCREEN", Location = new Point(10, 5), AutoSize = true, ForeColor = Color.FromArgb(238, 240, 243), BackColor = titleBar.BackColor, Font = new Font("Segoe UI", 13, FontStyle.Bold) }; titleBar.Controls.Add(logoWhite);
            Label logoRed = new Label { Text = "CAM", Location = new Point(logoWhite.Right - 2, 5), AutoSize = true, ForeColor = Color.FromArgb(255, 62, 69), BackColor = titleBar.BackColor, Font = new Font("Segoe UI", 13, FontStyle.Bold) }; titleBar.Controls.Add(logoRed);
            Label admin = new Label { Text = "ADMIN", Location = new Point(logoRed.Right + 5, 8), Size = new Size(40, 17), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(63, 69, 77), Font = new Font("Segoe UI", 8) }; titleBar.Controls.Add(admin);
            Panel unregistered = new Panel { Location = new Point(admin.Right + 4, 8), Size = new Size(83, 17), BackColor = Color.FromArgb(36, 45, 56) }; titleBar.Controls.Add(unregistered);
            Label un = new Label { Text = "UN", Location = new Point(4, 1), AutoSize = true, ForeColor = Color.FromArgb(255, 211, 51), BackColor = unregistered.BackColor, Font = new Font("Segoe UI Semibold", 8, FontStyle.Bold) }; unregistered.Controls.Add(un);
            Label registered = new Label { Text = "REGISTERED", Location = new Point(23, 1), AutoSize = true, ForeColor = Color.FromArgb(220, 223, 227), BackColor = unregistered.BackColor, Font = new Font("Segoe UI", 8) }; unregistered.Controls.Add(registered);
            Button key = MakeSpriteButton("btn_key.png", 415, 4, 33, 24, 4); SetSpriteBackground(key, titleBar.BackColor); key.Click += delegate { ShowPage("Sobre"); }; titleBar.Controls.Add(key);
            Button folder = MakeSpriteButton("btn_folder.png", 456, 4, 32, 24, 4); SetSpriteBackground(folder, titleBar.BackColor); folder.Click += delegate { Directory.CreateDirectory(outputFolder); Process.Start("explorer.exe", outputFolder); }; titleBar.Controls.Add(folder);
            Button clock = MakeSpriteButton("btn_clock.png", 488, 4, 32, 24, 4); SetSpriteBackground(clock, titleBar.BackColor); clock.Click += delegate { ShowPage("Gravação Agendada"); }; titleBar.Controls.Add(clock);
            Button help = MakeSpriteButton("btn_help.png", 520, 4, 32, 24, 4); SetSpriteBackground(help, titleBar.BackColor); help.Click += delegate { SetStatus("Escolha um modo de captura ou use F12 para iniciar/parar."); }; titleBar.Controls.Add(help);
            Button minimize = MakeSpriteButton("btn_minimize.png", 550, 6, 32, 24, 4); SetSpriteBackground(minimize, titleBar.BackColor); minimize.Click += delegate { WindowState = FormWindowState.Minimized; }; titleBar.Controls.Add(minimize);
            Button maximize = MakeTitleButton("□", 582, 0, 32, 36); maximize.Click += delegate { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }; titleBar.Controls.Add(maximize);
            Button close = MakeSpriteButton("btn_close.png", 614, 6, 36, 24, 4); SetSpriteBackground(close, titleBar.BackColor); close.Click += delegate { Close(); }; titleBar.Controls.Add(close);
            titleBar.Resize += delegate { key.Left = titleBar.ClientSize.Width - 235; folder.Left = titleBar.ClientSize.Width - 194; clock.Left = titleBar.ClientSize.Width - 162; help.Left = titleBar.ClientSize.Width - 130; minimize.Left = titleBar.ClientSize.Width - 100; maximize.Left = titleBar.ClientSize.Width - 68; close.Left = titleBar.ClientSize.Width - 36; };

            toolbar = new Panel { Location = new Point(0, 36), Size = new Size(650, 59), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = navy };
            Controls.Add(toolbar);
            Button region = MakeSpriteButton("btn_screen_rectangle.png", 4, 0, 87, 58, 4); region.Click += delegate { SelectMode(CaptureMode.Region); }; toolbar.Controls.Add(region);
            Button audio = MakeSpriteButton("btn_audio_main.png", 91, 0, 72, 58, 4); audio.Click += delegate { SelectMode(CaptureMode.AudioOnly); }; toolbar.Controls.Add(audio);
            Button game = MakeSpriteButton("btn_directx.png", 163, 0, 72, 58, 4); game.Click += delegate { SelectMode(CaptureMode.Game); }; toolbar.Controls.Add(game);
            Button webcam = MakeSpriteButton("btn_webcam_screen_off.png", 235, 4, 76, 50, 4); webcam.Click += delegate { ShowPage("Dispositivo"); }; toolbar.Controls.Add(webcam);
            Button speaker = MakeSpriteButton("btn_speaker_settings_off.png", 311, 7, 40, 44, 4); speaker.Click += delegate { ShowPage("Áudio"); }; toolbar.Controls.Add(speaker);
            Button microphone = MakeSpriteButton("btn_microphone_settings_off.png", 351, 7, 40, 44, 4); microphone.Click += delegate { ShowPage("Configurações de gravação"); }; toolbar.Controls.Add(microphone);
            Button mouse = MakeSpriteButton("btn_mouse_settings_off.png", 391, 7, 42, 44, 4); mouse.Click += delegate { drawCursor = !drawCursor; SetStatus("Cursor " + (drawCursor ? "visível" : "oculto") + " na gravação."); }; toolbar.Controls.Add(mouse);
            Button textOverlay = MakeSpriteButton("btn_text_settings_off.png", 433, 7, 42, 44, 4); textOverlay.Click += delegate { ShowPage("Avançado"); }; toolbar.Controls.Add(textOverlay);
            pauseButton = MakeSpriteButton("btn_pause.png", 488, 7, 34, 44, 4); pauseButton.Enabled = false; pauseButton.Click += delegate { TogglePause(); }; toolbar.Controls.Add(pauseButton);
            timerLabel = new Label { Text = "00:00:00", ForeColor = Color.White, Location = new Point(490, 20), AutoSize = true, BackColor = navy };
            timerLabel.Visible = false; toolbar.Controls.Add(timerLabel);
            recButton = MakeSpriteButton("btn_record.png", 532, 0, 66, 66, 4); recButton.Click += delegate { ToggleRecording(); }; recButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            toolbar.Controls.Add(recButton);
            Button screenshot = MakeSpriteButton("btn_capture.png", 612, 7, 34, 44, 4); screenshot.Anchor = AnchorStyles.Top | AnchorStyles.Right; screenshot.Click += delegate { TakeScreenshot(); }; toolbar.Controls.Add(screenshot);
            timerLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            toolbar.Resize += delegate {
                int rightGroup = Math.Max(235, toolbar.ClientSize.Width - 412);
                webcam.Left = rightGroup;
                speaker.Left = rightGroup + 76;
                microphone.Left = rightGroup + 116;
                mouse.Left = rightGroup + 156;
                textOverlay.Left = rightGroup + 198;
                recButton.Left = toolbar.ClientSize.Width - 104;
                screenshot.Left = toolbar.ClientSize.Width - 38;
                timerLabel.Left = toolbar.ClientSize.Width - 160;
                pauseButton.Left = toolbar.ClientSize.Width - 162;
            };

            nav = new Panel { Location = new Point(0, 97), Size = new Size(650, 31), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.FromArgb(29, 41, 57) }; Controls.Add(nav);
            Button home = MakeSpriteButton("btn_page_home_tab.png", 0, 0, 48, 30, 4); home.Click += delegate { ShowHome(); }; nav.Controls.Add(home);
            Button settings = MakeSpriteButton("btn_page_setting_tab.png", 48, 0, 48, 30, 4); settings.Click += delegate { ShowPage("Configurações de gravação"); }; nav.Controls.Add(settings);
            Button videos = MakeSpriteButton("btn_page_player_tab.png", 96, 0, 48, 30, 4); videos.Click += delegate { ShowPage("Vídeos"); }; nav.Controls.Add(videos);
            navText = new Label { Text = "▦   Por favor, selecione um modo de gravação", Location = new Point(177, 7), AutoSize = true, ForeColor = muted, BackColor = nav.BackColor }; nav.Controls.Add(navText);

            tabs = new Panel { Location = new Point(0, 128), Size = new Size(650, 54), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = canvas }; Controls.Add(tabs);
            tabs.Resize += LayoutTabs;
            content = new Panel { Location = new Point(0, 182), Size = new Size(650, 396), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = canvas }; Controls.Add(content);
            footer = new Panel { Location = new Point(0, 578), Size = new Size(650, 22), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.FromArgb(39, 56, 77) }; Controls.Add(footer);
            statusLabel = new Label { Text = "Pronto", Location = new Point(14, 6), AutoSize = true, ForeColor = muted, BackColor = footer.BackColor }; footer.Controls.Add(statusLabel);
            Label promo = new Label { Text = "▣   ScreenCam · captura de tela e edição", Location = new Point(210, 6), AutoSize = true, ForeColor = muted, BackColor = footer.BackColor }; footer.Controls.Add(promo);
        }

        Button MakeButton(string text, int x, int y, int w, int h, Color back, Color fore)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Font = new Font("Segoe UI", 9), UseVisualStyleBackColor = false, Margin = Padding.Empty };
        }

        Button MakeSpriteButton(string file, int x, int y, int w, int h, int frames)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "bandicam", file);
            Image strip;
            if (!skinImages.TryGetValue(file, out strip)) { strip = Image.FromFile(path); skinImages[file] = strip; }
            Button b = new Button { Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, BackColor = navy, ForeColor = pale, UseVisualStyleBackColor = false, Margin = Padding.Empty, TabStop = false };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = navy; b.FlatAppearance.MouseDownBackColor = navy;
            b.Paint += delegate(object sender, PaintEventArgs e) { int frameWidth = strip.Width / frames; e.Graphics.DrawImage(strip, new Rectangle(0, 0, w, h), new Rectangle(frameWidth, 0, frameWidth, strip.Height), GraphicsUnit.Pixel); };
            b.MouseEnter += delegate { b.Invalidate(); }; b.MouseLeave += delegate { b.Invalidate(); };
            return b;
        }

        Button MakeTitleButton(string text, int x, int y, int w, int h)
        {
            Button b = MakeButton(text, x, y, w, h, Color.FromArgb(51, 65, 85), Color.FromArgb(227, 231, 237));
            b.Font = new Font("Segoe UI", 12); b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 87, 107);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(92, 106, 126);
            return b;
        }

        void SetSpriteBackground(Button b, Color color)
        {
            b.BackColor = color; b.FlatAppearance.MouseOverBackColor = color; b.FlatAppearance.MouseDownBackColor = color;
        }

        void BuildTabs(string active)
        {
            tabs.Controls.Clear(); string[] names = { "Começar", "Vídeo", "Áudios", "Imagem", "✦ AI Studio" }; int[] widths = { 74, 74, 74, 74, 92 };
            int total = widths.Sum() + 4 * 10; int x = Math.Max(0, (tabs.ClientSize.Width - total) / 2);
            for (int i = 0; i < names.Length; i++) { string n = names[i]; Button b = MakeButton(n, x, 16, widths[i], 34, canvas, n == active ? blue : pale); x += widths[i] + 10; b.FlatAppearance.BorderSize = 0; b.Anchor = AnchorStyles.Top; b.Click += delegate { if (n == "Começar") ShowHome(); else if (n == "Vídeo") ShowPage("Vídeos"); else if (n == "Áudios") ShowPage("Áudios"); else if (n == "Imagem") ShowPage("Imagens"); else ShowPage("Sobre"); }; tabs.Controls.Add(b); }
        }

        void LayoutTabs(object sender, EventArgs e)
        {
            int total = 0; foreach (Control c in tabs.Controls) total += c.Width;
            total += Math.Max(0, tabs.Controls.Count - 1) * 10;
            int x = Math.Max(0, (tabs.ClientSize.Width - total) / 2);
            foreach (Control c in tabs.Controls) { c.Left = x; x += c.Width + 10; }
        }

        Label AddLabel(Control parent, string text, int x, int y, int size, Color color, bool bold)
        {
            Label l = new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = color, BackColor = parent.BackColor, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) }; parent.Controls.Add(l); return l;
        }

        Button AddCard(string title, string icon, int x, int y, Action click)
        {
            Panel card = new Panel { Location = new Point(x, y), Size = new Size(145, 170), BackColor = tile, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
            content.Controls.Add(card);
            Panel art = new Panel { Location = new Point(0, 0), Size = new Size(143, 122), BackColor = Color.FromArgb(58, 82, 111) };
            art.Paint += delegate(object sender, PaintEventArgs e) { DrawThumbnail(e.Graphics, title); };
            card.Controls.Add(art);
            Label caption = new Label { Text = title, Location = new Point(8, 137), Size = new Size(128, 24), AutoSize = false, ForeColor = Color.White, BackColor = tile, Font = new Font("Segoe UI Semibold", 8), Cursor = Cursors.Hand };
            card.Controls.Add(caption);
            if (click != null) { card.Click += delegate { click(); }; caption.Click += delegate { click(); }; art.Click += delegate { click(); }; }
            return null;
        }

        void DrawThumbnail(Graphics g, string kind)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 35, 51))) g.FillRectangle(b, 10, 15, 124, 76);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(145, 183, 216))) g.FillRectangle(b, 15, 20, 114, 66);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 111, 145))) g.FillRectangle(b, 15, 42, 114, 44);
            if (kind == "Área do Retângulo") { using (Pen p = new Pen(Color.White, 2)) { p.DashPattern = new float[] { 3, 2 }; g.DrawRectangle(p, 35, 43, 73, 32); } }
            else if (kind == "Janela Específica") { using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 230, 239))) { g.FillRectangle(b, 25, 37, 56, 38); g.FillRectangle(b, 50, 28, 65, 45); } }
            else { Point[] mountain = { new Point(18, 80), new Point(48, 48), new Point(75, 75), new Point(98, 47), new Point(128, 80) }; using (SolidBrush b = new SolidBrush(Color.FromArgb(95, 125, 152))) g.FillPolygon(b, mountain); if (kind == "Gravação de Jogo") using (SolidBrush b = new SolidBrush(Color.FromArgb(22, 30, 42))) g.FillEllipse(b, 53, 59, 43, 22); }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 35, 51))) { g.FillRectangle(b, 69, 91, 7, 13); g.FillRectangle(b, 54, 104, 37, 4); }
        }

        void ShowHome()
        {
            currentPage = "Começar"; BuildTabs("Começar"); ClearContent();
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "get-started.html");
            if (!File.Exists(htmlPath)) { AddLabel(content, "Recursos de interface não encontrados em ui\\get-started.html.", 23, 37, 10, Color.White, true); return; }
            WebBrowser browser = new WebBrowser { Dock = DockStyle.None, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = false, AllowWebBrowserDrop = false, WebBrowserShortcutsEnabled = false };
            browser.Navigating += delegate(object sender, WebBrowserNavigatingEventArgs e) { if (e.Url.Scheme.Equals("bandicam-action", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; HandleBandicamAction(e.Url); } };
            browser.Location = new Point(21, 0); browser.Size = new Size(Math.Max(1, content.ClientSize.Width - 42), content.ClientSize.Height); browser.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            content.Controls.Add(browser); browser.Navigate(htmlPath);
        }

        void HandleBandicamAction(Uri uri)
        {
            string action = (uri.Host + uri.AbsolutePath).Trim('/').ToLowerInvariant();
            switch (action)
            {
                case "mode/rectangle": SelectRegion(); break;
                case "mode/fullscreen": selectedMode = CaptureMode.Fullscreen; navText.Text = "▦   Tela Cheia"; SetStatus("Modo Tela Cheia selecionado. Pressione REC."); break;
                case "mode/specific-window": ChooseWindow(CaptureMode.Window); break;
                case "mode/game": ChooseWindow(CaptureMode.Game); break;
                case "mode/device": ShowPage("Dispositivo"); break;
                case "mode/around-mouse": SelectAroundMouse(); break;
                case "mode/audio-only": selectedMode = CaptureMode.AudioOnly; navText.Text = "▦   Somente Áudio"; SetStatus("Modo Somente Áudio selecionado."); break;
                case "mode/play-edit": ShowPage("Vídeos"); break;
                case "settings/advanced": ShowPage("Avançado"); break;
                case "settings/recording": ShowPage("Configurações de gravação"); break;
                case "settings/video-format": ShowPage("Formato"); break;
                case "settings/scheduled-recording": ShowPage("Gravação Agendada"); break;
                case "help/recording-mode": SetStatus("Escolha um dos modos de captura ou use F12 para iniciar/parar."); break;
                default: SetStatus("Ação não disponível nesta versão."); break;
            }
        }

        void ClearContent()
        {
            while (content.Controls.Count > 0) { Control c = content.Controls[0]; content.Controls.RemoveAt(0); c.Dispose(); }
        }

        void AddLink(string text, int x, int y, Action action) { Button b = MakeButton(text, x, y, 172, 25, canvas, muted); b.FlatAppearance.BorderSize = 0; b.TextAlign = ContentAlignment.MiddleLeft; b.Click += delegate { action(); }; content.Controls.Add(b); }

        void ShowPage(string page)
        {
            currentPage = page; ClearContent(); BuildTabs(page == "Vídeos" ? "Vídeo" : page == "Imagens" ? "Imagem" : (page == "Áudio" || page == "Áudios") ? "Áudios" : "Começar");
            AddLabel(content, page, 23, 26, 15, Color.White, true);
            if (page == "Vídeos" || page == "Imagens" || page == "Áudios") { ShowMediaList(page); return; }
            if (page == "Dispositivo") { ShowDeviceSettings(); return; }
            if (page == "Formato") { ShowFormatSettings(); return; }
            if (page == "Áudio") { ShowAudioSettings(); return; }
            if (page == "Gravação Agendada") { ShowScheduleSettings(); return; }
            if (page == "Configurações de gravação" || page == "Avançado") { ShowRecordingSettings(); return; }
            if (page == "Sobre") { AddLabel(content, "Gravador de tela para Windows · captura com FFmpeg", 23, 76, 9, muted, false); return; }
            ShowFormatSettings();
        }

        void ShowFormatSettings()
        {
            AddLabel(content, "Formato de vídeo", 23, 78, 9, muted, false);
            formatCombo = new ComboBox { Location = new Point(23, 102), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = navy, ForeColor = Color.White }; formatCombo.Items.AddRange(new object[] { "MP4 (H.264)", "AVI (MPEG-4)" }); formatCombo.SelectedIndex = 0; content.Controls.Add(formatCombo);
            AddLabel(content, "Taxa de quadros (FPS)", 23, 147, 9, muted, false);
            fpsCombo = new ComboBox { Location = new Point(23, 171), Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = navy, ForeColor = Color.White }; fpsCombo.Items.AddRange(new object[] { "10", "15", "24", "30", "48", "60" }); fpsCombo.SelectedItem = fps.ToString(); content.Controls.Add(fpsCombo);
            AddLabel(content, "Qualidade H.264 (CRF)", 23, 216, 9, muted, false);
            qualityCombo = new ComboBox { Location = new Point(23, 240), Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = navy, ForeColor = Color.White }; qualityCombo.Items.AddRange(new object[] { "18", "20", "23", "26", "30", "35" }); qualityCombo.SelectedItem = crf.ToString(); content.Controls.Add(qualityCombo);
            AddLabel(content, "Menor valor = mais qualidade e arquivos maiores.", 145, 244, 8, muted, false);
            AddLabel(content, "Pasta de saída", 23, 282, 9, muted, false);
            TextBox folder = NewTextBox(outputFolder, 23, 306, 430); content.Controls.Add(folder); folderBox = folder;
            Button browse = MakeButton("Procurar…", 466, 304, 140, 28, navy2, pale); browse.Click += delegate { using (FolderBrowserDialog d = new FolderBrowserDialog()) { d.SelectedPath = folder.Text; if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath; } }; content.Controls.Add(browse);
            Button save = MakeButton("Salvar", 23, 350, 110, 31, Color.FromArgb(70, 136, 215), Color.White); save.Click += delegate { outputFolder = folder.Text; fps = int.Parse((string)fpsCombo.SelectedItem); crf = int.Parse((string)qualityCombo.SelectedItem); SetStatus("Formato e qualidade salvos."); }; content.Controls.Add(save);
        }

        void ShowRecordingSettings()
        {
            AddLabel(content, "Opções de gravação", 23, 77, 9, muted, false);
            micCheck = new CheckBox { Text = "Gravar microfone junto com o vídeo", Checked = recordMic, Location = new Point(23, 104), AutoSize = true, ForeColor = pale, BackColor = canvas }; content.Controls.Add(micCheck);
            Label mic = AddLabel(content, "Dispositivo de entrada", 23, 139, 9, muted, false);
            micCombo = NewDeviceCombo(false, 23, 162, 430); content.Controls.Add(micCombo);
            cursorCheck = new CheckBox { Text = "Mostrar cursor do mouse", Checked = drawCursor, Location = new Point(23, 205), AutoSize = true, ForeColor = pale, BackColor = canvas }; content.Controls.Add(cursorCheck);
            AddLabel(content, "Atalhos globais: F12 iniciar/parar · F11 captura de imagem", 23, 244, 9, muted, false);
            AddLabel(content, "Para capturar áudio do sistema, escolha um dispositivo loopback/mixagem estéreo disponível.", 23, 276, 8, muted, false);
            Button save = MakeButton("Salvar", 23, 318, 110, 31, Color.FromArgb(70, 136, 215), Color.White); save.Click += delegate { recordMic = micCheck.Checked; if (micCombo.SelectedItem != null) audioDevice = micCombo.SelectedItem.ToString(); drawCursor = cursorCheck.Checked; SetStatus("Configurações de gravação salvas."); }; content.Controls.Add(save);
            Button scheduled = MakeButton("Configurar gravação agendada…", 145, 318, 210, 31, navy2, pale); scheduled.Click += delegate { ShowPage("Gravação Agendada"); }; content.Controls.Add(scheduled);
        }

        void ShowAudioSettings()
        {
            AddLabel(content, "Gravação de áudio", 23, 78, 9, muted, false);
            CheckBox c = new CheckBox { Text = "Incluir microfone nas gravações", Checked = recordMic, Location = new Point(23, 106), AutoSize = true, ForeColor = pale, BackColor = canvas }; content.Controls.Add(c);
            ComboBox devices = NewDeviceCombo(false, 23, 141, 430); content.Controls.Add(devices);
            Button save = MakeButton("Salvar", 23, 186, 110, 31, Color.FromArgb(70, 136, 215), Color.White); save.Click += delegate { recordMic = c.Checked; if (devices.SelectedItem != null) audioDevice = devices.SelectedItem.ToString(); SetStatus("Áudio configurado."); }; content.Controls.Add(save);
        }

        void ShowDeviceSettings()
        {
            AddLabel(content, "Câmera / dispositivo de vídeo", 23, 77, 9, muted, false);
            cameraCombo = NewDeviceCombo(true, 23, 102, 480); content.Controls.Add(cameraCombo);
            AddLabel(content, "Escolha a câmera conectada. A captura usa o dispositivo DirectShow.", 23, 139, 8, muted, false);
            Button start = MakeButton("Iniciar captura de dispositivo", 23, 176, 220, 36, red, Color.White); start.Click += delegate { if (cameraCombo.SelectedItem != null) { videoDevice = cameraCombo.SelectedItem.ToString(); selectedMode = CaptureMode.Device; StartRecording(); } }; content.Controls.Add(start);
        }

        ComboBox NewDeviceCombo(bool video, int x, int y, int width)
        {
            List<string> found = EnumerateDirectShow(video);
            ComboBox c = new ComboBox { Location = new Point(x, y), Width = width, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = navy, ForeColor = Color.White };
            foreach (string item in found) c.Items.Add(item);
            string saved = video ? videoDevice : audioDevice;
            int ix = c.Items.IndexOf(saved); if (ix >= 0) c.SelectedIndex = ix; else if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        List<string> EnumerateDirectShow(bool video)
        {
            List<string> names = new List<string>();
            try {
                Process p = new Process(); p.StartInfo = new ProcessStartInfo { FileName = ResolveFfmpeg(), Arguments = "-list_devices true -f dshow -i dummy", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                p.Start(); string err = p.StandardError.ReadToEnd(); string output = p.StandardOutput.ReadToEnd(); p.WaitForExit(4000); string all = err + output;
                foreach (Match m in Regex.Matches(all, "\\\"([^\\\"]+)\\\" \\((video|audio)\\)", RegexOptions.IgnoreCase)) if (m.Groups[2].Value.Equals(video ? "video" : "audio", StringComparison.OrdinalIgnoreCase)) names.Add(m.Groups[1].Value);
                if (names.Count == 0) foreach (Match m in Regex.Matches(all, "\\\"([^\\\"]+)\\\" \\((video|audio)\\)", RegexOptions.IgnoreCase)) if (m.Groups[2].Value.Equals(video ? "video" : "audio", StringComparison.OrdinalIgnoreCase)) names.Add(m.Groups[1].Value);
                p.Dispose();
            } catch { }
            if (!video && names.Count == 0) names.Add(audioDevice);
            return names.Distinct().ToList();
        }

        void ShowScheduleSettings()
        {
            AddLabel(content, "Agendar uma gravação", 23, 77, 9, muted, false);
            scheduleDate = new DateTimePicker { Location = new Point(23, 106), Width = 225, Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm:ss", Value = DateTime.Now.AddMinutes(5) }; content.Controls.Add(scheduleDate);
            AddLabel(content, "Duração (minutos)", 23, 150, 9, muted, false);
            scheduleDuration = new NumericUpDown { Location = new Point(23, 174), Width = 100, Minimum = 1, Maximum = 480, Value = 30 }; content.Controls.Add(scheduleDuration);
            Button arm = MakeButton("Agendar", 23, 216, 120, 34, Color.FromArgb(70, 136, 215), Color.White); arm.Click += delegate { scheduleArmed = true; scheduleTimer.Start(); SetStatus("Gravação agendada para " + scheduleDate.Value.ToString("g")); }; content.Controls.Add(arm);
            Button cancel = MakeButton("Cancelar agenda", 153, 216, 145, 34, navy2, pale); cancel.Click += delegate { scheduleArmed = false; scheduleTimer.Stop(); SetStatus("Agendamento cancelado."); }; content.Controls.Add(cancel);
        }

        TextBox NewTextBox(string text, int x, int y, int w) { return new TextBox { Text = text, Location = new Point(x, y), Width = w, BackColor = Color.FromArgb(32, 43, 58), ForeColor = pale, BorderStyle = BorderStyle.FixedSingle }; }

        void ShowMediaList(string page)
        {
            mediaFilter = page == "Imagens" ? "image" : page == "Áudios" ? "audio" : "video";
            Directory.CreateDirectory(outputFolder);
            mediaPath = AddLabel(content, outputFolder, 22, 5, 8, muted, false);
            mediaPath.AutoEllipsis = true; mediaPath.Size = new Size(content.ClientSize.Width - 44, 20); mediaPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            fileList = new ListView { Location = new Point(21, 31), Size = new Size(Math.Max(280, content.ClientSize.Width - 218), content.ClientSize.Height - 48), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BackColor = Color.FromArgb(32, 43, 58), ForeColor = pale, BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.None };
            fileList.Columns.Add("Arquivo", Math.Max(190, content.ClientSize.Width - 300)); fileList.Columns.Add("Tamanho", 78);
            mediaIcons = new ImageList { ImageSize = new Size(34, 25), ColorDepth = ColorDepth.Depth32Bit };
            fileList.SmallImageList = mediaIcons; fileList.SelectedIndexChanged += delegate { UpdateMediaPreview(); }; fileList.DoubleClick += delegate { OpenSelected(); }; content.Controls.Add(fileList);
            Panel side = new Panel { Location = new Point(content.ClientSize.Width - 185, 31), Size = new Size(164, content.ClientSize.Height - 48), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right, BackColor = canvas }; content.Controls.Add(side);
            mediaPreview = new PictureBox { Location = new Point(5, 5), Size = new Size(150, 105), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 39, 54), BorderStyle = BorderStyle.FixedSingle }; side.Controls.Add(mediaPreview);
            AddLabel(side, "Informações de mídia", 5, 119, 9, pale, true);
            mediaInfo = new Label { Location = new Point(5, 142), Size = new Size(150, 70), AutoEllipsis = true, ForeColor = muted, BackColor = canvas, Font = new Font("Segoe UI", 8), Text = "Selecione um arquivo" }; side.Controls.Add(mediaInfo);
            Button open = MakeButton("Abrir", 5, 223, 70, 29, navy2, pale); open.Click += delegate { OpenSelected(); }; side.Controls.Add(open);
            Button edit = MakeButton(mediaFilter == "image" ? "Editar" : "Reproduzir", 82, 223, 73, 29, navy2, pale); edit.Click += delegate { OpenSelected(); }; side.Controls.Add(edit);
            Button remove = MakeButton("Excluir", 5, 260, 70, 27, canvas, muted); remove.FlatAppearance.BorderSize = 0; remove.Click += delegate { DeleteSelected(); }; side.Controls.Add(remove);
            Button refresh = MakeButton("↻", 82, 260, 35, 27, canvas, muted); refresh.FlatAppearance.BorderSize = 0; refresh.Click += delegate { RefreshMedia(mediaFilter); }; side.Controls.Add(refresh);
            Label sort = AddLabel(content, "Classificar por data  ▾", Math.Max(220, content.ClientSize.Width - 188), content.ClientSize.Height - 14, 8, muted, false); sort.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            RefreshMedia(mediaFilter);
        }

        string[] MediaPatterns(string kind)
        {
            if (kind == "image") return new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" };
            if (kind == "audio") return new[] { "*.wav", "*.mp3", "*.m4a", "*.aac", "*.wma", "*.ogg" };
            return new[] { "*.mp4", "*.avi", "*.mkv", "*.mov", "*.wmv" };
        }

        void RefreshMedia(string kind)
        {
            if (fileList == null || fileList.IsDisposed) return;
            fileList.BeginUpdate(); fileList.Items.Clear(); mediaIcons.Images.Clear();
            try {
                Directory.CreateDirectory(outputFolder);
                var files = MediaPatterns(kind).SelectMany(pattern => Directory.GetFiles(outputFolder, pattern)).Distinct().OrderByDescending(File.GetLastWriteTime);
                foreach (string path in files) {
                    Image icon = null;
                    if (kind == "image") try { using (Image src = Image.FromFile(path)) { icon = new Bitmap(src, new Size(34, 25)); } } catch { }
                    if (icon == null) { Bitmap bmp = new Bitmap(34, 25); using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(58, 82, 111)); using (Font f = new Font("Segoe UI", 7, FontStyle.Bold)) using (Brush b = new SolidBrush(Color.White)) g.DrawString(kind == "audio" ? "♫" : "▶", f, b, 12, 5); } icon = bmp; }
                    mediaIcons.Images.Add(icon);
                    FileInfo fi = new FileInfo(path); ListViewItem item = new ListViewItem(fi.Name, mediaIcons.Images.Count - 1); item.Tag = path; item.SubItems.Add(FormatSize(fi.Length)); fileList.Items.Add(item);
                }
            } catch { }
            fileList.EndUpdate(); UpdateMediaPreview();
        }

        string FormatSize(long n) { string[] u = { "B", "KB", "MB", "GB" }; double v = n; int i = 0; while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; } return v.ToString(i == 0 ? "0" : "0.0") + " " + u[i]; }
        string SelectedMediaPath() { return fileList != null && fileList.SelectedItems.Count > 0 ? fileList.SelectedItems[0].Tag as string : null; }
        void UpdateMediaPreview()
        {
            string path = SelectedMediaPath(); if (mediaPreview == null || mediaPreview.IsDisposed) return;
            if (mediaPreview.Image != null) { Image old = mediaPreview.Image; mediaPreview.Image = null; old.Dispose(); }
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) { if (mediaInfo != null) mediaInfo.Text = "Selecione um arquivo"; return; }
            FileInfo fi = new FileInfo(path);
            if (mediaFilter == "image") try { using (Image src = Image.FromFile(path)) mediaPreview.Image = new Bitmap(src); } catch { }
            if (mediaInfo != null) mediaInfo.Text = Path.GetFileName(path) + "\r\n" + FormatSize(fi.Length) + "\r\n" + fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
        }
        void OpenSelected() { string path = SelectedMediaPath(); if (!String.IsNullOrEmpty(path)) try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } }
        void DeleteSelected() { string path = SelectedMediaPath(); if (String.IsNullOrEmpty(path)) return; if (MessageBox.Show(this, "Excluir este arquivo?\r\n" + Path.GetFileName(path), "Confirmar exclusão", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { try { File.Delete(path); RefreshMedia(mediaFilter); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } } }

        void SelectMode(CaptureMode mode)
        {
            selectedMode = mode;
            if (mode == CaptureMode.Region) { SelectRegion(); return; }
            if (mode == CaptureMode.Device) { ShowPage("Dispositivo"); return; }
            if (mode == CaptureMode.Game || mode == CaptureMode.Window) { ChooseWindow(mode); return; }
            navText.Text = mode == CaptureMode.AudioOnly ? "▦   Modo somente Áudio - você só pode gravar áudio." : "▦   " + ModeName(mode); SetStatus(mode == CaptureMode.AudioOnly ? "Modo somente Áudio - você só pode gravar áudio." : "Modo " + ModeName(mode) + " selecionado.");
        }

        string ModeName(CaptureMode m) { switch (m) { case CaptureMode.Fullscreen: return "Tela Cheia"; case CaptureMode.Region: return "Área do Retângulo"; case CaptureMode.Window: return "Janela Específica"; case CaptureMode.Game: return "Gravação de Jogo"; case CaptureMode.AroundMouse: return "Ao redor do Mouse"; case CaptureMode.Device: return "Gravação de Dispositivo"; default: return "Somente Áudio"; } }

        void SelectRegion()
        {
            using (RegionForm r = new RegionForm()) if (r.ShowDialog(this) == DialogResult.OK && r.Selection.Width > 20 && r.Selection.Height > 20) { captureRect = r.Selection; selectedMode = CaptureMode.Region; navText.Text = "▦   Área do Retângulo"; SetStatus("Área selecionada: " + captureRect.Width + " × " + captureRect.Height + ". Pressione REC."); }
        }

        void SelectAroundMouse()
        {
            Point p = Cursor.Position; Rectangle screen = Screen.PrimaryScreen.Bounds; int w = Math.Min(800, screen.Width), h = Math.Min(450, screen.Height);
            captureRect = new Rectangle(Math.Max(screen.Left, Math.Min(p.X - w / 2, screen.Right - w)), Math.Max(screen.Top, Math.Min(p.Y - h / 2, screen.Bottom - h)), w, h);
            selectedMode = CaptureMode.AroundMouse; navText.Text = "▦   Ao redor do Mouse"; SetStatus("Região fixa centrada na posição atual do cursor. Pressione REC.");
        }

        sealed class WindowOption
        {
            public string Title; public IntPtr Handle;
            public override string ToString() { return Title; }
        }

        void ChooseWindow(CaptureMode mode)
        {
            List<WindowOption> windows = new List<WindowOption>();
            foreach (Process p in Process.GetProcesses()) try { if (!String.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowHandle != IntPtr.Zero) windows.Add(new WindowOption { Title = p.MainWindowTitle, Handle = p.MainWindowHandle }); } catch { }
            using (Form f = new Form { Text = mode == CaptureMode.Game ? "Selecione uma janela de jogo" : "Selecione uma janela", Size = new Size(420, 390), StartPosition = FormStartPosition.CenterParent, BackColor = canvas, ForeColor = pale }) {
                ListBox l = new ListBox { Location = new Point(12, 12), Size = new Size(380, 285), BackColor = Color.FromArgb(32, 43, 58), ForeColor = pale }; foreach (WindowOption w in windows.OrderBy(x => x.Title)) l.Items.Add(w); f.Controls.Add(l);
                Button ok = MakeButton("Selecionar", 12, 310, 110, 30, navy2, pale); ok.Click += delegate { if (l.SelectedItem != null) { WindowOption w = (WindowOption)l.SelectedItem; selectedWindow = w.Title; selectedWindowHandle = w.Handle; f.DialogResult = DialogResult.OK; f.Close(); } }; f.Controls.Add(ok);
                if (f.ShowDialog(this) == DialogResult.OK) { selectedMode = mode; navText.Text = "▦   " + ModeName(mode) + ": " + selectedWindow; SetStatus("Janela selecionada. Pressione REC para iniciar."); }
            }
        }

        Rectangle BringWindowForwardAndGetBounds()
        {
            if (selectedWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Selecione novamente a janela que deseja gravar.");
            if (IsIconic(selectedWindowHandle)) ShowWindow(selectedWindowHandle, 9); // SW_RESTORE
            else ShowWindow(selectedWindowHandle, 5); // SW_SHOW
            SetForegroundWindow(selectedWindowHandle);
            System.Threading.Thread.Sleep(350);
            RECT r;
            if (!GetWindowRect(selectedWindowHandle, out r)) throw new InvalidOperationException("Não foi possível obter a posição da janela selecionada.");
            Rectangle bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            Rectangle screen = Screen.FromHandle(selectedWindowHandle).Bounds;
            bounds = Rectangle.Intersect(bounds, screen);
            bounds.Width &= ~1; bounds.Height &= ~1;
            if (bounds.Width < 2 || bounds.Height < 2) throw new InvalidOperationException("A janela está fora da tela ou minimizada.");
            return bounds;
        }

        void ToggleRecording() { if (encoder != null && !encoder.HasExited) StopEncoder(false); else StartRecording(); }
        void TogglePause() { if (encoder == null || encoder.HasExited) return; try { encoder.StandardInput.WriteLine("p"); encoder.StandardInput.Flush(); SetStatus("Pausa alternada pelo FFmpeg."); } catch { } }

        void StartRecording()
        {
            if (selectedMode == CaptureMode.Region && captureRect.Width == 0) { SelectRegion(); if (captureRect.Width == 0) return; }
            if (selectedMode == CaptureMode.Window || selectedMode == CaptureMode.Game) if (String.IsNullOrEmpty(selectedWindow)) { ChooseWindow(selectedMode); if (String.IsNullOrEmpty(selectedWindow)) return; }
            if (selectedMode == CaptureMode.Device && String.IsNullOrEmpty(videoDevice)) { ShowPage("Dispositivo"); SetStatus("Selecione a câmera e inicie a captura."); return; }
            try {
                Directory.CreateDirectory(outputFolder); string ext = "mp4", container = "mp4"; if (formatCombo != null && formatCombo.SelectedItem != null && formatCombo.SelectedItem.ToString().StartsWith("AVI")) { ext = "avi"; container = "avi"; }
                activeFile = Path.Combine(outputFolder, "Gravacao_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "." + ext);
                List<string> a = new List<string> { "-hide_banner", "-y" };
                if (selectedMode == CaptureMode.AudioOnly) { a.AddRange(new[] { "-f", "dshow", "-i", "audio=\"" + audioDevice.Replace("\"", "") + "\"", "-vn", "-c:a", "aac", "-b:a", "192k" }); }
                else if (selectedMode == CaptureMode.Device) { a.AddRange(new[] { "-f", "dshow", "-i", "video=\"" + videoDevice.Replace("\"", "") + "\"", "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString() }); }
                else {
                    a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", drawCursor ? "1" : "0" });
                    if (selectedMode == CaptureMode.Window || selectedMode == CaptureMode.Game) {
                        Rectangle rect = BringWindowForwardAndGetBounds();
                        a.AddRange(new[] { "-offset_x", rect.X.ToString(), "-offset_y", rect.Y.ToString(), "-video_size", rect.Width + "x" + rect.Height, "-i", "desktop" });
                    }
                    else if (selectedMode == CaptureMode.Region || selectedMode == CaptureMode.AroundMouse) a.AddRange(new[] { "-offset_x", captureRect.X.ToString(), "-offset_y", captureRect.Y.ToString(), "-video_size", captureRect.Width + "x" + captureRect.Height, "-i", "desktop" });
                    else a.AddRange(new[] { "-i", "desktop" });
                    a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString(), "-pix_fmt", "yuv420p" });
                    if (recordMic && selectedMode != CaptureMode.Device) a.AddRange(new[] { "-f", "dshow", "-i", "audio=\"" + audioDevice.Replace("\"", "") + "\"", "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", "160k" });
                }
                if (container == "mp4") a.AddRange(new[] { "-movflags", "+faststart" });
                a.Add(activeFile);
                StartFfmpeg(a); started = DateTime.Now; clockTimer.Start(); pauseButton.Enabled = true; timerLabel.Visible = false; SetStatus("Gravando: " + ModeName(selectedMode));
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Não foi possível iniciar", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void StartFfmpeg(List<string> args)
        {
            StringBuilder line = new StringBuilder(); foreach (string arg in args) { if (line.Length > 0) line.Append(' '); line.Append(Quote(arg)); }
            encoder = new Process(); encoder.StartInfo = new ProcessStartInfo { FileName = ResolveFfmpeg(), Arguments = line.ToString(), UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
            encoder.ErrorDataReceived += delegate { }; encoder.EnableRaisingEvents = true; encoder.Exited += delegate { try { BeginInvoke((Action)delegate { FinishRecording(); }); } catch { } };
            encoder.Start(); encoder.BeginErrorReadLine();
        }

        string Quote(string s) { if (s == null) return "\"\""; StringBuilder b = new StringBuilder("\""); int slashes = 0; foreach (char c in s) { if (c == '\\') { slashes++; continue; } if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); slashes = 0; continue; } b.Append('\\', slashes); slashes = 0; b.Append(c); } b.Append('\\', slashes * 2); b.Append('"'); return b.ToString(); }

        string ResolveFfmpeg()
        {
            if (!String.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)) return Path.GetFullPath(ffmpegPath);
            string adjacent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"); if (File.Exists(adjacent)) return adjacent;
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { string p = Path.Combine(folder, "ffmpeg.exe"); if (File.Exists(p)) return p; }
            throw new FileNotFoundException("FFmpeg não encontrado. Instale o FFmpeg ou coloque ffmpeg.exe junto do ScreenStudio.");
        }

        void StopEncoder(bool immediate)
        {
            if (encoder == null || encoder.HasExited) return;
            try { if (immediate) encoder.Kill(); else { encoder.StandardInput.WriteLine("q"); encoder.StandardInput.Flush(); } } catch { }
        }
        void FinishRecording() { Process old = encoder; encoder = null; if (old != null) try { old.Dispose(); } catch { } clockTimer.Stop(); pauseButton.Enabled = false; timerLabel.Text = "00:00:00"; if (!String.IsNullOrEmpty(activeFile) && File.Exists(activeFile)) SetStatus("Arquivo salvo: " + Path.GetFileName(activeFile)); else SetStatus("Gravação encerrada."); if (currentPage == "Vídeos") RefreshMedia("video"); }
        void OnClock(object sender, EventArgs e) { if (encoder != null && !encoder.HasExited) { TimeSpan t = DateTime.Now - started; string time = String.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); statusLabel.Text = "Gravando · " + time; } else if (encoder != null) FinishRecording(); }
        void OnSchedule(object sender, EventArgs e) { if (scheduleArmed && DateTime.Now >= scheduleDate.Value) { scheduleArmed = false; scheduleTimer.Stop(); selectedMode = CaptureMode.Fullscreen; StartRecording(); if (scheduleDuration != null) { int mins = (int)scheduleDuration.Value; System.Windows.Forms.Timer stop = new System.Windows.Forms.Timer { Interval = mins * 60000 }; stop.Tick += delegate { stop.Stop(); StopEncoder(false); stop.Dispose(); }; stop.Start(); } } }
        void SetStatus(string text) { statusLabel.Text = text; }

        void TakeScreenshot()
        {
            try { Directory.CreateDirectory(outputFolder); string file = Path.Combine(outputFolder, "Imagem_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png"); List<string> a = new List<string> { "-hide_banner", "-y", "-f", "gdigrab", "-draw_mouse", drawCursor ? "1" : "0", "-i", "desktop", "-frames:v", "1", file }; Process p = new Process(); p.StartInfo = new ProcessStartInfo { FileName = ResolveFfmpeg(), Arguments = String.Join(" ", a.Select(Quote).ToArray()), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }; p.Start(); p.WaitForExit(10000); if (File.Exists(file)) SetStatus("Imagem salva: " + Path.GetFileName(file)); else SetStatus("Falha ao capturar imagem."); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Captura de imagem"); }
        }
    }

    internal sealed class RegionForm : Form
    {
        public Rectangle Selection { get; private set; }
        Point start; bool dragging;
        public RegionForm()
        {
            FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; TopMost = true; ShowInTaskbar = false; BackColor = Color.Black; Opacity = 0.30; Cursor = Cursors.Cross; DoubleBuffered = true;
            MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { start = e.Location; dragging = true; } };
            MouseMove += delegate(object s, MouseEventArgs e) { if (dragging) { Selection = Rect(start, e.Location); Invalidate(); } };
            MouseUp += delegate(object s, MouseEventArgs e) { if (dragging) { Selection = Rect(start, e.Location); dragging = false; DialogResult = DialogResult.OK; Close(); } };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            Paint += delegate(object s, PaintEventArgs e) { using (Pen p = new Pen(Color.Red, 2)) e.Graphics.DrawRectangle(p, Selection); using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.DrawString("Arraste para selecionar a área · Esc cancela", new Font("Segoe UI", 16, FontStyle.Bold), b, 20, 20); };
        }
        Rectangle Rect(Point a, Point b) { return new Rectangle(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y)); }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                    if (key != null) key.SetValue(Path.GetFileName(Application.ExecutablePath), 11001, RegistryValueKind.DWord);
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
