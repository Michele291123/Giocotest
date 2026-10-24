namespace ClayPigeon.Shared;

/// <summary>
/// Costanti e parametri di gioco CONDIVISI tra client e server.
///
/// Nota didattica fondamentale: queste costanti sono solo "di comodo" per far
/// combaciare la grafica del client con la simulazione del server. L'UNICA copia
/// che conta per punteggio, colpi e validazioni è quella del SERVER. Se un cheat
/// modifica questi valori sul client (es. raggio dei bersagli enorme per colpire
/// sempre), il server non ne tiene conto: lui usa i propri numeri. Questo è il
/// cuore del modello "server-authoritative".
/// </summary>
public static class GameConfig
{
    // Dimensioni dell'area di gioco in "coordinate mondo" (indipendenti dalla
    // risoluzione reale della finestra: il client scala in base alla finestra).
    public const int WorldWidth = 1280;
    public const int WorldHeight = 720;

    // Numero di bersagli per prova.
    public const int TargetsPerRound = 20;

    // Frequenza del tick di simulazione del server (Hz). Il server avanza la
    // fisica a passi fissi: rende la simulazione deterministica e verificabile.
    public const int ServerTickHz = 60;

    // Frequenza con cui il server invia gli "snapshot" di stato al client (Hz).
    // Più bassa del tick per risparmiare banda; il client interpola/estrapola.
    public const int SnapshotHz = 30;

    // Gravità applicata ai piattelli (px/s^2). Positiva = verso il basso.
    public const double Gravity = 420.0;

    // Finestra massima di "lag compensation": di quanto il server accetta di
    // riavvolgere lo stato per validare uno sparo rispetto al tempo dichiarato
    // dal client. Oltre questo, lo sparo è valutato sullo stato corrente.
    public const int MaxRewindMs = 250;

    // Tolleranza di clock: quanto può discostare il tempo dichiarato dal client
    // rispetto alla stima del server prima di insospettirsi (ms).
    public const int ClockToleranceMs = 500;

    /// <summary>Parametri che dipendono dalla difficoltà della prova.</summary>
    public static DifficultySettings For(Difficulty d) => d switch
    {
        Difficulty.Facile => new DifficultySettings(
            spawnIntervalMs: 1400,
            speedMin: 260, speedMax: 340,
            targetRadius: 38,
            timeToLiveMs: 4200),
        Difficulty.Media => new DifficultySettings(
            spawnIntervalMs: 950,
            speedMin: 440, speedMax: 560,
            targetRadius: 30,
            timeToLiveMs: 3000),
        Difficulty.Difficile => new DifficultySettings(
            spawnIntervalMs: 600,
            speedMin: 680, speedMax: 820,
            targetRadius: 23,
            timeToLiveMs: 2200),
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };

    /// <summary>Cadenza di fuoco e regole per ciascuna arma (validate dal server).</summary>
    public static WeaponSettings For(Weapon w) => w switch
    {
        // Fucile automatico: cadenza alta, fuoco continuo tenendo premuto.
        // minShotIntervalMs = intervallo minimo TRA colpi accettati dal server.
        Weapon.FucileAutomatico => new WeaponSettings(
            minShotIntervalMs: 110,   // ~545 colpi/min teorici
            semiAuto: false,
            tolerancePerShotMs: 15),  // piccola tolleranza per jitter di rete
        // Pistola semiautomatica: un colpo per pressione, cadenza più bassa.
        Weapon.PistolaSemiauto => new WeaponSettings(
            minShotIntervalMs: 230,
            semiAuto: true,
            tolerancePerShotMs: 15),
        _ => throw new ArgumentOutOfRangeException(nameof(w))
    };
}

public enum Difficulty { Facile, Media, Difficile }

public enum Weapon { FucileAutomatico, PistolaSemiauto }

public readonly record struct DifficultySettings(
    int spawnIntervalMs,
    double speedMin,
    double speedMax,
    double targetRadius,
    int timeToLiveMs);

public readonly record struct WeaponSettings(
    int minShotIntervalMs,
    bool semiAuto,
    int tolerancePerShotMs);
