using System.Drawing.Drawing2D;
using ClayPigeon.Shared;

namespace ClayPigeon.Client;

/// <summary>
/// Finestra di gioco (WinForms + GDI+). Disegna la scena a partire dagli
/// snapshot autorevoli del server e invia solo input di mira/sparo. Nessun
/// punteggio o esito colpo è calcolato qui: tutto arriva dal server.
/// </summary>
public sealed class GameForm : Form
{
    private enum UiState { Connecting, Menu, Playing, Ended, Kicked, Error }

    private readonly NetClient _net = new();
    private readonly string _host;
    private readonly int _port;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

    private UiState _state = UiState.Connecting;
    private string _statusText = "Connessione al server...";

    // Stato arma/input.
    private Weapon _weapon = Weapon.PistolaSemiauto;
    private Difficulty _lastDifficulty = Difficulty.Facile;
    private bool _leftHeld;
    private bool _pistolArmed = true;     // semiauto: serve rilascio tra i colpi
    private long _lastFireMs;

    // Ultimo snapshot del bersaglio.
    private bool _hasTarget;
    private double _sx, _sy, _svx, _svy, _srad;
    private long _snapRecvMs;

    // Statistiche correnti (dal server).
    private int _score, _shots, _hits;
    private int _roundId;
    private RoundEndedMsg? _lastRound;
    private string _kickReason = "";

    // Feedback sparo + avvisi anti-cheat.
    private long _shotFeedbackMs = long.MinValue;
    private bool _shotFeedbackHit;
    private readonly List<(string text, Color color, long expireMs)> _toasts = new();
    private long _lastHeartbeatMs;

    private PointF _mouse;

    public GameForm(string host, int port)
    {
        _host = host; _port = port;
        Text = "Clay Pigeon — Tiro al Piattello (client)";
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(640, 400);
        BackColor = Color.FromArgb(20, 24, 32);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        MouseMove += (_, e) => _mouse = e.Location;
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        KeyDown += OnKeyDown;

        _timer.Tick += OnTick;

        WireEvents();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _net.ConnectAsync(_host, _port);
            Post(() => { _state = UiState.Menu; _statusText = ""; });
        }
        catch (Exception ex)
        {
            Post(() => { _state = UiState.Error; _statusText = $"Connessione fallita a {_host}:{_port}\n{ex.Message}"; });
        }
    }

    // ----------------------------- Eventi di rete -----------------------------

    private void WireEvents()
    {
        _net.OnRoundStarted += m => Post(() =>
        {
            _state = UiState.Playing;
            _roundId = m.RoundId;
            _score = _shots = _hits = 0;
            _lastRound = null;
        });
        _net.OnSnapshot += m => Post(() =>
        {
            _hasTarget = m.HasTarget;
            if (m.HasTarget)
            {
                _sx = m.X; _sy = m.Y; _svx = m.Vx; _svy = m.Vy; _srad = m.Radius;
                _snapRecvMs = _net.ClientMs;
            }
        });
        _net.OnShotResult += m => Post(() =>
        {
            _score = m.TotalScore; _shots = m.ShotsFired; _hits = m.Hits;
            _shotFeedbackMs = _net.ClientMs; _shotFeedbackHit = m.Hit;
        });
        _net.OnTargetExpired += _ => Post(() => _hasTarget = false);
        _net.OnRoundEnded += m => Post(() => { _state = UiState.Ended; _lastRound = m; _hasTarget = false; });
        _net.OnAntiCheat += m => Post(() =>
        {
            var col = m.Action == "kick" ? Color.OrangeRed : Color.Gold;
            _toasts.Add(($"[ANTI-CHEAT {m.Action.ToUpper()}] {m.Code}: {m.Message}", col, _net.ClientMs + 6000));
        });
        _net.OnKicked += m => Post(() => { _state = UiState.Kicked; _kickReason = m.Reason; _hasTarget = false; });
        _net.OnDisconnected += reason => Post(() =>
        {
            if (_state != UiState.Kicked) { _state = UiState.Error; _statusText = $"Disconnesso: {reason}"; }
        });
    }

    private void Post(Action a)
    {
        if (IsDisposed) return;
        try { if (IsHandleCreated) BeginInvoke(a); else a(); }
        catch (ObjectDisposedException) { /* in chiusura */ }
    }

    // ----------------------------- Input -----------------------------

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _leftHeld = true;
        if (_state != UiState.Playing) return;

        if (_weapon == Weapon.PistolaSemiauto)
        {
            if (_pistolArmed) { _pistolArmed = false; TryFire(); }
        }
        else
        {
            TryFire(); // primo colpo immediato; i successivi dal timer
        }
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _leftHeld = false;
        _pistolArmed = true; // semiauto riarmata al rilascio
    }

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.D1: SetWeapon(Weapon.PistolaSemiauto); break;
            case Keys.D2: SetWeapon(Weapon.FucileAutomatico); break;
            case Keys.F: StartRound(Difficulty.Facile); break;
            case Keys.M: StartRound(Difficulty.Media); break;
            case Keys.D: StartRound(Difficulty.Difficile); break;
            case Keys.Escape: Close(); break;
        }
    }

    private void SetWeapon(Weapon w)
    {
        _weapon = w;
        if (_state is UiState.Playing or UiState.Menu or UiState.Ended)
            _ = _net.SwitchWeaponAsync(w);
    }

    private void StartRound(Difficulty d)
    {
        if (_state is not (UiState.Menu or UiState.Ended)) return;
        _lastDifficulty = d;
        _ = _net.JoinRoundAsync(d, _weapon);
    }

    private void TryFire()
    {
        long now = _net.ClientMs;
        var w = GameConfig.For(_weapon);
        // Il client rispetta la cadenza dell'arma: è il comportamento "onesto".
        // (Il server la impone comunque; un cheat che la ignora viene sgamato.)
        if (now - _lastFireMs < w.minShotIntervalMs) return;
        _lastFireMs = now;

        var (wx, wy) = ScreenToWorld(_mouse);
        _ = _net.FireAsync(wx, wy);
    }

    private void OnTick(object? s, EventArgs e)
    {
        long now = _net.ClientMs;

        // Fuoco automatico mentre il tasto è premuto.
        if (_state == UiState.Playing && _leftHeld && _weapon == Weapon.FucileAutomatico)
            TryFire();

        // Heartbeat periodico (serve al server per clock/lag).
        if (now - _lastHeartbeatMs > 500 && _state is UiState.Playing or UiState.Menu or UiState.Ended)
        {
            _lastHeartbeatMs = now;
            _ = _net.HeartbeatAsync();
        }

        // Pulizia toast scaduti.
        _toasts.RemoveAll(t => now > t.expireMs);

        Invalidate();
    }

    // ----------------------------- Proiezione mondo/schermo -----------------------------

    private (float scale, float ox, float oy) View()
    {
        float scale = Math.Min((float)ClientSize.Width / GameConfig.WorldWidth,
                               (float)ClientSize.Height / GameConfig.WorldHeight);
        float ox = (ClientSize.Width - GameConfig.WorldWidth * scale) / 2f;
        float oy = (ClientSize.Height - GameConfig.WorldHeight * scale) / 2f;
        return (scale, ox, oy);
    }

    private PointF WorldToScreen(double x, double y)
    {
        var (sc, ox, oy) = View();
        return new PointF((float)(ox + x * sc), (float)(oy + y * sc));
    }

    private (double x, double y) ScreenToWorld(PointF p)
    {
        var (sc, ox, oy) = View();
        double x = (p.X - ox) / sc;
        double y = (p.Y - oy) / sc;
        return (Math.Clamp(x, 0, GameConfig.WorldWidth), Math.Clamp(y, 0, GameConfig.WorldHeight));
    }

    // ----------------------------- Disegno -----------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor); // pulisce tutta la finestra (anche le bande del letterbox)
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawBackground(g);

        if (_hasTarget && _state == UiState.Playing)
            DrawTarget(g);

        DrawCrosshair(g);
        DrawHud(g);
        DrawToasts(g);

        switch (_state)
        {
            case UiState.Connecting:
            case UiState.Error:
                DrawCenterPanel(g, _state == UiState.Error ? "ERRORE" : "Connessione",
                    _statusText, _state == UiState.Error ? Color.OrangeRed : Color.White);
                break;
            case UiState.Menu:
                DrawCenterPanel(g, "TIRO AL PIATTELLO",
                    "Scegli la prova:\n\n[F] Facile    [M] Media    [D] Difficile\n\n" +
                    "Armi:  [1] Pistola semiauto    [2] Fucile automatico\n" +
                    $"Arma attuale: {WeaponName(_weapon)}\n\n" +
                    "Mira col mouse, spara col tasto sinistro.", Color.White);
                break;
            case UiState.Ended when _lastRound is { } r:
                DrawCenterPanel(g, "PROVA TERMINATA",
                    $"Punteggio: {r.TotalScore}\nColpiti: {r.Hits}/{r.Shots}\nPrecisione: {r.AccuracyPct}%\n\n" +
                    "[F] Facile   [M] Media   [D] Difficile per rigiocare", Color.White);
                break;
            case UiState.Kicked:
                DrawCenterPanel(g, "ESPULSO DAL SERVER", _kickReason + "\n\n(anti-cheat)", Color.OrangeRed);
                break;
        }
    }

    private void DrawBackground(Graphics g)
    {
        var (sc, ox, oy) = View();
        var world = new RectangleF(ox, oy, GameConfig.WorldWidth * sc, GameConfig.WorldHeight * sc);
        using var sky = new LinearGradientBrush(world,
            Color.FromArgb(70, 120, 180), Color.FromArgb(140, 190, 230), LinearGradientMode.Vertical);
        g.FillRectangle(sky, world);
        // Terreno.
        float groundY = (float)(oy + (GameConfig.WorldHeight - 40) * sc);
        using var ground = new SolidBrush(Color.FromArgb(60, 90, 50));
        g.FillRectangle(ground, world.Left, groundY, world.Width, world.Bottom - groundY);
    }

    private void DrawTarget(Graphics g)
    {
        double dt = Math.Clamp((_net.ClientMs - _snapRecvMs) / 1000.0, 0, 0.2);
        double x = _sx + _svx * dt;
        double y = _sy + _svy * dt + 0.5 * GameConfig.Gravity * dt * dt;

        var (sc, _, _) = View();
        var c = WorldToScreen(x, y);
        float r = (float)(_srad * sc);

        using var body = new SolidBrush(Color.FromArgb(230, 120, 40));
        using var rim = new Pen(Color.FromArgb(90, 40, 10), 2);
        g.FillEllipse(body, c.X - r, c.Y - r, r * 2, r * 2);
        g.DrawEllipse(rim, c.X - r, c.Y - r, r * 2, r * 2);
        using var inner = new SolidBrush(Color.FromArgb(255, 180, 90));
        g.FillEllipse(inner, c.X - r * 0.45f, c.Y - r * 0.45f, r * 0.9f, r * 0.9f);
    }

    private void DrawCrosshair(Graphics g)
    {
        // Feedback breve dell'ultimo sparo (verde = colpito, rosso = mancato).
        long since = _net.ClientMs - _shotFeedbackMs;
        Color col = Color.White;
        if (since < 250) col = _shotFeedbackHit ? Color.LimeGreen : Color.Tomato;

        using var pen = new Pen(col, 2);
        float x = _mouse.X, y = _mouse.Y, s = 12;
        g.DrawLine(pen, x - s, y, x - 4, y);
        g.DrawLine(pen, x + 4, y, x + s, y);
        g.DrawLine(pen, x, y - s, x, y - 4);
        g.DrawLine(pen, x, y + 4, x, y + s);
        g.DrawEllipse(pen, x - 2, y - 2, 4, 4);
    }

    private void DrawHud(Graphics g)
    {
        if (_state is UiState.Connecting or UiState.Error) return;
        using var font = new Font("Consolas", 12, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        g.FillRectangle(bg, 8, 8, 320, 92);
        using var fg = new SolidBrush(Color.White);
        double acc = _shots > 0 ? (double)_hits / _shots * 100 : 0;
        string txt =
            $"Arma: {WeaponName(_weapon)}\n" +
            $"Prova: {_lastDifficulty}   Round #{_roundId}\n" +
            $"Punteggio: {_score}   Colpiti: {_hits}/{_shots}\n" +
            $"Precisione: {acc:0.0}%   [sessione {_net.SessionId}]";
        g.DrawString(txt, font, fg, 14, 12);
    }

    private void DrawToasts(Graphics g)
    {
        using var font = new Font("Consolas", 10, FontStyle.Bold);
        float y = ClientSize.Height - 24;
        for (int i = _toasts.Count - 1; i >= 0 && i >= _toasts.Count - 5; i--)
        {
            var (text, color, _) = _toasts[i];
            using var b = new SolidBrush(color);
            g.DrawString(text, font, b, 12, y);
            y -= 20;
        }
    }

    private void DrawCenterPanel(Graphics g, string title, string body, Color accent)
    {
        using var dim = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        g.FillRectangle(dim, 0, 0, ClientSize.Width, ClientSize.Height);
        int w = 640, h = 320;
        var r = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        using var panel = new SolidBrush(Color.FromArgb(230, 24, 28, 38));
        using var border = new Pen(accent, 2);
        g.FillRectangle(panel, r);
        g.DrawRectangle(border, r);

        using var tFont = new Font("Segoe UI", 22, FontStyle.Bold);
        using var bFont = new Font("Segoe UI", 13, FontStyle.Regular);
        using var tBrush = new SolidBrush(accent);
        using var bBrush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(title, tFont, tBrush, new RectangleF(r.X, r.Y + 24, r.Width, 40), sf);
        g.DrawString(body, bFont, bBrush,
            new RectangleF(r.X + 30, r.Y + 80, r.Width - 60, r.Height - 100),
            new StringFormat { Alignment = StringAlignment.Center });
    }

    private static string WeaponName(Weapon w) =>
        w == Weapon.FucileAutomatico ? "Fucile automatico" : "Pistola semiauto";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _net.Dispose();
        base.OnFormClosed(e);
    }
}
