// Remo for Android - terminal rendering, touch gestures and input (replaces TermView.cs)
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Typeface
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.text.InputType
import android.view.GestureDetector
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.view.ViewConfiguration
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * 操作の対応 (PC版 → Android):
 *  ドラッグ選択 → 長押し+ドラッグ / 右クリックコピー → 選択後の「コピー」/ 右クリックペースト → 「貼付」ボタン・2本指タップ
 *  ホイール → 縦スワイプ(全画面アプリでは上下キー) / Ctrl+ホイール → ピンチ / 右ダブルクリック → ダブルタップでクリア
 */
@SuppressLint("ClickableViewAccessibility")
class TerminalView(ctx: Context) : View(ctx) {
    var session: TermSession? = null
        private set

    var onSelectionChanged: ((Boolean) -> Unit)? = null
    var onNotify: ((String) -> Unit)? = null
    var onFontChanged: ((Float) -> Unit)? = null
    var onModifiersUsed: (() -> Unit)? = null

    /** 補助キー列の Ctrl / Alt (1回だけ有効) */
    var ctrlOn = false
    var altOn = false

    var fontSp = 12f
        set(v) { field = v.coerceIn(7f, 36f); applyFont(); }

    private val density = resources.displayMetrics.density
    private val handler = Handler(Looper.getMainLooper())
    private val pad = 6f * density

    private val pN = Paint(Paint.ANTI_ALIAS_FLAG)
    private val pB = Paint(Paint.ANTI_ALIAS_FLAG)
    private val pI = Paint(Paint.ANTI_ALIAS_FLAG)
    private val pBI = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fill = Paint()
    private var cw = 10f
    private var chh = 20f
    private var baseline = 15f
    private var gridCols = 80
    private var gridRows = 24

    // 選択 (絶対行番号)
    private var selActive = false
    private var selDragging = false
    private var sAr = 0; private var sAc = 0; private var sBr = 0; private var sBc = 0

    private var composing = ""
    private var cursorPhase = true
    private var pinchAcc = 1f

    init {
        isFocusable = true
        isFocusableInTouchMode = true
        pN.typeface = Typeface.MONOSPACE
        pB.typeface = Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)
        pI.typeface = Typeface.create(Typeface.MONOSPACE, Typeface.ITALIC)
        pBI.typeface = Typeface.create(Typeface.MONOSPACE, Typeface.BOLD_ITALIC)
        setBackgroundColor(DEF_BG or 0xFF000000.toInt())
        applyFont()
        blinkLoop()
    }

    private fun blinkLoop() {
        handler.postDelayed({
            cursorPhase = !cursorPhase
            if (hasFocus()) invalidate()
            blinkLoop()
        }, 530)
    }

    private fun applyFont() {
        val px = fontSp * resources.displayMetrics.scaledDensity.coerceAtLeast(1f)
        for (p in arrayOf(pN, pB, pI, pBI)) p.textSize = px
        cw = pN.measureText("M")
        val fm = pN.fontMetrics
        chh = (fm.descent - fm.ascent) * 1.08f
        baseline = -fm.ascent + (chh - (fm.descent - fm.ascent)) / 2f
        recomputeGrid()
        invalidate()
    }

    fun bind(s: TermSession?) {
        session?.onUpdate = null
        session = s
        clearSelection()
        composing = ""
        if (s != null) {
            s.onUpdate = { postInvalidateOnAnimation() }
            recomputeGrid(true)
        }
        invalidate()
    }

    override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) {
        super.onSizeChanged(w, h, ow, oh)
        recomputeGrid()
    }

    private fun recomputeGrid(force: Boolean = false) {
        if (width == 0 || height == 0) return
        val c = max(2, ((width - 2 * pad) / cw).toInt())
        val r = max(1, ((height - 2 * pad) / chh).toInt())
        if (c != gridCols || r != gridRows || force) {
            gridCols = c; gridRows = r
            session?.resize(c, r)
        }
    }

    // ---------- 描画 ----------
    private fun resolve(v: Int, def: Int): Int = when {
        v == 0 -> def
        (v and 0x01000000) != 0 -> PAL[v and 0xFF]
        else -> v and 0xFFFFFF
    }

    private fun argb(rgb: Int) = rgb or 0xFF000000.toInt()

    private fun normSel(): IntArray {
        var r1 = sAr; var c1 = sAc; var r2 = sBr; var c2 = sBc
        if (r1 > r2 || (r1 == r2 && c1 > c2)) { val tr = r1; r1 = r2; r2 = tr; val tc = c1; c1 = c2; c2 = tc }
        return intArrayOf(r1, c1, r2, c2)
    }

    override fun onDraw(canvas: Canvas) {
        val s = session ?: return
        val t = s.term
        synchronized(t) {
            val d = t.scrolledIn - s.lastScrolledIn
            s.lastScrolledIn = t.scrolledIn
            if (s.viewOffset > 0 && d > 0 && !t.alt) s.viewOffset += d
            s.viewOffset = s.viewOffset.coerceIn(0, t.scrollbackCount)
            val first = t.scrollbackCount - s.viewOffset
            val sel = if (selActive) normSel() else null
            val rowsToDraw = min(gridRows, t.rows + s.viewOffset)

            for (y in 0 until rowsToDraw) {
                val abs = first + y
                val row = t.getLine(abs) ?: continue
                val py = pad + y * chh
                val n = min(row.c.size, gridCols)
                // 背景
                var x = 0
                while (x < n) {
                    val c = row.c[x]
                    var bg = resolve(c.bg, -1); var fg = resolve(c.fg, DEF_FG)
                    if ((c.attr and At.Reverse) != 0) { val tmp = if (bg == -1) DEF_BG else bg; bg = fg; fg = tmp }
                    val selected = sel != null && inSel(sel, abs, x)
                    if (selected) bg = SEL_BG
                    if (bg != -1) {
                        fill.color = argb(bg)
                        val wcells = if (c.w == 2) 2 else 1
                        canvas.drawRect(pad + x * cw, py, pad + (x + wcells) * cw + 0.5f, py + chh, fill)
                    }
                    x++
                }
                // 文字
                x = 0
                while (x < n) {
                    val c = row.c[x]
                    if (c.w == 0 || (c.ch == 0 && (c.attr and (At.Underline or At.Strike)) == 0)) { x++; continue }
                    var fg = resolve(c.fg, DEF_FG)
                    if ((c.attr and At.Reverse) != 0) { fg = if (c.bg == 0) DEF_BG else resolve(c.bg, DEF_BG) }
                    if (sel != null && (c.attr and At.Reverse) != 0 && inSel(sel, abs, x)) fg = DEF_FG
                    if ((c.attr and At.Hidden) != 0) { x++; continue }
                    var color = argb(fg)
                    if ((c.attr and At.Dim) != 0) color = (color and 0x00FFFFFF) or 0xA0000000.toInt()
                    val px = pad + x * cw
                    if (c.ch != 0 && c.ch != 32) {
                        val p = when { (c.attr and At.Bold) != 0 && (c.attr and At.Italic) != 0 -> pBI; (c.attr and At.Bold) != 0 -> pB; (c.attr and At.Italic) != 0 -> pI; else -> pN }
                        p.color = color
                        val str = if (c.ext != null) String(Character.toChars(c.ch)) + c.ext else String(Character.toChars(c.ch))
                        val avail = (if (c.w == 2) 2 else 1) * cw
                        if (c.ch < 0x80) canvas.drawText(str, px, py + baseline, p)
                        else {
                            val mw = p.measureText(str)
                            if (mw > avail * 1.02f) {
                                canvas.save(); canvas.scale(avail / mw, 1f, px, py)
                                canvas.drawText(str, px, py + baseline, p); canvas.restore()
                            } else if (c.w == 2 && mw < avail * 0.9f) canvas.drawText(str, px + (avail - mw) / 2f, py + baseline, p)
                            else canvas.drawText(str, px, py + baseline, p)
                        }
                    }
                    val wpx = (if (c.w == 2) 2 else 1) * cw
                    if ((c.attr and At.Underline) != 0) { fill.color = color; canvas.drawRect(px, py + chh - 2f * density, px + wpx, py + chh - 1f * density, fill) }
                    if ((c.attr and At.Strike) != 0) { fill.color = color; canvas.drawRect(px, py + chh / 2, px + wpx, py + chh / 2 + density, fill) }
                    x++
                }
            }

            // カーソル / 変換中の文字
            if (s.viewOffset == 0 && t.cursorVisible && t.cy < gridRows) {
                val cxp = pad + t.cx * cw
                val cyp = pad + t.cy * chh
                if (composing.isNotEmpty()) {
                    var xx = cxp
                    pN.color = argb(DEF_FG)
                    fill.color = argb(0x3B4261)
                    for (cp in composing.codePoints().toArray()) {
                        val w = if (Terminal.charWidth(cp) == 2) 2 else 1
                        canvas.drawRect(xx, cyp, xx + w * cw, cyp + chh, fill)
                        canvas.drawText(String(Character.toChars(cp)), xx, cyp + baseline, pN)
                        xx += w * cw
                    }
                    fill.color = argb(DEF_FG)
                    canvas.drawRect(cxp, cyp + chh - 2f * density, xx, cyp + chh, fill)
                } else if (hasFocus() && cursorPhase) {
                    fill.color = argb(CUR_COLOR) and 0x00FFFFFF or 0xB0000000.toInt()
                    canvas.drawRect(cxp, cyp, cxp + cw, cyp + chh, fill)
                } else if (!hasFocus()) {
                    fill.style = Paint.Style.STROKE; fill.strokeWidth = density; fill.color = argb(CUR_COLOR)
                    canvas.drawRect(cxp, cyp, cxp + cw, cyp + chh, fill); fill.style = Paint.Style.FILL
                }
            }
            // スクロール位置のインジケータ
            if (s.viewOffset > 0 && !t.alt) {
                val total = t.scrollbackCount + t.rows
                val h = height * t.rows.toFloat() / total
                val yy = (first.toFloat() / total) * height
                fill.color = 0x667982A9
                canvas.drawRect(width - 3f * density, yy, width.toFloat(), yy + max(h, 12f * density), fill)
            }
        }
    }

    private fun inSel(sel: IntArray, abs: Int, x: Int): Boolean {
        if (abs < sel[0] || abs > sel[2]) return false
        if (abs == sel[0] && x < sel[1]) return false
        if (abs == sel[2] && x > sel[3]) return false
        return true
    }

    // ---------- 選択・コピー・ペースト ----------
    val hasSelection: Boolean get() = selActive

    fun clearSelection() {
        if (selActive || selDragging) { selActive = false; selDragging = false; onSelectionChanged?.invoke(false); invalidate() }
    }

    fun selectedText(): String {
        val s = session ?: return ""
        if (!selActive) return ""
        val n = normSel()
        return synchronized(s.term) { s.term.getText(n[0], n[1], n[2], n[3]) }
    }

    fun copySelection(): Boolean {
        val txt = selectedText()
        if (txt.isEmpty()) return false
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Remo", txt))
        clearSelection()
        onNotify?.invoke("コピーしました")
        return true
    }

    /** スクロールバックを含む端末内の全文字をコピーする */
    fun copyAll(): Boolean {
        val s = session ?: return false
        val txt = synchronized(s.term) { s.term.getText(0, 0, s.term.totalLines - 1, s.term.cols - 1) }.trimEnd()
        if (txt.isEmpty()) { onNotify?.invoke("コピーする文字がありません"); return false }
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Remo", txt))
        clearSelection()
        onNotify?.invoke("すべてコピーしました")
        return true
    }

    fun paste() {
        val s = session ?: return
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val txt = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString() ?: ""
        if (txt.isEmpty()) { onNotify?.invoke("クリップボードが空です"); return }
        flushComposing()
        var x = txt.replace("\r\n", "\r").replace('\n', '\r')
        if (s.term.bracketedPaste) x = "\u001b[200~$x\u001b[201~"
        s.viewOffset = 0
        s.sendStr(x)
    }

    fun clearScreen() {
        val s = session ?: return
        if (s.term.alt) { onNotify?.invoke("全画面アプリの表示中はクリアできません"); return }
        synchronized(s.term) { s.term.clearAll() }
        s.viewOffset = 0
        clearSelection()
        invalidate()
        onNotify?.invoke("画面をクリアしました")
        performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
    }

    // ---------- 入力 ----------
    fun showKeyboard() {
        requestFocus()
        (context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(this, InputMethodManager.SHOW_IMPLICIT)
    }

    private fun flushComposing() {
        if (composing.isEmpty()) return
        val c = composing
        composing = ""
        session?.sendStr(c)
        (context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).restartInput(this)
        invalidate()
    }

    /** 補助キー列などから呼ぶ */
    fun sendRaw(s: String) {
        flushComposing()
        val sess = session ?: return
        sess.viewOffset = 0
        sess.sendStr(s)
    }

    fun sendKeyCode(code: Int) {
        flushComposing()
        handleKey(code, KeyEvent(KeyEvent.ACTION_DOWN, code), true)
    }

    private fun ctrlChar(c: Char): String? = when {
        c in 'a'..'z' -> (c.code - 96).toChar().toString()
        c in 'A'..'Z' -> (c.code - 64).toChar().toString()
        c in '@'..'_' -> (c.code - 64).toChar().toString()
        c == ' ' -> "\u0000"
        c == '?' -> "\u007f"
        else -> null
    }

    private fun sendText(text: CharSequence) {
        val s = session ?: return
        if (text.isEmpty()) return
        s.viewOffset = 0
        var str = text.toString()
        if (ctrlOn && str.length == 1) { ctrlChar(str[0])?.let { str = it } ; ctrlOn = false; onModifiersUsed?.invoke() }
        if (altOn) { str = "\u001b$str"; altOn = false; onModifiersUsed?.invoke() }
        s.sendStr(str)
    }

    private fun fkey(n: Int, mod: Int): String {
        val code = when (n) { 5 -> 15; 6 -> 17; 7 -> 18; 8 -> 19; 9 -> 20; 10 -> 21; 11 -> 23; else -> 24 }
        return if (mod == 1) "\u001b[$code~" else "\u001b[$code;$mod~"
    }

    private fun handleKey(keyCode: Int, ev: KeyEvent, fromBar: Boolean = false): Boolean {
        val s = session ?: return false
        val shift = ev.isShiftPressed
        val ctrl = ev.isCtrlPressed || ctrlOn
        val alt = ev.isAltPressed || altOn
        val mod = 1 + (if (shift) 1 else 0) + (if (alt) 2 else 0) + (if (ctrl) 4 else 0)
        val cur = if (s.term.appCursor) "\u001bO" else "\u001b["
        fun csi(l: String) = if (mod == 1) cur + l else "\u001b[1;$mod$l"
        fun tilde(n: Int) = if (mod == 1) "\u001b[$n~" else "\u001b[$n;$mod~"
        var seq: String? = when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP -> csi("A")
            KeyEvent.KEYCODE_DPAD_DOWN -> csi("B")
            KeyEvent.KEYCODE_DPAD_RIGHT -> csi("C")
            KeyEvent.KEYCODE_DPAD_LEFT -> csi("D")
            KeyEvent.KEYCODE_MOVE_HOME -> csi("H")
            KeyEvent.KEYCODE_MOVE_END -> csi("F")
            KeyEvent.KEYCODE_INSERT -> tilde(2)
            KeyEvent.KEYCODE_FORWARD_DEL -> tilde(3)
            KeyEvent.KEYCODE_PAGE_UP -> tilde(5)
            KeyEvent.KEYCODE_PAGE_DOWN -> tilde(6)
            KeyEvent.KEYCODE_F1 -> if (mod == 1) "\u001bOP" else "\u001b[1;${mod}P"
            KeyEvent.KEYCODE_F2 -> if (mod == 1) "\u001bOQ" else "\u001b[1;${mod}Q"
            KeyEvent.KEYCODE_F3 -> if (mod == 1) "\u001bOR" else "\u001b[1;${mod}R"
            KeyEvent.KEYCODE_F4 -> if (mod == 1) "\u001bOS" else "\u001b[1;${mod}S"
            in KeyEvent.KEYCODE_F5..KeyEvent.KEYCODE_F12 -> fkey(keyCode - KeyEvent.KEYCODE_F1 + 1, mod)
            KeyEvent.KEYCODE_TAB -> if (shift) "\u001b[Z" else "\t"
            KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> "\r"
            KeyEvent.KEYCODE_DEL -> if (alt) "\u001b\u007f" else "\u007f"
            KeyEvent.KEYCODE_ESCAPE -> "\u001b"
            else -> null
        }
        if (seq == null) {
            if (keyCode in listOf(KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_SHIFT_RIGHT, KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_CTRL_RIGHT,
                    KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_ALT_RIGHT, KeyEvent.KEYCODE_META_LEFT, KeyEvent.KEYCODE_META_RIGHT, KeyEvent.KEYCODE_CAPS_LOCK)) return true
            if (ev.isCtrlPressed && ev.isShiftPressed) {
                // Ctrl+Shift+C / V : コピー / 貼り付け
                if (keyCode == KeyEvent.KEYCODE_C) { copySelection(); return true }
                if (keyCode == KeyEvent.KEYCODE_V) { paste(); return true }
            }
            if (ev.isMetaPressed) return false
            val base = ev.getUnicodeChar(0)
            if (base == 0) return false
            if (ev.isCtrlPressed) ctrlOn = true
            val shown = ev.getUnicodeChar(if (shift) KeyEvent.META_SHIFT_ON else 0)
            sendText(String(Character.toChars(if (shown != 0) shown else base)))
            return true
        }
        s.viewOffset = 0
        s.sendStr(seq)
        if (ctrlOn || altOn) { ctrlOn = false; altOn = false; onModifiersUsed?.invoke() }
        return true
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK) return super.onKeyDown(keyCode, event)
        flushComposing()
        return handleKey(keyCode, event) || super.onKeyDown(keyCode, event)
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_ACTION_NONE
        return object : BaseInputConnection(this, true) {
            // 変換中の文字だけを Editable に保持する。確定したら端末へ送って空にする。
            private fun sync() {
                val ed = editable ?: return
                val s = getComposingSpanStart(ed); val en = getComposingSpanEnd(ed)
                composing = if (s >= 0 && en > s) ed.substring(s, en) else ""
                invalidate()
            }

            override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                val t = text?.toString() ?: ""
                super.commitText(text, newCursorPosition)
                editable?.clear()
                composing = ""
                sendText(t)
                invalidate()
                return true
            }

            override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
                val r = super.setComposingText(text, newCursorPosition)
                sync(); return r
            }

            override fun setComposingRegion(start: Int, end: Int): Boolean {
                val r = super.setComposingRegion(start, end)
                sync(); return r
            }

            override fun finishComposingText(): Boolean {
                val c = composing
                super.finishComposingText()
                editable?.clear()
                composing = ""
                if (c.isNotEmpty()) sendText(c)
                invalidate()
                return true
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                val ed = editable
                if (ed == null || ed.isEmpty()) { repeat(beforeLength) { session?.sendStr("\u007f") }; return true }
                val r = super.deleteSurroundingText(beforeLength, afterLength)
                sync(); return r
            }

            override fun sendKeyEvent(event: KeyEvent): Boolean {
                if (event.action == KeyEvent.ACTION_DOWN) {
                    val ed = editable
                    if (ed != null && ed.isNotEmpty() && event.keyCode == KeyEvent.KEYCODE_DEL) {
                        super.sendKeyEvent(event); sync(); return true
                    }
                    handleKey(event.keyCode, event)
                }
                return true
            }
        }
    }
    // ---------- タッチ ----------
    private val slop = ViewConfiguration.get(ctx).scaledTouchSlop
    private var scrollAcc = 0f
    private var flingV = 0f
    private var flinging = false
    private var downX = 0f; private var downY = 0f
    private var lastX = 0f; private var lastY = 0f
    private var longPressRun: Runnable? = null
    private var twoFingerDown = 0L
    private var scaled = false
    private var edgeRun: Runnable? = null

    private val gesture = GestureDetector(ctx, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent): Boolean { flinging = false; return true }

        override fun onSingleTapUp(e: MotionEvent): Boolean {
            if (selActive) clearSelection()
            showKeyboard()
            return true
        }

        override fun onDoubleTap(e: MotionEvent): Boolean { clearScreen(); return true }

        override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean {
            scrollBy(-dy)
            return true
        }

        override fun onFling(e1: MotionEvent?, e2: MotionEvent, vx: Float, vy: Float): Boolean {
            if (abs(vy) < 400f) return false
            flingV = vy; flinging = true
            var last = SystemClock.uptimeMillis()
            postOnAnimation(object : Runnable {
                override fun run() {
                    if (!flinging) return
                    val now = SystemClock.uptimeMillis()
                    val dt = (now - last) / 1000f; last = now
                    scrollBy(flingV * dt)
                    flingV *= 0.94f
                    if (abs(flingV) < 60f) flinging = false else postOnAnimation(this)
                }
            })
            return true
        }
    }).apply { setIsLongpressEnabled(false) }

    private val scaleDetector = ScaleGestureDetector(ctx, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScaleBegin(d: ScaleGestureDetector): Boolean { pinchAcc = 1f; scaled = true; return true }
        override fun onScale(d: ScaleGestureDetector): Boolean {
            pinchAcc *= d.scaleFactor
            if (pinchAcc > 1.08f || pinchAcc < 0.92f) {
                val ns = (fontSp * pinchAcc).coerceIn(7f, 36f)
                pinchAcc = 1f
                if (abs(ns - fontSp) > 0.2f) { fontSp = ns; onFontChanged?.invoke(fontSp) }
            }
            return true
        }
    })

    /** 指の動きに合わせて(px)スクロール。下へ動かす = 過去の出力へ */
    private fun scrollBy(dyPx: Float) {
        val s = session ?: return
        scrollAcc += dyPx
        val lines = (scrollAcc / chh).toInt()
        if (lines == 0) return
        scrollAcc -= lines * chh
        if (s.term.alt) {
            // 全画面アプリ: 上下キーとして送る (PC版のホイールと同じ)
            val k = if (lines > 0) (if (s.term.appCursor) "\u001bOA" else "\u001b[A") else (if (s.term.appCursor) "\u001bOB" else "\u001b[B")
            s.sendStr(k.repeat(min(abs(lines), 10)))
        } else {
            val old = s.viewOffset
            s.viewOffset = (s.viewOffset + lines).coerceIn(0, s.term.scrollbackCount)
            if (s.viewOffset == old) flinging = false
            invalidate()
        }
    }

    private fun cellAt(x: Float, y: Float): IntArray {
        val s = session!!
        val c = ((x - pad) / cw).toInt().coerceIn(0, gridCols - 1)
        val r = ((y - pad) / chh).toInt().coerceIn(0, gridRows - 1)
        val first = s.term.scrollbackCount - s.viewOffset
        return intArrayOf(first + r, c)
    }

    private fun isWordCh(c: Cell) = c.w == 0 || (c.ch != 0 && c.ch != 32 && !(c.ch < 128 && "\"'`()[]{}<>|,;".indexOf(c.ch.toChar()) >= 0))

    private fun startSelection(x: Float, y: Float) {
        val s = session ?: return
        val (ar, ac) = cellAt(x, y).let { it[0] to it[1] }
        var a = ac; var b = ac
        synchronized(s.term) {
            val row = s.term.getLine(ar)
            if (row != null && ac < row.c.size && isWordCh(row.c[ac])) {
                while (a > 0 && isWordCh(row.c[a - 1])) a--
                while (b < row.c.size - 1 && isWordCh(row.c[b + 1])) b++
            }
        }
        sAr = ar; sAc = a; sBr = ar; sBc = b
        selActive = true; selDragging = true
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        invalidate()
    }

    private fun dragSelection(x: Float, y: Float) {
        val p = cellAt(x, y)
        if (p[0] != sBr || p[1] != sBc) { sBr = p[0]; sBc = p[1]; invalidate() }
        // 画面端でのオートスクロール
        edgeRun?.let { handler.removeCallbacks(it) }
        val dir = when { y < pad + chh -> 1; y > height - pad - chh -> -1; else -> 0 }
        if (dir != 0) {
            edgeRun = object : Runnable {
                override fun run() {
                    val s = session ?: return
                    if (!selDragging || s.term.alt) return
                    s.viewOffset = (s.viewOffset + dir).coerceIn(0, s.term.scrollbackCount)
                    val q = cellAt(lastX, lastY.coerceIn(0f, height.toFloat()))
                    sBr = q[0]; sBc = q[1]
                    invalidate()
                    handler.postDelayed(this, 60)
                }
            }
            handler.postDelayed(edgeRun!!, 60)
        }
    }

    override fun onTouchEvent(ev: MotionEvent): Boolean {
        val s = session ?: return true
        scaleDetector.onTouchEvent(ev)

        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = ev.x; downY = ev.y; lastX = ev.x; lastY = ev.y; scrollAcc = 0f; scaled = false
                flinging = false
                longPressRun = Runnable {
                    longPressRun = null
                    startSelection(downX, downY)
                    onSelectionChanged?.invoke(true)
                }
                handler.postDelayed(longPressRun!!, ViewConfiguration.getLongPressTimeout().toLong())
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                cancelLpTimer()
                if (ev.pointerCount == 2) twoFingerDown = SystemClock.uptimeMillis()
            }
            MotionEvent.ACTION_POINTER_UP -> {
                // 2本指タップ = ペースト
                if (ev.pointerCount == 2 && !scaled && SystemClock.uptimeMillis() - twoFingerDown < 250) { paste(); twoFingerDown = 0 }
            }
        }

        if (selDragging) {
            when (ev.actionMasked) {
                MotionEvent.ACTION_MOVE -> { lastX = ev.x; lastY = ev.y; dragSelection(ev.x, ev.y) }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    selDragging = false
                    edgeRun?.let { handler.removeCallbacks(it) }
                    onSelectionChanged?.invoke(selActive)
                }
            }
            return true
        }

        if (ev.actionMasked == MotionEvent.ACTION_MOVE) {
            lastX = ev.x; lastY = ev.y
            if (abs(ev.x - downX) > slop || abs(ev.y - downY) > slop) cancelLpTimer()
        }
        if (ev.actionMasked == MotionEvent.ACTION_UP || ev.actionMasked == MotionEvent.ACTION_CANCEL) cancelLpTimer()

        if (!scaleDetector.isInProgress && !scaled) gesture.onTouchEvent(ev)
        else if (ev.actionMasked == MotionEvent.ACTION_UP || ev.actionMasked == MotionEvent.ACTION_CANCEL) {
            gesture.onTouchEvent(MotionEvent.obtain(ev).also { it.action = MotionEvent.ACTION_CANCEL })
        }
        return true
    }

    private fun cancelLpTimer() { longPressRun?.let { handler.removeCallbacks(it) }; longPressRun = null }

    override fun onDetachedFromWindow() {
        super.onDetachedFromWindow()
        handler.removeCallbacksAndMessages(null)
        session?.onUpdate = null
    }

    companion object {
        const val DEF_FG = 0xC0CAF5
        const val DEF_BG = 0x1A1B26
        const val SEL_BG = 0x33467C
        const val CUR_COLOR = 0xC0CAF5

        // Tokyo Night 風 (PC版と同一)
        val PAL: IntArray = IntArray(256).also { p ->
            val b = intArrayOf(0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
                0x565F89, 0xFF899D, 0xB9F27C, 0xFFC777, 0x9AB8FF, 0xCBA8FF, 0xA4E0FF, 0xC0CAF5)
            for (i in 0 until 16) p[i] = b[i]
            val lv = intArrayOf(0, 95, 135, 175, 215, 255)
            for (i in 0 until 216) p[16 + i] = (lv[i / 36] shl 16) or (lv[(i / 6) % 6] shl 8) or lv[i % 6]
            for (i in 0 until 24) { val g = 8 + i * 10; p[232 + i] = (g shl 16) or (g shl 8) or g }
        }
    }
}
