// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Remo
{
    static class Theme
    {
        public static Color Bg = C(0x1A1B26), Side = C(0x16161E), Card = C(0x1F2335), CardHover = C(0x292E42), Border = C(0x2F334D),
            Accent = C(0x7AA2F7), Text = C(0xC0CAF5), Sub = C(0x7982A9), Green = C(0x9ECE6A), Red = C(0xF7768E), Field = C(0x24283B), Menu = C(0x1F2030);
        public static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
        static string uiFont;
        public static Font F(float pt, FontStyle st = FontStyle.Regular)
        {
            if (uiFont == null)
            {
                uiFont = "Segoe UI";
                try
                {
                    foreach (var f in new InstalledFontCollection().Families)
                        if (f.Name == "Segoe UI Variable Text") { uiFont = f.Name; break; }
                }
                catch { }
            }
            return new Font(uiFont, pt, st, GraphicsUnit.Point);
        }
    }

    // ---- 暗色メニュー ----
    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHover; } }
        public override Color MenuItemBorder { get { return Theme.CardHover; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.CardHover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.CardHover; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Menu; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Menu; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Menu; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Menu; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color MenuStripGradientBegin { get { return Theme.Side; } }
        public override Color MenuStripGradientEnd { get { return Theme.Side; } }
        public override Color CheckBackground { get { return Theme.CardHover; } }
        public override Color CheckSelectedBackground { get { return Theme.CardHover; } }
        public override Color CheckPressedBackground { get { return Theme.CardHover; } }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Sub;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }
    }

    static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void DrawEmoji(Graphics g, string icon, Rectangle r)
        {
            Bitmap b = Emoji.Render(icon, r.Width);
            if (b != null) { g.DrawImage(b, r); return; }
            using (Font f = new Font("Segoe UI Emoji", r.Width * 0.55f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, icon, f, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ---- 接続先リスト(カード形式) ----
    class SiteList : Control
    {
        public List<Site> Sites;
        public Site Connected, Connecting;
        public Action<Site> ItemClick, ItemEdit, ItemDelete, ItemClone;
        public Action AddClick;
        int hover = -2, scroll;
        Font fName, fSub, fPill;

        public SiteList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Side;
            fName = Theme.F(10.5f, FontStyle.Bold); fSub = Theme.F(8.5f); fPill = Theme.F(8.5f, FontStyle.Bold); fEdit = Theme.F(8f);
        }

        public bool Compact;
        ToolTip tip = new ToolTip();
        int CardH { get { return Compact ? Theme.S(44) : Theme.S(66); } }
        int Gap { get { return Theme.S(8); } }
        int Pad { get { return Compact ? Theme.S(8) : Theme.S(12); } }

        Rectangle CardRect(int i)
        {
            return new Rectangle(Pad, Pad + i * (CardH + Gap) - scroll, Width - Pad * 2, CardH);
        }

        Rectangle ConnRect(Rectangle r) { int w = Theme.S(46); return new Rectangle(r.Right - w - Theme.S(10), r.Y + Theme.S(9), w, Theme.S(24)); }
        Rectangle EditRect(Rectangle r) { int w = Theme.S(46); return new Rectangle(r.Right - w - Theme.S(10), r.Y + Theme.S(38), w, Theme.S(20)); }
        bool hoverEdit;
        Font fEdit;

        int Total { get { return Pad * 2 + (Sites.Count + 1) * (CardH + Gap); } }

        int HitTest(Point p)
        {
            for (int i = 0; i <= Sites.Count; i++) if (CardRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Side);
            for (int i = 0; i <= Sites.Count; i++)
            {
                Rectangle r = CardRect(i);
                if (r.Bottom < 0 || r.Top > Height) continue;
                bool isAdd = i == Sites.Count;
                Site s = isAdd ? null : Sites[i];
                bool conn = s != null && s == Connected, busy = s != null && s == Connecting;
                Color fill = (hover == i) ? Theme.CardHover : Theme.Card;
                if (Compact)
                {
                    using (var path = Gfx.Round(r, Theme.S(10)))
                    {
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        using (var pen = new Pen(conn ? Theme.Green : busy ? Theme.Accent : (hover == i ? Theme.Accent : Theme.Border), conn ? 1.6f : 1f))
                        { if (isAdd) pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, path); }
                    }
                    int cs = Theme.S(26);
                    Gfx.DrawEmoji(g, isAdd ? "➕" : s.Icon, new Rectangle(r.X + (r.Width - cs) / 2, r.Y + (r.Height - cs) / 2, cs, cs));
                    if (conn) using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, r.Right - Theme.S(11), r.Y + Theme.S(4), Theme.S(7), Theme.S(7));
                    continue;
                }
                using (var path = Gfx.Round(r, Theme.S(10)))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var pen = new Pen(conn ? Theme.Green : (hover == i ? Theme.Accent : Theme.Border), conn ? 1.6f : 1f))
                    { if (isAdd) pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, path); }
                }
                int isz = Theme.S(30);
                Rectangle ir = new Rectangle(r.X + Theme.S(12), r.Y + (r.Height - isz) / 2, isz, isz);
                if (isAdd)
                {
                    Gfx.DrawEmoji(g, "➕", ir);
                    TextRenderer.DrawText(g, "接続先を追加", fName, new Rectangle(ir.Right + Theme.S(10), r.Y, r.Width - isz - Theme.S(34), r.Height), Theme.Sub,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    continue;
                }
                Gfx.DrawEmoji(g, s.Icon, ir);
                int tx = ir.Right + Theme.S(10);
                int pillW = Theme.S(46);
                int tw = r.Right - tx - pillW - Theme.S(14);
                TextRenderer.DrawText(g, s.Name, fName, new Rectangle(tx, r.Y + Theme.S(12), tw, Theme.S(22)), Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left);
                TextRenderer.DrawText(g, s.User + "@" + s.HostOnly, fSub, new Rectangle(tx, r.Y + Theme.S(36), tw, Theme.S(18)), Theme.Sub,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left);
                // ピル(接続/切断)と編集ボタン
                Rectangle pr = ConnRect(r);
                Rectangle er = EditRect(r);
                bool eh = hover == i && hoverEdit;
                using (var path = Gfx.Round(er, Theme.S(10)))
                {
                    if (eh) using (var b = new SolidBrush(Color.FromArgb(40, Theme.Sub))) g.FillPath(b, path);
                    using (var pen = new Pen(eh ? Theme.Sub : Theme.Border, 1f)) g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, "編集", fEdit, er, eh ? Theme.Text : Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Color pc = conn ? Theme.Red : (busy ? Theme.Sub : Theme.Accent);
                string pt = conn ? "切断" : (busy ? "…" : "接続");
                using (var path = Gfx.Round(pr, Theme.S(12)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(hover == i && !hoverEdit ? 70 : 38, pc))) g.FillPath(b, path);
                    using (var pen = new Pen(pc, 1f)) g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, pt, fPill, pr, pc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (conn)
                {
                    using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, r.X + Theme.S(5), r.Y + Theme.S(5), Theme.S(8), Theme.S(8));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            bool he = !Compact && h >= 0 && h < Sites.Count && EditRect(CardRect(h)).Contains(e.Location);
            if (h != hover || he != hoverEdit)
            {
                bool changed = h != hover;
                hover = h; hoverEdit = he; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate();
                if (changed)
                {
                    string t = null;
                    if (Compact && h >= 0) t = h == Sites.Count ? "接続先を追加" : Sites[h].Name + "\n" + Sites[h].User + "@" + Sites[h].HostOnly + (Sites[h] == Connected ? "\n(クリックで切断)" : "\n(クリックで接続)");
                    tip.SetToolTip(this, t);
                }
            }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -2; tip.SetToolTip(this, null); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, Total - Height);
            scroll = Math.Max(0, Math.Min(max, scroll - e.Delta / 2));
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h < 0) return;
            if (h == Sites.Count) { if (e.Button == MouseButtons.Left && AddClick != null) AddClick(); return; }
            Site s = Sites[h];
            if (e.Button == MouseButtons.Left)
            {
                if (!Compact && EditRect(CardRect(h)).Contains(e.Location)) { if (ItemEdit != null) ItemEdit(s); }
                else if (ItemClick != null) ItemClick(s);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ContextMenuStrip m = new ContextMenuStrip();
                m.Renderer = new DarkRenderer(); m.Font = Theme.F(9.5f); m.BackColor = Theme.Menu; m.ForeColor = Theme.Text;
                m.Items.Add("編集…", null, delegate { if (ItemEdit != null) ItemEdit(s); });
                m.Items.Add("複製", null, delegate { if (ItemClone != null) ItemClone(s); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("削除…", null, delegate { if (ItemDelete != null) ItemDelete(s); });
                m.Show(this, e.Location);
            }
        }
    }

    class DBPanel : Panel { public DBPanel() { DoubleBuffered = true; } }

    // ---- 接続先の編集ダイアログ ----
    class SiteDialog : Form
    {
        public Site Result;
        TextBox tName, tHost, tUser, tPass;
        ComboBox cEnc;
        CheckBox cShow;
        string icon;
        List<Rectangle> iconRects = new List<Rectangle>();
        static readonly string[] Icons = { "🖥️", "💻", "🐧", "🍓", "☁️", "🏫", "🔧", "🧪", "🚀", "🗄️", "🌐", "🏠", "🔒", "📡", "🛠️", "🎮" };
        DBPanel iconPanel;

        public SiteDialog(Site s, bool isNew)
        {
            Site src = s ?? new Site();
            icon = string.IsNullOrEmpty(src.Icon) ? "🖥️" : src.Icon;
            Text = isNew ? "接続先を追加" : "接続先を編集";
            Font = Theme.F(9.5f);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(440), Theme.S(470));
            int x = Theme.S(24), w = ClientSize.Width - x * 2, y = Theme.S(18);

            Action<string, Control> add = delegate (string label, Control c)
            {
                Label l = new Label(); l.Text = label; l.ForeColor = Theme.Sub; l.AutoSize = true; l.Location = new Point(x, y);
                Controls.Add(l); y += Theme.S(22);
                c.Location = new Point(x, y); c.Width = w; Controls.Add(c); y += c.Height + Theme.S(12);
            };
            tName = Field(src.Name); tHost = Field(src.Host); tUser = Field(src.User); tPass = Field(src.Pass); tPass.UseSystemPasswordChar = true;
            add("サイト名", tName);
            add("接続先 (IPアドレス / ホスト名。ポート指定は 例: 192.0.2.10:2222)", tHost);
            add("ユーザー名", tUser);
            add("パスワード", tPass);
            cShow = new CheckBox(); cShow.Text = "パスワードを表示"; cShow.ForeColor = Theme.Sub; cShow.AutoSize = true;
            cShow.Location = new Point(x, y - Theme.S(8)); cShow.CheckedChanged += delegate { tPass.UseSystemPasswordChar = !cShow.Checked; };
            Controls.Add(cShow); y += Theme.S(26);
            cEnc = new ComboBox(); cEnc.DropDownStyle = ComboBoxStyle.DropDownList; cEnc.FlatStyle = FlatStyle.Flat;
            cEnc.BackColor = Theme.Field; cEnc.ForeColor = Theme.Text;
            cEnc.Items.Add("UTF-8"); cEnc.Items.Add("シフトJIS (Shift_JIS)"); cEnc.SelectedIndex = src.Enc == 1 ? 1 : 0;
            add("文字コード", cEnc);
            Label li = new Label(); li.Text = "アイコン"; li.ForeColor = Theme.Sub; li.AutoSize = true; li.Location = new Point(x, y); Controls.Add(li); y += Theme.S(22);
            iconPanel = new DBPanel(); iconPanel.Location = new Point(x, y); iconPanel.Size = new Size(w, Theme.S(88)); iconPanel.BackColor = Theme.Bg;
            iconPanel.Paint += PaintIcons; iconPanel.MouseUp += IconClick;
            Controls.Add(iconPanel);
            y += iconPanel.Height + Theme.S(14);

            Button ok = Btn("保存", true), cancel = Btn("キャンセル", false);
            ok.Location = new Point(ClientSize.Width - x - Theme.S(100) * 2 - Theme.S(10), y);
            cancel.Location = new Point(ClientSize.Width - x - Theme.S(100), y);
            ok.Click += OnOk; cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(ClientSize.Width, y + Theme.S(54));
            Result = null; resultBase = src;
        }
        Site resultBase;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); N.DarkTitle(Handle, true); }

        TextBox Field(string v)
        {
            TextBox t = new TextBox(); t.Text = v ?? ""; t.BackColor = Theme.Field; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.F(10.5f);
            return t;
        }
        Button Btn(string text, bool primary)
        {
            Button b = new Button(); b.Text = text; b.Size = new Size(Theme.S(100), Theme.S(34)); b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border;
            b.BackColor = primary ? Theme.Accent : Theme.Card; b.ForeColor = primary ? Theme.Bg : Theme.Text; b.UseVisualStyleBackColor = false;
            return b;
        }

        void PaintIcons(object o, PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            iconRects.Clear();
            int cell = Theme.S(40), per = 8;
            for (int i = 0; i < Icons.Length; i++)
            {
                Rectangle r = new Rectangle((i % per) * (cell + Theme.S(4)), (i / per) * (cell + Theme.S(4)), cell, cell);
                iconRects.Add(r);
                bool sel = Icons[i] == icon;
                using (var p = Gfx.Round(r, Theme.S(8)))
                {
                    using (var b = new SolidBrush(sel ? Color.FromArgb(60, Theme.Accent) : Theme.Card)) g.FillPath(b, p);
                    using (var pen = new Pen(sel ? Theme.Accent : Theme.Border, sel ? 1.8f : 1f)) g.DrawPath(pen, p);
                }
                Gfx.DrawEmoji(g, Icons[i], new Rectangle(r.X + Theme.S(7), r.Y + Theme.S(7), cell - Theme.S(14), cell - Theme.S(14)));
            }
        }
        void IconClick(object o, MouseEventArgs e)
        {
            for (int i = 0; i < iconRects.Count; i++) if (iconRects[i].Contains(e.Location)) { icon = Icons[i]; iconPanel.Invalidate(); return; }
        }

        void OnOk(object o, EventArgs e)
        {
            string err = null;
            if (tName.Text.Trim().Length == 0) err = "サイト名を入力してください。";
            else if (tHost.Text.Trim().Length == 0) err = "接続先を入力してください。";
            else if (tUser.Text.Trim().Length == 0) err = "ユーザー名を入力してください。";
            if (err != null) { MessageBox.Show(this, err, "Remo", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Site s = resultBase.Clone();
            string oldHost = s.Host;
            s.Name = tName.Text.Trim(); s.Host = tHost.Text.Trim(); s.User = tUser.Text.Trim(); s.Pass = tPass.Text;
            s.Enc = cEnc.SelectedIndex; s.Icon = icon;
            if (oldHost != s.Host) s.HostKey = "";
            Result = s;
            DialogResult = DialogResult.OK;
        }
    }
}

namespace Remo
{
    // ---- パスワード入力ダイアログ(書き出し/読み込み用) ----
    class PasswordDialog : Form
    {
        TextBox t1, t2;
        bool confirm;
        public string Password { get { return t1.Text; } }

        public PasswordDialog(string title, string message, bool confirm)
        {
            this.confirm = confirm;
            Text = title; Font = Theme.F(9.5f);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            int x = Theme.S(24), w = Theme.S(380), y = Theme.S(18);
            Label m = new Label(); m.Text = message; m.ForeColor = Theme.Text; m.Location = new Point(x, y);
            m.Size = new Size(w, Theme.S(58)); Controls.Add(m); y += Theme.S(62);
            t1 = Box(); t1.Location = new Point(x, y); t1.Width = w; Controls.Add(t1); y += t1.Height + Theme.S(10);
            if (confirm)
            {
                Label l2 = new Label(); l2.Text = "確認のためもう一度入力"; l2.ForeColor = Theme.Sub; l2.AutoSize = true; l2.Location = new Point(x, y);
                Controls.Add(l2); y += Theme.S(22);
                t2 = Box(); t2.Location = new Point(x, y); t2.Width = w; Controls.Add(t2); y += t2.Height + Theme.S(10);
            }
            CheckBox show = new CheckBox(); show.Text = "パスワードを表示"; show.ForeColor = Theme.Sub; show.AutoSize = true; show.Location = new Point(x, y);
            show.CheckedChanged += delegate { t1.UseSystemPasswordChar = !show.Checked; if (t2 != null) t2.UseSystemPasswordChar = !show.Checked; };
            Controls.Add(show); y += Theme.S(34);
            Button ok = Btn("OK", true), cancel = Btn("キャンセル", false);
            ok.Location = new Point(x + w - Theme.S(100) * 2 - Theme.S(10), y); cancel.Location = new Point(x + w - Theme.S(100), y);
            ok.Click += OnOk; cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(w + x * 2, y + Theme.S(54));
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); N.DarkTitle(Handle, true); }

        TextBox Box()
        {
            TextBox t = new TextBox(); t.BackColor = Theme.Field; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.F(10.5f); t.UseSystemPasswordChar = true; return t;
        }
        Button Btn(string text, bool primary)
        {
            Button b = new Button(); b.Text = text; b.Size = new Size(Theme.S(100), Theme.S(34)); b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border;
            b.BackColor = primary ? Theme.Accent : Theme.Card; b.ForeColor = primary ? Theme.Bg : Theme.Text; b.UseVisualStyleBackColor = false;
            return b;
        }
        void OnOk(object o, EventArgs e)
        {
            if (t1.Text.Length == 0) { MessageBox.Show(this, "パスワードを入力してください。", "Remo"); return; }
            if (confirm)
            {
                if (t1.Text.Length < 6) { MessageBox.Show(this, "パスワードは6文字以上にしてください。", "Remo"); return; }
                if (t1.Text != t2.Text) { MessageBox.Show(this, "2つのパスワードが一致しません。", "Remo"); return; }
            }
            DialogResult = DialogResult.OK;
        }
    }
}
