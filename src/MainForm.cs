// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Remo
{
    class MainForm : Form
    {
        AppData data;
        MenuStrip menu;
        Panel side;
        SiteList list;
        Label status;
        DBPanel header; bool toggleHover; ToolTip headerTip = new ToolTip(); ToolStripMenuItem miSide;
        TermView view;
        Terminal term;
        System.Windows.Forms.Timer statusTimer;
        ToolStripMenuItem miUtf8, miSjis, miDisconnect, miEdit, miDelete;

        SshClient client;
        Site curSite;           // 接続中(または接続試行中)のサイト
        bool connected;
        string oscTitle = "";
        readonly object qLock = new object();
        List<byte[]> queue = new List<byte[]>();
        bool drainPosted, capture, feedErr;

        public MainForm(string[] args)
        {
            capture = Array.IndexOf(args, "--capture") >= 0;
            Log.W("ctor: load"); data = Store.Load();
            Theme.Scale = ScreenDpi() / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(9.5f);
            Text = "Remo";
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Theme.S(640), Theme.S(420));
            ClientSize = new Size(Theme.S(1040), Theme.S(660));
            if (data.WinW > 0 && data.WinH > 0) Size = new Size(data.WinW, data.WinH);
            RestorePosition();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Log.W("ctor: menu"); BuildMenu();

            Log.W("ctor: side"); side = new Panel(); side.Dock = DockStyle.Left; side.Width = Theme.S(250); side.BackColor = Theme.Side;
            list = new SiteList(); list.Sites = data.Sites; list.Dock = DockStyle.Fill;
            list.ItemClick = OnSiteClick; list.AddClick = AddSite; list.ItemEdit = EditSite; list.ItemDelete = DeleteSite; list.ItemClone = CloneSite;
            header = new DBPanel(); header.Dock = DockStyle.Top; header.Height = Theme.S(56); header.BackColor = Theme.Side;
            header.Paint += delegate (object o, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!data.SideCollapsed)
                {
                    Gfx.DrawEmoji(g, "🔌", new Rectangle(Theme.S(16), Theme.S(14), Theme.S(28), Theme.S(28)));
                    TextRenderer.DrawText(g, "Remo", Theme.F(14f, FontStyle.Bold), new Point(Theme.S(52), Theme.S(12)), Theme.Text, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, "かんたん SSH クライアント" + VersionText, Theme.F(8f), new Point(Theme.S(53), Theme.S(35)), Theme.Sub, TextFormatFlags.NoPadding);
                }
                Rectangle tr = ToggleRect();
                if (toggleHover) using (var b = new SolidBrush(Theme.CardHover)) using (var p = Gfx.Round(tr, Theme.S(6))) g.FillPath(b, p);
                using (var pen = new Pen(toggleHover ? Theme.Text : Theme.Sub, Math.Max(1.5f, Theme.Scale * 1.6f)))
                {
                    int cx = tr.X + tr.Width / 2, cy = tr.Y + tr.Height / 2, hw = Theme.S(8), gap = Theme.S(5);
                    for (int k = -1; k <= 1; k++) g.DrawLine(pen, cx - hw, cy + k * gap, cx + hw, cy + k * gap);
                }
            };
            header.MouseMove += delegate (object o, MouseEventArgs e) { bool h = ToggleRect().Contains(e.Location); if (h != toggleHover) { toggleHover = h; header.Invalidate(); header.Cursor = h ? Cursors.Hand : Cursors.Default; } };
            header.MouseLeave += delegate { if (toggleHover) { toggleHover = false; header.Invalidate(); } };
            header.MouseUp += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left && ToggleRect().Contains(e.Location)) ToggleSidebar(); };
            headerTip.SetToolTip(header, "サイドバーの開閉 (Ctrl+Shift+B)");
            status = new Label(); status.Dock = DockStyle.Bottom; status.Height = Theme.S(44); status.ForeColor = Theme.Sub; status.BackColor = Theme.Side;
            status.Padding = new Padding(Theme.S(14), Theme.S(4), Theme.S(8), 0); status.Font = Theme.F(8.5f); status.AutoEllipsis = true;
            side.Controls.Add(list); side.Controls.Add(header); side.Controls.Add(status);
            ApplySidebar();

            Log.W("ctor: term/view"); term = new Terminal(80, 24);
            view = new TermView();
            view.Term = term; view.Dock = DockStyle.Fill;
            view.FontPt = data.FontPt; view.FontName = data.FontName; view.BuildFonts();
            view.Sjis = delegate { return term.Sjis; };
            view.SendBytes = delegate (byte[] b) { SendToServer(b); };
            view.SizeChanged2 = delegate (int c, int r) { if (client != null && connected) client.Resize(c, r); UpdateStatus(); };
            view.TripleClicked = RestoreSize;
            view.FontChanged2 = delegate (float pt) { data.FontPt = pt; Store.Save(data); };
            term.Reply = delegate (byte[] b) { SendToServer(b); };
            term.TitleChanged = delegate (string t) { oscTitle = t; UpdateTitle(); };

            Log.W("ctor: layout"); Controls.Add(view);
            Controls.Add(side);
            Controls.Add(menu);
            menu.SendToBack(); // 最前面に配置されるメニューを先頭に
            Controls.SetChildIndex(menu, Controls.Count - 1);
            Controls.SetChildIndex(side, 1);
            Controls.SetChildIndex(view, 0);

            statusTimer = new System.Windows.Forms.Timer(); statusTimer.Interval = 4000;
            statusTimer.Tick += delegate { statusTimer.Stop(); UpdateStatus(); };

            Log.W("ctor: done");
            Shown += delegate
            {
                Log.W("shown");
                if (Emoji.DisabledByCrash) MessageBox.Show(this, "前回の起動中にカラー絵文字の描画で異常が発生したため、今回は絵文字描画を無効にして起動しました。\n詳細: " + Log.Path, "Remo");
                view.Focus();
                if (Store.LoadError != null) MessageBox.Show(this, "保存済みの接続先を読み込めませんでした(別のWindowsユーザーで作成された可能性があります)。\n" + Store.LoadError, "Remo");
                if (Array.IndexOf(args, "--demo") >= 0) Demo();
                else Banner();
                UpdateStatus();
            };
        }

        int ScreenDpi()
        {
            try { using (Graphics g = CreateGraphics()) return (int)g.DpiX; } catch { return 96; }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); N.DarkTitle(Handle, true); }

        // ---- メニュー ----
        void BuildMenu()
        {
            menu = new MenuStrip();
            menu.Renderer = new DarkRenderer(); menu.BackColor = Theme.Side; menu.ForeColor = Theme.Text; menu.Font = Theme.F(9.5f);
            menu.Padding = new Padding(Theme.S(6), Theme.S(3), 0, Theme.S(3));
            Func<string, EventHandler, ToolStripMenuItem> mk = delegate (string t, EventHandler h)
            {
                var it = new ToolStripMenuItem(t); it.ForeColor = Theme.Text; if (h != null) it.Click += h; return it;
            };
            var conn = mk("接続先", null);
            var add = mk("接続先を追加…", delegate { AddSite(); });
            miEdit = mk("接続先を編集", null); miDelete = mk("接続先を削除", null);
            miEdit.DropDownOpening += delegate { FillSiteMenu(miEdit, EditSite); };
            miDelete.DropDownOpening += delegate { FillSiteMenu(miDelete, DeleteSite); };
            miEdit.DropDownItems.Add("-"); miDelete.DropDownItems.Add("-");
            miDisconnect = mk("切断", delegate { Disconnect(true); });
            var exit = mk("終了", delegate { Close(); });
            var exp = mk("接続先を書き出す…", delegate { ExportSites(); });
            var imp = mk("接続先を読み込む…", delegate { ImportSites(); });
            SetMenuIcon(exp, "📤"); SetMenuIcon(imp, "📥");
            conn.DropDownItems.AddRange(new ToolStripItem[] { add, miEdit, miDelete, new ToolStripSeparator(), exp, imp, new ToolStripSeparator(), miDisconnect, new ToolStripSeparator(), exit });
            SetMenuIcon(add, "➕"); SetMenuIcon(miDisconnect, "⛔");

            var view2 = mk("表示", null);
            miSide = mk("サイドバーを表示", delegate { ToggleSidebar(); });
            miSide.ShortcutKeys = Keys.Control | Keys.Shift | Keys.B;
            miSide.ShortcutKeyDisplayString = "Ctrl+Shift+B";
            SetMenuIcon(miSide, "📑");
            var remember = mk("現在のウィンドウサイズを記憶", delegate { RememberSize(); });
            SetMenuIcon(remember, "📐");
            var enc = mk("文字コード", null);
            miUtf8 = mk("UTF-8", delegate { SetEncoding(false); });
            miSjis = mk("シフトJIS", delegate { SetEncoding(true); });
            enc.DropDownItems.AddRange(new ToolStripItem[] { miUtf8, miSjis });
            SetMenuIcon(enc, "🔤");
            var font = mk("フォント", null);
            font.DropDownItems.Add("-");
            font.DropDownOpening += delegate { FillFontMenu(font); };
            SetMenuIcon(font, "🅰️");
            var size = mk("フォントサイズ", null);
            foreach (float pt in new float[] { 8, 9, 10, 10.5f, 11, 12, 13, 14, 16, 18, 20, 24, 28 })
            {
                float p = pt;
                var it = mk(pt.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pt", delegate { ChangeFont(view.FontName, p); });
                it.Tag = p;
                size.DropDownItems.Add(it);
            }
            size.DropDownOpening += delegate
            {
                foreach (ToolStripItem it in size.DropDownItems) { var mi = it as ToolStripMenuItem; if (mi != null) mi.Checked = mi.Tag is float && Math.Abs((float)mi.Tag - view.FontPt) < 0.01f; }
            };
            size.DropDown.BackColor = Theme.Menu;
            SetMenuIcon(size, "🔠");
            view2.DropDownItems.AddRange(new ToolStripItem[] { miSide, new ToolStripSeparator(), remember, new ToolStripSeparator(), font, size, new ToolStripSeparator(), enc });
            view2.DropDownOpening += delegate
            {
                miUtf8.Checked = !term.Sjis; miSjis.Checked = term.Sjis;
                miSide.Checked = !data.SideCollapsed;
            };
            conn.DropDownOpening += delegate { miDisconnect.Enabled = curSite != null; };
            var help = mk("ヘルプ", null);
            var about = mk("Remo について…", delegate { ShowAbout(); });
            SetMenuIcon(about, "ℹ️");
            help.DropDownItems.Add(about);
            help.DropDown.BackColor = Theme.Menu;
            menu.Items.AddRange(new ToolStripItem[] { conn, view2, help });
            foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.Text;
            foreach (ToolStripItem it in conn.DropDownItems) it.ForeColor = Theme.Text;
            foreach (ToolStripItem it in view2.DropDownItems) it.ForeColor = Theme.Text;
            foreach (ToolStripItem it in enc.DropDownItems) it.ForeColor = Theme.Text;
            foreach (ToolStripItem d in new ToolStripItem[] { conn, view2, enc }) { var dd = d as ToolStripDropDownItem; if (dd != null) { dd.DropDown.BackColor = Theme.Menu; } }
        }

        void SetMenuIcon(ToolStripMenuItem mi, string emoji)
        {
            try { Bitmap b = Emoji.Render(emoji, Theme.S(16)); if (b != null) mi.Image = b; } catch { }
        }

        void FillFontMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            Cursor = Cursors.WaitCursor;
            List<string> fonts;
            try { fonts = TermView.MonospaceFonts(); } finally { Cursor = Cursors.Default; }
            string cur = view.CurrentFont;
            foreach (string name in fonts)
            {
                string n = name;
                var it = new ToolStripMenuItem(n); it.ForeColor = Theme.Text;
                it.Checked = string.Equals(n, cur, StringComparison.OrdinalIgnoreCase);
                it.Click += delegate { ChangeFont(n, view.FontPt); };
                parent.DropDownItems.Add(it);
            }
            if (fonts.Count == 0) { var x = new ToolStripMenuItem("(等幅フォントが見つかりません)"); x.Enabled = false; parent.DropDownItems.Add(x); }
            parent.DropDown.BackColor = Theme.Menu;
        }

        void ChangeFont(string name, float pt)
        {
            view.SetFont(name, pt);
            data.FontName = view.FontName; data.FontPt = view.FontPt;
            Store.Save(data);
            UpdateStatus();
            ShowStatus("フォント: " + view.CurrentFont + " " + view.FontPt + "pt");
        }

        void FillSiteMenu(ToolStripMenuItem parent, Action<Site> act)
        {
            parent.DropDownItems.Clear();
            if (data.Sites.Count == 0) { var n = new ToolStripMenuItem("(接続先がありません)"); n.Enabled = false; parent.DropDownItems.Add(n); return; }
            foreach (Site s in data.Sites)
            {
                Site cs = s;
                var it = new ToolStripMenuItem(s.Name); it.ForeColor = Theme.Text;
                it.Click += delegate { act(cs); };
                parent.DropDownItems.Add(it);
            }
            parent.DropDown.BackColor = Theme.Menu;
        }

        // ---- 接続先の編集 ----
        void AddSite()
        {
            using (var d = new SiteDialog(null, true))
                if (d.ShowDialog(this) == DialogResult.OK) { data.Sites.Add(d.Result); Store.Save(data); list.Invalidate(); }
            view.Focus();
        }
        void EditSite(Site s)
        {
            using (var d = new SiteDialog(s, false))
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    int i = data.Sites.IndexOf(s);
                    if (i >= 0)
                    {
                        data.Sites[i] = d.Result;
                        if (curSite == s) curSite = d.Result;
                        if (list.Connected == s) list.Connected = d.Result;
                        if (list.Connecting == s) list.Connecting = d.Result;
                        Store.Save(data); list.Invalidate();
                    }
                }
            view.Focus();
        }
        void CloneSite(Site s)
        {
            Site c = s.Clone(); c.Name = s.Name + " (コピー)"; c.HostKey = "";
            data.Sites.Insert(data.Sites.IndexOf(s) + 1, c); Store.Save(data); list.Invalidate();
        }
        void DeleteSite(Site s)
        {
            if (MessageBox.Show(this, "「" + s.Name + "」を削除しますか?", "Remo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (curSite == s) Disconnect(true);
            data.Sites.Remove(s); Store.Save(data); list.Invalidate(); view.Focus();
        }

        // ---- 書き出し / 読み込み ----
        void ExportSites()
        {
            if (data.Sites.Count == 0) { MessageBox.Show(this, "書き出す接続先がありません。", "Remo"); return; }
            string pw;
            using (var d = new PasswordDialog("接続先を書き出す", "書き出すファイルを保護するパスワードを決めてください。\n別のPCで読み込むときに必要です(忘れると開けません)。", true))
            {
                if (d.ShowDialog(this) != DialogResult.OK) { view.Focus(); return; }
                pw = d.Password;
            }
            using (var sf = new SaveFileDialog())
            {
                sf.Title = "接続先の書き出し"; sf.Filter = "Remo 接続先ファイル (*.remo)|*.remo";
                sf.FileName = "Remo接続先_" + DateTime.Now.ToString("yyyyMMdd") + ".remo";
                if (sf.ShowDialog(this) != DialogResult.OK) { view.Focus(); return; }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    Store.Export(sf.FileName, data.Sites, pw);
                    Cursor = Cursors.Default;
                    MessageBox.Show(this, data.Sites.Count + " 件の接続先を書き出しました。\n\n" + sf.FileName, "Remo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { Cursor = Cursors.Default; MessageBox.Show(this, "書き出しに失敗しました: " + ex.Message, "Remo"); }
            }
            view.Focus();
        }

        void ImportSites()
        {
            string path;
            using (var of = new OpenFileDialog())
            {
                of.Title = "接続先の読み込み"; of.Filter = "Remo 接続先ファイル (*.remo)|*.remo|すべてのファイル (*.*)|*.*";
                if (of.ShowDialog(this) != DialogResult.OK) { view.Focus(); return; }
                path = of.FileName;
            }
            List<Site> sites = null;
            while (sites == null)
            {
                string pw;
                using (var d = new PasswordDialog("接続先を読み込む", "書き出したときに決めたパスワードを入力してください。", false))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) { view.Focus(); return; }
                    pw = d.Password;
                }
                try { Cursor = Cursors.WaitCursor; sites = Store.Import(path, pw); }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    if (MessageBox.Show(this, ex.Message + "\nもう一度入力しますか?", "Remo", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) { view.Focus(); return; }
                }
                finally { Cursor = Cursors.Default; }
            }
            int dup = 0;
            foreach (Site s in sites) if (FindByName(s.Name) != null) dup++;
            DialogResult mode = DialogResult.No;
            if (dup > 0)
            {
                mode = MessageBox.Show(this, "同じ名前の接続先が " + dup + " 件あります。\n\n[はい] 上書きする\n[いいえ] 別名(読込)で追加する\n[キャンセル] 読み込みをやめる",
                    "Remo", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (mode == DialogResult.Cancel) { view.Focus(); return; }
            }
            int added = 0, replaced = 0;
            foreach (Site s in sites)
            {
                Site old = FindByName(s.Name);
                if (old == null) { data.Sites.Add(s); added++; }
                else if (mode == DialogResult.Yes)
                {
                    if (curSite == old) Disconnect(true);
                    data.Sites[data.Sites.IndexOf(old)] = s; replaced++;
                }
                else { s.Name = s.Name + " (読込)"; data.Sites.Add(s); added++; }
            }
            Store.Save(data); list.Invalidate();
            MessageBox.Show(this, "読み込みました: 追加 " + added + " 件" + (replaced > 0 ? "、上書き " + replaced + " 件" : ""), "Remo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            view.Focus();
        }

        Site FindByName(string name)
        {
            foreach (Site s in data.Sites) if (s.Name == name) return s;
            return null;
        }

        public static string VersionText
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v == null || (v.Major == 0 && v.Minor == 0)) return "";
                return "  v" + v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        void ShowAbout()
        {
            string v = VersionText.Trim();
            MessageBox.Show(this,
                "Remo " + v + "  かんたん SSH クライアント\n" +
                "Copyright (C) 2026 onboard-dev\n\n" +
                "このプログラムはフリーソフトウェアです。GNU General Public License\n" +
                "バージョン3(またはそれ以降)の条件のもとで再配布・改変できます。\n\n" +
                "このプログラムは無保証です。詳しくは LICENSE をご覧ください。\n\n" +
                "ソースコード: https://github.com/onboard-dev/remo",
                "Remo について", MessageBoxButtons.OK, MessageBoxIcon.Information);
            view.Focus();
        }

        // ---- サイドバー ----
        Rectangle ToggleRect()
        {
            int sz = Theme.S(32);
            if (data.SideCollapsed) return new Rectangle((side.Width - sz) / 2, Theme.S(12), sz, sz);
            return new Rectangle(side.Width - sz - Theme.S(10), Theme.S(12), sz, sz);
        }

        void ApplySidebar()
        {
            bool c = data.SideCollapsed;
            list.Compact = c;
            status.Visible = !c;
            side.Width = c ? Theme.S(60) : Theme.S(250);
            list.Invalidate(); header.Invalidate();
        }

        void ToggleSidebar()
        {
            data.SideCollapsed = !data.SideCollapsed;
            ApplySidebar();
            Store.Save(data);
            view.Focus();
        }

        // ---- ウィンドウサイズ ----
        void RememberSize()
        {
            Size sz = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            data.WinW = sz.Width; data.WinH = sz.Height;
            Store.Save(data);
            ShowStatus("ウィンドウサイズを記憶しました (" + sz.Width + " × " + sz.Height + ")");
        }
        void RestoreSize()
        {
            if (data.WinW <= 0 || data.WinH <= 0) { ShowStatus("記憶したサイズがありません。表示メニューから記憶してください"); return; }
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Size = new Size(data.WinW, data.WinH);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Point p = Location;
            if (p.X + Width > wa.Right) p.X = Math.Max(wa.Left, wa.Right - Width);
            if (p.Y + Height > wa.Bottom) p.Y = Math.Max(wa.Top, wa.Bottom - Height);
            Location = p;
            ShowStatus("記憶したサイズに戻しました (" + data.WinW + " × " + data.WinH + ")");
        }

        // ---- 表示 ----
        void ShowStatus(string s) { status.Text = s; statusTimer.Stop(); statusTimer.Start(); }
        void UpdateStatus()
        {
            string enc = term.Sjis ? "シフトJIS" : "UTF-8";
            if (curSite != null) status.Text = (connected ? "● 接続中: " : "… 接続中: ") + curSite.Name + "\n" + enc + "  ·  " + term.Cols + " × " + term.Rows;
            else status.Text = "未接続\n" + enc + "  ·  " + term.Cols + " × " + term.Rows;
        }
        void UpdateTitle()
        {
            string t = "Remo";
            if (curSite != null) t = curSite.Name + " — " + curSite.User + "@" + curSite.HostOnly + " — Remo";
            if (oscTitle.Length > 0 && curSite != null) t = oscTitle + " — " + curSite.Name + " — Remo";
            Text = t;
        }

        void Banner()
        {
            Print("\x1b[38;5;111m  Remo\x1b[0m  \x1b[90mSSHクライアント\x1b[0m\r\n\r\n  左の接続先ボタンをクリックすると接続します。\r\n  \x1b[90m選択して右クリック=コピー / 何も選択せず右クリック=ペースト\x1b[0m\r\n");
        }

        void Print(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            bool sj = term.Sjis; term.SetEncoding(false);
            term.Feed(b, b.Length);
            term.SetEncoding(sj);
            view.AfterFeed();
        }

        void Demo()
        {
            var sb = new StringBuilder();
            sb.Append("\x1b[1;38;5;111mRemo デモ画面\x1b[0m  カラー・絵文字・日本語の表示テスト\r\n\r\n");
            sb.Append("絵文字: 🚀 🔥 ✅ ❌ ⚠️ 🐧 🍣 🎉 🌸 😀 👍🏽 ❤️ ⭐ 📦 🔒 🌏\r\n");
            sb.Append("日本語: 吾輩は猫である。名前はまだ無い。 ｶﾀｶﾅ 全角ＡＢＣ\r\n");
            sb.Append("罫線:   ┌──────┬──────┐ ╔═══╗ ▁▂▃▄▅▆▇█\r\n        │ 左   │ 右   │ ╚═══╝\r\n        └──────┴──────┘\r\n\r\n");
            for (int i = 0; i < 8; i++) sb.Append("\x1b[4" + i + "m  \x1b[0m");
            sb.Append(" ");
            for (int i = 0; i < 8; i++) sb.Append("\x1b[10" + i + "m  \x1b[0m");
            sb.Append("\r\n");
            for (int i = 16; i < 52; i++) sb.Append("\x1b[48;5;" + i + "m \x1b[0m");
            sb.Append("\r\n");
            for (int i = 0; i < 64; i++) { int r = 255 - i * 4, g = i * 4, b = 160; sb.Append("\x1b[48;2;" + r + ";" + g + ";" + b + "m \x1b[0m"); }
            sb.Append("\r\n\r\n\x1b[1mBold\x1b[0m \x1b[3mItalic\x1b[0m \x1b[4mUnderline\x1b[0m \x1b[9mStrike\x1b[0m \x1b[7mReverse\x1b[0m \x1b[2mDim\x1b[0m \x1b[31;1mRed\x1b[32mGreen\x1b[33mYellow\x1b[34mBlue\x1b[35mMagenta\x1b[36mCyan\x1b[0m\r\n");
            sb.Append("\r\n  CPU[\x1b[32m||||||||\x1b[33m||||\x1b[31m||\x1b[90m                 \x1b[0m 42.0%]\r\n  Mem[\x1b[34m|||||||||||||||\x1b[90m          \x1b[0m 3.1G/7.8G]\r\n");
            Print(sb.ToString());
        }

        // ---- 接続 ----
        void OnSiteClick(Site s)
        {
            if (curSite == s) { Disconnect(true); return; }
            if (curSite != null)
            {
                if (MessageBox.Show(this, "「" + curSite.Name + "」を切断して「" + s.Name + "」に接続しますか?", "Remo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Disconnect(false);
            }
            Connect(s);
        }

        void Connect(Site s)
        {
            curSite = s; connected = false; oscTitle = "";
            list.Connecting = s; list.Connected = null; list.Invalidate();
            term.FullReset(); term.Scrollback.Clear(); view.ViewOffset = 0;
            term.SetEncoding(s.Enc == 1);
            Print("\x1b[38;5;111m接続中…\x1b[0m " + s.User + "@" + s.HostOnly + ":" + s.Port + "\r\n");
            UpdateTitle(); UpdateStatus();
            view.Focus();
            SshClient cl = new SshClient(s.HostOnly, s.Port, s.User, s.Pass, term.Cols, term.Rows);
            client = cl;
            cl.DataReceived = delegate (byte[] d, int n)
            {
                if (cl != client) return;
                byte[] copy = new byte[n]; Array.Copy(d, copy, n);
                lock (qLock)
                {
                    queue.Add(copy);
                    if (drainPosted) return;
                    drainPosted = true;
                }
                try { BeginInvoke(new Action(Drain)); } catch { }
            };
            cl.Closed = delegate (string reason)
            {
                try { BeginInvoke(new Action(delegate { OnClosed(cl, reason); })); } catch { }
            };
            cl.HostKeyCheck = delegate (string alg, string fp)
            {
                bool ok = false;
                try { Invoke(new Action(delegate { ok = CheckHostKey(s, alg, fp); })); } catch { }
                return ok;
            };
            Thread th = new Thread(delegate ()
            {
                try
                {
                    cl.Connect(10000);
                    BeginInvoke(new Action(delegate
                    {
                        if (cl != client) { cl.Disconnect(); return; }
                        connected = true; list.Connected = s; list.Connecting = null; list.Invalidate(); UpdateStatus();
                        if (cl.BannerText.Length > 0) Print(cl.BannerText.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                        view.Focus();
                    }));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (cl != client) return;
                            Print("\r\n\x1b[31m接続できませんでした: " + msg + "\x1b[0m\r\n");
                            ResetState();
                        }));
                    }
                    catch { }
                }
            });
            th.IsBackground = true; th.Start();
        }

        bool CheckHostKey(Site s, string alg, string fp)
        {
            string cur = alg + " " + fp;
            if (string.IsNullOrEmpty(s.HostKey)) { s.HostKey = cur; Store.Save(data); Print("\x1b[90mホスト鍵を登録しました (" + fp + ")\x1b[0m\r\n"); return true; }
            if (s.HostKey == cur) return true;
            var r = MessageBox.Show(this, "「" + s.Name + "」のホスト鍵が前回と異なります。\n\n前回: " + s.HostKey + "\n今回: " + cur +
                "\n\nサーバーの再構築であれば問題ありませんが、通信を盗聴されている可能性もあります。\n新しい鍵を信頼して接続を続けますか?", "ホスト鍵の警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return false;
            s.HostKey = cur; Store.Save(data); return true;
        }

        void Drain()
        {
            List<byte[]> items;
            lock (qLock) { items = queue; queue = new List<byte[]>(); drainPosted = false; }
            foreach (var b in items)
            {
                if (capture) { try { using (var fs = new System.IO.FileStream(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "remo-capture.bin"), System.IO.FileMode.Append)) fs.Write(b, 0, b.Length); } catch { } }
                try { term.Feed(b, b.Length); }
                catch (Exception ex) { if (!feedErr) { feedErr = true; Log.W("FEED ERROR: " + ex); } }
            }
            view.AfterFeed();
            UpdateTitleIfNeeded();
        }
        string lastTitle = "";
        void UpdateTitleIfNeeded() { if (lastTitle != oscTitle) { lastTitle = oscTitle; UpdateTitle(); } }

        void SendToServer(byte[] b)
        {
            SshClient cl = client;
            if (cl == null || !connected) return;
            try { cl.Write(b, b.Length); } catch (Exception ex) { Log.W("WRITE ERROR: " + ex.Message); }
        }

        void OnClosed(SshClient cl, string reason)
        {
            if (cl != client) return;
            Drain();
            Print("\r\n\x1b[33m[切断] " + reason + "\x1b[0m\r\n");
            ResetState();
        }

        void ResetState()
        {
            client = null; connected = false; curSite = null;
            list.Connected = null; list.Connecting = null; list.Invalidate();
            oscTitle = ""; UpdateTitle(); UpdateStatus();
        }

        void Disconnect(bool print)
        {
            SshClient cl = client;
            if (cl == null) return;
            client = null;
            try { cl.Disconnect(); } catch { }
            if (print) Print("\r\n\x1b[33m[切断しました]\x1b[0m\r\n");
            ResetState();
        }

        void SetEncoding(bool sjis)
        {
            term.SetEncoding(sjis);
            if (curSite != null) { curSite.Enc = sjis ? 1 : 0; Store.Save(data); }
            ShowStatus("文字コード: " + (sjis ? "シフトJIS" : "UTF-8"));
            view.MarkDirty();
        }

        // 前回終了したディスプレイに表示する(そのディスプレイが無ければ主ディスプレイ中央)
        void RestorePosition()
        {
            if (!data.HasPos) return;
            Point center = new Point(data.PosX + Width / 2, data.PosY + Height / 2);
            Screen target = null;
            foreach (Screen sc in Screen.AllScreens)
                if (sc.Bounds.Contains(center) || sc.Bounds.Contains(new Point(data.PosX + 20, data.PosY + 20))) { target = sc; break; }
            if (target == null) return;
            Rectangle wa = target.WorkingArea;
            int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
            int x = Math.Max(wa.Left, Math.Min(data.PosX, wa.Right - w));
            int y = Math.Max(wa.Top, Math.Min(data.PosY, wa.Bottom - h));
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, w, h);
            if (data.PosMax) WindowState = FormWindowState.Maximized;
        }

        void SavePosition()
        {
            try
            {
                Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                data.PosX = b.X; data.PosY = b.Y; data.HasPos = true;
                data.PosMax = WindowState == FormWindowState.Maximized;
                Store.Save(data);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (connected && MessageBox.Show(this, "接続中です。終了しますか?", "Remo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true; return;
            }
            SavePosition();
            base.OnFormClosing(e);
            try { if (client != null) client.Disconnect(); } catch { }
        }
    }

    static class Log
    {
        static string path;
        public static string Path
        {
            get
            {
                if (path != null) return path;
                try
                {
                    string d = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                    string p = System.IO.Path.Combine(d, "remo-log.txt");
                    System.IO.File.AppendAllText(p, "");
                    path = p;
                }
                catch
                {
                    try { System.IO.Directory.CreateDirectory(Store.Dir); path = System.IO.Path.Combine(Store.Dir, "remo-log.txt"); } catch { path = "remo-log.txt"; }
                }
                return path;
            }
        }
        public static void W(string s)
        {
            try { System.IO.File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch { }
        }
    }

    static class Program
    {
        static void Fatal(string where, Exception ex)
        {
            Log.W("FATAL(" + where + "): " + ex);
            try { MessageBox.Show("Remoでエラーが発生しました。\n\n" + ex.Message + "\n\n詳細: " + Log.Path, "Remo"); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { System.IO.File.WriteAllText(Log.Path, ""); } catch { }
            Log.W("start" + MainForm.VersionText + " " + Environment.OSVersion + " clr=" + Environment.Version + " 64bit=" + Environment.Is64BitProcess);
            AppDomain.CurrentDomain.UnhandledException += delegate (object o, UnhandledExceptionEventArgs e) { Fatal("unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString())); };
            try
            {
                try { N.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object o, System.Threading.ThreadExceptionEventArgs e) { Fatal("thread", e.Exception); };
                Log.W("creating form");
                MainForm f = new MainForm(args);
                Log.W("form created, running");
                Application.Run(f);
                Log.W("exit normally");
            }
            catch (Exception ex) { Fatal("main", ex); }
        }
    }
}
