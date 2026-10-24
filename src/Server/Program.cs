using System.Net;
using System.Net.Sockets;
using ClayPigeon.Server;
using ClayPigeon.Shared;

// ---------------------------------------------------------------------------
// Server del "tiro al piattello" — server-authoritative + anti-cheat.
// Avvio:  ClayPigeonServer [porta]        (default 5055)
// Gira su Raspberry Pi (linux-arm64) e su qualunque PC con .NET 8.
// ---------------------------------------------------------------------------

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5055;

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"[server] in ascolto su 0.0.0.0:{port}  (tick {GameConfig.ServerTickHz}Hz, snapshot {GameConfig.SnapshotHz}Hz)");
Console.WriteLine("[server] premi Ctrl+C per uscire");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.IsCancellationRequested)
    {
        TcpClient tcp;
        try { tcp = await listener.AcceptTcpClientAsync(cts.Token); }
        catch (OperationCanceledException) { break; }

        _ = HandleClientAsync(tcp, cts.Token);
    }
}
finally
{
    listener.Stop();
    Console.WriteLine("[server] arrestato");
}

static async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
{
    var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    Console.WriteLine($"[server] connessione da {remote}");
    try
    {
        tcp.NoDelay = true; // bassa latenza: utile per un gioco d'azione
        using var stream = tcp.GetStream();
        using var chan = new NetChannel(stream);
        var session = new GameSession(chan, Console.WriteLine);
        await session.RunAsync(ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[server] sessione {remote} terminata: {ex.Message}");
    }
    finally
    {
        tcp.Dispose();
        Console.WriteLine($"[server] chiusa {remote}");
    }
}
