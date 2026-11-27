// Remo for Android - ported from Remo (Windows) Term.cs
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import java.nio.charset.Charset

/** 色: 0=既定, 0x01000000|idx=パレット, 0x02000000|rgb=True Color */
class Cell(
    @JvmField val ch: Int = 0,          // 0 = 空白
    @JvmField val ext: String? = null,  // 結合文字・絵文字シーケンスの続き
    @JvmField val fg: Int = 0,
    @JvmField val bg: Int = 0,
    @JvmField val attr: Int = 0,
    @JvmField val w: Int = 1,           // 1=通常 2=全角の左 0=全角の右(継続)
    @JvmField val wide2: Boolean = false
) {
    fun withExt(e: String) = Cell(ch, e, fg, bg, attr, w, wide2)
}

object At {
    const val Bold = 1; const val Dim = 2; const val Italic = 4; const val Underline = 8
    const val Blink = 16; const val Reverse = 32; const val Hidden = 64; const val Strike = 128
}

class Row(n: Int) {
    @JvmField var c: Array<Cell> = Array(n) { Cell() }
    @JvmField var wrap = false
}

class Terminal(cols: Int, rows: Int) {
    @JvmField var cols = cols
    @JvmField var rows = rows
    @JvmField val scrollback = ArrayList<Row>()
    @JvmField var maxScrollback = 10000
    private var screen: Array<Row>
    private var altScreen: Array<Row>
    @JvmField var alt = false
    @JvmField var cx = 0
    @JvmField var cy = 0
    private var wrapPending = false
    private var top = 0
    private var bottom = 0
    private var tabs = BooleanArray(0)

    private var curFg = 0
    private var curBg = 0
    private var curAttr = 0

    @JvmField var appCursor = false
    @JvmField var appKeypad = false
    @JvmField var autoWrap = true
    @JvmField var origin = false
    @JvmField var insert = false
    @JvmField var cursorVisible = true
    @JvmField var bracketedPaste = false
    @JvmField var sjis = false

    private var sx = 0; private var sy = 0; private var sFg = 0; private var sBg = 0; private var sAttr = 0
    private var sWrap = false; private var sOrigin = false
    private val charset = IntArray(2)
    private var gl = 0

    @JvmField var reply: ((ByteArray) -> Unit)? = null
    @JvmField var titleChanged: ((String) -> Unit)? = null
    @JvmField @Volatile var version = 0
    @JvmField var scrolledIn = 0 // スクロールバックに入った行数の累計(ビュー固定用)

    private enum class St { Ground, Esc, EscInter, Csi, Osc, OscEsc, Str, StrEsc }
    private var st = St.Ground
    private val csiBuf = StringBuilder()
    private val oscBuf = StringBuilder()
    private var escInter = ""
    private var zwjPending = false
    private var lastCp = 0

    init {
        screen = newScreen(rows); altScreen = newScreen(rows)
        top = 0; bottom = rows - 1
        resetTabs()
    }

    private fun newScreen(n: Int): Array<Row> = Array(n) { Row(cols) }
    private fun resetTabs() { tabs = BooleanArray(cols); var i = 8; while (i < cols) { tabs[i] = true; i += 8 } }

    /** スクロールバックと画面を消し、カーソルのある行だけを先頭に残す */
    fun clearAll() {
        scrollback.clear()
        val keep = screen[minOf(cy, rows - 1)]
        for (i in 0 until rows) screen[i] = Row(cols)
        screen[0] = keep
        cy = 0
        version++
    }

    fun setEncoding(s: Boolean) { sjis = s; sjLead = 0; u8need = 0 }

    fun fullReset() {
        curFg = 0; curBg = 0; curAttr = 0
        appCursor = false; appKeypad = false; origin = false; insert = false; bracketedPaste = false
        autoWrap = true; cursorVisible = true
        charset[0] = 0; charset[1] = 0; gl = 0
        alt = false
        screen = newScreen(rows); altScreen = newScreen(rows)
        top = 0; bottom = rows - 1; cx = 0; cy = 0; wrapPending = false
        resetTabs(); st = St.Ground; version++
    }

    // ---- 表示用アクセス ----
    val totalLines: Int get() = (if (alt) 0 else scrollback.size) + rows
    val scrollbackCount: Int get() = if (alt) 0 else scrollback.size
    private val cur: Array<Row> get() = if (alt) altScreen else screen
    fun screenRow(y: Int): Row = cur[y]

    /** 絶対行番号(0=最古のスクロールバック) */
    fun getLine(abs: Int): Row? {
        val sb = if (alt) 0 else scrollback.size
        if (abs < 0) return null
        if (abs < sb) return scrollback[abs]
        val r = abs - sb
        return if (r < rows) cur[r] else null
    }

    // ---- リサイズ ----
    fun resize(newCols: Int, newRows: Int) {
        var cl = newCols; var rw = newRows
        if (cl < 2) cl = 2
        if (rw < 1) rw = 1
        if (cl == cols && rw == rows) return
        if (rw < rows) {
            val remove = rows - rw
            val fromTop = maxOf(0, minOf(remove, cy - rw + 1))
            for (i in 0 until fromTop) if (!alt) pushScrollback(screen[i])
            val ns = Array(rw) { screen[it + fromTop] }
            screen = ns
            altScreen = Array(rw) { altScreen[it] }
            cy -= fromTop; if (cy < 0) cy = 0
        } else if (rw > rows) {
            val oldRows = rows
            val os = screen; val oa = altScreen
            screen = Array(rw) { if (it < oldRows) os[it] else Row(cols) }
            altScreen = Array(rw) { if (it < oldRows) oa[it] else Row(cols) }
        }
        rows = rw
        if (cl != cols) {
            for (r in screen) resizeRow(r, cl)
            for (r in altScreen) resizeRow(r, cl)
            cols = cl
            resetTabs()
        }
        top = 0; bottom = rows - 1
        if (cx >= cols) cx = cols - 1
        if (cy >= rows) cy = rows - 1
        wrapPending = false
        version++
    }

    private fun resizeRow(r: Row, n: Int) {
        if (r.c.size == n) return
        val old = r.c
        val m = minOf(n, old.size)
        val nc = Array(n) { if (it < m) old[it] else Cell() }
        if (m < old.size && m > 0 && nc[m - 1].w == 2) nc[m - 1] = blankCell()
        r.c = nc
    }

    private fun blankCell() = Cell(bg = curBg)

    private fun pushScrollback(r: Row) {
        var last = r.c.size - 1
        while (last >= 0 && r.c[last].ch == 0 && r.c[last].bg == 0 && r.c[last].ext == null &&
            (r.c[last].attr and (At.Reverse or At.Underline or At.Strike)) == 0) last--
        val n = Row(0)
        n.c = Array(last + 1) { r.c[it] }
        n.wrap = r.wrap
        scrollback.add(n)
        if (scrollback.size > maxScrollback) {
            scrollback.subList(0, scrollback.size - maxScrollback + 500).clear()
        }
        scrolledIn++
    }

    // ---- 入力 ----
    private var sjLead = 0
    private var u8need = 0
    private var u8cp = 0

    fun feed(d: ByteArray, n: Int) {
        for (i in 0 until n) feedByte(d[i].toInt() and 0xFF)
        version++
    }

    private val sjisCs: Charset by lazy {
        try { Charset.forName("windows-31j") } catch (e: Exception) { Charset.forName("Shift_JIS") }
    }

    private fun feedByte(b: Int) {
        if (sjis) {
            if (sjLead != 0) {
                val l = sjLead; sjLead = 0
                if (b >= 0x40 && b != 0x7F && b <= 0xFC) {
                    val s = String(byteArrayOf(l.toByte(), b.toByte()), sjisCs)
                    var cp = if (s.isNotEmpty()) s.codePointAt(0) else 0xFFFD
                    if (cp == 0xFFFD) cp = 0xFFFD
                    handle(cp, true)
                    return
                }
                handle(0xFFFD, true)
            }
            if ((b in 0x81..0x9F) || (b in 0xE0..0xFC)) { sjLead = b; return }
            if (b in 0xA1..0xDF) { handle(0xFF61 + (b - 0xA1), false); return }
            handle(b, false)
            return
        }
        if (u8need > 0) {
            if ((b and 0xC0) == 0x80) {
                u8cp = (u8cp shl 6) or (b and 0x3F)
                if (--u8need == 0) handle(u8cp, false)
                return
            }
            u8need = 0; handle(0xFFFD, false)
        }
        when {
            b < 0x80 -> handle(b, false)
            (b and 0xE0) == 0xC0 -> { u8need = 1; u8cp = b and 0x1F }
            (b and 0xF0) == 0xE0 -> { u8need = 2; u8cp = b and 0x0F }
            (b and 0xF8) == 0xF0 -> { u8need = 3; u8cp = b and 0x07 }
            else -> handle(0xFFFD, false)
        }
    }

    private fun cpStr(cp: Int): String =
        String(Character.toChars(if (cp in 0..0x10FFFF && (cp < 0xD800 || cp > 0xDFFF)) cp else 0xFFFD))

    // ---- パーサ ----
    private fun handle(cp: Int, dbl: Boolean) {
        when (st) {
            St.Ground -> {
                if (cp < 0x20 || cp == 0x7F) { control(cp); return }
                if (cp in 0x80 until 0xA0) return
                putChar(cp, dbl)
            }
            St.Esc -> {
                if (cp == 0x1B) return
                if (cp < 0x20) { control(cp); return }
                if (cp in 0x20..0x2F) { escInter = cp.toChar().toString(); st = St.EscInter; return }
                st = St.Ground
                escFinal(cp.toChar(), "")
            }
            St.EscInter -> {
                if (cp < 0x20) { control(cp); return }
                if (cp in 0x20..0x2F) { escInter += cp.toChar(); return }
                st = St.Ground
                escFinal(cp.toChar(), escInter)
            }
            St.Csi -> {
                if (cp == 0x1B) { st = St.Esc; return }
                if (cp == 0x18 || cp == 0x1A) { st = St.Ground; return }
                if (cp < 0x20) { control(cp); return }
                if (cp in 0x40..0x7E) { st = St.Ground; doCsi(cp.toChar(), csiBuf.toString()); return }
                if (csiBuf.length < 256) csiBuf.append(cp.toChar())
            }
            St.Osc -> {
                if (cp == 0x07) { st = St.Ground; doOsc(oscBuf.toString()); return }
                if (cp == 0x1B) { st = St.OscEsc; return }
                if (cp == 0x18 || cp == 0x1A) { st = St.Ground; return }
                if (oscBuf.length < 2048) oscBuf.append(cpStr(cp))
            }
            St.OscEsc -> {
                st = St.Ground
                if (cp == '\\'.code) doOsc(oscBuf.toString())
                else { st = St.Esc; handle(cp, dbl) }
            }
            St.Str -> {
                if (cp == 0x07) { st = St.Ground; return }
                if (cp == 0x1B) { st = St.StrEsc; return }
                if (cp == 0x18 || cp == 0x1A) { st = St.Ground }
            }
            St.StrEsc -> {
                if (cp == '\\'.code) st = St.Ground else { st = St.Esc; handle(cp, dbl) }
            }
        }
    }

    private fun control(c: Int) {
        when (c) {
            0x1B -> { st = St.Esc; escInter = "" }
            0x07 -> {}
            0x08 -> { if (cx > 0) cx--; wrapPending = false }
            0x09 -> tab()
            0x0A, 0x0B, 0x0C -> lineFeed()
            0x0D -> { cx = 0; wrapPending = false }
            0x0E -> gl = 1
            0x0F -> gl = 0
            0x18, 0x1A -> st = St.Ground
        }
    }

    private fun tab() {
        wrapPending = false
        var x = cx + 1
        while (x < cols - 1 && !tabs[x]) x++
        cx = minOf(x, cols - 1)
    }

    private fun escFinal(f: Char, inter: String) {
        if (inter == "(" || inter == ")") {
            val g = if (inter == "(") 0 else 1
            charset[g] = if (f == '0') 1 else 0
            return
        }
        if (inter == "#") {
            if (f == '8') for (y in 0 until rows) for (x in 0 until cols) cur[y].c[x] = Cell(ch = 'E'.code, bg = curBg)
            return
        }
        if (inter.isNotEmpty()) return
        when (f) {
            '[' -> { st = St.Csi; csiBuf.setLength(0) }
            ']' -> { st = St.Osc; oscBuf.setLength(0) }
            'P', 'X', '^', '_' -> st = St.Str
            '7' -> saveCursor()
            '8' -> restoreCursor()
            'D' -> lineFeed()
            'E' -> { cx = 0; lineFeed() }
            'M' -> reverseIndex()
            'H' -> if (cx < cols) tabs[cx] = true
            'c' -> fullReset()
            '=' -> appKeypad = true
            '>' -> appKeypad = false
        }
    }

    private fun saveCursor() {
        sx = cx; sy = cy; sFg = curFg; sBg = curBg; sAttr = curAttr; sWrap = wrapPending; sOrigin = origin
    }

    private fun restoreCursor() {
        cx = minOf(sx, cols - 1); cy = minOf(sy, rows - 1)
        curFg = sFg; curBg = sBg; curAttr = sAttr; wrapPending = sWrap; origin = sOrigin
    }

    private fun doOsc(s: String) {
        val i = s.indexOf(';')
        if (i < 0) return
        val cmd = s.substring(0, i)
        if (cmd == "0" || cmd == "2") titleChanged?.invoke(s.substring(i + 1))
    }

    // ---- CSI ----
    private class Parsed(val a: IntArray, val priv: Boolean, val prefix: Char, val subs: List<IntArray>)

    private fun parse(raw: String): Parsed {
        var p = raw
        var priv = false; var prefix = '\u0000'
        val subs = ArrayList<IntArray>()
        if (p.isNotEmpty() && (p[0] == '?' || p[0] == '>' || p[0] == '<' || p[0] == '=')) {
            prefix = p[0]; priv = p[0] == '?'; p = p.substring(1)
        }
        var cut = p.length
        for (k in p.indices) if (p[k].code in 0x20..0x2F) { cut = k; break }
        p = p.substring(0, cut)
        if (p.isEmpty()) return Parsed(IntArray(0), priv, prefix, subs)
        val res = ArrayList<Int>()
        for (part in p.split(';')) {
            val sp = part.split(':')
            val arr = IntArray(sp.size) { k -> if (sp[k].isNotEmpty()) (sp[k].toIntOrNull() ?: -1) else -1 }
            subs.add(arr)
            res.add(arr[0])
        }
        return Parsed(res.toIntArray(), priv, prefix, subs)
    }

    private fun pa(a: IntArray, i: Int, def: Int): Int = if (i >= a.size || a[i] <= 0) def else a[i]

    private fun doCsi(f: Char, raw: String) {
        val ps = parse(raw)
        val a = ps.a; val priv = ps.priv; val prefix = ps.prefix
        val inter = StringBuilder()
        for (ch in raw) if (ch.code in 0x20..0x2F) inter.append(ch)
        if (inter.isNotEmpty() && !(f == 'q' || f == 'p')) return
        if (!(f == 'm' || f == 'h' || f == 'l' || f == 'n' || f == 'c')) wrapPending = false
        when (f) {
            'A' -> moveY(-pa(a, 0, 1))
            'B', 'e' -> moveY(pa(a, 0, 1))
            'C', 'a' -> cx = minOf(cols - 1, cx + pa(a, 0, 1))
            'D' -> cx = maxOf(0, cx - pa(a, 0, 1))
            'E' -> { moveY(pa(a, 0, 1)); cx = 0 }
            'F' -> { moveY(-pa(a, 0, 1)); cx = 0 }
            'G', '`' -> cx = maxOf(0, minOf(cols - 1, pa(a, 0, 1) - 1))
            'H', 'f' -> moveTo(pa(a, 0, 1) - 1, pa(a, 1, 1) - 1)
            'd' -> { var y = pa(a, 0, 1) - 1; if (origin) y += top; cy = maxOf(0, minOf(rows - 1, y)) }
            'I' -> repeat(pa(a, 0, 1)) { tab() }
            'Z' -> repeat(pa(a, 0, 1)) { var x = cx - 1; while (x > 0 && !tabs[x]) x--; cx = maxOf(0, x) }
            'J' -> eraseDisplay(if (a.isNotEmpty() && a[0] > 0) a[0] else 0)
            'K' -> eraseLine(if (a.isNotEmpty() && a[0] > 0) a[0] else 0)
            'L' -> insertLines(pa(a, 0, 1))
            'M' -> deleteLines(pa(a, 0, 1))
            '@' -> insertChars(pa(a, 0, 1))
            'P' -> deleteChars(pa(a, 0, 1))
            'X' -> eraseChars(pa(a, 0, 1))
            'S' -> repeat(pa(a, 0, 1)) { scrollUp() }
            'T' -> repeat(pa(a, 0, 1)) { scrollDown() }
            'b' -> { val n = pa(a, 0, 1); if (lastCp > 0) for (i in 0 until minOf(n, 500)) putChar(lastCp, false) }
            'g' -> if (a.isEmpty() || a[0] == 0) { if (cx < cols) tabs[cx] = false } else if (a[0] == 3) tabs = BooleanArray(cols)
            'm' -> sgr(a, ps.subs)
            'r' -> {
                var t = pa(a, 0, 1) - 1; var b = pa(a, 1, rows) - 1
                if (t < 0) t = 0
                if (b >= rows) b = rows - 1
                if (t < b) { top = t; bottom = b } else { top = 0; bottom = rows - 1 }
                moveTo(0, 0)
            }
            's' -> if (!priv && prefix == '\u0000') saveCursor()
            'u' -> if (prefix == '\u0000') restoreCursor()
            'h' -> setMode(a, priv, true)
            'l' -> setMode(a, priv, false)
            'n' -> if (prefix == '\u0000') {
                if (pa(a, 0, 0) == 5) send("\u001b[0n")
                else if (pa(a, 0, 0) == 6) {
                    var y = cy + 1; if (origin) y -= top
                    send("\u001b[$y;${cx + 1}R")
                }
            }
            'c' -> if (prefix == '\u0000') send("\u001b[?62;1;4;6;22c") else if (prefix == '>') send("\u001b[>0;10;0c")
            't' -> if (pa(a, 0, 0) == 18) send("\u001b[8;$rows;${cols}t")
                   else if (pa(a, 0, 0) == 14) send("\u001b[4;${rows * 16};${cols * 8}t")
            'p' -> if (inter.toString() == "!") fullReset()
        }
    }

    private fun send(s: String) { reply?.invoke(s.toByteArray(Charsets.US_ASCII)) }

    private fun moveY(d: Int) {
        var ny = cy + d
        if (d < 0) { var lim = if (cy >= top) top else 0; if (origin) lim = top; ny = maxOf(lim, ny) }
        else { val lim = if (cy <= bottom) bottom else rows - 1; ny = minOf(lim, ny) }
        cy = ny
    }

    private fun moveTo(yy: Int, x: Int) {
        var y = yy
        if (origin) { y += top; y = maxOf(top, minOf(bottom, y)) }
        cy = maxOf(0, minOf(rows - 1, y))
        cx = maxOf(0, minOf(cols - 1, x))
    }

    private fun setMode(a: IntArray, priv: Boolean, on: Boolean) {
        for (m in a) {
            if (priv) {
                when (m) {
                    1 -> appCursor = on
                    6 -> { origin = on; moveTo(0, 0) }
                    7 -> autoWrap = on
                    25 -> cursorVisible = on
                    47, 1047 -> switchAlt(on, false)
                    1048 -> if (on) saveCursor() else restoreCursor()
                    1049 -> switchAlt(on, true)
                    2004 -> bracketedPaste = on
                }
            } else if (m == 4) insert = on
        }
    }

    private fun switchAlt(on: Boolean, saveCur: Boolean) {
        if (on == alt) return
        if (on) {
            if (saveCur) saveCursor()
            alt = true
            altScreen = newScreen(rows)
            top = 0; bottom = rows - 1
        } else {
            alt = false
            top = 0; bottom = rows - 1
            if (saveCur) restoreCursor()
        }
        wrapPending = false
    }

    // ---- SGR ----
    private fun pal256(n: Int) = 0x01000000 or (n and 0xFF)
    private fun cl(v: Int) = if (v < 0) 0 else if (v > 255) 255 else v

    private fun sgr(a: IntArray, subs: List<IntArray>) {
        if (a.isEmpty()) { curFg = 0; curBg = 0; curAttr = 0; return }
        var i = 0
        while (i < a.size) {
            val v = if (a[i] < 0) 0 else a[i]
            val sp = subs[i]
            when (v) {
                0 -> { curFg = 0; curBg = 0; curAttr = 0 }
                1 -> curAttr = curAttr or At.Bold
                2 -> curAttr = curAttr or At.Dim
                3 -> curAttr = curAttr or At.Italic
                4 -> curAttr = if (sp.size > 1 && sp[1] == 0) curAttr and At.Underline.inv() else curAttr or At.Underline
                5, 6 -> curAttr = curAttr or At.Blink
                7 -> curAttr = curAttr or At.Reverse
                8 -> curAttr = curAttr or At.Hidden
                9 -> curAttr = curAttr or At.Strike
                21 -> curAttr = curAttr or At.Underline
                22 -> curAttr = curAttr and (At.Bold or At.Dim).inv()
                23 -> curAttr = curAttr and At.Italic.inv()
                24 -> curAttr = curAttr and At.Underline.inv()
                25 -> curAttr = curAttr and At.Blink.inv()
                27 -> curAttr = curAttr and At.Reverse.inv()
                28 -> curAttr = curAttr and At.Hidden.inv()
                29 -> curAttr = curAttr and At.Strike.inv()
                39 -> curFg = 0
                49 -> curBg = 0
                38, 48 -> {
                    var col = 0; var ok = false
                    if (sp.size > 1) {
                        if (sp[1] == 5 && sp.size > 2) { col = pal256(sp[2]); ok = true }
                        else if (sp[1] == 2) {
                            val o = if (sp.size >= 6) 3 else 2
                            if (sp.size > o + 2) { col = 0x02000000 or (cl(sp[o]) shl 16) or (cl(sp[o + 1]) shl 8) or cl(sp[o + 2]); ok = true }
                        }
                    } else if (i + 1 < a.size) {
                        if (a[i + 1] == 5 && i + 2 < a.size) { col = pal256(a[i + 2]); i += 2; ok = true }
                        else if (a[i + 1] == 2 && i + 4 < a.size) {
                            col = 0x02000000 or (cl(a[i + 2]) shl 16) or (cl(a[i + 3]) shl 8) or cl(a[i + 4]); i += 4; ok = true
                        }
                    }
                    if (ok) { if (v == 38) curFg = col else curBg = col }
                }
                else -> when {
                    v in 30..37 -> curFg = pal256(v - 30)
                    v in 40..47 -> curBg = pal256(v - 40)
                    v in 90..97 -> curFg = pal256(v - 90 + 8)
                    v in 100..107 -> curBg = pal256(v - 100 + 8)
                }
            }
            i++
        }
    }

    // ---- 編集操作 ----
    private fun lineFeed() {
        wrapPending = false
        if (cy == bottom) scrollUp() else if (cy < rows - 1) cy++
    }

    private fun reverseIndex() {
        wrapPending = false
        if (cy == top) scrollDown() else if (cy > 0) cy--
    }

    private fun scrollUp() {
        val s = cur
        val r = s[top]
        if (!alt && top == 0) pushScrollback(r)
        for (y in top until bottom) s[y] = s[y + 1]
        s[bottom] = blankRow()
    }

    private fun scrollDown() {
        val s = cur
        for (y in bottom downTo top + 1) s[y] = s[y - 1]
        s[top] = blankRow()
    }

    private fun blankRow(): Row {
        val r = Row(cols)
        if (curBg != 0) for (i in 0 until cols) r.c[i] = Cell(bg = curBg)
        return r
    }

    private fun insertLines(nn: Int) {
        if (cy < top || cy > bottom) return
        val s = cur
        val n = minOf(nn, bottom - cy + 1)
        for (y in bottom downTo cy + n) s[y] = s[y - n]
        for (y in cy until cy + n) s[y] = blankRow()
        cx = 0
    }

    private fun deleteLines(nn: Int) {
        if (cy < top || cy > bottom) return
        val s = cur
        val n = minOf(nn, bottom - cy + 1)
        for (y in cy..bottom - n) s[y] = s[y + n]
        for (y in bottom - n + 1..bottom) s[y] = blankRow()
        cx = 0
    }

    private fun fixWide(r: Row, x: Int) {
        if (x < 0 || x >= r.c.size) return
        if (r.c[x].w == 2 && x + 1 < r.c.size) r.c[x + 1] = blankCell()
        else if (r.c[x].w == 0 && x > 0) r.c[x - 1] = blankCell()
    }

    private fun insertChars(nn: Int) {
        val r = cur[cy]
        val n = minOf(nn, cols - cx)
        fixWide(r, cx)
        for (x in cols - 1 downTo cx + n) r.c[x] = r.c[x - n]
        for (x in cx until cx + n) r.c[x] = blankCell()
        if (cols > 0 && r.c[cols - 1].w == 2) r.c[cols - 1] = blankCell()
    }

    private fun deleteChars(nn: Int) {
        val r = cur[cy]
        val n = minOf(nn, cols - cx)
        fixWide(r, cx)
        for (x in cx until cols - n) r.c[x] = r.c[x + n]
        for (x in cols - n until cols) r.c[x] = blankCell()
        if (cx < cols && r.c[cx].w == 0) r.c[cx] = blankCell()
    }

    private fun eraseChars(nn: Int) {
        val r = cur[cy]
        val n = minOf(nn, cols - cx)
        fixWide(r, cx); fixWide(r, cx + n - 1)
        for (x in cx until cx + n) r.c[x] = blankCell()
    }

    private fun eraseLine(m: Int) {
        val r = cur[cy]
        var from = 0; var to = cols - 1
        if (m == 0) from = cx else if (m == 1) to = cx
        if (m == 0 || m == 2) r.wrap = false
        fixWide(r, from); fixWide(r, to)
        for (x in from..to) r.c[x] = blankCell()
    }

    private fun eraseDisplay(m: Int) {
        when (m) {
            0 -> { eraseLine(0); for (y in cy + 1 until rows) cur[y] = blankRow() }
            1 -> { eraseLine(1); for (y in 0 until cy) cur[y] = blankRow() }
            2 -> for (y in 0 until rows) cur[y] = blankRow()
            3 -> scrollback.clear()
        }
    }

    // ---- 文字出力 ----
    private fun putChar(cpIn: Int, dbl: Boolean) {
        var cp = cpIn
        if (charset[gl] == 1 && cp < 128) cp = DEC_MAP[cp].code
        var w = if (dbl) 2 else charWidth(cp)
        var r = cur[cy]
        if (w == 0 || zwjPending) {
            zwjPending = cp == 0x200D
            var tx = if (wrapPending) cx else cx - 1
            if (tx in 0 until cols) {
                if (r.c[tx].w == 0 && tx > 0) tx--
                if (r.c[tx].ch != 0) r.c[tx] = r.c[tx].withExt((r.c[tx].ext ?: "") + cpStr(cp))
            }
            return
        }
        lastCp = cp
        if (wrapPending && autoWrap) {
            r.wrap = true
            cx = 0; lineFeed(); r = cur[cy]
        }
        wrapPending = false
        if (w == 2 && cx == cols - 1) {
            if (autoWrap) {
                r.c[cx] = blankCell()
                r.wrap = true
                cx = 0; lineFeed(); r = cur[cy]
            } else w = 1
        }
        if (insert) insertChars(w)
        fixWide(r, cx)
        if (w == 2) fixWide(r, cx + 1)
        r.c[cx] = Cell(ch = cp, fg = curFg, bg = curBg, attr = curAttr, w = w, wide2 = dbl)
        if (w == 2) r.c[cx + 1] = Cell(fg = curFg, bg = curBg, attr = curAttr, w = 0)
        cx += w
        if (cx >= cols) { cx = cols - 1; wrapPending = true }
    }

    // ---- 選択テキスト取得 ----
    fun getText(ar1: Int, ac1: Int, ar2: Int, ac2: Int): String {
        var r1 = ar1; var c1 = ac1; var r2 = ar2; var c2 = ac2
        if (r1 > r2 || (r1 == r2 && c1 > c2)) { var t = r1; r1 = r2; r2 = t; t = c1; c1 = c2; c2 = t }
        val sb = StringBuilder()
        for (y in r1..r2) {
            val row = getLine(y) ?: continue
            val from = if (y == r1) c1 else 0
            val to = if (y == r2) c2 else maxOf(row.c.size, cols) - 1
            val line = StringBuilder()
            for (x in from..to) {
                if (x >= row.c.size) { line.append(' '); continue }
                val c = row.c[x]
                if (c.w == 0) continue
                if (c.ch == 0) line.append(' ')
                else { line.append(cpStr(c.ch)); if (c.ext != null) line.append(c.ext) }
            }
            var ls = line.toString()
            if (y != r2 && !row.wrap) ls = ls.trimEnd(' ')
            sb.append(ls)
            if (y != r2 && !row.wrap) sb.append('\n')
        }
        return sb.toString()
    }

    companion object {
        private val DEC_MAP: CharArray = run {
            val m = CharArray(128) { it.toChar() }
            val from = "`abcdefghijklmnopqrstuvwxyz{|}~"
            val to = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"
            for (i in from.indices) m[from[i].code] = to[i]
            m
        }

        private val WIDE = intArrayOf(
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
        )

        fun isWide(cp: Int): Boolean {
            var lo = 0; var hi = WIDE.size / 2 - 1
            while (lo <= hi) {
                val mid = (lo + hi) shr 1
                if (cp < WIDE[mid * 2]) hi = mid - 1
                else if (cp > WIDE[mid * 2 + 1]) lo = mid + 1
                else return true
            }
            return false
        }

        fun charWidth(cp: Int): Int {
            if (cp < 0x300) return 1
            if (cp in 0x1F3FB..0x1F3FF) return 0
            if (cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0x2060 || cp == 0xFEFF ||
                (cp in 0xFE00..0xFE0F) || (cp in 0xE0100..0xE01EF)) return 0
            if (cp in 0xE0020..0xE007F) return 0
            if (cp in 0xD800..0xDFFF) return 1
            if (cp > 0x10FFFF) return 1
            if (isWide(cp)) return 2
            val t = Character.getType(cp)
            if (t == Character.NON_SPACING_MARK.toInt() || t == Character.ENCLOSING_MARK.toInt() || t == Character.FORMAT.toInt()) return 0
            return 1
        }
    }
}
