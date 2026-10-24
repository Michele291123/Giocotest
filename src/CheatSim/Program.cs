using ClayPigeon.CheatSim;

// ---------------------------------------------------------------------------
// Laboratorio di simulazione cheat (SOLO contro il nostro server, a scopo
// didattico). Mostra le difese anti-cheat che scattano.
//
// Avvio:  ClayPigeonCheatSim <scenario> [host] [porta]
//   scenario: forge | replay | clock | bounds | firerate | aimbot | honest | all
//   default host/porta: 127.0.0.1 5055
//
// Esempio:  ClayPigeonCheatSim all 192.168.1.50 5055
// ---------------------------------------------------------------------------

string scenario = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
string host = args.Length > 1 ? args[1] : "127.0.0.1";
int port = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 5055;

Console.WriteLine($"[cheatsim] scenario='{scenario}' server={host}:{port}");
Console.WriteLine("[cheatsim] NB: ogni '<<< ANTI-CHEAT' è una difesa del server che scatta.\n");

try
{
    await Scenarios.Run(scenario, host, port);
}
catch (Exception ex)
{
    Console.WriteLine($"[cheatsim] errore: {ex.Message}");
    Console.WriteLine("[cheatsim] il server è avviato e raggiungibile?");
}

Console.WriteLine("\n[cheatsim] fine.");
