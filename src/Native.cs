// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Remo
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; public RECT(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; } }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPels, biYPels; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TEXTMETRIC
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
            tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public ushort tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct COMPOSITIONFORM { public uint dwStyle; public int x, y; public RECT rc; }

    [StructLayout(LayoutKind.Sequential)]
    struct CANDIDATEFORM { public uint dwIndex, dwStyle; public int x, y; public RECT rc; }

    static class N
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int i);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike,
            uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint c);
        [DllImport("gdi32.dll")] public static extern uint SetBkColor(IntPtr hdc, uint c);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int m);
        [DllImport("gdi32.dll")] public static extern uint SetTextAlign(IntPtr hdc, uint a);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint opt, ref RECT rc, string s, uint n, int[] dx);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC tm);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int n, out SIZE sz);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr h);
        [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr h, IntPtr c);
        [DllImport("imm32.dll")] public static extern bool ImmSetCompositionWindow(IntPtr c, ref COMPOSITIONFORM f);
        [DllImport("imm32.dll")] public static extern bool ImmSetCandidateWindow(IntPtr c, ref CANDIDATEFORM f);

        public const uint ETO_OPAQUE = 2, ETO_CLIPPED = 4, SRCCOPY = 0x00CC0020;

        public static uint ColorRef(int rgb) { return (uint)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF)); }

        public static void DarkTitle(IntPtr h, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4);
            }
            catch { }
        }
    }

    // ---- Direct2D / DirectWrite (カラー絵文字描画) ----
    static class Emoji
    {
        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr opts, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid iid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint D_Release(IntPtr self);
        [StructLayout(LayoutKind.Sequential)]
        struct RTProps { public int type, format, alpha; public float dpiX, dpiY; public int usage, minLevel; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateDCRT(IntPtr self, ref RTProps p, out IntPtr rt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_BindDC(IntPtr self, IntPtr hdc, ref RECT rc);
        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float r, g, b, a; }
        [StructLayout(LayoutKind.Sequential)] struct RectF { public float l, t, r, b; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateBrush(IntPtr self, ref ColorF c, IntPtr props, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_BeginDraw(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_EndDraw(IntPtr self, IntPtr t1, IntPtr t2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Clear(IntPtr self, IntPtr color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void D_DrawText(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, uint len, IntPtr fmt, ref RectF rc, IntPtr brush, int opts, int measuring);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_SetColor(IntPtr self, ref ColorF c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int W_CreateTextFormat(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr coll, int weight, int style, int stretch, float size,
            [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr fmt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int W_SetInt(IntPtr self, int v);

        static T Slot<T>(IntPtr obj, int index) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        static IntPtr d2d, dwrite, rt, brush;
        static bool ok, tried;
        static Dictionary<int, IntPtr> formats = new Dictionary<int, IntPtr>();
        static bool drawing; static int curW, curH;
        static uint curColor = 0xFFFFFFFF;
        public static string FontName = "Segoe UI Emoji";

        public static bool Available { get { Init(); return ok; } }

        static string Marker { get { return System.IO.Path.Combine(Store.Dir, "d2d-pending.flag"); } }
        public static bool DisabledByCrash;
        static bool markerCleared;

        public static void Init()
        {
            if (tried) return;
            tried = true;
            try
            {
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--nod2d") >= 0) { Log.W("d2d disabled by option"); return; }
                try
                {
                    if (System.IO.File.Exists(Marker)) { DisabledByCrash = true; Log.W("d2d disabled: previous run crashed"); return; }
                    System.IO.Directory.CreateDirectory(Store.Dir);
                    System.IO.File.WriteAllText(Marker, "x");
                }
                catch { }
                Log.W("d2d init start");
                Guid iF = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
                if (D2D1CreateFactory(0, ref iF, IntPtr.Zero, out d2d) != 0) return;
                Guid iW = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
                if (DWriteCreateFactory(0, ref iW, out dwrite) != 0) return;
                RTProps p = new RTProps();
                p.type = 0; p.format = 87; p.alpha = 1; p.dpiX = 96; p.dpiY = 96; p.usage = 0; p.minLevel = 0;
                if (Slot<D_CreateDCRT>(d2d, 16)(d2d, ref p, out rt) != 0) return;
                ColorF w = new ColorF { r = 1, g = 1, b = 1, a = 1 };
                if (Slot<D_CreateBrush>(rt, 8)(rt, ref w, IntPtr.Zero, out brush) != 0) return;
                ok = true;
                Log.W("d2d init ok");
            }
            catch (Exception e) { ok = false; Log.W("d2d init failed: " + e.Message); }
        }

        static void ClearMarker()
        {
            if (markerCleared) return;
            markerCleared = true;
            try { System.IO.File.Delete(Marker); } catch { }
            Log.W("d2d first draw ok");
        }

        static IntPtr Format(float size)
        {
            int key = (int)(size * 10);
            IntPtr f;
            if (formats.TryGetValue(key, out f)) return f;
            if (Slot<W_CreateTextFormat>(dwrite, 15)(dwrite, FontName, IntPtr.Zero, 400, 0, 5, size, "ja-jp", out f) != 0) return IntPtr.Zero;
            Slot<W_SetInt>(f, 3)(f, 2); // 水平中央
            Slot<W_SetInt>(f, 4)(f, 2); // 垂直中央
            Slot<W_SetInt>(f, 5)(f, 1); // 折り返し無し
            formats[key] = f;
            return f;
        }

        public static bool Begin(IntPtr hdc, int w, int h)
        {
            if (!Available) return false;
            try
            {
                N.GdiFlush();
                if (!markerCleared) Log.W("d2d bind dc");
                RECT rc = new RECT(0, 0, w, h);
                if (Slot<D_BindDC>(rt, 57)(rt, hdc, ref rc) != 0) return false;
                if (!markerCleared) Log.W("d2d begin draw");
                Slot<D_BeginDraw>(rt, 48)(rt);
                drawing = true; curW = w; curH = h; curColor = 0xFFFFFFFF;
                return true;
            }
            catch { ok = false; return false; }
        }

        public static void Draw(string text, int x, int y, int w, int h, int rgb, float size)
        {
            if (!drawing) return;
            try
            {
                IntPtr f = Format(size);
                if (f == IntPtr.Zero) return;
                uint c = (uint)rgb | 0xFF000000;
                if (c != curColor)
                {
                    ColorF col = new ColorF { r = ((rgb >> 16) & 255) / 255f, g = ((rgb >> 8) & 255) / 255f, b = (rgb & 255) / 255f, a = 1 };
                    Slot<D_SetColor>(brush, 8)(brush, ref col);
                    curColor = c;
                }
                RectF r = new RectF { l = x, t = y, r = x + w, b = y + h };
                Slot<D_DrawText>(rt, 27)(rt, text, (uint)text.Length, f, ref r, brush, 2 | 4, 0);
            }
            catch { }
        }

        public static void End()
        {
            if (!drawing) return;
            drawing = false;
            try { Slot<D_EndDraw>(rt, 49)(rt, IntPtr.Zero, IntPtr.Zero); ClearMarker(); } catch { }
        }

        // UI用: 絵文字をアルファ付きビットマップへ描画
        static Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        public static Bitmap Render(string text, int px)
        {
            string key = text + "|" + px;
            Bitmap bmp;
            if (cache.TryGetValue(key, out bmp)) return bmp;
            bmp = null;
            if (Available && !drawing)
            {
                IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    dc = N.CreateCompatibleDC(IntPtr.Zero);
                    BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                    bi.biSize = 40; bi.biWidth = px; bi.biHeight = -px; bi.biPlanes = 1; bi.biBitCount = 32;
                    IntPtr bits;
                    dib = N.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
                    old = N.SelectObject(dc, dib);
                    if (Begin(dc, px, px))
                    {
                        Slot<D_Clear>(rt, 47)(rt, IntPtr.Zero);
                        Draw(text, 0, 0, px, px, 0xFFFFFF, px * 0.78f);
                        End();
                        bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                        var bd = bmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                        byte[] buf = new byte[px * px * 4];
                        Marshal.Copy(bits, buf, 0, buf.Length);
                        Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
                        bmp.UnlockBits(bd);
                    }
                }
                catch { bmp = null; }
                finally
                {
                    if (dc != IntPtr.Zero) { if (old != IntPtr.Zero) N.SelectObject(dc, old); N.DeleteDC(dc); }
                    if (dib != IntPtr.Zero) N.DeleteObject(dib);
                }
            }
            cache[key] = bmp;
            return bmp;
        }
    }
}
