using ClayPigeon.Shared;

namespace ClayPigeon.Server;

/// <summary>
/// Logica di gioco per UNA connessione. È qui che vive il modello
/// "server-authoritative": il server simula i piattelli, decide i colpi, tiene
/// il punteggio e applica l'anti-cheat. Il client manda solo intenzioni (dove e
/// quando spara) e disegna gli snapshot che riceve.
/// </summary>
public sealed class GameSession
{
    private enum State { Handshake, Idle, RoundActive, RoundEnded }

    private readonly NetChannel _chan;
    private readonly AntiCheat _ac = new();
    private readonly Action<string> _log;
    private readonly Random _rng = new();
    private readonly object _gate = new();

    private string _sessionId = "";
    private long _sessionStartServerMs;

    // Stima dell'offset di base client->server (per la lag compensation).
    private bool _offsetInit;
    private long _baseOffset;

    // Stato di gioco (protetto da _gate).
    private State _state = State.Handshake;
    private Difficulty _difficulty = Difficulty.Facile;
    private Weapon _weapon = Weapon.PistolaSemiauto;
    private ActiveTarget? _target;
    private int _roundId;
    private int _nextTargetId = 1;
    private int _targetsSpawned;
    private int _totalScore;
    private int _shots;
    private int _hits;
    private long _nextSpawnAtMs;
    private long _lastSnapshotMs;

    private string? _pendingKick;

    public GameSession(NetChannel chan, Action<string> log)
    {
        _chan = chan;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!await HandshakeAsync(ct)) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tick = Task.Run(() => TickLoopAsync(cts.Token), cts.Token);
        try
        {
            await ReadLoopAsync(cts.Token);
        }
        finally
        {
            cts.Cancel();
            try { await tick; } catch { /* atteso alla chiusura */ }
        }
    }

    // ----------------------------- Handshake -----------------------------

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        var first = await _chan.ReadAsync(ct);
        if (first is null || first.Type != MsgType.Hello)
            return false;

        var hello = first.Deserialize<HelloMsg>();
        _sessionId = Guid.NewGuid().ToString("N")[..12];
        string serverNonce = Security.NewNonce();
        byte[] key = Security.DeriveSessionKey(_sessionId, hello.ClientNonce, serverNonce);
        _chan.SetSessionKey(key);
        _sessionStartServerMs = ServerClock.NowMs;

        await _chan.SendAsync(MsgType.Welcome, new WelcomeMsg(
            _sessionId, serverNonce, ServerClock.NowMs,
            GameConfig.WorldWidth, GameConfig.WorldHeight),
            ServerClock.NowMs, ct);

        lock (_gate) _state = State.Idle;
        _log($"[{_sessionId}] handshake ok (client v{hello.ClientVersion})");
        return true;
    }

    // ----------------------------- Read loop -----------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Incoming? msg;
            try { msg = await _chan.ReadAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"[{_sessionId}] errore lettura: {ex.Message}");
                return;
            }
            if (msg is null) { _log($"[{_sessionId}] disconnesso"); return; }

            long recv = ServerClock.NowMs;

            // 1) Integrità del canale (firma + sequenza) su ogni messaggio firmato.
            var env = _ac.CheckEnvelope(msg);
            if (await ApplyAcAsync(env, ct)) return;        // kick
            if (env.Action == AcAction.Warn) continue;      // messaggio ignorato (replay)

            // 2) Coerenza temporale.
            var clk = _ac.CheckClock(msg.Ts, recv, _sessionStartServerMs);
            if (await ApplyAcAsync(clk, ct)) return;

            // 3) Calibrazione offset per lag compensation (prima misura).
            if (!_offsetInit) { _baseOffset = recv - msg.Ts; _offsetInit = true; }

            switch (msg.Type)
            {
                case MsgType.JoinRound: await HandleJoinAsync(msg.Deserialize<JoinRoundMsg>(), ct); break;
                case MsgType.SwitchWeapon: HandleSwitch(msg.Deserialize<SwitchWeaponMsg>()); break;
                case MsgType.Fire: if (await HandleFireAsync(msg, recv, ct)) return; break;
                case MsgType.Heartbeat: /* usato solo per clock/offset, già aggiornati */ break;
                default: _log($"[{_sessionId}] tipo sconosciuto: {msg.Type}"); break;
            }
        }
    }

    private async Task HandleJoinAsync(JoinRoundMsg join, CancellationToken ct)
    {
        long now = ServerClock.NowMs;
        RoundStartedMsg started;
        lock (_gate)
        {
            _difficulty = join.Difficulty;
            _weapon = join.Weapon;
            _ac.ResetRoundMetrics();
            _state = State.RoundActive;
            _roundId++;
            _targetsSpawned = 0;
            _totalScore = 0;
            _shots = 0;
            _hits = 0;
            _target = null;
            _nextSpawnAtMs = now + 800; // piccola attesa iniziale
            started = new RoundStartedMsg(_roundId, _difficulty, GameConfig.TargetsPerRound);
        }
        _log($"[{_sessionId}] round {started.RoundId} start ({join.Difficulty}, {join.Weapon})");
        await _chan.SendAsync(MsgType.RoundStarted, started, now, ct);
    }

    private void HandleSwitch(SwitchWeaponMsg sw)
    {
        lock (_gate) _weapon = sw.Weapon;
        _log($"[{_sessionId}] arma -> {sw.Weapon}");
    }

    private async Task<bool> HandleFireAsync(Incoming msg, long recv, CancellationToken ct)
    {
        var fire = msg.Deserialize<FireMsg>();

        // Validazione confini della mira (coordinate impossibili = manomissione).
        var bounds = _ac.CheckAimBounds(fire.AimX, fire.AimY);
        if (await ApplyAcAsync(bounds, ct)) return true;

        // Tempo-server stimato dello sparo (lag compensation).
        long latency = Math.Clamp((recv - msg.Ts) - _baseOffset, 0, GameConfig.MaxRewindMs);
        long shotServerMs = recv - latency;

        ShotResultMsg result;
        AcResult rateRes;

        lock (_gate)
        {
            // Limite di cadenza dell'arma: uno sparo troppo ravvicinato è rifiutato.
            var (accepted, rr) = _ac.RegisterShot(shotServerMs, GameConfig.For(_weapon));
            rateRes = rr;

            bool hit = false;
            int shotScore = 0;

            if (accepted && _state == State.RoundActive && _target is { Destroyed: false } t)
            {
                var (tx, ty, _, _) = t.StateAt(shotServerMs);
                double dx = fire.AimX - tx, dy = fire.AimY - ty;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist <= t.Radius)
                {
                    hit = true;
                    t.Destroyed = true;
                    _hits++;
                    double reaction = shotServerMs - t.SpawnMs;
                    _ac.RegisterHit(reaction, dist);
                    // Punteggio: base + bonus per precisione (centro = bonus pieno).
                    int bonus = (int)Math.Round(Math.Max(0, (t.Radius - dist) / t.Radius) * 5);
                    shotScore = 10 + bonus;
                    _totalScore += shotScore;
                }
                _shots++; // conteggio sparo legittimo (già contato in AntiCheat)
            }

            result = new ShotResultMsg(hit, _target?.Id ?? 0, shotScore, _totalScore, _shots, _hits);
        }

        if (await ApplyAcAsync(rateRes, ct)) return true;
        await _chan.SendAsync(MsgType.ShotResult, result, ServerClock.NowMs, ct);
        return false;
    }

    // ----------------------------- Tick loop -----------------------------

    private async Task TickLoopAsync(CancellationToken ct)
    {
        int tickMs = 1000 / GameConfig.ServerTickHz;
        int snapMs = 1000 / GameConfig.SnapshotHz;

        while (!ct.IsCancellationRequested)
        {
            long now = ServerClock.NowMs;
            var toSend = new List<(string type, object payload)>();
            string? kick = null;

            lock (_gate)
            {
                Advance(now, snapMs, toSend);
                if (_pendingKick is not null) { kick = _pendingKick; _pendingKick = null; }
            }

            foreach (var (type, payload) in toSend)
                await _chan.SendAsync(type, payload, ServerClock.NowMs, ct);

            if (kick is not null)
            {
                await _chan.SendAsync(MsgType.AntiCheat,
                    new AntiCheatMsg("aimbot_pattern", kick, "kick"), ServerClock.NowMs, ct);
                await _chan.SendAsync(MsgType.Kicked, new KickedMsg(kick), ServerClock.NowMs, ct);
                _log($"[{_sessionId}] KICK: {kick}");
                return;
            }

            try { await Task.Delay(tickMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Avanza la simulazione e accoda i messaggi da inviare. Chiamato
    /// sotto lock; non deve fare I/O.</summary>
    private void Advance(long now, int snapMs, List<(string, object)> toSend)
    {
        if (_state != State.RoundActive)
            return;

        // Bersaglio distrutto: rimuovilo e programma il prossimo.
        if (_target is { Destroyed: true })
        {
            _target = null;
            _nextSpawnAtMs = now + GameConfig.For(_difficulty).spawnIntervalMs;
        }
        // Bersaglio scaduto (mancato): avvisa il client e programma il prossimo.
        else if (_target is { } cur && cur.IsExpired(now))
        {
            toSend.Add((MsgType.TargetExpired, new TargetExpiredMsg(cur.Id)));
            _target = null;
            _nextSpawnAtMs = now + GameConfig.For(_difficulty).spawnIntervalMs;
        }

        // Spawn del prossimo bersaglio se è il momento e ne restano.
        if (_target is null && _targetsSpawned < GameConfig.TargetsPerRound && now >= _nextSpawnAtMs)
        {
            _target = SpawnTarget(now);
            _targetsSpawned++;
        }

        // Fine prova: tutti spawnati e nessuno attivo.
        if (_target is null && _targetsSpawned >= GameConfig.TargetsPerRound)
        {
            _state = State.RoundEnded;
            double acc = _shots > 0 ? (double)_hits / _shots * 100.0 : 0;
            toSend.Add((MsgType.RoundEnded,
                new RoundEndedMsg(_roundId, _totalScore, _hits, _shots, Math.Round(acc, 1))));

            var verdict = _ac.EvaluateRound(_difficulty);
            if (verdict.Action == AcAction.Warn)
                toSend.Add((MsgType.AntiCheat, new AntiCheatMsg(verdict.Code, verdict.Message, "warn")));
            else if (verdict.Action == AcAction.Kick)
                _pendingKick = verdict.Message;

            _log($"[{_sessionId}] round {_roundId} fine: score={_totalScore} " +
                 $"acc={acc:0.0}% ({_hits}/{_shots}) verdetto={verdict.Code}:{verdict.Action}");
            return;
        }

        // Snapshot periodico dello stato autorevole.
        if (now - _lastSnapshotMs >= snapMs)
        {
            _lastSnapshotMs = now;
            if (_target is { } t)
            {
                var (x, y, vx, vy) = t.StateAt(now);
                toSend.Add((MsgType.Snapshot,
                    new SnapshotMsg(true, t.Id, x, y, vx, vy, t.Radius, now)));
            }
            else
            {
                toSend.Add((MsgType.Snapshot, new SnapshotMsg(false, 0, 0, 0, 0, 0, 0, now)));
            }
        }
    }

    private ActiveTarget SpawnTarget(long now)
    {
        var ds = GameConfig.For(_difficulty);
        bool fromLeft = _rng.Next(2) == 0;
        int dir = fromLeft ? 1 : -1;
        double t = ds.crossMs / 1000.0;                     // durata dell'attraversamento (s)

        // Raggio casuale, uguale per tutte le prove.
        double radius = GameConfig.TargetRadiusMin +
            _rng.NextDouble() * (GameConfig.TargetRadiusMax - GameConfig.TargetRadiusMin);

        // Parte appena fuori da un lato e attraversa TUTTA la larghezza (più margine):
        // vx costante garantisce il passaggio "davanti" in ogni difficoltà.
        double startX = fromLeft ? -20 : GameConfig.WorldWidth + 20;
        double crossDist = GameConfig.WorldWidth + 80;
        double vx = dir * crossDist / t;

        // Arco verticale simmetrico: lanciato verso l'alto, con la gravità ricade
        // alla stessa quota dopo 't'. Altezza del picco = g*t^2/8.
        double startY = GameConfig.WorldHeight - (90 + _rng.NextDouble() * 70); // 90..160 dal fondo
        double vy = -0.5 * GameConfig.Gravity * t;

        return new ActiveTarget(_nextTargetId++, startX, startY, vx, vy,
            radius, now, ds.timeToLiveMs);
    }

    // ----------------------------- Utility AC -----------------------------

    /// <summary>Applica un esito anti-cheat. Ritorna true se la sessione va chiusa (kick).</summary>
    private async Task<bool> ApplyAcAsync(AcResult r, CancellationToken ct)
    {
        if (r.Action == AcAction.None) return false;

        string action = r.IsKick ? "kick" : "warn";
        await _chan.SendAsync(MsgType.AntiCheat, new AntiCheatMsg(r.Code, r.Message, action), ServerClock.NowMs, ct);
        _log($"[{_sessionId}] AC {action.ToUpper()} {r.Code}: {r.Message}");

        if (r.IsKick)
        {
            await _chan.SendAsync(MsgType.Kicked, new KickedMsg(r.Message), ServerClock.NowMs, ct);
            return true;
        }
        return false;
    }
}
