// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Remo
{
    class Site
    {
        public string Name = "", Host = "", User = "", Pass = "", Icon = "🖥️", HostKey = "";
        public int Enc; // 0=UTF-8 1=Shift_JIS
        public int Port
        {
            get { int p; int i = Host.LastIndexOf(':'); if (i > 0 && !Host.EndsWith("]") && int.TryParse(Host.Substring(i + 1), out p)) return p; return 22; }
        }
        public string HostOnly
        {
            get
            {
                string h = Host.Trim(); int i = h.LastIndexOf(':'); int p;
                if (i > 0 && h.IndexOf(':') == i && int.TryParse(h.Substring(i + 1), out p)) h = h.Substring(0, i);
                if (h.StartsWith("[") && h.EndsWith("]")) h = h.Substring(1, h.Length - 2);
                return h;
            }
        }
        public Site Clone() { return (Site)MemberwiseClone(); }
    }

    class AppData
    {
        public List<Site> Sites = new List<Site>();
        public int WinW, WinH;
        public float FontPt = 11f;
        public string FontName = "";
        public bool SideCollapsed;
    }

    // 接続情報はWindowsのDPAPI(ユーザー単位)で暗号化して保存する。マスターパスワードは不要。
    static class Store
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Remo.SSH.v1");
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Remo"); } }
        public static string FilePath { get { return Path.Combine(Dir, "remo.dat"); } }
        public static string LoadError;

        static string E(string s) { return Uri.EscapeDataString(s ?? ""); }
        static string U(string s) { return Uri.UnescapeDataString(s ?? ""); }

        public static AppData Load()
        {
            AppData d = new AppData();
            try
            {
                if (!File.Exists(FilePath)) return d;
                byte[] enc = File.ReadAllBytes(FilePath);
                byte[] plain = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
                string text = Encoding.UTF8.GetString(plain);
                foreach (string line in text.Split(new char[] { '\n' }))
                {
                    string l = line.TrimEnd(new char[] { '\r' });
                    if (l.Length == 0) continue;
                    string[] f = l.Split(new char[] { '\t' });
                    if (f[0] == "S" && f.Length >= 8)
                    {
                        Site s = new Site();
                        s.Name = U(f[1]); s.Host = U(f[2]); s.User = U(f[3]); s.Pass = U(f[4]);
                        int.TryParse(f[5], out s.Enc); s.Icon = U(f[6]); s.HostKey = U(f[7]);
                        d.Sites.Add(s);
                    }
                    else if (f[0] == "W" && f.Length >= 4)
                    {
                        int.TryParse(f[1], out d.WinW); int.TryParse(f[2], out d.WinH);
                        float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d.FontPt);
                        if (d.FontPt < 7 || d.FontPt > 36) d.FontPt = 11;
                        if (f.Length >= 5) d.FontName = U(f[4]);
                        if (f.Length >= 6) d.SideCollapsed = f[5] == "1";
                        else d.FontPt = 11; // 旧形式: 既定値11ptへ
                    }
                }
            }
            catch (Exception e)
            {
                LoadError = e.Message;
                try { File.Move(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
                d = new AppData();
            }
            return d;
        }

        static string SiteLine(Site s)
        {
            return "S\t" + E(s.Name) + "\t" + E(s.Host) + "\t" + E(s.User) + "\t" + E(s.Pass) + "\t" + s.Enc + "\t" + E(s.Icon) + "\t" + E(s.HostKey) + "\n";
        }

        // ---- 書き出し / 読み込み (パスワードで暗号化した持ち運び用ファイル) ----
        // 形式: "REMOEXP1"(8) + salt(16) + iv(16) + 暗号文(AES-256-CBC) + HMAC-SHA256(32)
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("REMOEXP1");
        const int Iter = 200000;

        static void DeriveKeys(string password, byte[] salt, out byte[] encKey, out byte[] macKey)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iter))
            {
                byte[] k = kdf.GetBytes(64);
                encKey = new byte[32]; macKey = new byte[32];
                Array.Copy(k, 0, encKey, 0, 32); Array.Copy(k, 32, macKey, 0, 32);
            }
        }

        public static void Export(string path, List<Site> sites, string password)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("REMOX1\n");
            foreach (Site s in sites) sb.Append(SiteLine(s));
            byte[] plain = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] salt = new byte[16], iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) { rng.GetBytes(salt); rng.GetBytes(iv); }
            byte[] ek, mk; DeriveKeys(password, salt, out ek, out mk);
            byte[] ct;
            using (var aes = new RijndaelManaged())
            {
                aes.BlockSize = 128; aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var enc = aes.CreateEncryptor(ek, iv)) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            MemoryStream ms = new MemoryStream();
            ms.Write(Magic, 0, Magic.Length); ms.Write(salt, 0, 16); ms.Write(iv, 0, 16); ms.Write(ct, 0, ct.Length);
            byte[] body = ms.ToArray();
            byte[] mac; using (var h = new HMACSHA256(mk)) mac = h.ComputeHash(body);
            ms.Write(mac, 0, mac.Length);
            File.WriteAllBytes(path, ms.ToArray());
        }

        // 失敗時は例外(メッセージは利用者向け)
        public static List<Site> Import(string path, string password)
        {
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < 8 + 16 + 16 + 16 + 32) throw new Exception("Remoの書き出しファイルではありません。");
            for (int i = 0; i < 8; i++) if (all[i] != Magic[i]) throw new Exception("Remoの書き出しファイルではありません。");
            byte[] salt = new byte[16], iv = new byte[16];
            Array.Copy(all, 8, salt, 0, 16); Array.Copy(all, 24, iv, 0, 16);
            int ctLen = all.Length - 40 - 32;
            byte[] ek, mk; DeriveKeys(password, salt, out ek, out mk);
            byte[] mac; using (var h = new HMACSHA256(mk)) mac = h.ComputeHash(all, 0, all.Length - 32);
            int diff = 0; for (int i = 0; i < 32; i++) diff |= mac[i] ^ all[all.Length - 32 + i];
            if (diff != 0) throw new Exception("パスワードが違うか、ファイルが壊れています。");
            byte[] plain;
            using (var aes = new RijndaelManaged())
            {
                aes.BlockSize = 128; aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var dec = aes.CreateDecryptor(ek, iv)) plain = dec.TransformFinalBlock(all, 40, ctLen);
            }
            List<Site> list = new List<Site>();
            foreach (string line in Encoding.UTF8.GetString(plain).Split(new char[] { '\n' }))
            {
                string[] f = line.TrimEnd(new char[] { '\r' }).Split(new char[] { '\t' });
                if (f[0] == "S" && f.Length >= 8)
                {
                    Site s = new Site();
                    s.Name = U(f[1]); s.Host = U(f[2]); s.User = U(f[3]); s.Pass = U(f[4]);
                    int.TryParse(f[5], out s.Enc); s.Icon = U(f[6]); s.HostKey = U(f[7]);
                    list.Add(s);
                }
            }
            return list;
        }

        public static void Save(AppData d)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("REMO1\n");
                foreach (Site s in d.Sites) sb.Append(SiteLine(s));
                sb.Append("W\t" + d.WinW + "\t" + d.WinH + "\t" + d.FontPt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + E(d.FontName) + "\t" + (d.SideCollapsed ? "1" : "0") + "\n");
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(sb.ToString()), Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, enc);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("設定の保存に失敗しました: " + e.Message, "Remo");
            }
        }
    }
}
