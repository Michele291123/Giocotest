using ClayPigeon.Shared;

namespace ClayPigeon.CheatSim;

/// <summary>
/// Scenari di cheat simulati contro il nostro server. Ognuno apre una sessione
/// nuova, si comporta in modo scorretto a livello di protocollo e stampa come
/// reagisce l'anti-cheat. L'ultimo scenario ("honest") gioca in modo corretto e
/// serve a mostrare che NON viene segnalato (controllo falsi positivi).
/// </summary>
public static class Scenarios
{
    public static async Task Run(string name, string host, int port)
    {
        switch (name)
        {
            case "firerate": await FireRate(host, port); break;
            case "aimbot": await Aimbot(host, port); break;
            case "forge": await ForgedMac(host, port); break;
            case "replay": await Replay(host, port); break;
            case "clock": await ClockTamper(host, port); break;
            case "bounds": await OutOfBounds(host, port); break;
            case "honest": await Honest(host, port); break;
            case "all":
                foreach (var s in new[] { "forge", "replay", "clock", "bounds", "firerate", "aimbot", "honest" })
                { await Run(s, host, port); await Task.Delay(800); }
                break;
            default: Console.WriteLine($"scenario sconosciuto: {name}"); break;
        }
    }

    // ---- 1. Cadenza di fuoco impossibile (speedhack / auto-fire) ----
    private static async Task FireRate(string host, int port)
    {
        Header("FIRE-RATE / SPEEDHACK", "Pistola semiauto che spara ogni ~15 ms (limite reale 230 ms).");
        using var c = await Connect(host, port);
        await c.SendSignedAsync(MsgType.JoinRound, new JoinRoundMsg(Difficulty.Facile, Weapon.PistolaSemiauto));
        await Task.Delay(900);

        for (int i = 0; i < 150 && !c.Kicked; i++)
        {
            var s = c.Snapshot;
            double ax = s is { HasTarget: true } ? s.X : GameConfig.WorldWidth / 2.0;
            double ay = s is { HasTarget: true } ? s.Y : GameConfig.WorldHeight / 2.0;
            await c.SendSignedAsync(MsgType.Fire, new FireMsg(ax, ay, c.ClientMs));
            await Task.Delay(15);
        }
        await WaitKickOr(c, 3000);
        Verdict(c, "Atteso: violazioni 'fire_rate' -> warn -> kick (gli spari extra NON contano).");
    }

    // ---- 2. Aimbot: reazione istantanea + mira perfetta ----
    private static async Task Aimbot(string host, int port)
    {
        Header("AIMBOT", "Legge gli snapshot, mira esatta al centro, reazione ~0, un colpo per bersaglio.");
        using var c = await Connect(host, port);
        bool roundEnded = false;
        c.OnRoundEnded += r => { roundEnded = true; Console.WriteLine($"    round: acc={r.AccuracyPct}% {r.Hits}/{r.Shots} score={r.TotalScore}"); };
        await c.SendSignedAsync(MsgType.JoinRound, new JoinRoundMsg(Difficulty.Media, Weapon.PistolaSemiauto));

        SnapshotMsg? lastSnap = null; long observedAt = 0; var firedFor = new HashSet<int>(); long lastShot = -9999;
        long start = c.ClientMs;
        while (!c.Kicked && !roundEnded && c.ClientMs - start < 45000)
        {
            var s = c.Snapshot;
            if (s is { HasTarget: true })
            {
                if (!ReferenceEquals(s, lastSnap)) { lastSnap = s; observedAt = c.ClientMs; }
                bool canFire = c.ClientMs - lastShot >= GameConfig.For(Weapon.PistolaSemiauto).minShotIntervalMs;
                if (!firedFor.Contains(s.TargetId) && canFire)
                {
                    // Predizione di traiettoria: estrapola la posizione attuale dal
                    // moto noto. È ciò che fa un aimbot reale; il server lo smaschera
                    // non col "dove" ma col "come" (precisione + reazione disumane).
                    double dt = (c.ClientMs - observedAt) / 1000.0;
                    double ax = s.X + s.Vx * dt;
                    double ay = s.Y + s.Vy * dt + 0.5 * GameConfig.Gravity * dt * dt;
                    await c.SendSignedAsync(MsgType.Fire, new FireMsg(ax, ay, c.ClientMs));
                    firedFor.Add(s.TargetId);
                    lastShot = c.ClientMs;
                }
            }
            await Task.Delay(5);
        }
        await WaitKickOr(c, 2000);
        Verdict(c, "Atteso: a fine prova 'aimbot_pattern' -> kick (reazione/precisione non umane).");
    }

    // ---- 3. Pacchetto falsificato (firma non valida) ----
    private static async Task ForgedMac(string host, int port)
    {
        Header("PACCHETTO FALSIFICATO", "Un attaccante esterno invia un Fire firmato con la CHIAVE SBAGLIATA.");
        using var c = await Connect(host, port);
        byte[] wrongKey = new byte[32];
        new Random().NextBytes(wrongKey);
        await c.SendCraftedAsync(MsgType.Fire, new FireMsg(100, 100, c.ClientMs),
            seq: c.CurrentSeq + 1, ts: c.ClientMs, signingKey: wrongKey);
        await WaitKickOr(c, 2500);
        Verdict(c, "Atteso: 'packet_auth' -> kick immediato (MAC non verificato).");
    }

    // ---- 4. Replay (riutilizzo di un pacchetto valido) ----
    private static async Task Replay(string host, int port)
    {
        Header("REPLAY", "Cattura un Fire valido e lo re-invia identico più volte (stesso seq/MAC).");
        using var c = await Connect(host, port);
        await c.SendSignedAsync(MsgType.JoinRound, new JoinRoundMsg(Difficulty.Facile, Weapon.PistolaSemiauto));
        await Task.Delay(700);

        long seq = c.CurrentSeq + 1;
        byte[] frame = c.BuildSignedFrame(MsgType.Fire, new FireMsg(200, 300, c.ClientMs), seq, c.ClientMs);
        await c.SendRawFrameAsync(frame);          // 1) primo invio: valido
        for (int i = 0; i < 4 && !c.Kicked; i++)   // 2) repliche: stesso seq -> replay
        {
            await c.SendRawFrameAsync(frame);
            await Task.Delay(120);
        }
        await WaitKickOr(c, 2500);
        Verdict(c, "Atteso: 'replay' (seq non crescente) -> warn ripetuti -> kick.");
    }

    // ---- 5. Manomissione del clock ----
    private static async Task ClockTamper(string host, int port)
    {
        Header("CLOCK TAMPER", "Invia heartbeat con timestamp che tornano indietro nel tempo.");
        using var c = await Connect(host, port);
        await c.SendSignedAsync(MsgType.Heartbeat, new HeartbeatMsg(10_000), tsOverride: 10_000);
        foreach (long ts in new long[] { 8_000, 6_000, 4_000, 2_000 })
        {
            if (c.Kicked) break;
            await c.SendSignedAsync(MsgType.Heartbeat, new HeartbeatMsg(ts), tsOverride: ts);
            await Task.Delay(150);
        }
        await WaitKickOr(c, 2500);
        Verdict(c, "Atteso: 'clock_tamper' (tempo non monotòno) -> warn -> kick.");
    }

    // ---- 6. Coordinate fuori area ----
    private static async Task OutOfBounds(string host, int port)
    {
        Header("COORDINATE FUORI AREA", "Fire con mira a (99999, -99999): impossibile su uno schermo reale.");
        using var c = await Connect(host, port);
        await c.SendSignedAsync(MsgType.JoinRound, new JoinRoundMsg(Difficulty.Facile, Weapon.PistolaSemiauto));
        await Task.Delay(500);
        await c.SendSignedAsync(MsgType.Fire, new FireMsg(99999, -99999, c.ClientMs));
        await WaitKickOr(c, 2500);
        Verdict(c, "Atteso: 'bounds' -> kick (validazione dei confini).");
    }

    // ---- 7. Giocatore ONESTO (nessun falso positivo atteso) ----
    private static async Task Honest(string host, int port)
    {
        Header("GIOCATORE ONESTO (controllo falsi positivi)",
            "Reazione umana 260-420 ms, mira con errore 8-14 px, cadenza rispettata.");
        using var c = await Connect(host, port);
        bool roundEnded = false;
        c.OnRoundEnded += r => { roundEnded = true; Console.WriteLine($"    round: acc={r.AccuracyPct}% {r.Hits}/{r.Shots} score={r.TotalScore}"); };
        await c.SendSignedAsync(MsgType.JoinRound, new JoinRoundMsg(Difficulty.Media, Weapon.PistolaSemiauto));

        var rng = new Random();
        var seen = new Dictionary<int, long>();   // targetId -> istante in cui lo abbiamo "visto"
        var fired = new HashSet<int>();
        long lastShot = -9999; long start = c.ClientMs;

        while (!c.Kicked && !roundEnded && c.ClientMs - start < 60000)
        {
            var s = c.Snapshot;
            if (s is { HasTarget: true })
            {
                if (!seen.ContainsKey(s.TargetId)) seen[s.TargetId] = c.ClientMs + rng.Next(260, 420); // tempo di reazione
                bool reacted = c.ClientMs >= seen[s.TargetId];
                bool canFire = c.ClientMs - lastShot >= GameConfig.For(Weapon.PistolaSemiauto).minShotIntervalMs;
                if (reacted && canFire && !fired.Contains(s.TargetId))
                {
                    double err = 8 + rng.NextDouble() * 6;
                    double ang = rng.NextDouble() * Math.PI * 2;
                    await c.SendSignedAsync(MsgType.Fire,
                        new FireMsg(s.X + Math.Cos(ang) * err, s.Y + Math.Sin(ang) * err, c.ClientMs));
                    fired.Add(s.TargetId);
                    lastShot = c.ClientMs;
                }
            }
            await Task.Delay(5);
        }
        Verdict(c, "Atteso: NESSUN kick (comportamento umano, l'anti-cheat non deve scattare).");
    }

    // ----------------------------- Utility -----------------------------

    private static async Task<RawClient> Connect(string host, int port)
    {
        var c = new RawClient();
        c.OnAntiCheat += m => Console.WriteLine($"    <<< ANTI-CHEAT {m.Action.ToUpper()} [{m.Code}] {m.Message}");
        await c.ConnectAsync(host, port);
        Console.WriteLine($"    connesso (sessione {c.SessionId})");
        return c;
    }

    private static async Task WaitKickOr(RawClient c, int timeoutMs)
    {
        long t = Environment.TickCount64;
        while (!c.Kicked && Environment.TickCount64 - t < timeoutMs)
            await Task.Delay(50);
    }

    private static void Header(string title, string desc)
    {
        Console.WriteLine();
        Console.WriteLine("==================================================================");
        Console.WriteLine($"  SCENARIO: {title}");
        Console.WriteLine($"  {desc}");
        Console.WriteLine("==================================================================");
    }

    private static void Verdict(RawClient c, string expected)
    {
        Console.WriteLine($"    {expected}");
        var (hits, shots) = c.Tally;
        if (c.Kicked)
            Console.WriteLine($"    RISULTATO: ESPULSO -> \"{c.KickReason}\"  (colpi validi {hits}/{shots})");
        else
            Console.WriteLine($"    RISULTATO: non espulso  (colpi validi {hits}/{shots})");
    }
}
