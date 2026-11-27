// Remo for Android - ported from Remo (Windows) Store.cs
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
package dev.remo.android

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.SecretKeyFactory
import javax.crypto.KeyGenerator
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

class Site(
    var name: String = "",
    var host: String = "",
    var user: String = "",
    var pass: String = "",
    var icon: String = "🖥️",
    var hostKey: String = "",
    var lang: String = DEFAULT_LANG,
    var enc: Int = 0 // 0=UTF-8 1=Shift_JIS
) {
    val port: Int
        get() {
            val i = host.lastIndexOf(':')
            if (i > 0 && !host.endsWith("]")) host.substring(i + 1).trim().toIntOrNull()?.let { return it }
            return 22
        }
    val hostOnly: String
        get() {
            var h = host.trim()
            val i = h.lastIndexOf(':')
            if (i > 0 && h.indexOf(':') == i && h.substring(i + 1).toIntOrNull() != null) h = h.substring(0, i)
            if (h.startsWith("[") && h.endsWith("]")) h = h.substring(1, h.length - 1)
            return h
        }

    fun copy() = Site(name, host, user, pass, icon, hostKey, lang, enc)

    companion object { const val DEFAULT_LANG = "C.UTF-8" }
}

class AppData {
    val sites = ArrayList<Site>()
    var fontSp = 12f
    var sideCollapsed = false
}

/** 接続情報は Android Keystore の AES-GCM 鍵(端末固有)で暗号化して保存する。 */
object Store {
    private const val ALIAS = "remo.store.v1"
    private const val FILE = "remo.dat"
    @JvmField var loadError: String? = null

    private fun e(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")
    private fun u(s: String) = URLDecoder.decode(s.replace("+", "%2B"), "UTF-8")

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).build()
        )
        return g.generateKey()
    }

    private fun parseSite(f: List<String>): Site = Site(
        name = u(f[1]), host = u(f[2]), user = u(f[3]), pass = u(f[4]),
        enc = f[5].toIntOrNull() ?: 0, icon = u(f[6]), hostKey = u(f[7]),
        lang = if (f.size >= 9) u(f[8]) else Site.DEFAULT_LANG
    )

    private fun siteLine(s: Site) =
        "S\t${e(s.name)}\t${e(s.host)}\t${e(s.user)}\t${e(s.pass)}\t${s.enc}\t${e(s.icon)}\t${e(s.hostKey)}\t${e(s.lang)}\n"

    fun load(ctx: Context): AppData {
        val d = AppData()
        val file = File(ctx.filesDir, FILE)
        try {
            if (!file.exists()) return d
            val all = file.readBytes()
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, all.copyOfRange(0, 12)))
            val text = String(c.doFinal(all, 12, all.size - 12), Charsets.UTF_8)
            for (line in text.split('\n')) {
                val l = line.trimEnd('\r')
                if (l.isEmpty()) continue
                val f = l.split('\t')
                if (f[0] == "S" && f.size >= 8) d.sites.add(parseSite(f))
                else if (f[0] == "A" && f.size >= 3) {
                    d.fontSp = f[1].toFloatOrNull()?.coerceIn(6f, 40f) ?: 12f
                    d.sideCollapsed = f[2] == "1"
                }
            }
        } catch (ex: Exception) {
            loadError = ex.message
            try { file.renameTo(File(ctx.filesDir, "$FILE.broken-${System.currentTimeMillis()}")) } catch (_: Exception) {}
            d.sites.clear()
        }
        return d
    }

    fun save(ctx: Context, d: AppData) {
        val sb = StringBuilder("REMOA1\n")
        for (s in d.sites) sb.append(siteLine(s))
        sb.append("A\t${d.fontSp}\t${if (d.sideCollapsed) "1" else "0"}\n")
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val ct = c.doFinal(sb.toString().toByteArray(Charsets.UTF_8))
        val tmp = File(ctx.filesDir, "$FILE.tmp")
        tmp.writeBytes(c.iv + ct)
        val dst = File(ctx.filesDir, FILE)
        if (!tmp.renameTo(dst)) { dst.delete(); tmp.renameTo(dst) }
    }

    // ---- 書き出し / 読み込み (Windows 版 Remo と互換) ----
    // 形式: "REMOEXP1"(8) + salt(16) + iv(16) + 暗号文(AES-256-CBC/PKCS7) + HMAC-SHA256(32)
    private val MAGIC = "REMOEXP1".toByteArray(Charsets.US_ASCII)
    private const val ITER = 200000

    private fun derive(password: String, salt: ByteArray): Pair<ByteArray, ByteArray> {
        val f = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA1")
        val k = f.generateSecret(PBEKeySpec(password.toCharArray(), salt, ITER, 512)).encoded
        return k.copyOfRange(0, 32) to k.copyOfRange(32, 64)
    }

    private fun hmac(key: ByteArray, data: ByteArray, off: Int, len: Int): ByteArray {
        val m = Mac.getInstance("HmacSHA256"); m.init(SecretKeySpec(key, "HmacSHA256")); m.update(data, off, len); return m.doFinal()
    }

    fun export(sites: List<Site>, password: String): ByteArray {
        val sb = StringBuilder("REMOX1\n")
        for (s in sites) sb.append(siteLine(s))
        val plain = sb.toString().toByteArray(Charsets.UTF_8)
        val rng = SecureRandom()
        val salt = ByteArray(16).also { rng.nextBytes(it) }
        val iv = ByteArray(16).also { rng.nextBytes(it) }
        val (ek, mk) = derive(password, salt)
        val c = Cipher.getInstance("AES/CBC/PKCS5Padding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(ek, "AES"), IvParameterSpec(iv))
        val ct = c.doFinal(plain)
        val bo = ByteArrayOutputStream()
        bo.write(MAGIC); bo.write(salt); bo.write(iv); bo.write(ct)
        val body = bo.toByteArray()
        bo.write(hmac(mk, body, 0, body.size))
        return bo.toByteArray()
    }

    /** 失敗時は例外(メッセージは利用者向け) */
    fun import(all: ByteArray, password: String): List<Site> {
        if (all.size < 8 + 16 + 16 + 16 + 32) throw Exception("Remoの書き出しファイルではありません。")
        for (i in 0 until 8) if (all[i] != MAGIC[i]) throw Exception("Remoの書き出しファイルではありません。")
        val salt = all.copyOfRange(8, 24); val iv = all.copyOfRange(24, 40)
        val ctLen = all.size - 40 - 32
        val (ek, mk) = derive(password, salt)
        val mac = hmac(mk, all, 0, all.size - 32)
        if (!MessageDigest.isEqual(mac, all.copyOfRange(all.size - 32, all.size))) throw Exception("パスワードが違うか、ファイルが壊れています。")
        val c = Cipher.getInstance("AES/CBC/PKCS5Padding")
        c.init(Cipher.DECRYPT_MODE, SecretKeySpec(ek, "AES"), IvParameterSpec(iv))
        val plain = c.doFinal(all, 40, ctLen)
        val list = ArrayList<Site>()
        for (line in String(plain, Charsets.UTF_8).split('\n')) {
            val f = line.trimEnd('\r').split('\t')
            if (f[0] == "S" && f.size >= 8) list.add(parseSite(f))
        }
        return list
    }
}
