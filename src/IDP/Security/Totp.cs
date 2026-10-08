using System.Security.Cryptography;
using System.Text;

namespace EnterpriseWebPlatform.IdentityServer.Security;

/// <summary>
/// Time-based one-time passwords (TOTP, RFC 6238) with the settings Google Authenticator
/// supports reliably: HMAC-SHA1, 6 digits, a new code every 30 seconds. Small enough to read.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>A new random secret (160 bits, as RFC 4226 recommends).</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long TimeStepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one time step (RFC 4226 HOTP with the step as the counter).</summary>
    public static string CodeAt(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, timeStep >>= 8)
            counter[i] = (byte)(timeStep & 0xFF);

        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// The time step a code matches, allowing one step of clock drift either way (±30 s);
    /// null when it matches none. Constant-time comparison.
    /// </summary>
    public static long? MatchingTimeStep(byte[] secret, string code, DateTimeOffset now)
    {
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
            return null;

        var current = TimeStepOf(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeAt(secret, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    /// <summary>The "otpauth://" URI the authenticator app reads from the QR code.</summary>
    public static string OtpAuthUri(string issuer, string accountName, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}" +
        $"?secret={Base32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>RFC 4648 base32 without padding - the key format authenticator apps accept for manual entry.</summary>
    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }
}