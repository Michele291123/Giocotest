using System.Diagnostics;
using System.Net.Sockets;
using ClayPigeon.Shared;

namespace ClayPigeon.Client;

/// <summary>
/// Livello di rete del client. Fa l'handshake, firma i messaggi con la chiave di
/// sessione e consegna gli eventi del server tramite callback. Gira su un task di
/// background: la form si occupa di marshallare gli eventi sul thread UI.
///
/// Nota: il client dichiara DOVE e QUANDO spara, ma non calcola mai se ha colpito
/// né il punteggio. Quei dati arrivano sempre dal server.
/// </summary>
public sealed class NetClient : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _clientNonce = Security.NewNonce();
    private TcpClient? _tcp;
    private NetChannel? _chan;
    private CancellationTokenSource? _cts;

    public string SessionId { get; private set; } = "";
    public long ClientMs => _clock.ElapsedMilliseconds;

    // Eventi (invocati dal thread di rete).
    public event Action<WelcomeMsg>? OnWelcome;
    public event Action<RoundStartedMsg>? OnRoundStarted;
    public event Action<SnapshotMsg>? OnSnapshot;
    public event Action<ShotResultMsg>? OnShotResult;
    public event Action<TargetExpiredMsg>? OnTargetExpired;
    public event Action<RoundEndedMsg>? OnRoundEnded;
    public event Action<AntiCheatMsg>? OnAntiCheat;
    public event Action<KickedMsg>? OnKicked;
    public event Action<string>? OnDisconnected;

    public async Task ConnectAsync(string host, int port)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port);
        _chan = new NetChannel(_tcp.GetStream());

        // Handshake: Hello (non firmato) -> Welcome -> deriva chiave di sessione.
        await _chan.SendAsync(MsgType.Hello, new HelloMsg("1.0", _clientNonce), ClientMs);
        var welcome = await _chan.ReadAsync() ?? throw new IOException("nessun Welcome dal server");
        if (welcome.Type != MsgType.Welcome) throw new IOException($"atteso Welcome, ricevuto {welcome.Type}");

        var w = welcome.Deserialize<WelcomeMsg>();
        SessionId = w.SessionId;
        byte[] key = Security.DeriveSessionKey(w.SessionId, _clientNonce, w.ServerNonce);
        _chan.SetSessionKey(key);
        OnWelcome?.Invoke(w);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _chan!.ReadAsync(ct);
                if (msg is null) { OnDisconnected?.Invoke("connessione chiusa"); return; }

                switch (msg.Type)
                {
                    case MsgType.RoundStarted: OnRoundStarted?.Invoke(msg.Deserialize<RoundStartedMsg>()); break;
                    case MsgType.Snapshot: OnSnapshot?.Invoke(msg.Deserialize<SnapshotMsg>()); break;
                    case MsgType.ShotResult: OnShotResult?.Invoke(msg.Deserialize<ShotResultMsg>()); break;
                    case MsgType.TargetExpired: OnTargetExpired?.Invoke(msg.Deserialize<TargetExpiredMsg>()); break;
                    case MsgType.RoundEnded: OnRoundEnded?.Invoke(msg.Deserialize<RoundEndedMsg>()); break;
                    case MsgType.AntiCheat: OnAntiCheat?.Invoke(msg.Deserialize<AntiCheatMsg>()); break;
                    case MsgType.Kicked: OnKicked?.Invoke(msg.Deserialize<KickedMsg>()); return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnDisconnected?.Invoke(ex.Message);
        }
    }

    public Task JoinRoundAsync(Difficulty d, Weapon w) =>
        _chan!.SendAsync(MsgType.JoinRound, new JoinRoundMsg(d, w), ClientMs);

    public Task FireAsync(double aimX, double aimY) =>
        _chan!.SendAsync(MsgType.Fire, new FireMsg(aimX, aimY, ClientMs), ClientMs);

    public Task SwitchWeaponAsync(Weapon w) =>
        _chan!.SendAsync(MsgType.SwitchWeapon, new SwitchWeaponMsg(w), ClientMs);

    public Task HeartbeatAsync() =>
        _chan!.SendAsync(MsgType.Heartbeat, new HeartbeatMsg(ClientMs), ClientMs);

    public void Dispose()
    {
        _cts?.Cancel();
        _chan?.Dispose();
        _tcp?.Dispose();
    }
}
