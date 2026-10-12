// Remo - simple SSH client for Windows
// Copyright (C) 2026 onboard-dev
// SPDX-License-Identifier: GPL-3.0-or-later
// This program comes with ABSOLUTELY NO WARRANTY. See LICENSE for details.

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Remo
{
    // X25519 (RFC 7748) と Ed25519 署名検証 (RFC 8032) の最小実装。BigIntegerのみ使用。
    static class Curve25519
    {
        static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        static readonly BigInteger D;
        static readonly BigInteger SqrtM1;
        static readonly BigInteger[] Base;

        static BigInteger Mod(BigInteger a) { a %= P; return a.Sign < 0 ? a + P : a; }
        static BigInteger Inv(BigInteger a) { return BigInteger.ModPow(Mod(a), P - 2, P); }
        static BigInteger FromLE(byte[] b, int off, int len)
        {
            byte[] t = new byte[len + 1];
            Array.Copy(b, off, t, 0, len);
            return new BigInteger(t);
        }
        static byte[] ToLE(BigInteger v, int len)
        {
            byte[] b = v.ToByteArray();
            byte[] r = new byte[len];
            Array.Copy(b, 0, r, 0, Math.Min(len, b.Length));
            return r;
        }

        static Curve25519()
        {
            D = Mod(-121665 * Inv(121666));
            SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
            BigInteger by = Mod(4 * Inv(5));
            BigInteger bx;
            RecoverX(by, 0, out bx);
            Base = new BigInteger[] { bx, by, 1, Mod(bx * by) };
        }

        public static byte[] X25519(byte[] k, byte[] u)
        {
            byte[] kk = (byte[])k.Clone();
            kk[0] &= 248; kk[31] &= 127; kk[31] |= 64;
            byte[] uu = (byte[])u.Clone();
            uu[31] &= 127;
            BigInteger x1 = Mod(FromLE(uu, 0, 32));
            BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
            int swap = 0;
            for (int t = 254; t >= 0; t--)
            {
                int kt = (kk[t >> 3] >> (t & 7)) & 1;
                swap ^= kt;
                if (swap != 0) { var a = x2; x2 = x3; x3 = a; a = z2; z2 = z3; z3 = a; }
                swap = kt;
                BigInteger A = (x2 + z2) % P, AA = A * A % P;
                BigInteger B = Mod(x2 - z2), BB = B * B % P;
                BigInteger E = Mod(AA - BB);
                BigInteger C = (x3 + z3) % P, Dd = Mod(x3 - z3);
                BigInteger DA = Dd * A % P, CB = C * B % P;
                x3 = (DA + CB) % P; x3 = x3 * x3 % P;
                z3 = Mod(DA - CB); z3 = z3 * z3 % P * x1 % P;
                x2 = AA * BB % P;
                z2 = E * ((AA + 121665 * E) % P) % P;
            }
            if (swap != 0) { var a = x2; x2 = x3; x3 = a; a = z2; z2 = z3; z3 = a; }
            BigInteger r = x2 * Inv(z2) % P;
            return ToLE(r, 32);
        }

        public static byte[] BasePoint()
        {
            byte[] b = new byte[32]; b[0] = 9; return b;
        }

        static bool RecoverX(BigInteger y, int sign, out BigInteger x)
        {
            x = 0;
            if (y >= P) return false;
            BigInteger y2 = y * y % P;
            BigInteger x2 = Mod((y2 - 1) * Inv(D * y2 + 1));
            if (x2.IsZero)
            {
                if (sign != 0) return false;
                x = 0; return true;
            }
            BigInteger xx = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (Mod(xx * xx - x2) != 0) xx = xx * SqrtM1 % P;
            if (Mod(xx * xx - x2) != 0) return false;
            if ((int)(xx & 1) != sign) xx = P - xx;
            x = xx;
            return true;
        }

        static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
        {
            BigInteger A = Mod((p[1] - p[0]) * (q[1] - q[0]));
            BigInteger B = Mod((p[1] + p[0]) * (q[1] + q[0]));
            BigInteger C = Mod(2 * p[3] * q[3] % P * D);
            BigInteger Dd = Mod(2 * p[2] * q[2]);
            BigInteger E = B - A, F = Dd - C, G = Dd + C, H = B + A;
            return new BigInteger[] { Mod(E * F), Mod(G * H), Mod(F * G), Mod(E * H) };
        }

        static BigInteger[] Mul(BigInteger s, BigInteger[] p)
        {
            BigInteger[] q = new BigInteger[] { 0, 1, 1, 0 };
            BigInteger[] cur = p;
            while (s > 0)
            {
                if (!s.IsEven) q = Add(q, cur);
                cur = Add(cur, cur);
                s >>= 1;
            }
            return q;
        }

        static bool Eq(BigInteger[] p, BigInteger[] q)
        {
            if (Mod(p[0] * q[2] - q[0] * p[2]) != 0) return false;
            if (Mod(p[1] * q[2] - q[1] * p[2]) != 0) return false;
            return true;
        }

        static BigInteger[] Decode(byte[] s, int off)
        {
            byte[] t = new byte[32];
            Array.Copy(s, off, t, 0, 32);
            int sign = (t[31] >> 7) & 1;
            t[31] &= 127;
            BigInteger y = FromLE(t, 0, 32);
            BigInteger x;
            if (!RecoverX(y, sign, out x)) return null;
            return new BigInteger[] { x, y, 1, Mod(x * y) };
        }

        public static bool Ed25519Verify(byte[] pub, byte[] msg, byte[] sig)
        {
            if (pub.Length != 32 || sig.Length != 64) return false;
            BigInteger[] A = Decode(pub, 0);
            BigInteger[] R = Decode(sig, 0);
            if (A == null || R == null) return false;
            BigInteger S = FromLE(sig, 32, 32);
            if (S >= L) return false;
            byte[] hin = new byte[64 + msg.Length];
            Array.Copy(sig, 0, hin, 0, 32);
            Array.Copy(pub, 0, hin, 32, 32);
            Array.Copy(msg, 0, hin, 64, msg.Length);
            byte[] hh;
            using (var sha = SHA512.Create()) hh = sha.ComputeHash(hin);
            BigInteger h = FromLE(hh, 0, 64) % L;
            BigInteger[] left = Mul(S, Base);
            BigInteger[] right = Add(R, Mul(h, A));
            return Eq(left, right);
        }
    }
}
