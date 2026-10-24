using ClayPigeon.Client;

// ---------------------------------------------------------------------------
// Client del "tiro al piattello" (Windows).
// Avvio:  ClayPigeonClient [host] [porta]
//   es.   ClayPigeonClient 192.168.1.50 5055   (server sul Raspberry Pi)
//   default: 127.0.0.1 5055
// ---------------------------------------------------------------------------

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5055;

ApplicationConfiguration.Initialize();
Application.Run(new GameForm(host, port));
