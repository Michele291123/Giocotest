using System.Security.Cryptography;
using System.Text;

namespace ClayPigeon.Shared;

/// <summary>
/// Autenticazione dei messaggi (HMAC-SHA256) e derivazione della chiave di
/// sessione.
///
/// COSA PROTEGGE: un attaccante "esterno" (che intercetta/inietta pacchetti
/// sulla rete senza aver aperto il client ufficiale) non conosce la chiave e
/// quindi non può falsificare messaggi validi né replicarli.
///
/// COSA **NON** PROTEGGE (da spiegare bene nel report): la chiave pre-condivisa
/// vive dentro il binario del client. Chi fa reverse engineering del client può
/// estrarla e firmare pacchetti validi. Perciò l'HMAC da solo NON ferma un cheat
/// che gira dentro/accanto al client ufficiale: ferma solo gli attacchi "da fuori".
/// La vera difesa contro i cheat interni è il modello server-authoritative +
/// rilevamento comportamentale + integrità del client (anti-cheat kernel).
/// </summary>
public static class Security
{
    // Chiave pre-condivisa "di root". In un gioco reale NON sarebbe una costante
    // nel sorgente: verrebbe consegnata al client in modo offuscato/attestato e
    // ruotata. Qui è esplicita apposta, per didattica.
    private static readonly byte[] RootKey =
        Encoding.UTF8.GetBytes("CLAY-PIGEON-DEMO-ROOT-KEY-v1-non-usare-in-produzione");

    /// <summary>
    /// Deriva la chiave di sessione combinando la root key con i due nonce
    /// (client e server) scambiati nell'handshake. Legare la chiave ai nonce
    /// rende ogni sessione diversa e impedisce il replay di una sessione dentro
    /// un'altra.
    /// </summary>
    public static byte[] DeriveSessionKey(string sessionId, string clientNonce, string serverNonce)
    {
        var info = Encoding.UTF8.GetBytes($"{sessionId}:{clientNonce}:{serverNonce}");
        using var h = new HMACSHA256(RootKey);
        return h.ComputeHash(info);
    }

    /// <summary>Genera un nonce casuale (base64) per l'handshake.</summary>
    public static string NewNonce()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b);
    }

    /// <summary>Calcola l'HMAC (base64) sui byte canonici del messaggio.</summary>
    public static string ComputeMac(byte[] sessionKey, ReadOnlySpan<byte> canonical)
    {
        using var h = new HMACSHA256(sessionKey);
        var mac = h.ComputeHash(canonical.ToArray());
        return Convert.ToBase64String(mac);
    }

    /// <summary>
    /// Verifica un MAC in modo resistente ai timing attack
    /// (confronto a tempo costante).
    /// </summary>
    public static bool VerifyMac(byte[] sessionKey, ReadOnlySpan<byte> canonical, string macBase64)
    {
        string expected = ComputeMac(sessionKey, canonical);
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(macBase64 ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(a, b.Length == a.Length ? b : new byte[a.Length]);
    }
}
