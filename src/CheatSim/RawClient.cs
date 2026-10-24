using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using ClayPigeon.Shared;

namespace ClayPigeon.CheatSim;

/// <summary>
/// Client "grezzo" usato dai simulatori di cheat. A differenza del client
/// ufficiale, qui controlliamo a mano numero di sequenza, MAC e timestamp, così
/// da poter costruire pacchetti malevoli e osservare le difese del server.
/// Lavora SOLO contro il nostro server, a livello di protocollo: è un test
/// d'integrazione dell'anti-cheat, non uno strumento contro software di terzi.
/// </summary>
public sealed class RawClient : IDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream _stream = null!;
    private byte[] _key = Array.Empty<byte>();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _seq;

    public string SessionId { get; private set; } = "";
    public long ClientMs => _clock.ElapsedMilliseconds;

    // Stato osservato dal server (aggiornato dal reader).
    private readonly object _gate = new();
    private SnapshotMsg? _snapshot;
    private bool _kicked;
    private string _kickReason = "";
    private int _hits, _shots;

    public SnapshotMsg? Snapshot { get { lock (_gate) return _snapshot; } }
    public bool Kicked { get { lock (_gate) return _kicked; } }
    public string KickReason { get { lock (_gate) return _kickReason; } }
    public (int hits, int shots) Tally { get { lock (_gate) return (_hits, _shots); } }

    public event Action<AntiCheatMsg>? OnAntiCheat;
    public event Action<RoundEndedMsg>? OnRoundEnded;

    public async Task ConnectAsync(string host, int port)
    {
        await _tcp.ConnectAsync(host, port);
        _stream = _tcp.GetStream();

        // Hello non firmato.
        string clientNonce = Security.NewNonce();
        await SendInternalAsync(MsgType.Hello, new HelloMsg("cheatsim", clientNonce), NextSeq(), ClientMs, null);

        var welcome = await ReadAsync() ?? throw new IOException("nessun Welcome");
        var w = JsonSerializer.Deserialize<WelcomeMsg>(welcome.Payload, Protocol.JsonOptions)!;
        SessionId = w.SessionId;
        _key = Security.DeriveSessionKey(w.SessionId, clientNonce, w.ServerNonce);

        _ = Task.Run(ReadLoopAsync);
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    // --- Invii "onesti" (firma corretta) e malevoli (parametri forzati) ---

    public Task SendSignedAsync(string type, object payload, long? tsOverride = null) =>
        SendInternalAsync(type, payload, NextSeq(), tsOverride ?? ClientMs, _key);

    /// <summary>Invio con parametri completamente controllati (per gli attacchi).</summary>
    public Task SendCraftedAsync(string type, object payload, long seq, long ts,
        byte[]? signingKey) =>
        SendInternalAsync(type, payload, seq, ts, signingKey);

    /// <summary>Costruisce un frame firmato e lo restituisce SENZA inviarlo
    /// (per poterlo poi re-inviare identico: attacco di replay).</summary>
    public byte[] BuildSignedFrame(string type, object payload, long seq, long ts)
    {
        string payloadJson = JsonSerializer.Serialize(payload, Protocol.JsonOptions);
        var env = new Envelope { Type = type, Seq = seq, Ts = ts, Payload = payloadJson };
        var canonical = Protocol.Canonical(type, seq, ts, payloadJson);
        env.Mac = Security.ComputeMac(_key, canonical);
        return Frame(env);
    }

    public async Task SendRawFrameAsync(byte[] frame) => await _stream.WriteAsync(frame);

    private async Task SendInternalAsync(string type, object payload, long seq, long ts, byte[]? signingKey)
    {
        string payloadJson = JsonSerializer.Serialize(payload, Protocol.JsonOptions);
        var env = new Envelope { Type = type, Seq = seq, Ts = ts, Payload = payloadJson };
        if (signingKey is not null)
        {
            var canonical = Protocol.Canonical(type, seq, ts, payloadJson);
            env.Mac = Security.ComputeMac(signingKey, canonical);
        }
        await _stream.WriteAsync(Frame(env));
    }

    private static byte[] Frame(Envelope env)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(env, Protocol.JsonOptions);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var env = await ReadAsync();
                if (env is null) return;
                Dispatch(env);
            }
        }
        catch { /* connessione chiusa */ }
    }

    private void Dispatch(Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.Snapshot:
                lock (_gate) _snapshot = JsonSerializer.Deserialize<SnapshotMsg>(env.Payload, Protocol.JsonOptions);
                break;
            case MsgType.ShotResult:
                var sr = JsonSerializer.Deserialize<ShotResultMsg>(env.Payload, Protocol.JsonOptions)!;
                lock (_gate) { _hits = sr.Hits; _shots = sr.ShotsFired; }
                break;
            case MsgType.AntiCheat:
                OnAntiCheat?.Invoke(JsonSerializer.Deserialize<AntiCheatMsg>(env.Payload, Protocol.JsonOptions)!);
                break;
            case MsgType.RoundEnded:
                OnRoundEnded?.Invoke(JsonSerializer.Deserialize<RoundEndedMsg>(env.Payload, Protocol.JsonOptions)!);
                break;
            case MsgType.Kicked:
                var k = JsonSerializer.Deserialize<KickedMsg>(env.Payload, Protocol.JsonOptions)!;
                lock (_gate) { _kicked = true; _kickReason = k.Reason; }
                break;
        }
    }

    private async Task<Envelope?> ReadAsync()
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(lenBuf)) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > NetChannel.MaxFrameBytes) throw new InvalidDataException("frame non valido");
        var body = new byte[len];
        if (!await ReadExactAsync(body)) return null;
        return JsonSerializer.Deserialize<Envelope>(body, Protocol.JsonOptions);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(off));
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public long CurrentSeq => Interlocked.Read(ref _seq);

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp.Dispose();
    }
}
