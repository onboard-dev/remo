// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Remo
{
    class TermView : Control
    {
        public Terminal Term;
        public Action<byte[]> SendBytes;          // キー入力 → サーバー
        public Action<int, int> SizeChanged2;     // cols, rows
        public Action TripleClicked;
        public Func<bool> Sjis;                   // 現在の文字コードがSJISか

        // ---- 配色 (Tokyo Night風) ----
        public static int DefFg = 0xC0CAF5, DefBg = 0x1A1B26, SelBg = 0x33467C, CurColor = 0xC0CAF5;
        static readonly int[] Pal = BuildPalette();
        static int[] BuildPalette()
        {
            int[] p = new int[256];
            int[] b = { 0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
                        0x565F89, 0xFF899D, 0xB9F27C, 0xFFC777, 0x9AB8FF, 0xCBA8FF, 0xA4E0FF, 0xC0CAF5 };
            for (int i = 0; i < 16; i++) p[i] = b[i];
            int[] lv = { 0, 95, 135, 175, 215, 255 };
            for (int i = 0; i < 216; i++) p[16 + i] = (lv[i / 36] << 16) | (lv[(i / 6) % 6] << 8) | lv[i % 6];
            for (int i = 0; i < 24; i++) { int g = 8 + i * 10; p[232 + i] = (g << 16) | (g << 8) | g; }
            return p;
        }

        // ---- フォント ----
        string latinName, jpName;
        public float FontPt = 11f;
        public string FontName = "";
        int cw, ch, baseLine, jpOffW, jpOffN;
        IntPtr[] fonts = new IntPtr[8]; // [cls*4 + bold*2 + italic]
        IntPtr memDC, dib, oldBmp, bits;
        int bufW, bufH;
        int padL, padT;
        float scale = 1f;

        // ---- 状態 ----
        public int ViewOffset;   // 0=最新
        int lastScrolledIn;
        bool selActive, selDragging; int sAr, sAc, sBr, sBc;
        bool cursorPhase = true;
        Timer blink;
        bool dirty = true;
        bool composing, errShown, firstPaintLogged;

        public TermView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            TabStop = true;
            BackColor = Theme.C(DefBg);
            Cursor = Cursors.IBeam;
            try { using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f; } catch { }
            padL = (int)(8 * scale); padT = (int)(6 * scale);
            blink = new Timer(); blink.Interval = 530;
            blink.Tick += delegate { cursorPhase = !cursorPhase; if (Focused && Term != null && Term.CursorVisible) { dirty = true; Invalidate(); } };
            blink.Start();
        }

        public int Cw { get { return cw; } }
        public int Ch { get { return ch; } }
        public float DpiScale { get { return scale; } }

        static List<string> fams;
        static List<string> Families()
        {
            if (fams != null) return fams;
            fams = new List<string>();
            try { foreach (var f in new InstalledFontCollection().Families) fams.Add(f.Name); } catch { }
            return fams;
        }

        static string Find(string[] c, string def)
        {
            foreach (var n in c) foreach (var f in Families()) if (string.Equals(f, n, StringComparison.OrdinalIgnoreCase)) return f;
            return def;
        }

        public string CurrentFont { get { return latinName; } }

        void PickFonts()
        {
            string def = Find(new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "MS Gothic" }, "Courier New");
            latinName = (!string.IsNullOrEmpty(FontName) && Families().Contains(FontName)) ? FontName : def;
            if (IsJapanese(latinName)) jpName = latinName;
            else jpName = Find(new[] { "BIZ UDGothic", "BIZ UDゴシック", "MS Gothic", "ＭＳ ゴシック", "Yu Gothic", "Meiryo" }, "MS Gothic");
        }

        // フォントの情報(等幅か・日本語を含むか)を調べる
        static void Probe(string name, out bool fixedPitch, out bool japanese)
        {
            fixedPitch = false; japanese = false;
            IntPtr dc = N.CreateCompatibleDC(IntPtr.Zero);
            IntPtr f = N.CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 4, 0, 5, 0, name);
            IntPtr old = N.SelectObject(dc, f);
            try
            {
                TEXTMETRIC tm; N.GetTextMetricsW(dc, out tm);
                SIZE a, b; N.GetTextExtentPoint32W(dc, "iiii", 4, out a); N.GetTextExtentPoint32W(dc, "WWWW", 4, out b);
                fixedPitch = (tm.tmPitchAndFamily & 1) == 0 || (a.cx == b.cx && a.cx > 0);
                if (a.cx != b.cx) fixedPitch = false;
                japanese = tm.tmCharSet == 128;
            }
            finally { N.SelectObject(dc, old); N.DeleteObject(f); N.DeleteDC(dc); }
        }

        static Dictionary<string, bool> jpCache = new Dictionary<string, bool>();
        static bool IsJapanese(string name)
        {
            bool j;
            if (jpCache.TryGetValue(name, out j)) return j;
            bool fx; Probe(name, out fx, out j);
            jpCache[name] = j;
            return j;
        }

        static List<string> mono;
        public static List<string> MonospaceFonts()
        {
            if (mono != null) return mono;
            mono = new List<string>();
            foreach (string n in Families())
            {
                if (n.StartsWith("@")) continue;
                try
                {
                    bool fx, jp; Probe(n, out fx, out jp);
                    jpCache[n] = jp;
                    if (fx) mono.Add(n);
                }
                catch { }
            }
            mono.Sort(StringComparer.CurrentCultureIgnoreCase);
            return mono;
        }

        public void SetFont(string name, float pt)
        {
            FontName = name ?? "";
            FontPt = Math.Max(7f, Math.Min(36f, pt));
            BuildFonts();
            RecalcGrid();
            dirty = true;
            Invalidate();
        }

        IntPtr MakeFont(string name, int px, bool bold, bool italic)
        {
            return N.CreateFontW(-px, 0, 0, 0, bold ? 700 : 400, italic ? 1u : 0u, 0, 0, 1, 4, 0, 5, 0x31, name);
        }

        public void BuildFonts()
        {
            PickFonts();
            Log.W("buildfonts " + latinName + " / " + jpName);
            foreach (var f in fonts) if (f != IntPtr.Zero) N.DeleteObject(f);
            Array.Clear(fonts, 0, fonts.Length);
            int px = (int)Math.Round(FontPt * 96f * scale / 72f);
            IntPtr dc = N.GetDC(IntPtr.Zero);
            IntPtr mdc = N.CreateCompatibleDC(dc);
            IntPtr lf = MakeFont(latinName, px, false, false);
            IntPtr old = N.SelectObject(mdc, lf);
            TEXTMETRIC tm; N.GetTextMetricsW(mdc, out tm);
            SIZE sz; N.GetTextExtentPoint32W(mdc, "0000000000", 10, out sz);
            cw = Math.Max(4, (sz.cx + 5) / 10);
            int lAsc = tm.tmAscent, lDesc = tm.tmDescent;
            N.SelectObject(mdc, old); N.DeleteObject(lf);
            // 全角文字も同じ文字サイズ(px)で描き、2マスの中央に置く
            IntPtr jf = MakeFont(jpName, px, false, false);
            old = N.SelectObject(mdc, jf);
            TEXTMETRIC tj; N.GetTextMetricsW(mdc, out tj);
            SIZE js; N.GetTextExtentPoint32W(mdc, "\u3042", 1, out js);
            int jpAdv = js.cx > 0 ? js.cx : px;
            jpOffW = Math.Max(0, (cw * 2 - jpAdv) / 2);
            jpOffN = Math.Max(0, (cw - jpAdv / 2) / 2);
            N.SelectObject(mdc, old); N.DeleteObject(jf);
            N.DeleteDC(mdc); N.ReleaseDC(IntPtr.Zero, dc);
            int asc = Math.Max(lAsc, tj.tmAscent), desc = Math.Max(lDesc, tj.tmDescent);
            baseLine = asc + 1;
            ch = asc + desc + 2;
            int jpx = px;
            Log.W("font " + FontPt + "pt = " + px + "px, cell " + cw + "x" + ch + ", jpAdv " + jpAdv + ", dpi x" + scale);
            for (int cls = 0; cls < 2; cls++)
                for (int b = 0; b < 2; b++)
                    for (int i = 0; i < 2; i++)
                        fonts[cls * 4 + b * 2 + i] = MakeFont(cls == 0 ? latinName : jpName, cls == 0 ? px : jpx, b == 1, i == 1);
            dirty = true;
        }

        public void SetFontPt(float pt)
        {
            FontPt = Math.Max(7f, Math.Min(36f, pt));
            BuildFonts();
            RecalcGrid();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (fonts[0] == IntPtr.Zero) BuildFonts();
            RecalcGrid();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (fonts[0] != IntPtr.Zero) RecalcGrid();
            dirty = true; Invalidate();
        }

        public void RecalcGrid()
        {
            if (Term == null || cw <= 0 || ch <= 0) return;
            int cols = Math.Max(2, (ClientSize.Width - padL * 2) / cw);
            int rows = Math.Max(1, (ClientSize.Height - padT * 2) / ch);
            if (cols != Term.Cols || rows != Term.Rows)
            {
                Term.Resize(cols, rows);
                if (SizeChanged2 != null) SizeChanged2(Term.Cols, Term.Rows);
                ClearSelection();
            }
            dirty = true;
        }

        public void MarkDirty() { dirty = true; Invalidate(); }

        // 出力を受けたときに呼ぶ
        public void AfterFeed()
        {
            if (ViewOffset > 0 && !Term.Alt)
            {
                ViewOffset += Term.ScrolledIn - lastScrolledIn;
                if (ViewOffset > Term.ScrollbackCount) ViewOffset = Term.ScrollbackCount;
            }
            else if (Term.Alt) ViewOffset = 0;
            lastScrolledIn = Term.ScrolledIn;
            cursorPhase = true;
            dirty = true;
            Invalidate();
            if (composing) UpdateImePos();
        }

        // ---- 描画 ----
        protected override void OnPaintBackground(PaintEventArgs e) { }

        void EnsureBuffer(IntPtr hdc)
        {
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            if (memDC != IntPtr.Zero && w == bufW && h == bufH) return;
            if (memDC != IntPtr.Zero)
            {
                N.SelectObject(memDC, oldBmp); N.DeleteObject(dib); N.DeleteDC(memDC);
            }
            memDC = N.CreateCompatibleDC(hdc);
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            dib = N.CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = N.SelectObject(memDC, dib);
            bufW = w; bufH = h; dirty = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            IntPtr hdc = g.GetHdc();
            try
            {
                if (Term == null || fonts[0] == IntPtr.Zero)
                {
                    return;
                }
                EnsureBuffer(hdc);
                if (dirty)
                {
                    if (!firstPaintLogged) { firstPaintLogged = true; Log.W("first render"); }
                    try { Render(); }
                    catch (Exception ex)
                    {
                        if (!errShown) { errShown = true; Log.W("RENDER ERROR: " + ex); }
                    }
                    dirty = false;
                }
                Rectangle c = e.ClipRectangle;
                N.BitBlt(hdc, c.X, c.Y, c.Width, c.Height, memDC, c.X, c.Y, N.SRCCOPY);
            }
            finally { g.ReleaseHdc(hdc); }
        }

        void FillRect(int x, int y, int w, int h, int rgb)
        {
            RECT r = new RECT(x, y, x + w, y + h);
            N.SetBkColor(memDC, N.ColorRef(rgb));
            N.ExtTextOutW(memDC, 0, 0, N.ETO_OPAQUE, ref r, null, 0, null);
        }

        struct Pending { public string Text; public int X, Y, W, Rgb; public bool Wide; }
        List<Pending> emojis = new List<Pending>();

        // 現在のランの情報
        StringBuilder runText = new StringBuilder();
        List<int> runDx = new List<int>();
        int runX, runY, runW, runFg, runBg, runFont, runOff; byte runDeco; bool runActive;

        void FlushRun()
        {
            if (!runActive) return;
            runActive = false;
            if (runText.Length == 0) return;
            RECT r = new RECT(runX, runY, runX + runW, runY + ch);
            N.SelectObject(memDC, fonts[runFont]);
            N.SetTextColor(memDC, N.ColorRef(runFg));
            N.SetBkColor(memDC, N.ColorRef(runBg));
            N.SetTextAlign(memDC, 24);
            N.ExtTextOutW(memDC, runX + runOff, runY + baseLine, N.ETO_OPAQUE | N.ETO_CLIPPED, ref r, runText.ToString(), (uint)runText.Length, runDx.ToArray());
            int th = Math.Max(1, ch / 14);
            if ((runDeco & At.Underline) != 0) FillRect(runX, runY + baseLine + 1, runW, th, runFg);
            if ((runDeco & At.Strike) != 0) FillRect(runX, runY + ch / 2, runW, th, runFg);
            runText.Length = 0; runDx.Clear();
        }

        int Resolve(int c, bool isFg, byte attr)
        {
            if (c == 0) return isFg ? DefFg : DefBg;
            if ((c >> 24) == 1)
            {
                int idx = c & 0xFF;
                if (isFg && (attr & At.Bold) != 0 && idx < 8) idx += 8;
                return Pal[idx];
            }
            return c & 0xFFFFFF;
        }

        static int Blend(int a, int b, int na, int nb)
        {
            int r = (((a >> 16) & 255) * na + ((b >> 16) & 255) * nb) / (na + nb);
            int g = (((a >> 8) & 255) * na + ((b >> 8) & 255) * nb) / (na + nb);
            int bl = ((a & 255) * na + (b & 255) * nb) / (na + nb);
            return (r << 16) | (g << 8) | bl;
        }

        bool InSel(int absRow, int col)
        {
            if (!selActive) return false;
            int r1 = sAr, c1 = sAc, r2 = sBr, c2 = sBc;
            if (r1 > r2 || (r1 == r2 && c1 > c2)) { int t = r1; r1 = r2; r2 = t; t = c1; c1 = c2; c2 = t; }
            if (absRow < r1 || absRow > r2) return false;
            if (absRow == r1 && col < c1) return false;
            if (absRow == r2 && col > c2) return false;
            return true;
        }

        void Render()
        {
            Terminal t = Term;
            FillRect(0, 0, bufW, bufH, DefBg);
            emojis.Clear();
            int sbCount = t.ScrollbackCount;
            bool showCursor = t.CursorVisible && ViewOffset == 0;
            bool focused = Focused;
            bool curOn = showCursor && (!focused || cursorPhase || composing);
            for (int y = 0; y < t.Rows; y++)
            {
                int abs = sbCount - ViewOffset + y;
                Row row = t.GetLine(abs);
                int py = padT + y * ch;
                runActive = false;
                for (int x = 0; x < t.Cols; x++)
                {
                    Cell c;
                    if (row != null && x < row.C.Length) c = row.C[x]; else { c = new Cell(); c.W = 1; }
                    if (c.W == 0)
                    {
                        if (x > 0 && row != null && x - 1 < row.C.Length && row.C[x - 1].W == 2) continue;
                        c = new Cell(); c.W = 1;
                    }
                    int w = c.W;
                    int fg = Resolve(c.Fg, true, c.Attr), bg = Resolve(c.Bg, false, c.Attr);
                    if ((c.Attr & At.Reverse) != 0) { int tmp = fg; fg = bg; bg = tmp; }
                    if ((c.Attr & At.Dim) != 0) fg = Blend(fg, bg, 2, 1);
                    if ((c.Attr & At.Hidden) != 0) fg = bg;
                    if (InSel(abs, x)) { bg = SelBg; if (fg == bg) fg = DefFg; }
                    bool isCursor = curOn && abs == sbCount + t.CY && (x == t.CX || (w == 2 && x + 1 == t.CX));
                    bool hollow = false;
                    if (isCursor)
                    {
                        if (focused) { int tmp = bg; bg = CurColor; fg = (tmp == CurColor) ? DefBg : tmp; }
                        else hollow = true;
                    }
                    int ccp = c.Ch;
                    int px = padL + x * cw;
                    if (ccp != 0 && Terminal.NeedsDWrite(ccp) && Emoji.Available)
                    {
                        FlushRun();
                        FillRect(px, py, w * cw, ch, bg);
                        Pending p = new Pending();
                        p.Text = char.ConvertFromUtf32(ccp) + (c.Ext ?? ""); p.X = px; p.Y = py; p.W = w * cw; p.Rgb = fg; p.Wide = w == 2 || Terminal.IsWide(ccp) || (c.Ext != null && c.Ext.IndexOf('️') >= 0);
                        emojis.Add(p);
                    }
                    else
                    {
                        bool jp = w == 2 || (ccp >= 0xFF61 && ccp <= 0xFF9F);
                        int font = (jp ? 4 : 0) + (((c.Attr & At.Bold) != 0) ? 2 : 0) + (((c.Attr & At.Italic) != 0) ? 1 : 0);
                        byte deco = (byte)(c.Attr & (At.Underline | At.Strike));
                        int off = jp ? (w == 2 ? jpOffW : jpOffN) : 0;
                        if (runActive && (fg != runFg || bg != runBg || font != runFont || deco != runDeco || off != runOff)) FlushRun();
                        if (!runActive)
                        {
                            runActive = true; runX = px; runY = py; runW = 0; runFg = fg; runBg = bg; runFont = font; runDeco = deco; runOff = off;
                        }
                        if (ccp == 0 || ccp < 0x20) { runText.Append(' '); runDx.Add(w * cw); }
                        else if (ccp >= 0x10000)
                        {
                            string s = char.ConvertFromUtf32(ccp);
                            runText.Append(s); runDx.Add(w * cw); runDx.Add(0);
                        }
                        else if (ccp >= 0xD800 && ccp <= 0xDFFF) { runText.Append('?'); runDx.Add(w * cw); }
                        else { runText.Append((char)ccp); runDx.Add(w * cw); }
                        runW += w * cw;
                    }
                    if (hollow)
                    {
                        FlushRun();
                        int cwid = w * cw;
                        FillRect(px, py, cwid, 1, CurColor); FillRect(px, py + ch - 1, cwid, 1, CurColor);
                        FillRect(px, py, 1, ch, CurColor); FillRect(px + cwid - 1, py, 1, ch, CurColor);
                    }
                }
                FlushRun();
            }
            if (emojis.Count > 0 && Emoji.Begin(memDC, bufW, bufH))
            {
                float sizeW = Math.Min(ch * 0.78f, cw * 2 * 0.80f);
                float sizeN = Math.Min(ch * 0.78f, cw * 0.95f);
                foreach (var p in emojis)
                    Emoji.Draw(p.Text, p.X, p.Y, p.W, ch, p.Rgb, p.W >= cw * 2 ? sizeW : sizeN);
                Emoji.End();
            }
        }

        // ---- IME ----
        const int WM_IME_STARTCOMPOSITION = 0x10D, WM_IME_ENDCOMPOSITION = 0x10E, WM_IME_COMPOSITION = 0x10F, WM_SYSCHAR = 0x106, WM_IME_CHAR = 0x286;

        void UpdateImePos()
        {
            if (!IsHandleCreated || Term == null) return;
            IntPtr himc = N.ImmGetContext(Handle);
            if (himc == IntPtr.Zero) return;
            try
            {
                int x = padL + Term.CX * cw, y = padT + Term.CY * ch;
                COMPOSITIONFORM cf = new COMPOSITIONFORM(); cf.dwStyle = 2; cf.x = x; cf.y = y;
                N.ImmSetCompositionWindow(himc, ref cf);
                CANDIDATEFORM cd = new CANDIDATEFORM(); cd.dwIndex = 0; cd.dwStyle = 0x40; cd.x = x; cd.y = y + ch;
                N.ImmSetCandidateWindow(himc, ref cd);
            }
            finally { N.ImmReleaseContext(Handle, himc); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_IME_STARTCOMPOSITION) { composing = true; UpdateImePos(); }
            else if (m.Msg == WM_IME_COMPOSITION) { UpdateImePos(); }
            else if (m.Msg == WM_IME_ENDCOMPOSITION) { composing = false; }
            else if (m.Msg == WM_SYSCHAR)
            {
                // Alt+文字 → ESC + 文字
                char c = (char)m.WParam.ToInt32();
                if (c >= 0x20 && c != 0x7F)
                {
                    Send(Concat(new byte[] { 0x1B }, Encode(c.ToString())));
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // ---- キー入力 ----
        public Encoding Enc { get { return (Sjis != null && Sjis()) ? Encoding.GetEncoding(932) : new UTF8Encoding(false); } }
        byte[] Encode(string s) { return Enc.GetBytes(s); }
        static byte[] Concat(byte[] a, byte[] b) { byte[] r = new byte[a.Length + b.Length]; Array.Copy(a, r, a.Length); Array.Copy(b, 0, r, a.Length, b.Length); return r; }

        void Send(byte[] b)
        {
            if (b == null || b.Length == 0) return;
            if (ViewOffset != 0) { ViewOffset = 0; dirty = true; Invalidate(); }
            if (selActive) { selActive = false; dirty = true; Invalidate(); }
            cursorPhase = true;
            if (SendBytes != null) SendBytes(b);
        }
        void SendStr(string s) { Send(Encoding.ASCII.GetBytes(s)); }

        protected override bool IsInputKey(Keys keyData) { return true; }
        protected override bool IsInputChar(char charCode) { return true; }

        static int ModNum(Keys k)
        {
            int m = 1;
            if ((k & Keys.Shift) != 0) m += 1;
            if ((k & Keys.Alt) != 0) m += 2;
            if ((k & Keys.Control) != 0) m += 4;
            return m;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Term == null) return base.ProcessCmdKey(ref msg, keyData);
            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0;
            int mod = ModNum(keyData);
            string cur = Term.AppCursor ? "\x1bO" : "\x1b[";

            // コピー/ペースト系ショートカット
            if (ctrl && shift && key == Keys.V) { Paste(); return true; }
            if (ctrl && shift && key == Keys.C) { CopySelection(false); return true; }
            if (shift && !ctrl && !alt && key == Keys.Insert) { Paste(); return true; }
            if (shift && !ctrl && !alt && key == Keys.PageUp) { ScrollView(Term.Rows - 1); return true; }
            if (shift && !ctrl && !alt && key == Keys.PageDown) { ScrollView(-(Term.Rows - 1)); return true; }
            if (alt && key == Keys.F4) return base.ProcessCmdKey(ref msg, keyData);
            if (alt && key == Keys.Space) return base.ProcessCmdKey(ref msg, keyData);

            string seq = null;
            switch (key)
            {
                case Keys.Up: seq = mod == 1 ? cur + "A" : "\x1b[1;" + mod + "A"; break;
                case Keys.Down: seq = mod == 1 ? cur + "B" : "\x1b[1;" + mod + "B"; break;
                case Keys.Right: seq = mod == 1 ? cur + "C" : "\x1b[1;" + mod + "C"; break;
                case Keys.Left: seq = mod == 1 ? cur + "D" : "\x1b[1;" + mod + "D"; break;
                case Keys.Home: seq = mod == 1 ? cur + "H" : "\x1b[1;" + mod + "H"; break;
                case Keys.End: seq = mod == 1 ? cur + "F" : "\x1b[1;" + mod + "F"; break;
                case Keys.Insert: seq = mod == 1 ? "\x1b[2~" : "\x1b[2;" + mod + "~"; break;
                case Keys.Delete: seq = mod == 1 ? "\x1b[3~" : "\x1b[3;" + mod + "~"; break;
                case Keys.PageUp: seq = mod == 1 ? "\x1b[5~" : "\x1b[5;" + mod + "~"; break;
                case Keys.PageDown: seq = mod == 1 ? "\x1b[6~" : "\x1b[6;" + mod + "~"; break;
                case Keys.F1: seq = mod == 1 ? "\x1bOP" : "\x1b[1;" + mod + "P"; break;
                case Keys.F2: seq = mod == 1 ? "\x1bOQ" : "\x1b[1;" + mod + "Q"; break;
                case Keys.F3: seq = mod == 1 ? "\x1bOR" : "\x1b[1;" + mod + "R"; break;
                case Keys.F4: seq = mod == 1 ? "\x1bOS" : "\x1b[1;" + mod + "S"; break;
                case Keys.F5: seq = FKey(15, mod); break;
                case Keys.F6: seq = FKey(17, mod); break;
                case Keys.F7: seq = FKey(18, mod); break;
                case Keys.F8: seq = FKey(19, mod); break;
                case Keys.F9: seq = FKey(20, mod); break;
                case Keys.F10: seq = FKey(21, mod); break;
                case Keys.F11: seq = FKey(23, mod); break;
                case Keys.F12: seq = FKey(24, mod); break;
                case Keys.Tab: seq = shift ? "\x1b[Z" : "\t"; break;
                case Keys.Enter: seq = alt ? "\x1b\r" : "\r"; break;
                case Keys.Escape: seq = "\x1b"; break;
                case Keys.Back: seq = alt ? "\x1b\x7f" : (ctrl ? "\x08" : "\x7f"); break;
                case Keys.Space: if (ctrl) seq = "\0"; break;
            }
            if (seq != null) { Send(Encoding.ASCII.GetBytes(seq)); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static string FKey(int n, int mod) { return mod == 1 ? "\x1b[" + n + "~" : "\x1b[" + n + ";" + mod + "~"; }

        char highSurrogate;
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            char c = e.KeyChar;
            e.Handled = true;
            if (char.IsHighSurrogate(c)) { highSurrogate = c; return; }
            string s;
            if (char.IsLowSurrogate(c)) { if (highSurrogate == 0) return; s = new string(new[] { highSurrogate, c }); highSurrogate = '\0'; }
            else s = c.ToString();
            if (c < 0x20 || c == 0x7F)
            {
                if (c == '\r' || c == '\t' || c == 0x1B || c == 0x08) { }
                Send(new byte[] { (byte)c });
                return;
            }
            Send(Encode(s));
        }

        // ---- コピー/ペースト ----
        public bool HasSelection { get { return selActive && !(sAr == sBr && sAc == sBc && !selDragging && false); } }

        public void ClearSelection() { if (selActive) { selActive = false; dirty = true; Invalidate(); } }

        public bool CopySelection(bool clear)
        {
            if (!selActive || Term == null) return false;
            string s = Term.GetText(sAr, sAc, sBr, sBc);
            if (s.Length == 0) return false;
            try { Clipboard.SetText(s, TextDataFormat.UnicodeText); }
            catch { try { System.Threading.Thread.Sleep(30); Clipboard.SetText(s, TextDataFormat.UnicodeText); } catch { } }
            if (clear) ClearSelection();
            return true;
        }

        public void Paste()
        {
            string s = null;
            try { if (Clipboard.ContainsText()) s = Clipboard.GetText(TextDataFormat.UnicodeText); } catch { }
            if (string.IsNullOrEmpty(s)) return;
            s = s.Replace("\r\n", "\r").Replace('\n', '\r');
            if (Term != null && Term.BracketedPaste) s = "\x1b[200~" + s + "\x1b[201~";
            Send(Encode(s));
        }

        // ---- マウス ----
        DateTime lastDown = DateTime.MinValue; Point lastDownPt; int clickCount;

        bool CellAt(Point p, out int absRow, out int col)
        {
            int y = (p.Y - padT) / Math.Max(1, ch);
            int x = (p.X - padL) / Math.Max(1, cw);
            if (p.Y < padT) y = 0; if (p.X < padL) x = 0;
            y = Math.Max(0, Math.Min(Term.Rows - 1, y));
            x = Math.Max(0, Math.Min(Term.Cols - 1, x));
            absRow = Term.ScrollbackCount - ViewOffset + y;
            col = x;
            Row r = Term.GetLine(absRow);
            if (r != null && x < r.C.Length && r.C[x].W == 0 && x > 0) col = x - 1;
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Term == null) return;
            if (e.Button == MouseButtons.Left)
            {
                DateTime now = DateTime.Now;
                if ((now - lastDown).TotalMilliseconds <= SystemInformation.DoubleClickTime && Math.Abs(e.X - lastDownPt.X) < 6 && Math.Abs(e.Y - lastDownPt.Y) < 6) clickCount++;
                else clickCount = 1;
                lastDown = now; lastDownPt = e.Location;
                if (clickCount >= 3)
                {
                    clickCount = 0; selActive = false; selDragging = false; dirty = true; Invalidate();
                    if (TripleClicked != null) TripleClicked();
                    return;
                }
                int r, c; CellAt(e.Location, out r, out c);
                if (clickCount == 2) { SelectWord(r, c); selDragging = false; return; }
                sAr = sBr = r; sAc = sBc = c; selDragging = true; selActive = false;
                dirty = true; Invalidate();
                Capture = true;
            }
        }

        void SelectWord(int r, int c)
        {
            Row row = Term.GetLine(r);
            if (row == null) return;
            Func<int, bool> isWord = delegate (int x)
            {
                if (x < 0 || x >= row.C.Length) return false;
                int ch0 = row.C[x].Ch;
                if (row.C[x].W == 0) return true;
                return ch0 != 0 && ch0 != ' ' && ch0 != '\t';
            };
            if (!isWord(c)) return;
            int a = c, b = c;
            while (a > 0 && isWord(a - 1)) a--;
            while (b < row.C.Length - 1 && isWord(b + 1)) b++;
            sAr = sBr = r; sAc = a; sBc = b; selActive = true;
            dirty = true; Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selDragging && Term != null && (e.Button & MouseButtons.Left) != 0)
            {
                if (e.Y < padT) ScrollView(1); else if (e.Y > ClientSize.Height - padT) ScrollView(-1);
                int r, c; CellAt(e.Location, out r, out c);
                if (r != sBr || c != sBc || !selActive)
                {
                    sBr = r; sBc = c;
                    selActive = !(sAr == sBr && sAc == sBc);
                    dirty = true; Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { selDragging = false; Capture = false; }
            else if (e.Button == MouseButtons.Right)
            {
                if (selActive) CopySelection(true);   // 選択あり → コピー
                else Paste();                          // 選択なし → ペースト
            }
        }

        public void ScrollView(int lines)
        {
            if (Term == null || Term.Alt) return;
            ViewOffset = Math.Max(0, Math.Min(Term.ScrollbackCount, ViewOffset + lines));
            dirty = true; Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Term == null) return;
            int notches = e.Delta / 120; if (notches == 0) notches = e.Delta > 0 ? 1 : -1;
            if ((Control.ModifierKeys & Keys.Control) != 0)
            {
                SetFontPt(FontPt + notches);
                if (FontChanged2 != null) FontChanged2(FontPt);
                return;
            }
            if (Term.Alt)
            {
                string k = (notches > 0 ? (Term.AppCursor ? "\x1bOA" : "\x1b[A") : (Term.AppCursor ? "\x1bOB" : "\x1b[B"));
                var sb = new StringBuilder(); for (int i = 0; i < Math.Abs(notches) * 3; i++) sb.Append(k);
                SendStr(sb.ToString());
            }
            else ScrollView(notches * 3);
        }
        public Action<float> FontChanged2;

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); dirty = true; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); dirty = true; Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (blink != null) blink.Stop();
            base.Dispose(disposing);
        }
    }
}
