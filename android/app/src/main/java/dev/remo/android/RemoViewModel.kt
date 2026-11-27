// Remo for Android
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import android.app.Application
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import java.util.concurrent.CompletableFuture

class Ask(val title: String, val message: String, val isText: Boolean, val secret: Boolean, val result: CompletableFuture<String?>)

class RemoViewModel(app: Application) : AndroidViewModel(app), SessionHost {
    private val data = Store.load(app)
    val sites = mutableStateListOf<Site>().apply { addAll(data.sites) }
    val sessions = mutableStateListOf<TermSession>()
    var active by mutableStateOf<TermSession?>(null)
    /** 縦画面(1ペイン)で端末画面を表示しているか */
    var showTerminal by mutableStateOf(false)
    var fontSp by mutableStateOf(data.fontSp)
    var sideCollapsed by mutableStateOf(data.sideCollapsed)
    var message by mutableStateOf<String?>(null)
    var ask by mutableStateOf<Ask?>(null)
    private var nextId = 1
    private val main = Handler(Looper.getMainLooper())

    init { Store.loadError?.let { message = "設定を読み込めませんでした: $it" } }

    fun persist() {
        data.sites.clear(); data.sites.addAll(sites)
        data.fontSp = fontSp; data.sideCollapsed = sideCollapsed
        try { Store.save(getApplication(), data) } catch (e: Exception) { message = "設定の保存に失敗しました: ${e.message}" }
    }

    // ---- 接続先 ----
    fun saveSite(old: Site?, edited: Site) {
        if (old == null) sites.add(edited)
        else {
            val i = sites.indexOf(old)
            if (old.host != edited.host) edited.hostKey = ""
            if (i >= 0) sites[i] = edited
        }
        persist()
    }

    fun duplicate(s: Site) {
        val c = s.copy(); c.name = s.name + " (コピー)"; c.hostKey = ""
        sites.add(sites.indexOf(s) + 1, c); persist()
    }

    fun delete(s: Site) {
        sessions.filter { it.site === s }.forEach { closeSession(it) }
        sites.remove(s); persist()
    }

    fun sessionFor(s: Site): TermSession? = sessions.lastOrNull { it.site === s }

    // ---- 接続 ----
    fun connect(s: Site) {
        val ex = sessionFor(s)
        if (ex != null && ex.alive) { activate(ex); return }
        if (ex != null) { sessions.remove(ex); ex.close() }
        val ses = TermSession(nextId++, s, this)
        sessions.add(ses)
        activate(ses)
        ses.connect()
        onSessionsChanged()
    }

    fun activate(s: TermSession) { active = s; showTerminal = true }

    fun disconnect(s: TermSession) { s.disconnect(); onSessionsChanged() }

    fun closeSession(s: TermSession) {
        val i = sessions.indexOf(s)
        sessions.remove(s)
        s.close()
        if (active === s) {
            active = sessions.getOrNull(minOf(i, sessions.size - 1))
            if (active == null) showTerminal = false
        }
        onSessionsChanged()
    }

    // ---- SessionHost ----
    override fun onSessionsChanged() {
        main.post {
            val ctx = getApplication<Application>()
            val n = sessions.count { it.alive }
            val i = Intent(ctx, KeepAliveService::class.java)
            try {
                if (n > 0) { i.putExtra("text", "${n} 件のSSH接続中"); ctx.startForegroundService(i) } else ctx.stopService(i)
            } catch (_: Exception) {}
        }
    }

    override fun onSiteChanged(site: Site) { main.post { persist() } }

    override fun askYesNo(title: String, message: String): Boolean = ask(Ask(title, message, false, false, CompletableFuture())) == "yes"

    override fun askText(message: String, secret: Boolean): String? = ask(Ask("入力", message, true, secret, CompletableFuture()))

    private fun ask(a: Ask): String? {
        main.post { ask = a }
        return try { a.result.get() } catch (_: Exception) { null }
    }

    fun answer(a: Ask, value: String?) { a.result.complete(value); if (ask === a) ask = null }

    // ---- 書き出し・読み込み ----
    fun export(uri: Uri, password: String) {
        Thread {
            try {
                val bytes = Store.export(sites.toList(), password)
                getApplication<Application>().contentResolver.openOutputStream(uri, "wt")!!.use { it.write(bytes) }
                main.post { message = "${sites.size} 件を書き出しました" }
            } catch (e: Exception) { main.post { message = "書き出しに失敗しました: ${e.message}" } }
        }.start()
    }

    fun import(uri: Uri, password: String) {
        Thread {
            try {
                val bytes = getApplication<Application>().contentResolver.openInputStream(uri)!!.use { it.readBytes() }
                val list = Store.import(bytes, password)
                main.post { sites.addAll(list); persist(); message = "${list.size} 件を読み込みました" }
            } catch (e: Exception) { main.post { message = e.message ?: "読み込みに失敗しました" } }
        }.start()
    }

    override fun onCleared() { sessions.forEach { it.close() }; onSessionsChanged() }
}
