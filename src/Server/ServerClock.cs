using System.Diagnostics;

namespace ClayPigeon.Server;

/// <summary>
/// Orologio monotòno del server: millisecondi trascorsi dall'avvio del processo.
/// Usiamo <see cref="Stopwatch"/> (monotòno) e NON l'ora di sistema, perché
/// l'ora di sistema può essere spostata e non è adatta a misurare intervalli.
/// Tutte le decisioni temporali del server (fisica, cadenza di fuoco, lag comp)
/// si basano su questo orologio: è una sorgente di tempo che il client non può
/// toccare.
/// </summary>
public static class ServerClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static long NowMs => Sw.ElapsedMilliseconds;
}
