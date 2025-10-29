// BaseUsuarios.Api/Utils/ResetToken.cs
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaseUsuarios.Api.Utils;

public static class ResetToken
{
    // Token sin estado: header.payload.signature (base64url)
    // payload incluye: sub (UsuarioId), exp (unix), pur ("pwd_reset"), fp (huella del PasswordHash)
    public static string Create(ulong userId, string passwordHash, TimeSpan ttl, string secret)
    {
        var header = new { alg = "HS256", typ = "JWT", kid = "reset-v1" };
        var fp = Fingerprint(passwordHash); // huella corta que cambia cuando cambias el password

        var exp = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var payload = new { sub = userId, exp, pur = "pwd_reset", fp };

        string h = B64Url(JsonSerializer.Serialize(header));
        string p = B64Url(JsonSerializer.Serialize(payload));
        string sig = Sign($"{h}.{p}", secret);
        return $"{h}.{p}.{sig}";
    }

    public static (bool ok, ulong userId, string fp, long exp, string? err) Validate(string token, string secret)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return (false, 0, "", 0, "formato");
            if (!TimeSafeEquals(parts[2], Sign($"{parts[0]}.{parts[1]}", secret))) return (false, 0, "", 0, "firma");

            var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1].Replace('-', '+').Replace('_', '/'))));
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("pur", out var pur) || pur.GetString() != "pwd_reset") return (false, 0, "", 0, "purpose");

            var sub = doc.RootElement.GetProperty("sub").GetUInt64();
            var exp = doc.RootElement.GetProperty("exp").GetInt64();
            var fp  = doc.RootElement.GetProperty("fp").GetString()!;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp) return (false, 0, "", exp, "expirado");

            return (true, sub, fp, exp, null);
        }
        catch { return (false, 0, "", 0, "parse"); }
    }

    // Huella derivada del PasswordHash actual; invalida automáticamente el token si el usuario ya cambió su password
    public static string Fingerprint(string passwordHash)
    {
        using var h = SHA256.Create();
        var digest = h.ComputeHash(Encoding.UTF8.GetBytes(passwordHash ?? ""));
        // 16 bytes (128 bits) bastan; base64url
        return Convert.ToBase64String(digest, Base64FormattingOptions.None)
            .TrimEnd('=').Replace('+','-').Replace('/','_')
            .Substring(0, 22); // ~128 bits en b64url
    }

    static string Sign(string data, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = h.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(sig).TrimEnd('=').Replace('+','-').Replace('/','_');
    }

    static string B64Url(string s)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=');
        return b.Replace('+','-').Replace('/','_');
    }
    static string Pad(string s) => s + new string('=', (4 - s.Length % 4) % 4);

    static bool TimeSafeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
