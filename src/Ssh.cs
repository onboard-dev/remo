// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Remo
{
    class SshException : Exception { public SshException(string m) : base(m) { } }

    class SshWriter
    {
        MemoryStream ms = new MemoryStream();
        public SshWriter Byte(int b) { ms.WriteByte((byte)b); return this; }
        public SshWriter Bool(bool b) { ms.WriteByte(b ? (byte)1 : (byte)0); return this; }
        public SshWriter U32(uint v)
        {
            ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
            return this;
        }
        public SshWriter Raw(byte[] b) { ms.Write(b, 0, b.Length); return this; }
        public SshWriter Raw(byte[] b, int o, int l) { ms.Write(b, o, l); return this; }
        public SshWriter Str(byte[] b) { U32((uint)b.Length); return Raw(b); }
        public SshWriter Str(byte[] b, int o, int l) { U32((uint)l); return Raw(b, o, l); }
        public SshWriter Str(string s) { return Str(Encoding.UTF8.GetBytes(s)); }
        public SshWriter MPInt(BigInteger n)
        {
            if (n.IsZero) return U32(0);
            byte[] b = n.ToByteArray();
            Array.Reverse((Array)b);
            return Str(b);
        }
        public byte[] ToArray() { return ms.ToArray(); }
    }

    class SshReader
    {
        byte[] d; public int Pos;
        public SshReader(byte[] data, int pos = 0) { d = data; Pos = pos; }
        public int Byte() { if (Pos >= d.Length) throw new SshException("パケット解析エラー"); return d[Pos++]; }
        public bool Bool() { return Byte() != 0; }
        public uint U32()
        {
            if (Pos + 4 > d.Length) throw new SshException("パケット解析エラー");
            uint v = (uint)(d[Pos] << 24 | d[Pos + 1] << 16 | d[Pos + 2] << 8 | d[Pos + 3]);
            Pos += 4; return v;
        }
        public byte[] Str()
        {
            uint l = U32();
            if (l > (uint)(d.Length - Pos)) throw new SshException("パケット解析エラー");
            byte[] r = new byte[l]; Array.Copy(d, Pos, r, 0, l); Pos += (int)l; return r;
        }
        public string StrS() { return Encoding.UTF8.GetString(Str()); }
        public byte[] Bytes(int n) { if (Pos + n > d.Length) throw new SshException("パケット解析エラー"); byte[] r = new byte[n]; Array.Copy(d, Pos, r, 0, n); Pos += n; return r; }
        public BigInteger MPInt() { return Sh.BigBE(Str()); }
        public bool End { get { return Pos >= d.Length; } }
    }

    static class Sh
    {
        public static BigInteger BigBE(byte[] b)
        {
            byte[] t = new byte[b.Length + 1];
            for (int i = 0; i < b.Length; i++) t[i] = b[b.Length - 1 - i];
            return new BigInteger(t);
        }
        public static byte[] Cat(params byte[][] a)
        {
            int n = 0; foreach (var x in a) n += x.Length;
            byte[] r = new byte[n]; int p = 0;
            foreach (var x in a) { Array.Copy(x, 0, r, p, x.Length); p += x.Length; }
            return r;
        }
        public static bool Eq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int r = 0; for (int i = 0; i < a.Length; i++) r |= a[i] ^ b[i];
            return r == 0;
        }
    }

    class AesCtr
    {
        ICryptoTransform enc; byte[] ctr; byte[] ks = new byte[16]; int pos = 16;
        public AesCtr(byte[] key, byte[] iv)
        {
            RijndaelManaged aes = new RijndaelManaged(); aes.BlockSize = 128;
            aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            enc = aes.CreateEncryptor(key, new byte[16]);
            ctr = (byte[])iv.Clone();
        }
        public void Process(byte[] buf, int off, int len)
        {
            for (int i = 0; i < len; i++)
            {
                if (pos == 16)
                {
                    enc.TransformBlock(ctr, 0, 16, ks, 0);
                    for (int j = 15; j >= 0; j--) { if (++ctr[j] != 0) break; }
                    pos = 0;
                }
                buf[off + i] ^= ks[pos++];
            }
        }
    }

    class SshClient
    {
        // ---- 公開イベント ----
        public Action<byte[], int> DataReceived;
        public Action<string> Closed;
        public Func<string, string, bool> HostKeyCheck; // (alg, fingerprint) -> accept
        public string BannerText = "";

        Socket sock; Stream stream;
        string host; int port; string user, pass;
        Thread reader;
        volatile bool closed;
        int cols, rows;

        // transport state
        uint seqIn, seqOut;
        AesCtr encC, decC;
        HMAC macC, macS; bool etmC, etmS; int macCLen, macSLen;
        byte[] sessionId;
        string vC = "SSH-2.0-Remo_1.0", vS;
        readonly object sendLock = new object();
        ManualResetEvent kexDone = new ManualResetEvent(false);
        bool firstKex = true;

        // channel state
        uint remoteChan; uint remoteWindow; uint remoteMax;
        long localWindow; const uint LocalWindowInit = 2 * 1024 * 1024;
        readonly object winLock = new object();
        public bool ChannelOpen;
        ManualResetEvent chanReady = new ManualResetEvent(false);
        int chanStage; // 0=open 1=pty 2=shell 3=running

        public SshClient(string host, int port, string user, string pass, int cols, int rows)
        {
            this.host = host; this.port = port; this.user = user; this.pass = pass; this.cols = cols; this.rows = rows;
        }

        // ---- 接続 ----
        public void Connect(int timeoutMs)
        {
            IPAddress[] addrs = Dns.GetHostAddresses(host);
            Exception last = null;
            foreach (var a in addrs)
            {
                Socket s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var ar = s.BeginConnect(new IPEndPoint(a, port), null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { s.Close(); throw new SshException("接続がタイムアウトしました"); }
                    s.EndConnect(ar);
                    sock = s; break;
                }
                catch (Exception e) { last = e; try { s.Close(); } catch { } }
            }
            if (sock == null) throw (last ?? new SshException("接続できません"));
            sock.NoDelay = true;
            try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
            stream = new NetworkStream(sock, false);

            // バージョン交換
            byte[] vb = Encoding.ASCII.GetBytes(vC + "\r\n");
            stream.Write(vb, 0, vb.Length);
            vS = ReadLine();
            for (int i = 0; i < 50 && !vS.StartsWith("SSH-"); i++) vS = ReadLine();
            if (!vS.StartsWith("SSH-2.0-") && !vS.StartsWith("SSH-1.99-")) throw new SshException("SSH-2.0 非対応のサーバーです: " + vS);

            DoKex(null);
            firstKex = false;
            Authenticate();
            OpenSession();
            chanStage = 3;
            reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Start();
            var ka = new Thread(KeepAliveLoop); ka.IsBackground = true; ka.Start();
        }

        string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new SshException("接続が切断されました");
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 1024) throw new SshException("不正なバージョン文字列");
            }
            return sb.ToString();
        }

        void ReadFull(byte[] b, int off, int n)
        {
            while (n > 0)
            {
                int r = stream.Read(b, off, n);
                if (r <= 0) throw new SshException("接続が切断されました");
                off += r; n -= r;
            }
        }

        // ---- パケット層 ----
        void SendRaw(byte[] payload)
        {
            lock (sendLock)
            {
                int blk = encC != null ? 16 : 8;
                bool etm = macC != null && etmC;
                int lp = etm ? 0 : 4;
                int pad = blk - ((lp + 1 + payload.Length) % blk);
                if (pad < 4) pad += blk;
                int plen = 1 + payload.Length + pad;
                byte[] pk = new byte[4 + plen];
                pk[0] = (byte)(plen >> 24); pk[1] = (byte)(plen >> 16); pk[2] = (byte)(plen >> 8); pk[3] = (byte)plen;
                pk[4] = (byte)pad;
                Array.Copy(payload, 0, pk, 5, payload.Length);
                if (encC != null)
                {
                    byte[] rnd = new byte[pad]; Rng(rnd); Array.Copy(rnd, 0, pk, 5 + payload.Length, pad);
                }
                byte[] mac = null;
                if (encC == null) { }
                else if (etm)
                {
                    encC.Process(pk, 4, plen);
                    mac = Mac(macC, seqOut, pk);
                }
                else
                {
                    mac = Mac(macC, seqOut, pk);
                    encC.Process(pk, 0, pk.Length);
                }
                stream.Write(pk, 0, pk.Length);
                if (mac != null) stream.Write(mac, 0, mac.Length);
                stream.Flush();
                seqOut++;
            }
        }

        static RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
        static void Rng(byte[] b) { lock (rng) rng.GetBytes(b); }

        static byte[] Mac(HMAC h, uint seq, byte[] data)
        {
            byte[] s = new byte[4 + data.Length];
            s[0] = (byte)(seq >> 24); s[1] = (byte)(seq >> 16); s[2] = (byte)(seq >> 8); s[3] = (byte)seq;
            Array.Copy(data, 0, s, 4, data.Length);
            return h.ComputeHash(s);
        }

        // 通常送信: 鍵交換中は待つ
        void Send(byte[] payload)
        {
            while (true)
            {
                if (closed) throw new SshException("切断されています");
                kexDone.WaitOne(500);
                lock (sendLock)
                {
                    if (!kexDone.WaitOne(0)) continue;
                    SendRaw(payload);
                    return;
                }
            }
        }

        byte[] ReadPacket()
        {
            if (decC == null)
            {
                byte[] hd = new byte[4]; ReadFull(hd, 0, 4);
                int len = hd[0] << 24 | hd[1] << 16 | hd[2] << 8 | hd[3];
                if (len < 5 || len > 262144) throw new SshException("不正なパケット長");
                byte[] body = new byte[len]; ReadFull(body, 0, len);
                seqIn++;
                int pad = body[0];
                return Sub(body, 1, len - 1 - pad);
            }
            if (etmS)
            {
                byte[] hd = new byte[4]; ReadFull(hd, 0, 4);
                int len = hd[0] << 24 | hd[1] << 16 | hd[2] << 8 | hd[3];
                if (len < 5 || len > 262144) throw new SshException("不正なパケット長");
                byte[] rest = new byte[len + macSLen]; ReadFull(rest, 0, rest.Length);
                byte[] all = new byte[4 + len]; Array.Copy(hd, 0, all, 0, 4); Array.Copy(rest, 0, all, 4, len);
                byte[] mac = Mac(macS, seqIn, all);
                byte[] got = Sub(rest, len, macSLen);
                if (!Sh.Eq(mac, got)) throw new SshException("MAC検証エラー");
                decC.Process(all, 4, len);
                seqIn++;
                int pad = all[4];
                return Sub(all, 5, len - 1 - pad);
            }
            else
            {
                byte[] first = new byte[16]; ReadFull(first, 0, 16);
                decC.Process(first, 0, 16);
                int len = first[0] << 24 | first[1] << 16 | first[2] << 8 | first[3];
                if (len < 5 || len > 262144 || ((len + 4) % 16) != 0) throw new SshException("不正なパケット長");
                byte[] all = new byte[4 + len];
                Array.Copy(first, 0, all, 0, 16);
                int rem = len + 4 - 16;
                byte[] rest = new byte[rem + macSLen]; ReadFull(rest, 0, rest.Length);
                Array.Copy(rest, 0, all, 16, rem);
                decC.Process(all, 16, rem);
                byte[] mac = Mac(macS, seqIn, all);
                byte[] got = Sub(rest, rem, macSLen);
                if (!Sh.Eq(mac, got)) throw new SshException("MAC検証エラー");
                seqIn++;
                int pad = all[4];
                return Sub(all, 5, len - 1 - pad);
            }
        }

        static byte[] Sub(byte[] a, int o, int l) { byte[] r = new byte[l]; Array.Copy(a, o, r, 0, l); return r; }

        // KEX/認証中に使う: IGNORE/DEBUG/BANNERを読み飛ばして次のメッセージを返す
        byte[] ReadMsg()
        {
            while (true)
            {
                byte[] p = ReadPacket();
                if (p.Length == 0) continue;
                switch (p[0])
                {
                    case 2: case 4: case 7: continue;
                    case 80: HandleGlobal(p); continue;
                    case 1:
                        {
                            var r = new SshReader(p, 1); r.U32(); string d = r.StrS();
                            throw new SshException("サーバーが切断しました: " + d);
                        }
                    case 53:
                        {
                            var r = new SshReader(p, 1); BannerText += r.StrS(); continue;
                        }
                }
                return p;
            }
        }

        // ---- 鍵交換 ----
        static readonly string KexList = "curve25519-sha256,curve25519-sha256@libssh.org,diffie-hellman-group14-sha256,diffie-hellman-group14-sha1";
        static readonly string HostKeyList = "ssh-ed25519,ecdsa-sha2-nistp256,rsa-sha2-512,rsa-sha2-256,ssh-rsa";
        static readonly string CipherList = "aes256-ctr,aes192-ctr,aes128-ctr";
        static readonly string MacList = "hmac-sha2-256-etm@openssh.com,hmac-sha2-512-etm@openssh.com,hmac-sha2-256,hmac-sha2-512,hmac-sha1";

        static string Pick(string mine, string theirs, string what)
        {
            var t = new List<string>(theirs.Split(new char[] { ',' }));
            foreach (var m in mine.Split(new char[] { ',' })) if (t.Contains(m)) return m;
            throw new SshException("共通の" + what + "がありません (サーバー: " + theirs + ")");
        }

        static readonly BigInteger Group14P = BigInteger.Parse("0" +
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
            "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
            "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
            "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
            "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
            "15728E5A8AACAA68FFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);

        void DoKex(byte[] peerInit)
        {
            lock (sendLock) { kexDone.Reset(); }
            // 自分のKEXINIT
            var w = new SshWriter();
            w.Byte(20);
            byte[] cookie = new byte[16]; Rng(cookie); w.Raw(cookie);
            w.Str(KexList).Str(HostKeyList).Str(CipherList).Str(CipherList).Str(MacList).Str(MacList)
             .Str("none").Str("none").Str("").Str("").Bool(false).U32(0);
            byte[] myInit = w.ToArray();
            SendRaw(myInit);
            if (peerInit == null)
            {
                peerInit = ReadMsg();
                if (peerInit[0] != 20) throw new SshException("KEXINITを期待しましたが " + peerInit[0] + " を受信");
            }
            var r = new SshReader(peerInit, 17);
            string[] lists = new string[10];
            for (int i = 0; i < 10; i++) lists[i] = r.StrS();
            string kex = Pick(KexList, lists[0], "鍵交換方式");
            string hka = Pick(HostKeyList, lists[1], "ホスト鍵方式");
            string encCS = Pick(CipherList, lists[2], "暗号方式");
            string encSC = Pick(CipherList, lists[3], "暗号方式");
            string macCS = Pick(MacList, lists[4], "MAC方式");
            string macSC = Pick(MacList, lists[5], "MAC方式");
            Pick("none", lists[6], "圧縮方式");
            Pick("none", lists[7], "圧縮方式");

            bool sha1 = kex.EndsWith("sha1");
            Func<byte[], byte[]> hash = delegate (byte[] d)
            {
                using (HashAlgorithm h = sha1 ? (HashAlgorithm)SHA1.Create() : SHA256.Create()) return h.ComputeHash(d);
            };

            byte[] Ks, sig; byte[] H; BigInteger K;
            if (kex.StartsWith("curve25519"))
            {
                byte[] priv = new byte[32]; Rng(priv);
                byte[] qc = Curve25519.X25519(priv, Curve25519.BasePoint());
                SendRaw(new SshWriter().Byte(30).Str(qc).ToArray());
                byte[] m = ReadMsg();
                if (m[0] != 31) throw new SshException("鍵交換応答エラー(" + m[0] + ")");
                var rr = new SshReader(m, 1);
                Ks = rr.Str(); byte[] qs = rr.Str(); sig = rr.Str();
                if (qs.Length != 32) throw new SshException("不正なサーバー公開鍵");
                byte[] shared = Curve25519.X25519(priv, qs);
                bool allz = true; foreach (var b in shared) if (b != 0) allz = false;
                if (allz) throw new SshException("不正な共有秘密");
                K = Sh.BigBE(shared); // RFC 8731: 32バイトをそのままビッグエンディアン整数として扱う
                H = hash(new SshWriter().Str(vC).Str(vS).Str(myInit).Str(peerInit).Str(Ks).Str(qc).Str(qs).MPInt(K).ToArray());
            }
            else
            {
                byte[] xb = new byte[33]; Rng(xb); xb[32] = 0; xb[31] &= 0x7f;
                BigInteger x = new BigInteger(xb);
                BigInteger e = BigInteger.ModPow(2, x, Group14P);
                SendRaw(new SshWriter().Byte(30).MPInt(e).ToArray());
                byte[] m = ReadMsg();
                if (m[0] != 31) throw new SshException("鍵交換応答エラー(" + m[0] + ")");
                var rr = new SshReader(m, 1);
                Ks = rr.Str(); BigInteger f = rr.MPInt(); sig = rr.Str();
                if (f <= 1 || f >= Group14P - 1) throw new SshException("不正なDH値");
                K = BigInteger.ModPow(f, x, Group14P);
                H = hash(new SshWriter().Str(vC).Str(vS).Str(myInit).Str(peerInit).Str(Ks).MPInt(e).MPInt(f).MPInt(K).ToArray());
            }

            if (sessionId == null) sessionId = H;
            // ホスト鍵の署名検証
            string fp = VerifyHostKey(hka, Ks, sig, H);
            if (firstKex)
            {
                if (HostKeyCheck != null && !HostKeyCheck(hka, fp)) throw new SshException("ホスト鍵が拒否されました");
            }

            SendRaw(new byte[] { 21 });
            // 鍵導出
            int keyLenC = KeyLen(encCS), keyLenS = KeyLen(encSC);
            byte[] ivC = Derive(hash, K, H, 'A', 16), ivS = Derive(hash, K, H, 'B', 16);
            byte[] kC = Derive(hash, K, H, 'C', keyLenC), kS = Derive(hash, K, H, 'D', keyLenS);
            int mkC = MacKeyLen(macCS), mkS = MacKeyLen(macSC);
            byte[] mC = Derive(hash, K, H, 'E', mkC), mS = Derive(hash, K, H, 'F', mkS);
            lock (sendLock)
            {
                encC = new AesCtr(kC, ivC);
                macC = MakeMac(macCS, mC); etmC = macCS.Contains("etm"); macCLen = macC.HashSize / 8;
            }
            byte[] nk = ReadMsg();
            if (nk[0] != 21) throw new SshException("NEWKEYSを期待しましたが " + nk[0] + " を受信");
            decC = new AesCtr(kS, ivS);
            macS = MakeMac(macSC, mS); etmS = macSC.Contains("etm"); macSLen = macS.HashSize / 8;
            lock (sendLock) { kexDone.Set(); }
        }

        static int KeyLen(string c) { return c.StartsWith("aes256") ? 32 : c.StartsWith("aes192") ? 24 : 16; }
        static int MacKeyLen(string m) { return m.Contains("sha2-512") ? 64 : m.Contains("sha2-256") ? 32 : 20; }
        static HMAC MakeMac(string m, byte[] key)
        {
            if (m.Contains("sha2-512")) return new HMACSHA512(key);
            if (m.Contains("sha2-256")) return new HMACSHA256(key);
            return new HMACSHA1(key);
        }

        byte[] Derive(Func<byte[], byte[]> hash, BigInteger K, byte[] H, char x, int len)
        {
            byte[] kb = new SshWriter().MPInt(K).ToArray();
            byte[] res = hash(Sh.Cat(kb, H, new byte[] { (byte)x }, sessionId));
            while (res.Length < len) res = Sh.Cat(res, hash(Sh.Cat(kb, H, res)));
            return Sub(res, 0, len);
        }

        string VerifyHostKey(string alg, byte[] Ks, byte[] sigBlob, byte[] H)
        {
            var kr = new SshReader(Ks);
            string kt = kr.StrS();
            var sr = new SshReader(sigBlob);
            string sa = sr.StrS();
            byte[] sg = sr.Str();
            bool ok = false;
            if (alg == "ssh-ed25519")
            {
                if (kt != "ssh-ed25519" || sa != "ssh-ed25519") throw new SshException("ホスト鍵種別不一致");
                ok = Curve25519.Ed25519Verify(kr.Str(), H, sg);
            }
            else if (alg == "ecdsa-sha2-nistp256")
            {
                if (kt != alg || sa != alg) throw new SshException("ホスト鍵種別不一致");
                kr.StrS(); byte[] q = kr.Str();
                if (q.Length != 65 || q[0] != 4) throw new SshException("不正なECDSA公開鍵");
                var blob = new SshWriter();
                blob.Raw(new byte[] { 0x45, 0x43, 0x53, 0x31, 32, 0, 0, 0 }).Raw(q, 1, 64);
                using (CngKey ck = CngKey.Import(blob.ToArray(), CngKeyBlobFormat.EccPublicBlob))
                using (var ec = new ECDsaCng(ck))
                {
                    var rs = new SshReader(sg);
                    byte[] rb = Fixed(rs.Str(), 32), sb = Fixed(rs.Str(), 32);
                    ec.HashAlgorithm = CngAlgorithm.Sha256;
                    ok = ec.VerifyData(H, Sh.Cat(rb, sb));
                }
            }
            else
            {
                if (kt != "ssh-rsa" || sa != alg) throw new SshException("ホスト鍵種別不一致");
                BigInteger e = kr.MPInt(), n = kr.MPInt();
                var prm = new RSAParameters();
                prm.Exponent = Unsigned(e); prm.Modulus = Unsigned(n);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(prm);
                    string hn = alg == "rsa-sha2-512" ? "SHA512" : alg == "rsa-sha2-256" ? "SHA256" : "SHA1";
                    ok = rsa.VerifyData(H, CryptoConfig.MapNameToOID(hn), sg);
                }
            }
            if (!ok) throw new SshException("ホスト鍵の署名検証に失敗しました");
            using (var sha = SHA256.Create())
                return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(Ks)).TrimEnd(new char[] { '=' });
        }

        static byte[] Unsigned(BigInteger v)
        {
            byte[] b = v.ToByteArray(); Array.Reverse((Array)b);
            int i = 0; while (i < b.Length - 1 && b[i] == 0) i++;
            return Sub(b, i, b.Length - i);
        }
        static byte[] Fixed(byte[] b, int n)
        {
            int i = 0; while (i < b.Length && b[i] == 0) i++;
            int l = b.Length - i;
            if (l > n) throw new SshException("不正な署名");
            byte[] r = new byte[n]; Array.Copy(b, i, r, n - l, l); return r;
        }

        // ---- 認証 ----
        void Authenticate()
        {
            SendRaw(new SshWriter().Byte(5).Str("ssh-userauth").ToArray());
            byte[] m = ReadMsg();
            if (m[0] != 6) throw new SshException("ssh-userauthが使用できません");

            SendRaw(new SshWriter().Byte(50).Str(user).Str("ssh-connection").Str("password").Bool(false).Str(pass).ToArray());
            string methods = "";
            while (true)
            {
                m = ReadMsg();
                if (m[0] == 52) return;
                if (m[0] == 51) { var r = new SshReader(m, 1); methods = r.StrS(); break; }
                if (m[0] == 60) throw new SshException("パスワードの変更が必要です。サーバー側で変更してください");
                throw new SshException("認証応答エラー(" + m[0] + ")");
            }
            if (methods.Split(new char[] { ',' }).Length > 0 && Array.IndexOf(methods.Split(new char[] { ',' }), "keyboard-interactive") >= 0)
            {
                SendRaw(new SshWriter().Byte(50).Str(user).Str("ssh-connection").Str("keyboard-interactive").Str("").Str("").ToArray());
                int guard = 0;
                while (guard++ < 10)
                {
                    m = ReadMsg();
                    if (m[0] == 52) return;
                    if (m[0] == 51) break;
                    if (m[0] == 60)
                    {
                        var r = new SshReader(m, 1);
                        r.Str(); r.Str(); r.Str();
                        uint n = r.U32();
                        var w = new SshWriter(); w.Byte(61).U32(n);
                        for (uint i = 0; i < n; i++) { r.Str(); r.Bool(); w.Str(pass); }
                        SendRaw(w.ToArray());
                        continue;
                    }
                    break;
                }
            }
            throw new SshException("認証に失敗しました (ユーザー名またはパスワードを確認してください)");
        }

        // ---- チャンネル ----
        void OpenSession()
        {
            localWindow = LocalWindowInit;
            SendRaw(new SshWriter().Byte(90).Str("session").U32(0).U32(LocalWindowInit).U32(32768).ToArray());
            byte[] m = ReadMsg();
            if (m[0] == 92) throw new SshException("チャンネルを開けませんでした");
            if (m[0] != 91) throw new SshException("チャンネル応答エラー(" + m[0] + ")");
            var r = new SshReader(m, 1);
            r.U32(); remoteChan = r.U32(); remoteWindow = r.U32(); remoteMax = r.U32();
            if (remoteMax > 65536) remoteMax = 65536;
            if (remoteMax < 512) remoteMax = 512;

            var w = new SshWriter();
            w.Byte(98).U32(remoteChan).Str("pty-req").Bool(true).Str("xterm-256color").U32((uint)cols).U32((uint)rows).U32(0).U32(0);
            // 端末モード: ERASE=DEL, 最後にEND
            w.Str(new byte[] { 3, 0, 0, 0, 127, 0 });
            SendRaw(w.ToArray());
            WaitReply("pty-req");
            SendRaw(new SshWriter().Byte(98).U32(remoteChan).Str("shell").Bool(true).ToArray());
            WaitReply("shell");
            ChannelOpen = true;
        }

        void WaitReply(string what)
        {
            while (true)
            {
                byte[] m = ReadMsg();
                if (m[0] == 99) return;
                if (m[0] == 100) throw new SshException("サーバーが " + what + " を拒否しました");
                if (m[0] == 93) { var r = new SshReader(m, 1); r.U32(); remoteWindow += r.U32(); continue; }
                if (m[0] == 80) { HandleGlobal(m); continue; }
                if (m[0] == 94 || m[0] == 95) { HandleData(m); continue; }
                // その他は無視
            }
        }

        List<byte[]> earlyData = new List<byte[]>();

        void HandleGlobal(byte[] m)
        {
            var r = new SshReader(m, 1); r.StrS(); bool want = r.Bool();
            if (want) SendRaw(new byte[] { 82 });
        }

        void HandleData(byte[] m)
        {
            var r = new SshReader(m, 1);
            r.U32();
            if (m[0] == 95) r.U32();
            byte[] d = r.Str();
            if (chanStage < 3)
            {
                earlyData.Add(d);
            }
            else Deliver(d);
            lock (winLock) { localWindow -= d.Length; }
            if (localWindow < LocalWindowInit / 2)
            {
                uint add = (uint)(LocalWindowInit - localWindow);
                lock (winLock) { localWindow += add; }
                Send(new SshWriter().Byte(93).U32(remoteChan).U32(add).ToArray());
            }
        }

        void Deliver(byte[] d)
        {
            var cb = DataReceived;
            if (cb != null) cb(d, d.Length);
        }

        void ReadLoop()
        {
            string reason = "切断されました";
            try
            {
                foreach (var d in earlyData) Deliver(d);
                earlyData.Clear();
                while (!closed)
                {
                    byte[] m = ReadPacket();
                    if (m.Length == 0) continue;
                    switch (m[0])
                    {
                        case 1: { var r = new SshReader(m, 1); r.U32(); reason = "サーバーが切断しました: " + r.StrS(); closed = true; break; }
                        case 2: case 4: case 7: case 53: break;
                        case 20: DoKex(m); break;
                        case 80: HandleGlobalLoop(m); break;
                        case 93:
                            {
                                var r = new SshReader(m, 1); r.U32(); uint a = r.U32();
                                lock (winLock) { remoteWindow += a; Monitor.PulseAll(winLock); }
                                break;
                            }
                        case 94: case 95: HandleData(m); break;
                        case 96: break;
                        case 97:
                            reason = "サーバー側でセッションが終了しました"; closed = true; break;
                        case 98:
                            {
                                var r = new SshReader(m, 1); r.U32(); r.StrS(); bool want = r.Bool();
                                if (want) Send(new SshWriter().Byte(100).U32(remoteChan).ToArray());
                                break;
                            }
                        default: break;
                    }
                }
            }
            catch (Exception e)
            {
                if (!closed) reason = e is SshException ? e.Message : "通信エラー: " + e.Message;
                else reason = "切断しました";
            }
            closed = true;
            lock (winLock) { Monitor.PulseAll(winLock); }
            Cleanup();
            var cb = Closed; if (cb != null) cb(reason);
        }

        void HandleGlobalLoop(byte[] m)
        {
            var r = new SshReader(m, 1); r.StrS(); bool want = r.Bool();
            if (want) Send(new byte[] { 82 });
        }

        void KeepAliveLoop()
        {
            int n = 0;
            while (!closed)
            {
                Thread.Sleep(1000);
                if (++n < 30) continue;
                n = 0;
                try { Send(new SshWriter().Byte(80).Str("keepalive@openssh.com").Bool(true).ToArray()); } catch { return; }
            }
        }

        // ---- 公開操作 ----
        public void Write(byte[] data, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n;
                lock (winLock)
                {
                    while (remoteWindow == 0 && !closed) Monitor.Wait(winLock, 500);
                    if (closed) return;
                    n = (int)Math.Min((long)len - off, Math.Min(remoteWindow, remoteMax));
                    remoteWindow -= (uint)n;
                }
                Send(new SshWriter().Byte(94).U32(remoteChan).Str(data, off, n).ToArray());
                off += n;
            }
        }

        public void Resize(int c, int r)
        {
            cols = c; rows = r;
            if (!ChannelOpen || closed) return;
            try { Send(new SshWriter().Byte(98).U32(remoteChan).Str("window-change").Bool(false).U32((uint)c).U32((uint)r).U32(0).U32(0).ToArray()); } catch { }
        }

        public bool IsClosed { get { return closed; } }

        public void Disconnect()
        {
            if (closed) { Cleanup(); return; }
            try
            {
                if (ChannelOpen) { try { SendRaw(new SshWriter().Byte(97).U32(remoteChan).ToArray()); } catch { } }
                try { SendRaw(new SshWriter().Byte(1).U32(11).Str("bye").Str("").ToArray()); } catch { }
            }
            catch { }
            closed = true;
            Cleanup();
        }

        void Cleanup()
        {
            try { if (sock != null) sock.Close(); } catch { }
        }
    }
}
