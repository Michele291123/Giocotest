# Clay Pigeon — Tiro al Piattello (progetto didattico anti-cheat)

Gioco 2D di tiro al piattello con **architettura client-server moderna** e difese
**anti-cheat server-authoritative**. Scopo: **studiare la didattica dei cheat e
delle contromisure**. C'è anche un **laboratorio di simulazione cheat** che attacca
il *tuo stesso server* a livello di rete, così vedi ogni difesa scattare.

> Per capire *come* e *perché* funziona tutto, leggi **[REPORT.md](REPORT.md)**.
> È il cuore del progetto: spiega passo-passo il modello e le difese.

## Com'è fatto

| Progetto | Cos'è | Dove gira |
|---|---|---|
| `src/Shared` | Protocollo di rete, messaggi, crittografia (HMAC), costanti di gioco | libreria condivisa |
| `src/Server` | Il "cervello": simula i piattelli, decide i colpi, tiene il punteggio, applica l'anti-cheat | **Raspberry Pi** (o qualunque PC) |
| `src/Client` | Il gioco che vedi e giochi (WinForms, 2D) | **PC Windows** (`.exe`) |
| `src/CheatSim` | Simulatori di cheat (speedhack, aimbot, pacchetti falsi, replay, clock, ecc.) | il tuo PC |

- **3 prove**: Facile, Media, Difficile (cambiano intervallo di comparsa e velocità del piattello).
- **20 bersagli per prova**, uno alla volta. Finita la prova puoi rigiocarne un'altra.
- **2 armi**: Fucile automatico (fuoco continuo) e Pistola semiautomatica (un colpo a pressione).

## Requisiti

- **.NET SDK 8** su ogni macchina dove compili: <https://dotnet.microsoft.com/download/dotnet/8.0>
- Il client richiede **Windows** (usa WinForms). Server e CheatSim girano anche su Linux/Pi.

## Build & avvio

> Guida completa passo-passo (prerequisiti, Pi, Windows, risoluzione problemi):
> **[BUILD.md](BUILD.md)**. Qui sotto la versione rapida.

### 1) Server sul Raspberry Pi
Sul Pi (o compili sul PC e copi la cartella `dist/server` sul Pi):
```bash
./scripts/build-server-pi.sh            # Pi a 64 bit (default: linux-arm64)
./scripts/build-server-pi.sh linux-arm  # Pi a 32 bit
# poi, sul Pi:
./ClayPigeonServer 5055
```
Annota l'IP del Pi (`hostname -I`) e apri la porta 5055 se hai un firewall.

### 2) Client sul PC Windows
In PowerShell:
```powershell
./scripts/build-client-windows.ps1
./dist/client/ClayPigeonClient.exe <IP-DEL-PI> 5055
```
Comandi in gioco: **F/M/D** = avvia prova Facile/Media/Difficile · **1** = pistola · **2** = fucile ·
mouse = mira · tasto sinistro = spara · **Esc** = esci.

### 3) Laboratorio cheat (contro il tuo server)
Con il server avviato, da un PC con .NET:
```bash
./scripts/build-cheatsim.sh
dotnet dist/cheatsim/ClayPigeonCheatSim.dll all <IP-DEL-PI> 5055
```
Scenari singoli: `forge`, `replay`, `clock`, `bounds`, `firerate`, `aimbot`, `honest`.
Ogni riga `<<< ANTI-CHEAT ...` è una difesa del server che scatta. Lo scenario
`honest` gioca correttamente e **non** deve essere segnalato (controllo falsi positivi).

### Sviluppo rapido (tutto su un PC con .NET)
```bash
dotnet run --project src/Server            # finestra 1
dotnet run --project src/CheatSim -- all    # finestra 2
# (il client WinForms va avviato su Windows)
```

## Nota etica / ambito
I simulatori di cheat lavorano **solo contro il server di questo progetto**, a
livello di protocollo: sono test d'integrazione dell'anti-cheat. Le tecniche a
livello di memoria/kernel e la loro difesa sono spiegate **a parole** nel report,
senza fornire strumenti pronti all'uso. Usa tutto solo sul tuo sistema.
