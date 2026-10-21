using System.Text;

namespace AfifPbx.Gsm;

public sealed record IncomingSms(string Sender, string Text, DateTime? Timestamp, int? ConcatRef, int ConcatTotal, int ConcatSeq);

/// <summary>
/// SMS PDU encoding/decoding (3GPP TS 23.040). Handles GSM 7-bit and UCS-2 (Arabic and other
/// scripts), multipart messages, and the SMSC prefix the modem puts in front.
/// </summary>
public static class SmsPdu
{
    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001bÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7ExtChars = "^{}\\[~]|€";
    private static readonly byte[] Gsm7ExtCodes = { 0x14, 0x28, 0x29, 0x2F, 0x3C, 0x3D, 0x3E, 0x40, 0x65 };
    private static readonly Random Rnd = new();

    public static bool IsGsm7(string text) =>
        text.All(c => Gsm7Basic.IndexOf(c) >= 0 || Gsm7ExtChars.IndexOf(c) >= 0);

    // ------------------------------------------------------------------ encode (SMS-SUBMIT)

    /// <summary>
    /// Build one or more SMS-SUBMIT PDUs. Returns (pdu hex including a "00" SMSC field, TPDU length for AT+CMGS).
    /// </summary>
    public static List<(string Pdu, int TpduLength)> EncodeSubmit(string number, string text)
    {
        bool gsm7 = IsGsm7(text);
        var parts = Split(text, gsm7);
        int reference = Rnd.Next(1, 255);
        var result = new List<(string, int)>();
        for (int i = 0; i < parts.Count; i++)
        {
            var tp = new List<byte>();
            bool multi = parts.Count > 1;
            tp.Add((byte)(0x01 | (multi ? 0x40 : 0x00)));     // SMS-SUBMIT, UDHI if multipart
            tp.Add(0x00);                                    // TP-MR (set by modem)
            tp.AddRange(EncodeAddress(number));
            tp.Add(0x00);                                    // TP-PID
            tp.Add(gsm7 ? (byte)0x00 : (byte)0x08);          // TP-DCS
            byte[] udh = multi ? new byte[] { 0x05, 0x00, 0x03, (byte)reference, (byte)parts.Count, (byte)(i + 1) } : Array.Empty<byte>();
            if (gsm7)
            {
                var septets = ToSeptets(parts[i]);
                int udhSeptets = udh.Length == 0 ? 0 : (udh.Length * 8 + 6) / 7;
                tp.Add((byte)(udhSeptets + septets.Count));
                tp.AddRange(PackSeptets(septets, udh));
            }
            else
            {
                var ucs = Encoding.BigEndianUnicode.GetBytes(parts[i]);
                tp.Add((byte)(udh.Length + ucs.Length));
                tp.AddRange(udh);
                tp.AddRange(ucs);
            }
            string hex = "00" + Convert.ToHexString(tp.ToArray());
            result.Add((hex, tp.Count));
        }
        return result;
    }

    private static List<string> Split(string text, bool gsm7)
    {
        // limits: single 160 septets / 70 UCS-2 chars; multipart 153 / 67
        if (gsm7)
        {
            if (ToSeptets(text).Count <= 160) return new() { text };
            var parts = new List<string>();
            var sb = new StringBuilder();
            int len = 0;
            foreach (char c in text)
            {
                int w = Gsm7ExtChars.IndexOf(c) >= 0 ? 2 : 1;
                if (len + w > 153) { parts.Add(sb.ToString()); sb.Clear(); len = 0; }
                sb.Append(c); len += w;
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }
        if (text.Length <= 70) return new() { text };
        var list = new List<string>();
        for (int i = 0; i < text.Length; i += 67)
        {
            int n = Math.Min(67, text.Length - i);
            // don't split a surrogate pair
            if (n == 67 && char.IsHighSurrogate(text[i + n - 1])) n--;
            list.Add(text.Substring(i, n));
            if (n == 66) i--;
        }
        return list;
    }

    private static byte[] EncodeAddress(string number)
    {
        string n = new(number.Where(c => char.IsDigit(c) || c == '+').ToArray());
        bool intl = n.StartsWith('+');
        if (intl) n = n[1..];
        var b = new List<byte> { (byte)n.Length, (byte)(intl ? 0x91 : 0x81) };
        b.AddRange(SemiOctets(n));
        return b.ToArray();
    }

    private static byte[] SemiOctets(string digits)
    {
        if (digits.Length % 2 == 1) digits += "F";
        var r = new byte[digits.Length / 2];
        for (int i = 0; i < r.Length; i++)
            r[i] = (byte)((Hex(digits[2 * i + 1]) << 4) | Hex(digits[2 * i]));
        return r;
    }

    private static int Hex(char c) => c is 'F' or 'f' ? 0xF : c - '0';

    private static List<byte> ToSeptets(string text)
    {
        var s = new List<byte>();
        foreach (char c in text)
        {
            int i = Gsm7Basic.IndexOf(c);
            if (i >= 0) { s.Add((byte)i); continue; }
            int e = Gsm7ExtChars.IndexOf(c);
            if (e >= 0) { s.Add(0x1B); s.Add(Gsm7ExtCodes[e]); continue; }
            s.Add(0x3F); // '?'
        }
        return s;
    }

    private static byte[] PackSeptets(List<byte> septets, byte[] udh)
    {
        // UDH occupies whole octets, followed by fill bits so the text starts on a septet boundary
        int udhBits = udh.Length * 8;
        int fill = udh.Length == 0 ? 0 : (7 - udhBits % 7) % 7;
        int totalBits = udhBits + fill + septets.Count * 7;
        var outp = new byte[(totalBits + 7) / 8];
        Array.Copy(udh, outp, udh.Length);
        int bit = udhBits + fill;
        foreach (var s in septets)
        {
            for (int k = 0; k < 7; k++, bit++)
                if ((s & (1 << k)) != 0) outp[bit / 8] |= (byte)(1 << (bit % 8));
        }
        return outp;
    }

    // ------------------------------------------------------------------ decode (SMS-DELIVER)

    public static IncomingSms DecodeDeliver(string hex)
    {
        var b = Convert.FromHexString(hex.Trim());
        int p = 0;
        int smscLen = b[p++];
        p += smscLen;
        byte first = b[p++];
        bool udhi = (first & 0x40) != 0;
        int mti = first & 0x03;
        if (mti != 0) throw new FormatException("Not an SMS-DELIVER PDU");

        int addrLen = b[p++];
        int toa = b[p++];
        int addrBytes = (addrLen + 1) / 2;
        string sender;
        if ((toa & 0x70) == 0x50) // alphanumeric sender (e.g. operator name)
        {
            var sept = UnpackSeptets(b, p, addrLen * 4 / 7, 0);
            sender = SeptetsToString(sept);
        }
        else
        {
            var sb = new StringBuilder();
            for (int i = 0; i < addrBytes; i++)
            {
                int lo = b[p + i] & 0x0F, hi = b[p + i] >> 4;
                sb.Append((char)('0' + lo));
                if (hi != 0xF) sb.Append((char)('0' + hi));
            }
            sender = ((toa & 0x70) == 0x10 ? "+" : "") + sb.ToString()[..addrLen];
        }
        p += addrBytes;
        p++; // PID
        int dcs = b[p++];
        DateTime? ts = ParseTimestamp(b, p);
        p += 7;
        int udl = b[p++];

        int alphabet = (dcs & 0x80) == 0 ? (dcs >> 2) & 0x03 : (dcs & 0xF0) == 0xF0 ? (dcs >> 2) & 0x01 : (dcs & 0xF0) == 0xE0 ? 2 : 0;
        int? concatRef = null; int total = 1, seq = 1;
        int udhLen = 0;
        if (udhi)
        {
            udhLen = b[p] + 1;
            int q = p + 1;
            while (q < p + udhLen)
            {
                int iei = b[q], iel = b[q + 1];
                if (iei == 0x00 && iel == 3) { concatRef = b[q + 2]; total = b[q + 3]; seq = b[q + 4]; }
                else if (iei == 0x08 && iel == 4) { concatRef = (b[q + 2] << 8) | b[q + 3]; total = b[q + 4]; seq = b[q + 5]; }
                q += 2 + iel;
            }
        }

        string text;
        if (alphabet == 2) // UCS-2
        {
            int start = p + udhLen;
            int len = Math.Min(udl - udhLen, b.Length - start);
            text = Encoding.BigEndianUnicode.GetString(b, start, Math.Max(0, len));
        }
        else if (alphabet == 1) // 8-bit data
        {
            int start = p + udhLen;
            text = Encoding.Latin1.GetString(b, start, Math.Max(0, Math.Min(udl - udhLen, b.Length - start)));
        }
        else // GSM 7-bit
        {
            int udhSeptets = udhLen == 0 ? 0 : (udhLen * 8 + 6) / 7;
            var sept = UnpackSeptets(b, p, udl, udhSeptets);
            text = SeptetsToString(sept);
        }
        return new IncomingSms(sender, text, ts, concatRef, total, seq);
    }

    private static List<byte> UnpackSeptets(byte[] b, int start, int count, int skip)
    {
        var r = new List<byte>();
        for (int i = skip; i < count; i++)
        {
            int bit = i * 7;
            int byteIdx = start + bit / 8, shift = bit % 8;
            if (byteIdx >= b.Length) break;
            int v = b[byteIdx] >> shift;
            if (shift > 1 && byteIdx + 1 < b.Length) v |= b[byteIdx + 1] << (8 - shift);
            r.Add((byte)(v & 0x7F));
        }
        return r;
    }

    private static string SeptetsToString(List<byte> s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Count; i++)
        {
            if (s[i] == 0x1B && i + 1 < s.Count)
            {
                int e = Array.IndexOf(Gsm7ExtCodes, s[++i]);
                sb.Append(e >= 0 ? Gsm7ExtChars[e] : ' ');
            }
            else sb.Append(Gsm7Basic[s[i]]);
        }
        return sb.ToString();
    }

    private static DateTime? ParseTimestamp(byte[] b, int p)
    {
        try
        {
            int D(int i) => (b[p + i] & 0x0F) * 10 + (b[p + i] >> 4);
            int tzRaw = b[p + 6];
            int tzQuarters = (tzRaw & 0x07) * 10 + (tzRaw >> 4);
            bool neg = (tzRaw & 0x08) != 0;
            var local = new DateTime(2000 + D(0), D(1), D(2), D(3), D(4), D(5), DateTimeKind.Unspecified);
            var offset = TimeSpan.FromMinutes(15 * tzQuarters * (neg ? -1 : 1));
            return new DateTimeOffset(local, offset).UtcDateTime;
        }
        catch { return null; }
    }
}
