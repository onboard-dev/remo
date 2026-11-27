// Remo for Android
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.jcraft.jsch.ChannelShell
import com.jcraft.jsch.HostKey
import com.jcraft.jsch.HostKeyRepository
import com.jcraft.jsch.JSch
import com.jcraft.jsch.UIKeyboardInteractive
import com.jcraft.jsch.UserInfo
import java.io.OutputStream
import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.Executors

enum class ConnState { Idle, Connecting, Connected, Closed }

/** 1つの接続(タブ) */
class TermSession(val id: Int, val site: Site, private val host: SessionHost) {
    @Volatile var term = Terminal(80, 24)
    var state by mutableStateOf(ConnState.Idle)
    var title by mutableStateOf("")
    var revision by mutableIntStateOf(0) // 画面更新通知(UI 再描画用)
    @Volatile var onUpdate: (() -> Unit)? = null

    // 表示状態(ビューが再生成されても保持)
    @JvmField var viewOffset = 0
    @JvmField var lastScrolledIn = 0
    @JvmField var cols = 80
    @JvmField var rows = 24

    private var jsession: com.jcraft.jsch.Session? = null
    private var channel: ChannelShell? = null
    private var out: OutputStream? = null
    private val writer = Executors.newSingleThreadExecutor()

    val alive: Boolean get() = state == ConnState.Connecting || state == ConnState.Connected
    val displayName: String get() = title.ifBlank { site.name.ifBlank { site.host } }

    init {
        term.setEncoding(site.enc == 1)
        term.reply = { b -> send(b) }
        term.titleChanged = { t -> title = t }
    }

    fun print(s: String) {
        synchronized(term) { term.feed(s.toByteArray(Charsets.UTF_8), s.toByteArray(Charsets.UTF_8).size) }
        onUpdate?.invoke()
    }

    fun connect() {
        if (alive) return
        state = ConnState.Connecting
        Thread({ doConnect() }, "ssh-connect-$id").start()
    }

    private fun appendCfg(s: com.jcraft.jsch.Session, key: String, extra: String) {
        val cur = JSch.getConfig(key) ?: ""
        if (!cur.split(',').contains(extra)) s.setConfig(key, if (cur.isEmpty()) extra else "$cur,$extra")
    }

    private fun doConnect() {
        try {
            print("\u001b[90m${site.hostOnly}:${site.port} に接続しています...\u001b[0m\r\n")
            val jsch = JSch()
            jsch.hostKeyRepository = HostRepo()
            val s = jsch.getSession(site.user, site.hostOnly, site.port)
            if (site.pass.isNotEmpty()) s.setPassword(site.pass)
            s.setConfig("StrictHostKeyChecking", "ask")
            s.setConfig("PreferredAuthentications", "password,keyboard-interactive")
            // 古いサーバー向けの互換設定 (PC 版 Remo と同等の範囲)
            appendCfg(s, "server_host_key", "ssh-rsa")
            appendCfg(s, "kex", "diffie-hellman-group14-sha1")
            appendCfg(s, "mac.c2s", "hmac-sha1"); appendCfg(s, "mac.s2c", "hmac-sha1")
            s.userInfo = UI()
            s.setServerAliveInterval(30000)
            s.connect(15000)
            jsession = s

            val ch = s.openChannel("shell") as ChannelShell
            ch.setPtyType("xterm-256color", cols, rows, 0, 0)
            if (site.lang.isNotEmpty()) try { ch.setEnv("LANG", site.lang) } catch (_: Throwable) {}
            if (site.enc == 0) try { ch.setTerminalMode(byteArrayOf(42, 0, 0, 0, 1, 0)) } catch (_: Throwable) {} // IUTF8
            val input = ch.inputStream
            out = ch.outputStream
            ch.connect(10000)
            channel = ch
            state = ConnState.Connected
            host.onSessionsChanged()

            val buf = ByteArray(16384)
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                synchronized(term) { term.feed(buf, n) }
                onUpdate?.invoke()
            }
            print("\r\n\u001b[90m[切断されました]\u001b[0m\r\n")
        } catch (e: Throwable) {
            if (state != ConnState.Closed) print("\r\n\u001b[91m接続できませんでした: ${e.message ?: e.javaClass.simpleName}\u001b[0m\r\n")
        } finally {
            cleanup()
            state = ConnState.Closed
            host.onSessionsChanged()
        }
    }

    fun send(b: ByteArray) {
        val o = out ?: return
        writer.execute { try { o.write(b); o.flush() } catch (_: Throwable) {} }
    }

    fun sendStr(s: String) = send(s.toByteArray(if (site.enc == 1) charset("windows-31j") else Charsets.UTF_8))

    fun resize(c: Int, r: Int) {
        cols = c; rows = r
        synchronized(term) { term.resize(c, r) }
        val ch = channel
        if (ch != null && ch.isConnected) Thread { try { ch.setPtySize(c, r, 0, 0) } catch (_: Throwable) {} }.start()
        onUpdate?.invoke()
    }

    fun disconnect() {
        if (state == ConnState.Idle || state == ConnState.Closed) return
        state = ConnState.Closed
        Thread { cleanup() }.start()
    }

    private fun cleanup() {
        try { channel?.disconnect() } catch (_: Throwable) {}
        try { jsession?.disconnect() } catch (_: Throwable) {}
        channel = null; jsession = null; out = null
    }

    fun close() { disconnect(); writer.shutdown() }

    // ---- ホスト鍵 (保存形式は PC 版と同じ "alg SHA256:xxx") ----
    private inner class HostRepo : HostKeyRepository {
        private fun fp(h: HostKey): String =
            "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(
                MessageDigest.getInstance("SHA-256").digest(Base64.getDecoder().decode(h.key)))

        override fun check(host: String?, key: ByteArray?): Int {
            if (key == null) return HostKeyRepository.NOT_INCLUDED
            val f = "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(key))
            val saved = site.hostKey
            if (saved.isEmpty()) {
                // 初回接続: 自動登録 (PC 版と同じ)
                site.hostKey = "key $f"
                this@TermSession.host.onSiteChanged(site)
                print("\u001b[90mホスト鍵を登録しました ($f)\u001b[0m\r\n")
                return HostKeyRepository.OK
            }
            return if (saved.endsWith(f)) HostKeyRepository.OK else HostKeyRepository.CHANGED
        }

        override fun add(hostkey: HostKey?, ui: UserInfo?) {
            if (hostkey != null) { site.hostKey = "${hostkey.type} ${fp(hostkey)}"; this@TermSession.host.onSiteChanged(site) }
        }
        override fun remove(host: String?, type: String?) {}
        override fun remove(host: String?, type: String?, key: ByteArray?) {}
        override fun getKnownHostsRepositoryID(): String = "remo"
        override fun getHostKey(): Array<HostKey> = emptyArray()
        override fun getHostKey(host: String?, type: String?): Array<HostKey> = emptyArray()
    }

    private inner class UI : UserInfo, UIKeyboardInteractive {
        private var passUsed = false
        override fun getPassphrase(): String? = null
        override fun getPassword(): String? = site.pass.ifEmpty { host.askText("「${site.name}」のパスワード", true) }
        override fun promptPassword(message: String?): Boolean {
            if (site.pass.isNotEmpty() && !passUsed) { passUsed = true; return true }
            if (site.pass.isNotEmpty()) return false // 認証失敗後は再試行しない
            return true
        }
        override fun promptPassphrase(message: String?) = false
        override fun promptYesNo(message: String?): Boolean =
            host.askYesNo("ホスト鍵の確認", "「${site.name}」のホスト鍵が前回と異なります。\nなりすましの可能性があります。\n\n${message ?: ""}\n\n接続しますか?")
        override fun showMessage(message: String?) { if (message != null) print("\u001b[90m${message.replace("\n", "\r\n")}\u001b[0m\r\n") }

        override fun promptKeyboardInteractive(dest: String?, name: String?, instr: String?, prompt: Array<out String>?, echo: BooleanArray?): Array<String>? {
            if (prompt == null) return null
            val res = ArrayList<String>()
            for (i in prompt.indices) {
                val isPass = echo == null || !echo[i]
                if (isPass && site.pass.isNotEmpty() && !passUsed) { passUsed = true; res.add(site.pass); continue }
                val t = host.askText(prompt[i].ifBlank { "入力してください" }, isPass) ?: return null
                res.add(t)
            }
            return res.toTypedArray()
        }
    }
}

/** 接続側から UI 層へ問い合わせ・通知するためのインターフェース */
interface SessionHost {
    fun onSessionsChanged()
    fun onSiteChanged(site: Site)
    /** UI スレッド外から呼ばれ、回答があるまでブロックする */
    fun askYesNo(title: String, message: String): Boolean
    fun askText(message: String, secret: Boolean): String?
}
