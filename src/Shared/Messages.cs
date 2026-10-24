namespace ClayPigeon.Shared;

/// <summary>
/// Tipi di messaggio del protocollo. Sono stringhe stabili perché viaggiano
/// sul filo come discriminatore dell'envelope (vedi <see cref="Protocol"/>).
/// </summary>
public static class MsgType
{
    // Client -> Server
    public const string Hello = "Hello";
    public const string JoinRound = "JoinRound";
    public const string Fire = "Fire";
    public const string SwitchWeapon = "SwitchWeapon";
    public const string Heartbeat = "Heartbeat";

    // Server -> Client
    public const string Welcome = "Welcome";
    public const string RoundStarted = "RoundStarted";
    public const string Snapshot = "Snapshot";
    public const string ShotResult = "ShotResult";
    public const string TargetExpired = "TargetExpired";
    public const string RoundEnded = "RoundEnded";
    public const string AntiCheat = "AntiCheat";
    public const string Kicked = "Kicked";
}

// ----------------------------- Client -> Server -----------------------------

/// <summary>Primo messaggio del client: avvia l'handshake. NON è firmato
/// perché la chiave di sessione non esiste ancora.</summary>
public sealed record HelloMsg(string ClientVersion, string ClientNonce);

/// <summary>Richiesta di iniziare una prova con difficoltà e arma scelte.</summary>
public sealed record JoinRoundMsg(Difficulty Difficulty, Weapon Weapon);

/// <summary>Uno sparo. Il client NON dice "ho colpito": dichiara solo DOVE e
/// QUANDO ha sparato. È il server a decidere l'esito.</summary>
public sealed record FireMsg(double AimX, double AimY, long ClientTimeMs);

public sealed record SwitchWeaponMsg(Weapon Weapon);

public sealed record HeartbeatMsg(long ClientTimeMs);

// ----------------------------- Server -> Client -----------------------------

public sealed record WelcomeMsg(
    string SessionId,
    string ServerNonce,
    long ServerTimeMs,
    int WorldWidth,
    int WorldHeight);

public sealed record RoundStartedMsg(
    int RoundId,
    Difficulty Difficulty,
    int TotalTargets);

/// <summary>Stato autorevole del bersaglio attivo in un certo istante.
/// Se non c'è bersaglio attivo, <see cref="HasTarget"/> è false.</summary>
public sealed record SnapshotMsg(
    bool HasTarget,
    int TargetId,
    double X,
    double Y,
    double Vx,
    double Vy,
    double Radius,
    long ServerTimeMs);

public sealed record ShotResultMsg(
    bool Hit,
    int TargetId,
    int ShotScore,
    int TotalScore,
    int ShotsFired,
    int Hits);

public sealed record TargetExpiredMsg(int TargetId);

public sealed record RoundEndedMsg(
    int RoundId,
    int TotalScore,
    int Hits,
    int Shots,
    double AccuracyPct);

/// <summary>Segnalazione anti-cheat verso il client. Action: "warn" o "kick".</summary>
public sealed record AntiCheatMsg(string Code, string Message, string Action);

public sealed record KickedMsg(string Reason);
