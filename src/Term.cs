// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Remo
{
    // 色: 0=既定, 0x01000000|idx=パレット, 0x02000000|rgb=True Color
    struct Cell
    {
        public int Ch;      // 0 = 空白
        public string Ext;  // 結合文字・絵文字シーケンスの続き
        public int Fg, Bg;
        public byte Attr;
        public byte W;      // 1=通常 2=全角の左 0=全角の右(継続)
        public bool Wide2;  // SJISの2バイト文字など「全角扱い」
    }

    static class At
    {
        public const byte Bold = 1, Dim = 2, Italic = 4, Underline = 8, Blink = 16, Reverse = 32, Hidden = 64, Strike = 128;
    }

    class Row
    {
        public Cell[] C;
        public bool Wrap;
        public Row(int n) { C = new Cell[n]; for (int i = 0; i < n; i++) C[i].W = 1; }
    }

    class Terminal
    {
        public int Cols, Rows;
        public List<Row> Scrollback = new List<Row>();
        public int MaxScrollback = 10000;
        Row[] screen, altScreen;
        public bool Alt;
        public int CX, CY;
        bool wrapPending;
        int top, bottom;
        bool[] tabs;
        // 属性
        int curFg, curBg; byte curAttr;
        // モード
        public bool AppCursor, AppKeypad, AutoWrap = true, Origin, Insert, CursorVisible = true, BracketedPaste;
        public bool Sjis;
        // 保存カーソル
        int sx, sy, sFg, sBg; byte sAttr; bool sWrap, sOrigin;
        int[] charset = new int[2]; int gl; // 0=ASCII 1=DEC graphics

        public void SetEncoding(bool sjis) { Sjis = sjis; sjLead = 0; u8need = 0; }

        public Action<byte[]> Reply;
        public Action<string> TitleChanged;
        public int Version; // 画面更新カウンタ

        // パーサ状態
        enum St { Ground, Esc, EscInter, Csi, Osc, OscEsc, Str, StrEsc }
        St st = St.Ground;
        StringBuilder csiBuf = new StringBuilder();
        StringBuilder oscBuf = new StringBuilder();
        string escInter = "";
        bool zwjPending;

        public Terminal(int cols, int rows)
        {
            Cols = cols; Rows = rows;
            screen = NewScreen(rows); altScreen = NewScreen(rows);
            top = 0; bottom = rows - 1;
            ResetTabs();
        }

        Row[] NewScreen(int rows)
        {
            Row[] s = new Row[rows];
            for (int i = 0; i < rows; i++) s[i] = new Row(Cols);
            return s;
        }

        void ResetTabs() { tabs = new bool[Cols]; for (int i = 8; i < Cols; i += 8) tabs[i] = true; }

        public void FullReset()
        {
            curFg = curBg = 0; curAttr = 0;
            AppCursor = AppKeypad = Origin = Insert = BracketedPaste = false; AutoWrap = true; CursorVisible = true;
            charset[0] = charset[1] = 0; gl = 0;
            Alt = false;
            screen = NewScreen(Rows); altScreen = NewScreen(Rows);
            top = 0; bottom = Rows - 1; CX = CY = 0; wrapPending = false;
            ResetTabs(); st = St.Ground; Version++;
        }

        // ---- 表示用アクセス ----
        public int TotalLines { get { return (Alt ? 0 : Scrollback.Count) + Rows; } }
        // 絶対行番号(0=最古のスクロールバック)
        public Row GetLine(int abs)
        {
            int sb = Alt ? 0 : Scrollback.Count;
            if (abs < 0) return null;
            if (abs < sb) return Scrollback[abs];
            int r = abs - sb;
            if (r < Rows) return Cur[r];
            return null;
        }
        public int ScrollbackCount { get { return Alt ? 0 : Scrollback.Count; } }
        Row[] Cur { get { return Alt ? altScreen : screen; } }
        public Row ScreenRow(int y) { return Cur[y]; }

        // ---- リサイズ ----
        public void Resize(int cols, int rows)
        {
            if (cols < 2) cols = 2; if (rows < 1) rows = 1;
            if (cols == Cols && rows == Rows) return;
            // 行数
            if (rows < Rows)
            {
                int remove = Rows - rows;
                // カーソルが下端を越える分だけ上を削る(メイン画面ならスクロールバックへ)
                int fromTop = Math.Max(0, Math.Min(remove, CY - rows + 1));
                Row[] ns = new Row[rows];
                for (int i = 0; i < fromTop; i++) if (!Alt) PushScrollback(screen[i]);
                for (int i = 0; i < rows; i++) ns[i] = screen[i + fromTop];
                screen = ns;
                Row[] na = new Row[rows];
                for (int i = 0; i < rows; i++) na[i] = altScreen[i];
                altScreen = na;
                CY -= fromTop; if (CY < 0) CY = 0;
            }
            else if (rows > Rows)
            {
                Row[] ns = new Row[rows]; Row[] na = new Row[rows];
                for (int i = 0; i < rows; i++)
                {
                    ns[i] = i < Rows ? screen[i] : new Row(Cols);
                    na[i] = i < Rows ? altScreen[i] : new Row(Cols);
                }
                screen = ns; altScreen = na;
            }
            Rows = rows;
            if (cols != Cols)
            {
                foreach (var r in screen) ResizeRow(r, cols);
                foreach (var r in altScreen) ResizeRow(r, cols);
                Cols = cols;
                ResetTabs();
            }
            top = 0; bottom = Rows - 1;
            if (CX >= Cols) CX = Cols - 1;
            if (CY >= Rows) CY = Rows - 1;
            wrapPending = false;
            Version++;
        }

        void ResizeRow(Row r, int cols)
        {
            if (r.C.Length == cols) return;
            Cell[] n = new Cell[cols];
            int m = Math.Min(cols, r.C.Length);
            Array.Copy(r.C, n, m);
            for (int i = m; i < cols; i++) n[i].W = 1;
            if (m < r.C.Length && m > 0 && n[m - 1].W == 2) { n[m - 1] = BlankCell(); }
            r.C = n;
        }

        Cell BlankCell()
        {
            Cell c = new Cell(); c.W = 1; c.Bg = curBg; return c;
        }

        void PushScrollback(Row r)
        {
            int last = r.C.Length - 1;
            while (last >= 0 && r.C[last].Ch == 0 && r.C[last].Bg == 0 && r.C[last].Ext == null && (r.C[last].Attr & (At.Reverse | At.Underline | At.Strike)) == 0) last--;
            Row n = new Row(0);
            n.C = new Cell[last + 1];
            Array.Copy(r.C, n.C, last + 1);
            n.Wrap = r.Wrap;
            Scrollback.Add(n);
            if (Scrollback.Count > MaxScrollback) Scrollback.RemoveRange(0, Scrollback.Count - MaxScrollback + 500);
            ScrolledIn++;
        }
        public int ScrolledIn; // スクロールバックに入った行数の累計(ビュー固定用)

        // ---- 入力 ----
        byte sjLead;
        int u8need, u8cp;

        public void Feed(byte[] d, int n)
        {
            for (int i = 0; i < n; i++) FeedByte(d[i]);
            Version++;
        }

        static Encoding sjisEnc;
        void FeedByte(byte b)
        {
            if (Sjis)
            {
                if (sjLead != 0)
                {
                    byte l = sjLead; sjLead = 0;
                    if (b >= 0x40 && b != 0x7F && b <= 0xFC)
                    {
                        if (sjisEnc == null) sjisEnc = Encoding.GetEncoding(932);
                        string s = sjisEnc.GetString(new byte[] { l, b });
                        int cp = s.Length > 0 ? char.ConvertToUtf32(s, 0) : 0xFFFD;
                        if (s.Length >= 1 && s[0] == '�') cp = 0xFFFD;
                        Handle(cp, true);
                        return;
                    }
                    Handle(0xFFFD, true);
                    // fallthrough: b を再処理
                }
                if ((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC)) { sjLead = b; return; }
                if (b >= 0xA1 && b <= 0xDF) { Handle(0xFF61 + (b - 0xA1), false); return; }
                Handle(b, false);
                return;
            }
            // UTF-8
            if (u8need > 0)
            {
                if ((b & 0xC0) == 0x80)
                {
                    u8cp = (u8cp << 6) | (b & 0x3F);
                    if (--u8need == 0) Handle(u8cp, false);
                    return;
                }
                u8need = 0; Handle(0xFFFD, false);
            }
            if (b < 0x80) Handle(b, false);
            else if ((b & 0xE0) == 0xC0) { u8need = 1; u8cp = b & 0x1F; }
            else if ((b & 0xF0) == 0xE0) { u8need = 2; u8cp = b & 0x0F; }
            else if ((b & 0xF8) == 0xF0) { u8need = 3; u8cp = b & 0x07; }
            else Handle(0xFFFD, false);
        }

        // ---- パーサ ----
        void Handle(int cp, bool dbl)
        {
            switch (st)
            {
                case St.Ground:
                    if (cp < 0x20 || cp == 0x7F) { Control(cp); return; }
                    if (cp >= 0x80 && cp < 0xA0) return;
                    PutChar(cp, dbl);
                    return;
                case St.Esc:
                    if (cp == 0x1B) return;
                    if (cp < 0x20) { Control(cp); return; }
                    if (cp >= 0x20 && cp <= 0x2F) { escInter = ((char)cp).ToString(); st = St.EscInter; return; }
                    st = St.Ground;
                    EscFinal((char)cp, "");
                    return;
                case St.EscInter:
                    if (cp < 0x20) { Control(cp); return; }
                    if (cp >= 0x20 && cp <= 0x2F) { escInter += (char)cp; return; }
                    st = St.Ground;
                    EscFinal((char)cp, escInter);
                    return;
                case St.Csi:
                    if (cp == 0x1B) { st = St.Esc; return; }
                    if (cp == 0x18 || cp == 0x1A) { st = St.Ground; return; }
                    if (cp < 0x20) { Control(cp); return; }
                    if (cp >= 0x40 && cp <= 0x7E) { st = St.Ground; DoCsi((char)cp, csiBuf.ToString()); return; }
                    if (csiBuf.Length < 256) csiBuf.Append((char)cp);
                    return;
                case St.Osc:
                    if (cp == 0x07) { st = St.Ground; DoOsc(oscBuf.ToString()); return; }
                    if (cp == 0x1B) { st = St.OscEsc; return; }
                    if (cp == 0x18 || cp == 0x1A) { st = St.Ground; return; }
                    if (oscBuf.Length < 2048) oscBuf.Append(char.ConvertFromUtf32(cp < 0x110000 && (cp < 0xD800 || cp > 0xDFFF) ? cp : 0xFFFD));
                    return;
                case St.OscEsc:
                    st = St.Ground;
                    if (cp == '\\') DoOsc(oscBuf.ToString());
                    else { st = St.Esc; Handle(cp, dbl); }
                    return;
                case St.Str:
                    if (cp == 0x07) { st = St.Ground; return; }
                    if (cp == 0x1B) { st = St.StrEsc; return; }
                    if (cp == 0x18 || cp == 0x1A) { st = St.Ground; return; }
                    return;
                case St.StrEsc:
                    if (cp == '\\') st = St.Ground; else { st = St.Esc; Handle(cp, dbl); }
                    return;
            }
        }

        void Control(int c)
        {
            switch (c)
            {
                case 0x1B: st = St.Esc; escInter = ""; break;
                case 0x07: break; // ベルは無視
                case 0x08: if (CX > 0) CX--; wrapPending = false; break;
                case 0x09: Tab(); break;
                case 0x0A: case 0x0B: case 0x0C: LineFeed(); break;
                case 0x0D: CX = 0; wrapPending = false; break;
                case 0x0E: gl = 1; break;
                case 0x0F: gl = 0; break;
                case 0x18: case 0x1A: st = St.Ground; break;
            }
        }

        void Tab()
        {
            wrapPending = false;
            int x = CX + 1;
            while (x < Cols - 1 && !tabs[x]) x++;
            CX = Math.Min(x, Cols - 1);
        }

        void EscFinal(char f, string inter)
        {
            if (inter == "(" || inter == ")")
            {
                int g = inter == "(" ? 0 : 1;
                charset[g] = f == '0' ? 1 : 0;
                return;
            }
            if (inter == "#") { if (f == '8') { for (int y = 0; y < Rows; y++) for (int x = 0; x < Cols; x++) { Cur[y].C[x] = BlankCell(); Cur[y].C[x].Ch = 'E'; } } return; }
            if (inter.Length > 0) return;
            switch (f)
            {
                case '[': st = St.Csi; csiBuf.Length = 0; break;
                case ']': st = St.Osc; oscBuf.Length = 0; break;
                case 'P': case 'X': case '^': case '_': st = St.Str; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E': CX = 0; LineFeed(); break;
                case 'M': ReverseIndex(); break;
                case 'H': if (CX < Cols) tabs[CX] = true; break;
                case 'c': FullReset(); break;
                case '=': AppKeypad = true; break;
                case '>': AppKeypad = false; break;
                case 'n': gl = 2 % 2; break;
            }
        }

        void SaveCursor()
        {
            sx = CX; sy = CY; sFg = curFg; sBg = curBg; sAttr = curAttr; sWrap = wrapPending; sOrigin = Origin;
        }
        void RestoreCursor()
        {
            CX = Math.Min(sx, Cols - 1); CY = Math.Min(sy, Rows - 1); curFg = sFg; curBg = sBg; curAttr = sAttr; wrapPending = sWrap; Origin = sOrigin;
        }

        // ---- OSC ----
        void DoOsc(string s)
        {
            int i = s.IndexOf(';');
            if (i < 0) return;
            string cmd = s.Substring(0, i);
            if (cmd == "0" || cmd == "2" || cmd == "1")
            {
                if (cmd != "1" && TitleChanged != null) TitleChanged(s.Substring(i + 1));
            }
        }

        // ---- CSI ----
        static int[] Parse(string p, out bool priv, out char prefix, out List<int[]> subs)
        {
            priv = false; prefix = '\0';
            subs = new List<int[]>();
            if (p.Length > 0 && (p[0] == '?' || p[0] == '>' || p[0] == '<' || p[0] == '=')) { prefix = p[0]; priv = p[0] == '?'; p = p.Substring(1); }
            // 中間バイト(スペース等)を除去
            int cut = p.Length;
            for (int k = 0; k < p.Length; k++) if (p[k] >= 0x20 && p[k] <= 0x2F) { cut = k; break; }
            p = p.Substring(0, cut);
            List<int> res = new List<int>();
            if (p.Length == 0) return new int[0];
            foreach (string part in p.Split(new char[] { ';' }))
            {
                string[] sp = part.Split(new char[] { ':' });
                int[] arr = new int[sp.Length];
                for (int k = 0; k < sp.Length; k++)
                {
                    int v; arr[k] = (sp[k].Length > 0 && int.TryParse(sp[k], out v)) ? v : -1;
                }
                subs.Add(arr);
                res.Add(arr[0]);
            }
            return res.ToArray();
        }

        static int P(int[] a, int i, int def)
        {
            if (i >= a.Length || a[i] <= 0) return def;
            return a[i];
        }

        void DoCsi(char f, string raw)
        {
            bool priv; char prefix; List<int[]> subs;
            int[] a = Parse(raw, out priv, out prefix, out subs);
            string inter = "";
            for (int k = 0; k < raw.Length; k++) if (raw[k] >= 0x20 && raw[k] <= 0x2F) inter += raw[k];
            if (inter.Length > 0 && !(f == 'q' || f == 'p')) return;
            wrapPending = (f == 'm' || f == 'h' || f == 'l' || f == 'n' || f == 'c') ? wrapPending : false;
            switch (f)
            {
                case 'A': MoveY(-P(a, 0, 1)); break;
                case 'B': case 'e': MoveY(P(a, 0, 1)); break;
                case 'C': case 'a': CX = Math.Min(Cols - 1, CX + P(a, 0, 1)); break;
                case 'D': CX = Math.Max(0, CX - P(a, 0, 1)); break;
                case 'E': MoveY(P(a, 0, 1)); CX = 0; break;
                case 'F': MoveY(-P(a, 0, 1)); CX = 0; break;
                case 'G': case '`': CX = Math.Max(0, Math.Min(Cols - 1, P(a, 0, 1) - 1)); break;
                case 'H': case 'f': MoveTo(P(a, 0, 1) - 1, P(a, 1, 1) - 1); break;
                case 'd': MoveTo(P(a, 0, 1) - 1, CX); { int y = P(a, 0, 1) - 1; if (Origin) y += top; CY = Math.Max(0, Math.Min(Rows - 1, y)); } break;
                case 'I': { int n = P(a, 0, 1); for (int i = 0; i < n; i++) Tab(); } break;
                case 'Z': { int n = P(a, 0, 1); for (int i = 0; i < n; i++) { int x = CX - 1; while (x > 0 && !tabs[x]) x--; CX = Math.Max(0, x); } } break;
                case 'J': EraseDisplay(a.Length > 0 && a[0] > 0 ? a[0] : 0); break;
                case 'K': EraseLine(a.Length > 0 && a[0] > 0 ? a[0] : 0); break;
                case 'L': InsertLines(P(a, 0, 1)); break;
                case 'M': DeleteLines(P(a, 0, 1)); break;
                case '@': InsertChars(P(a, 0, 1)); break;
                case 'P': DeleteChars(P(a, 0, 1)); break;
                case 'X': EraseChars(P(a, 0, 1)); break;
                case 'S': for (int i = 0; i < P(a, 0, 1); i++) ScrollUp(); break;
                case 'T': for (int i = 0; i < P(a, 0, 1); i++) ScrollDown(); break;
                case 'b': { int n = P(a, 0, 1); if (lastCp > 0) for (int i = 0; i < n && i < 500; i++) PutChar(lastCp, false); } break;
                case 'g': if (a.Length == 0 || a[0] == 0) { if (CX < Cols) tabs[CX] = false; } else if (a[0] == 3) tabs = new bool[Cols]; break;
                case 'm': Sgr(a, subs); break;
                case 'r':
                    {
                        int t = P(a, 0, 1) - 1, b = P(a, 1, Rows) - 1;
                        if (t < 0) t = 0; if (b >= Rows) b = Rows - 1;
                        if (t < b) { top = t; bottom = b; }
                        else { top = 0; bottom = Rows - 1; }
                        MoveTo(0, 0);
                    }
                    break;
                case 's': if (!priv && prefix == '\0') SaveCursor(); break;
                case 'u': if (prefix == '\0') RestoreCursor(); break;
                case 'h': SetMode(a, priv, true); break;
                case 'l': SetMode(a, priv, false); break;
                case 'n':
                    if (prefix == '\0')
                    {
                        if (P(a, 0, 0) == 5) Send("\x1b[0n");
                        else if (P(a, 0, 0) == 6)
                        {
                            int y = CY + 1; if (Origin) y -= top;
                            Send("\x1b[" + y + ";" + (CX + 1) + "R");
                        }
                    }
                    break;
                case 'c':
                    if (prefix == '\0') Send("\x1b[?62;1;4;6;22c");
                    else if (prefix == '>') Send("\x1b[>0;10;0c");
                    break;
                case 't':
                    if (P(a, 0, 0) == 18) Send("\x1b[8;" + Rows + ";" + Cols + "t");
                    else if (P(a, 0, 0) == 14) Send("\x1b[4;" + (Rows * 16) + ";" + (Cols * 8) + "t");
                    break;
                case 'p':
                    if (inter == "!") FullReset();
                    break;
            }
        }

        int lastCp;

        void Send(string s) { if (Reply != null) Reply(Encoding.ASCII.GetBytes(s)); }

        void MoveY(int d)
        {
            int ny = CY + d;
            // 範囲外へ出ない(スクロール領域内にいる場合は領域端で止まる)
            if (d < 0) { int lim = (CY >= top) ? top : 0; if (Origin) lim = top; ny = Math.Max(lim, ny); }
            else { int lim = (CY <= bottom) ? bottom : Rows - 1; ny = Math.Min(lim, ny); }
            CY = ny;
        }

        void MoveTo(int y, int x)
        {
            if (Origin) { y += top; y = Math.Max(top, Math.Min(bottom, y)); }
            CY = Math.Max(0, Math.Min(Rows - 1, y));
            CX = Math.Max(0, Math.Min(Cols - 1, x));
        }

        void SetMode(int[] a, bool priv, bool on)
        {
            foreach (int m in a)
            {
                if (priv)
                {
                    switch (m)
                    {
                        case 1: AppCursor = on; break;
                        case 6: Origin = on; MoveTo(0, 0); break;
                        case 7: AutoWrap = on; break;
                        case 25: CursorVisible = on; break;
                        case 47: case 1047: SwitchAlt(on, false); break;
                        case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
                        case 1049: SwitchAlt(on, true); break;
                        case 2004: BracketedPaste = on; break;
                    }
                }
                else
                {
                    if (m == 4) Insert = on;
                }
            }
        }

        void SwitchAlt(bool on, bool saveCursor)
        {
            if (on == Alt) return;
            if (on)
            {
                if (saveCursor) SaveCursor();
                Alt = true;
                altScreen = NewScreen(Rows);
                top = 0; bottom = Rows - 1;
            }
            else
            {
                Alt = false;
                top = 0; bottom = Rows - 1;
                if (saveCursor) RestoreCursor();
            }
            wrapPending = false;
        }

        // ---- SGR ----
        static int Pal256(int n)
        {
            return 0x01000000 | (n & 0xFF);
        }

        void Sgr(int[] a, List<int[]> subs)
        {
            if (a.Length == 0) { curFg = curBg = 0; curAttr = 0; return; }
            for (int i = 0; i < a.Length; i++)
            {
                int v = a[i] < 0 ? 0 : a[i];
                int[] sp = subs[i];
                switch (v)
                {
                    case 0: curFg = curBg = 0; curAttr = 0; break;
                    case 1: curAttr |= At.Bold; break;
                    case 2: curAttr |= At.Dim; break;
                    case 3: curAttr |= At.Italic; break;
                    case 4: if (sp.Length > 1 && sp[1] == 0) curAttr &= unchecked((byte)~At.Underline); else curAttr |= At.Underline; break;
                    case 5: case 6: curAttr |= At.Blink; break;
                    case 7: curAttr |= At.Reverse; break;
                    case 8: curAttr |= At.Hidden; break;
                    case 9: curAttr |= At.Strike; break;
                    case 21: curAttr |= At.Underline; break;
                    case 22: curAttr &= unchecked((byte)~(At.Bold | At.Dim)); break;
                    case 23: curAttr &= unchecked((byte)~At.Italic); break;
                    case 24: curAttr &= unchecked((byte)~At.Underline); break;
                    case 25: curAttr &= unchecked((byte)~At.Blink); break;
                    case 27: curAttr &= unchecked((byte)~At.Reverse); break;
                    case 28: curAttr &= unchecked((byte)~At.Hidden); break;
                    case 29: curAttr &= unchecked((byte)~At.Strike); break;
                    case 39: curFg = 0; break;
                    case 49: curBg = 0; break;
                    case 38: case 48:
                        {
                            int col = 0; bool ok = false;
                            if (sp.Length > 1)
                            {
                                // コロン区切り: 38:5:n / 38:2::r:g:b / 38:2:r:g:b
                                if (sp[1] == 5 && sp.Length > 2) { col = Pal256(sp[2]); ok = true; }
                                else if (sp[1] == 2)
                                {
                                    int o = sp.Length >= 6 ? 3 : 2;
                                    if (sp.Length > o + 2) { col = 0x02000000 | (Cl(sp[o]) << 16) | (Cl(sp[o + 1]) << 8) | Cl(sp[o + 2]); ok = true; }
                                }
                            }
                            else if (i + 1 < a.Length)
                            {
                                if (a[i + 1] == 5 && i + 2 < a.Length) { col = Pal256(a[i + 2]); i += 2; ok = true; }
                                else if (a[i + 1] == 2 && i + 4 < a.Length) { col = 0x02000000 | (Cl(a[i + 2]) << 16) | (Cl(a[i + 3]) << 8) | Cl(a[i + 4]); i += 4; ok = true; }
                            }
                            if (ok) { if (v == 38) curFg = col; else curBg = col; }
                        }
                        break;
                    default:
                        if (v >= 30 && v <= 37) curFg = Pal256(v - 30);
                        else if (v >= 40 && v <= 47) curBg = Pal256(v - 40);
                        else if (v >= 90 && v <= 97) curFg = Pal256(v - 90 + 8);
                        else if (v >= 100 && v <= 107) curBg = Pal256(v - 100 + 8);
                        break;
                }
            }
        }
        static int Cl(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        // ---- 編集操作 ----
        void LineFeed()
        {
            wrapPending = false;
            if (CY == bottom) ScrollUp();
            else if (CY < Rows - 1) CY++;
        }

        void ReverseIndex()
        {
            wrapPending = false;
            if (CY == top) ScrollDown();
            else if (CY > 0) CY--;
        }

        void ScrollUp()
        {
            Row[] s = Cur;
            Row r = s[top];
            if (!Alt && top == 0) PushScrollback(r);
            for (int y = top; y < bottom; y++) s[y] = s[y + 1];
            s[bottom] = BlankRow();
        }

        void ScrollDown()
        {
            Row[] s = Cur;
            for (int y = bottom; y > top; y--) s[y] = s[y - 1];
            s[top] = BlankRow();
        }

        Row BlankRow()
        {
            Row r = new Row(Cols);
            if (curBg != 0) for (int i = 0; i < Cols; i++) r.C[i].Bg = curBg;
            return r;
        }

        void InsertLines(int n)
        {
            if (CY < top || CY > bottom) return;
            Row[] s = Cur;
            n = Math.Min(n, bottom - CY + 1);
            for (int y = bottom; y >= CY + n; y--) s[y] = s[y - n];
            for (int y = CY; y < CY + n; y++) s[y] = BlankRow();
            CX = 0;
        }

        void DeleteLines(int n)
        {
            if (CY < top || CY > bottom) return;
            Row[] s = Cur;
            n = Math.Min(n, bottom - CY + 1);
            for (int y = CY; y <= bottom - n; y++) s[y] = s[y + n];
            for (int y = bottom - n + 1; y <= bottom; y++) s[y] = BlankRow();
            CX = 0;
        }

        void FixWide(Row r, int x)
        {
            // xの位置のセルが全角の片割れなら相方を空白化
            if (x < 0 || x >= r.C.Length) return;
            if (r.C[x].W == 2 && x + 1 < r.C.Length) { r.C[x + 1] = BlankCell(); }
            else if (r.C[x].W == 0 && x > 0) { r.C[x - 1] = BlankCell(); }
        }

        void InsertChars(int n)
        {
            Row r = Cur[CY];
            n = Math.Min(n, Cols - CX);
            FixWide(r, CX);
            for (int x = Cols - 1; x >= CX + n; x--) r.C[x] = r.C[x - n];
            for (int x = CX; x < CX + n; x++) r.C[x] = BlankCell();
            if (Cols > 0 && r.C[Cols - 1].W == 2) r.C[Cols - 1] = BlankCell();
        }

        void DeleteChars(int n)
        {
            Row r = Cur[CY];
            n = Math.Min(n, Cols - CX);
            FixWide(r, CX);
            for (int x = CX; x < Cols - n; x++) r.C[x] = r.C[x + n];
            for (int x = Cols - n; x < Cols; x++) r.C[x] = BlankCell();
            if (CX < Cols && r.C[CX].W == 0) r.C[CX] = BlankCell();
        }

        void EraseChars(int n)
        {
            Row r = Cur[CY];
            n = Math.Min(n, Cols - CX);
            FixWide(r, CX); FixWide(r, CX + n - 1);
            for (int x = CX; x < CX + n; x++) r.C[x] = BlankCell();
        }

        void EraseLine(int m)
        {
            Row r = Cur[CY];
            int from = 0, to = Cols - 1;
            if (m == 0) from = CX; else if (m == 1) to = CX;
            if (m == 0 || m == 2) r.Wrap = false;
            FixWide(r, from); FixWide(r, to);
            for (int x = from; x <= to; x++) r.C[x] = BlankCell();
        }

        void EraseDisplay(int m)
        {
            if (m == 0)
            {
                EraseLine(0);
                for (int y = CY + 1; y < Rows; y++) { Cur[y] = BlankRow(); }
            }
            else if (m == 1)
            {
                EraseLine(1);
                for (int y = 0; y < CY; y++) Cur[y] = BlankRow();
            }
            else if (m == 2)
            {
                for (int y = 0; y < Rows; y++) Cur[y] = BlankRow();
            }
            else if (m == 3) { Scrollback.Clear(); }
        }

        // ---- 文字出力 ----
        static readonly char[] DecMap = BuildDec();
        static char[] BuildDec()
        {
            char[] m = new char[128];
            for (int i = 0; i < 128; i++) m[i] = (char)i;
            string from = "`abcdefghijklmnopqrstuvwxyz{|}~";
            string to = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";
            for (int i = 0; i < from.Length; i++) m[from[i]] = to[i];
            return m;
        }

        void PutChar(int cp, bool dbl)
        {
            if (charset[gl] == 1 && cp < 128) cp = DecMap[cp];
            int w = dbl ? 2 : CharWidth(cp);
            Row r = Cur[CY];
            if (w == 0 || zwjPending)
            {
                // 結合文字: 直前のセルに付加
                if (cp == 0x200D) { zwjPending = true; }
                else zwjPending = false;
                int tx = wrapPending ? CX : CX - 1;
                if (tx >= 0 && tx < Cols)
                {
                    if (r.C[tx].W == 0 && tx > 0) tx--;
                    if (r.C[tx].Ch != 0)
                        r.C[tx].Ext = (r.C[tx].Ext ?? "") + char.ConvertFromUtf32(cp);
                }
                return;
            }
            lastCp = cp;
            if (wrapPending && AutoWrap)
            {
                r.Wrap = true;
                CX = 0; LineFeed(); r = Cur[CY];
            }
            wrapPending = false;
            if (w == 2 && CX == Cols - 1)
            {
                if (AutoWrap)
                {
                    r.C[CX] = BlankCell();
                    r.Wrap = true;
                    CX = 0; LineFeed(); r = Cur[CY];
                }
                else w = 1;
            }
            if (Insert) InsertChars(w);
            FixWide(r, CX);
            if (w == 2) FixWide(r, CX + 1);
            Cell c = new Cell();
            c.Ch = cp; c.Fg = curFg; c.Bg = curBg; c.Attr = curAttr; c.W = (byte)w; c.Wide2 = dbl;
            r.C[CX] = c;
            if (w == 2)
            {
                Cell k = new Cell(); k.W = 0; k.Fg = curFg; k.Bg = curBg; k.Attr = curAttr;
                r.C[CX + 1] = k;
            }
            CX += w;
            if (CX >= Cols) { CX = Cols - 1; wrapPending = true; }
        }

        // ---- 文字幅 ----
        static readonly int[] WideRanges = new int[] {
            0x1100,0x115F, 0x231A,0x231B, 0x2329,0x232A, 0x23E9,0x23EC, 0x23F0,0x23F0, 0x23F3,0x23F3,
            0x25FD,0x25FE, 0x2614,0x2615, 0x2648,0x2653, 0x267F,0x267F, 0x2693,0x2693, 0x26A1,0x26A1,
            0x26AA,0x26AB, 0x26BD,0x26BE, 0x26C4,0x26C5, 0x26CE,0x26CE, 0x26D4,0x26D4, 0x26EA,0x26EA,
            0x26F2,0x26F3, 0x26F5,0x26F5, 0x26FA,0x26FA, 0x26FD,0x26FD, 0x2705,0x2705, 0x270A,0x270B,
            0x2728,0x2728, 0x274C,0x274C, 0x274E,0x274E, 0x2753,0x2755, 0x2757,0x2757, 0x2795,0x2797,
            0x27B0,0x27B0, 0x27BF,0x27BF, 0x2B1B,0x2B1C, 0x2B50,0x2B50, 0x2B55,0x2B55,
            0x2E80,0x303E, 0x3041,0x33FF, 0x3400,0x4DBF, 0x4E00,0xA4CF, 0xA960,0xA97F, 0xAC00,0xD7A3,
            0xF900,0xFAFF, 0xFE10,0xFE19, 0xFE30,0xFE6F, 0xFF00,0xFF60, 0xFFE0,0xFFE6,
            0x1F004,0x1F004, 0x1F0CF,0x1F0CF, 0x1F18E,0x1F18E, 0x1F191,0x1F19A, 0x1F200,0x1F202,
            0x1F210,0x1F23B, 0x1F240,0x1F248, 0x1F250,0x1F251, 0x1F260,0x1F265,
            0x1F300,0x1F320, 0x1F32D,0x1F335, 0x1F337,0x1F37C, 0x1F37E,0x1F393, 0x1F3A0,0x1F3CA,
            0x1F3CF,0x1F3D3, 0x1F3E0,0x1F3F0, 0x1F3F4,0x1F3F4, 0x1F3F8,0x1F43E, 0x1F440,0x1F440,
            0x1F442,0x1F4FC, 0x1F4FF,0x1F53D, 0x1F54B,0x1F54E, 0x1F550,0x1F567, 0x1F57A,0x1F57A,
            0x1F595,0x1F596, 0x1F5A4,0x1F5A4, 0x1F5FB,0x1F64F, 0x1F680,0x1F6C5, 0x1F6CC,0x1F6CC,
            0x1F6D0,0x1F6D2, 0x1F6D5,0x1F6D7, 0x1F6DC,0x1F6DF, 0x1F6EB,0x1F6EC, 0x1F6F4,0x1F6FC,
            0x1F7E0,0x1F7EB, 0x1F7F0,0x1F7F0, 0x1F90C,0x1F93A, 0x1F93C,0x1F945, 0x1F947,0x1F9FF,
            0x1FA70,0x1FA7C, 0x1FA80,0x1FA89, 0x1FA8F,0x1FAC6, 0x1FACE,0x1FADC, 0x1FADF,0x1FAE9,
            0x1FAF0,0x1FAF8, 0x20000,0x2FFFD, 0x30000,0x3FFFD
        };

        public static bool IsWide(int cp)
        {
            int[] r = WideRanges;
            int lo = 0, hi = r.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < r[mid * 2]) hi = mid - 1;
                else if (cp > r[mid * 2 + 1]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        public static int CharWidth(int cp)
        {
            if (cp < 0x300) return 1;
            if (cp >= 0x1F3FB && cp <= 0x1F3FF) return 0; // 肌色修飾子
            if (cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0x2060 || cp == 0xFEFF || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) return 0;
            if (cp >= 0xE0020 && cp <= 0xE007F) return 0; // タグ文字
            if (cp >= 0xD800 && cp <= 0xDFFF) return 1;
            if (cp > 0x10FFFF) return 1;
            if (IsWide(cp)) return 1 + 1;
            UnicodeCategory uc;
            if (cp < 0x10000) uc = CharUnicodeInfo.GetUnicodeCategory((char)cp);
            else uc = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
            if (uc == UnicodeCategory.NonSpacingMark || uc == UnicodeCategory.EnclosingMark || uc == UnicodeCategory.Format) return 0;
            return 1;
        }

        // 描画側のための判定: DirectWriteで描くべき文字か
        public static bool NeedsDWrite(int cp)
        {
            if (cp >= 0x1F000) return true;
            if (cp >= 0x2600 && cp <= 0x27BF) return true;
            if (cp >= 0x2B00 && cp <= 0x2BFF) return true;
            if (cp == 0x2B50 || cp == 0x203C || cp == 0x2049 || cp == 0x2122 || cp == 0x2139) return true;
            if (cp >= 0x231A && cp <= 0x23FF) return true;
            if (cp >= 0x1F000) return true;
            return false;
        }

        // ---- 選択テキスト取得 ----
        public string GetText(int r1, int c1, int r2, int c2)
        {
            // 絶対行 r1,c1 ～ r2,c2 (含む)
            if (r1 > r2 || (r1 == r2 && c1 > c2)) { int t = r1; r1 = r2; r2 = t; t = c1; c1 = c2; c2 = t; }
            StringBuilder sb = new StringBuilder();
            for (int y = r1; y <= r2; y++)
            {
                Row row = GetLine(y);
                if (row == null) continue;
                int from = (y == r1) ? c1 : 0;
                int to = (y == r2) ? c2 : Math.Max(row.C.Length, Cols) - 1;
                StringBuilder line = new StringBuilder();
                for (int x = from; x <= to; x++)
                {
                    if (x >= row.C.Length) { if (y != r2) line.Append(' '); else line.Append(' '); continue; }
                    Cell c = row.C[x];
                    if (c.W == 0) continue;
                    if (c.Ch == 0) line.Append(' ');
                    else
                    {
                        line.Append(char.ConvertFromUtf32(c.Ch >= 0xD800 && c.Ch <= 0xDFFF ? 0xFFFD : c.Ch));
                        if (c.Ext != null) line.Append(c.Ext);
                    }
                }
                string ls = line.ToString();
                if (y != r2 && !row.Wrap) ls = ls.TrimEnd(new char[] { ' ' });
                else if (y == r2 && to >= row.C.Length - 1 && !row.Wrap) { }
                sb.Append(ls);
                if (y != r2 && !row.Wrap) sb.Append('\n');
            }
            return sb.ToString();
        }

        public string DumpScreen()
        {
            StringBuilder sb = new StringBuilder();
            for (int y = 0; y < Rows; y++)
            {
                StringBuilder l = new StringBuilder();
                for (int x = 0; x < Cols; x++)
                {
                    Cell c = Cur[y].C[x];
                    if (c.W == 0) continue;
                    l.Append(c.Ch == 0 ? " " : char.ConvertFromUtf32(c.Ch)); if (c.Ext != null) l.Append(c.Ext);
                }
                sb.AppendLine(l.ToString().TrimEnd());
            }
            return sb.ToString();
        }
    }
}
