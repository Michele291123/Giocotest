using ClayPigeon.Shared;

namespace ClayPigeon.Server;

/// <summary>
/// Il bersaglio (piattello) AUTOREVOLE: vive solo nel server. Il moto è
/// analitico (posizione calcolabile a qualunque istante) così il server può
/// "riavvolgere" la posizione a un tempo passato per la lag compensation senza
/// tenere uno storico. Il client riceve solo snapshot e disegna; non ha voce in
/// capitolo sulla posizione reale.
/// </summary>
public sealed class ActiveTarget
{
    public int Id { get; }
    public double X0 { get; }
    public double Y0 { get; }
    public double Vx0 { get; }
    public double Vy0 { get; }
    public double Radius { get; }
    public long SpawnMs { get; }
    public long TtlMs { get; }

    public bool Destroyed { get; set; }

    public ActiveTarget(int id, double x0, double y0, double vx0, double vy0,
        double radius, long spawnMs, long ttlMs)
    {
        Id = id; X0 = x0; Y0 = y0; Vx0 = vx0; Vy0 = vy0;
        Radius = radius; SpawnMs = spawnMs; TtlMs = ttlMs;
    }

    /// <summary>Stato (posizione e velocità) del bersaglio al tempo server indicato.</summary>
    public (double x, double y, double vx, double vy) StateAt(long serverMs)
    {
        double dt = (serverMs - SpawnMs) / 1000.0;
        if (dt < 0) dt = 0;
        double x = X0 + Vx0 * dt;
        double y = Y0 + Vy0 * dt + 0.5 * GameConfig.Gravity * dt * dt;
        double vy = Vy0 + GameConfig.Gravity * dt;
        return (x, y, Vx0, vy);
    }

    /// <summary>Il bersaglio è scaduto (per tempo o perché uscito dallo schermo)?</summary>
    public bool IsExpired(long serverMs)
    {
        if (serverMs - SpawnMs >= TtlMs) return true;
        var (x, y, _, _) = StateAt(serverMs);
        // margine ampio: è "uscito" solo quando è ben oltre i bordi.
        if (y - Radius > GameConfig.WorldHeight + 80) return true;
        if (x + Radius < -120 || x - Radius > GameConfig.WorldWidth + 120) return true;
        return false;
    }
}
