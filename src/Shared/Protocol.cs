using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClayPigeon.Shared;

/// <summary>
/// Envelope che incapsula ogni messaggio sul filo.
/// Formato wire: [4 byte lunghezza big-endian][JSON UTF-8 dell'envelope].
/// Il campo <see cref="Payload"/> è il JSON del messaggio specifico serializzato
/// come stringa: così il MAC può essere calcolato su byte esattamente identici
/// tra mittente e destinatario, senza ambiguità di ri-serializzazione.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("payload")] public string Payload { get; set; } = "{}";
}

/// <summary>Messaggio ricevuto e (quando c'è la chiave) già verificato.</summary>
public sealed class Incoming
{
    public required string Type { get; init; }
    public required long Seq { get; init; }
    public required long Ts { get; init; }
    public required string Payload { get; init; }
    /// <summary>null = MAC non verificato (handshake), true/false = esito verifica.</summary>
    public bool? MacValid { get; init; }

    public T Deserialize<T>() =>
        JsonSerializer.Deserialize<T>(Payload, Protocol.JsonOptions)
        ?? throw new InvalidDataException($"payload nullo per {Type}");
}

/// <summary>
/// Canale di rete orientato al messaggio su uno <see cref="Stream"/> (TCP).
/// Gestisce framing, firma (HMAC) in uscita e verifica in entrata, più i
/// numeri di sequenza. Non è thread-safe in scrittura: serializzare gli invii.
/// </summary>
public sealed class NetChannel : IDisposable
{
    public const int MaxFrameBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[]? _sessionKey;
    private long _sendSeq;

    public NetChannel(Stream stream) => _stream = stream;

    public void SetSessionKey(byte[] key) => _sessionKey = key;

    /// <summary>Numero di sequenza dell'ultimo messaggio inviato.</summary>
    public long LastSentSeq => _sendSeq;

    public async Task SendAsync(string type, object payload, long ts, CancellationToken ct = default)
    {
        long seq = Interlocked.Increment(ref _sendSeq);
        string payloadJson = JsonSerializer.Serialize(payload, Protocol.JsonOptions);

        var env = new Envelope { Type = type, Seq = seq, Ts = ts, Payload = payloadJson };
        if (_sessionKey is not null)
        {
            var canonical = Protocol.Canonical(type, seq, ts, payloadJson);
            env.Mac = Security.ComputeMac(_sessionKey, canonical);
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(env, Protocol.JsonOptions);
        if (body.Length > MaxFrameBytes)
            throw new InvalidOperationException("messaggio troppo grande");

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Legge il prossimo messaggio, o null se la connessione è chiusa.</summary>
    public async Task<Incoming?> ReadAsync(CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(lenBuf, ct).ConfigureAwait(false))
            return null;

        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > MaxFrameBytes)
            throw new InvalidDataException($"lunghezza frame non valida: {len}");

        var body = new byte[len];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
            return null;

        var env = JsonSerializer.Deserialize<Envelope>(body, Protocol.JsonOptions)
                  ?? throw new InvalidDataException("envelope nullo");

        bool? macValid = null;
        if (_sessionKey is not null)
        {
            var canonical = Protocol.Canonical(env.Type, env.Seq, env.Ts, env.Payload);
            macValid = env.Mac is not null && Security.VerifyMac(_sessionKey, canonical, env.Mac);
        }

        return new Incoming
        {
            Type = env.Type,
            Seq = env.Seq,
            Ts = env.Ts,
            Payload = env.Payload,
            MacValid = macValid,
        };
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(off), ct).ConfigureAwait(false);
            if (n == 0) return false; // connessione chiusa
            off += n;
        }
        return true;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _stream.Dispose();
    }
}

public static class Protocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Byte canonici su cui si calcola/verifica il MAC.</summary>
    public static byte[] Canonical(string type, long seq, long ts, string payloadJson) =>
        Encoding.UTF8.GetBytes($"{type}|{seq}|{ts}|{payloadJson}");
}
