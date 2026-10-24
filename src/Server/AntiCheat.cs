using ClayPigeon.Shared;

namespace ClayPigeon.Server;

public enum AcAction { None, Warn, Kick }

public readonly record struct AcResult(AcAction Action, string Code, string Message)
{
    public static readonly AcResult Ok = new(AcAction.None, "", "");
    public bool IsKick => Action == AcAction.Kick;
}

/// <summary>
/// Motore anti-cheat PER SESSIONE. Raccoglie metriche e applica regole. Tutte
/// le soglie sono costanti commentate: cambiandole si regola quanto il sistema
/// è severo. Le decisioni sono prese SOLO con dati che il server misura da sé
/// (tempi del suo orologio, esiti che calcola lui), mai fidandosi del client.
/// </summary>
public sealed class AntiCheat
{
    // --- Soglie (commentate per didattica) ---

    // Reazione umana minima plausibile a uno stimolo visivo nuovo. I migliori
    // atleti stanno sui ~150-200 ms; sotto ~120 ms ripetutamente non è umano.
    private const double HumanReactionFloorMs = 120;
    private const double SuspiciousReactionMs = 150;

    // Un aimbot colpisce quasi sempre esattamente al centro: errore di mira
    // costantemente vicino a zero è un segnale fortissimo.
    private const double SuspiciousAimErrorPx = 5.0;

    // Quanti pacchetti fuori sequenza/replay tolleriamo prima del kick.
    private const int ReplayKickThreshold = 3;

    // Violazioni di cadenza di fuoco: warning, poi kick.
    private const int FireRateWarnAt = 3;
    private const int FireRateKickAt = 6;

    // Deriva/manomissione del clock del client prima del kick.
    private const int ClockKickThreshold = 3;

    // --- Stato di integrità del canale ---
    private long _lastSeq;
    private int _replayCount;
    private long _lastClientTs = long.MinValue;
    private int _clockViolations;

    // --- Stato cadenza di fuoco ---
    private long _lastAcceptedShotServerMs = long.MinValue / 2;
    private int _fireRateViolations;

    // --- Metriche della prova in corso (per rilevamento aimbot) ---
    private readonly List<double> _reactionMs = new();
    private readonly List<double> _aimErrorPx = new();
    private int _shots;
    private int _hits;

    /// <summary>
    /// Verifica integrità dell'envelope: autenticità (MAC) e ordine (seq).
    /// Da chiamare su OGNI messaggio firmato, prima di interpretarlo.
    /// </summary>
    public AcResult CheckEnvelope(Incoming msg)
    {
        // 1) Autenticità. Se il MAC non torna, il pacchetto non proviene da chi
        //    possiede la chiave di sessione: iniezione/manomissione esterna.
        if (msg.MacValid == false)
            return new AcResult(AcAction.Kick, "packet_auth",
                "Firma del messaggio non valida (pacchetto falsificato o alterato).");

        // 2) Anti-replay / anti-riordino: la sequenza deve crescere sempre.
        if (msg.Seq <= _lastSeq)
        {
            _replayCount++;
            if (_replayCount >= ReplayKickThreshold)
                return new AcResult(AcAction.Kick, "replay",
                    $"Troppi messaggi fuori sequenza/replay (seq={msg.Seq} <= {_lastSeq}).");
            return new AcResult(AcAction.Warn, "replay",
                $"Messaggio fuori sequenza ignorato (seq={msg.Seq} <= {_lastSeq}).");
        }
        _lastSeq = msg.Seq;
        return AcResult.Ok;
    }

    /// <summary>
    /// Controlla la coerenza del tempo dichiarato dal client: deve essere
    /// monotòno (non può "tornare indietro") e non può divergere troppo dal
    /// tempo reale trascorso lato server. Intercetta speed/slow-hack basati su
    /// manipolazione del clock.
    /// </summary>
    public AcResult CheckClock(long clientTs, long serverRecvMs, long sessionStartServerMs)
    {
        if (clientTs < _lastClientTs)
        {
            _clockViolations++;
            return Clock($"Tempo client non monotòno ({clientTs} < {_lastClientTs}).");
        }

        // Tempo trascorso lato client vs lato server dall'inizio sessione.
        long serverElapsed = serverRecvMs - sessionStartServerMs;
        // Non conosciamo l'offset iniziale esatto, ma la DERIVA non deve crescere
        // oltre tolleranza: se il client "accelera" il suo tempo, il delta esplode.
        long drift = Math.Abs(clientTs - serverElapsed);
        _lastClientTs = clientTs;

        if (drift > GameConfig.ClockToleranceMs * 4)
        {
            _clockViolations++;
            return Clock($"Deriva di clock sospetta: {drift} ms.");
        }
        return AcResult.Ok;

        AcResult Clock(string why) => _clockViolations >= ClockKickThreshold
            ? new AcResult(AcAction.Kick, "clock_tamper", why)
            : new AcResult(AcAction.Warn, "clock_tamper", why);
    }

    /// <summary>
    /// Registra un tentativo di sparo e applica il limite di cadenza dell'arma.
    /// Ritorna (accettato, risultato anti-cheat). Uno sparo troppo ravvicinato
    /// NON viene accettato (quindi non può colpire) ed è conteggiato come
    /// violazione: è così che si neutralizza uno speedhack / auto-fire.
    /// </summary>
    public (bool accepted, AcResult result) RegisterShot(long shotServerMs, WeaponSettings w)
    {
        long interval = shotServerMs - _lastAcceptedShotServerMs;
        long minAllowed = w.minShotIntervalMs - w.tolerancePerShotMs;

        if (interval < minAllowed)
        {
            _fireRateViolations++;
            AcAction act = _fireRateViolations >= FireRateKickAt ? AcAction.Kick
                         : _fireRateViolations >= FireRateWarnAt ? AcAction.Warn
                         : AcAction.None;
            var res = new AcResult(act, "fire_rate",
                $"Cadenza di fuoco oltre il limite dell'arma ({interval} ms < {minAllowed} ms).");
            return (false, res); // sparo RIFIUTATO
        }

        _lastAcceptedShotServerMs = shotServerMs;
        _shots++;
        return (true, AcResult.Ok);
    }

    /// <summary>Registra un colpo andato a segno con le sue metriche.</summary>
    public void RegisterHit(double reactionMs, double aimErrorPx)
    {
        _hits++;
        _reactionMs.Add(reactionMs);
        _aimErrorPx.Add(aimErrorPx);
    }

    /// <summary>Verifica dei confini: coordinate di mira fuori dal mondo = client
    /// manomesso o pacchetto costruito a mano.</summary>
    public AcResult CheckAimBounds(double aimX, double aimY)
    {
        const double margin = 50;
        if (aimX < -margin || aimX > GameConfig.WorldWidth + margin ||
            aimY < -margin || aimY > GameConfig.WorldHeight + margin)
        {
            return new AcResult(AcAction.Kick, "bounds",
                $"Coordinate di mira fuori area ({aimX:0},{aimY:0}).");
        }
        return AcResult.Ok;
    }

    /// <summary>
    /// Valutazione aggregata a fine prova: incrocia accuratezza, tempo di
    /// reazione mediano e precisione mediana. Un giocatore umano può eccellere
    /// in una metrica, ma avere TUTTE e tre al livello "macchina" sulle prove
    /// difficili è il profilo tipico dell'aimbot.
    /// </summary>
    public AcResult EvaluateRound(Difficulty difficulty)
    {
        if (_shots < 10) return AcResult.Ok; // troppo pochi dati per concludere

        double accuracy = (double)_hits / _shots;
        double medReaction = Median(_reactionMs);
        double medAim = Median(_aimErrorPx);

        bool fastReaction = medReaction is >= 0 and <= SuspiciousReactionMs;
        bool pinpoint = medAim is >= 0 and <= SuspiciousAimErrorPx;
        bool superAccurate = accuracy >= 0.92;
        bool inhumanFloor = medReaction >= 0 && medReaction < HumanReactionFloorMs;

        int signals = (fastReaction ? 1 : 0) + (pinpoint ? 1 : 0) + (superAccurate ? 1 : 0);

        string detail =
            $"acc={accuracy:P0}, reaz.mediana={medReaction:0}ms, mira.mediana={medAim:0.0}px";

        // Profilo "macchina" pieno sulle prove non facili, oppure reazione sotto
        // la soglia fisica umana: kick.
        if ((signals == 3 && difficulty != Difficulty.Facile) || (inhumanFloor && pinpoint))
            return new AcResult(AcAction.Kick, "aimbot_pattern",
                $"Profilo di tiro non umano ({detail}).");

        if (signals >= 2)
            return new AcResult(AcAction.Warn, "aimbot_pattern",
                $"Metriche di tiro sospette ({detail}).");

        return AcResult.Ok;
    }

    /// <summary>Azzera le metriche per la prova successiva (non lo stato del canale).</summary>
    public void ResetRoundMetrics()
    {
        _reactionMs.Clear();
        _aimErrorPx.Clear();
        _shots = 0;
        _hits = 0;
        _fireRateViolations = 0;
        _lastAcceptedShotServerMs = long.MinValue / 2;
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return -1;
        var s = xs.ToList();
        s.Sort();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }
}
