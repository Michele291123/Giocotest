# Guida alla build e all'avvio

Guida passo-passo per compilare ed eseguire il progetto. Tre pezzi:
**Server** (sul Raspberry Pi), **Client** (sul PC Windows), **CheatSim** (su un PC
qualsiasi con .NET). Schema:

```
   PC Windows                         Raspberry Pi
 ┌─────────────┐    rete TCP :5055  ┌──────────────┐
 │   Client    │ ◀───────────────▶ │    Server    │
 │ (.exe GUI)  │                    │ (autorevole) │
 └─────────────┘                    └──────────────┘
        ▲
        │  gli stessi messaggi, ma "scorretti"
 ┌─────────────┐
 │  CheatSim   │  (test anti-cheat, contro il TUO server)
 └─────────────┘
```

---

## 0) Prerequisiti: installare il .NET SDK 8

Serve su **ogni macchina dove compili**. Scaricalo da
<https://dotnet.microsoft.com/download/dotnet/8.0> (scegli **SDK**, non solo il
Runtime). Verifica:
```bash
dotnet --version      # deve stampare 8.x.x
```

- **Windows (PC del client):** installa l'SDK x64. Per compilare la GUI WinForms
  non serve altro (il "Windows Desktop" è incluso nell'SDK su Windows).
- **Raspberry Pi (server):** vedi sotto — puoi installare l'SDK sul Pi **oppure**
  compilare sul PC e copiare il risultato (consigliato, più veloce).

> **Nota:** il client (`src/Client`) usa WinForms e si compila **solo su Windows**.
> Server e CheatSim si compilano anche su Linux/macOS/Pi.

---

## 1) Server — sul Raspberry Pi

### Opzione A (consigliata): compila sul PC, copia sul Pi
Dalla cartella del progetto, sul PC:
```bash
./scripts/build-server-pi.sh             # Pi a 64 bit (Raspberry Pi OS 64-bit)
./scripts/build-server-pi.sh linux-arm   # Pi a 32 bit
```
Produce un **eseguibile autonomo** in `dist/server/` (non serve installare .NET sul
Pi). Copialo sul Pi, per es.:
```bash
scp -r dist/server pi@<IP-DEL-PI>:~/claypigeon
```
Poi, **sul Pi**:
```bash
cd ~/claypigeon
chmod +x ClayPigeonServer
./ClayPigeonServer 5055
```

### Opzione B: compila direttamente sul Pi
Con l'SDK installato sul Pi:
```bash
dotnet run --project src/Server -- 5055
```

### Dopo l'avvio
- Trova l'IP del Pi: `hostname -I` (es. `192.168.1.50`).
- Se hai un firewall, apri la porta: `sudo ufw allow 5055/tcp`.
- Il server stampa in console ogni connessione e **ogni flag anti-cheat**.
- Per cambiare porta: passa un altro numero come argomento (`./ClayPigeonServer 6000`).

---

## 2) Client — sul PC Windows

In **PowerShell**, dalla cartella del progetto:
```powershell
./scripts/build-client-windows.ps1
```
Produce un singolo **`dist/client/ClayPigeonClient.exe`** autonomo. Avvialo
indicando l'IP del Pi e la porta:
```powershell
./dist/client/ClayPigeonClient.exe <IP-DEL-PI> 5055
```
Senza argomenti si collega a `127.0.0.1:5055` (utile se server e client sono sullo
stesso PC per una prova rapida).

**Comandi in gioco:**
| Tasto | Azione |
|---|---|
| `F` / `M` / `D` | Avvia prova Facile / Media / Difficile |
| `1` | Pistola semiautomatica |
| `2` | Fucile automatico |
| Mouse | Mira |
| Tasto sinistro | Spara (tieni premuto = raffica col fucile) |
| `Esc` | Esci |

---

## 3) CheatSim — su un PC qualsiasi con .NET

Con il **server avviato**:
```bash
./scripts/build-cheatsim.sh
dotnet dist/cheatsim/ClayPigeonCheatSim.dll all <IP-DEL-PI> 5055
```
Oppure senza build esplicita:
```bash
dotnet run --project src/CheatSim -- all <IP-DEL-PI> 5055
```
Scenari singoli al posto di `all`:
`forge` · `replay` · `clock` · `bounds` · `firerate` · `aimbot` · `honest`.

Ogni riga `<<< ANTI-CHEAT ...` è una difesa del server che scatta. Lo scenario
`honest` gioca in modo corretto e **non** deve essere segnalato.

---

## 4) Tutto su un solo PC (prova rapida di sviluppo)

Utile per testare server + cheatsim insieme (il client GUI resta su Windows):
```bash
dotnet run --project src/Server            # finestra 1
dotnet run --project src/CheatSim -- all    # finestra 2
```

---

## Build dell'intera soluzione

`dotnet build ClayPigeon.sln` compila tutto **solo su Windows** (per via del client
WinForms). Su Linux/Pi compila i singoli progetti che ti servono:
```bash
dotnet build src/Server/Server.csproj
dotnet build src/CheatSim/CheatSim.csproj
```

---

## Risoluzione problemi

| Sintomo | Causa / rimedio |
|---|---|
| `dotnet: command not found` | SDK non installato o non nel PATH — vedi passo 0. |
| Il client dice "Connessione fallita" | Server spento, IP/porta sbagliati, o firewall sul Pi. Verifica `hostname -I` e la porta. |
| `NETSDK1100` / errore sul target `-windows` | Stai compilando il **Client** fuori da Windows: compilalo su Windows. |
| `Permission denied` sul Pi | `chmod +x ClayPigeonServer`. |
| Build Pi per OS a 32 bit | Usa `./scripts/build-server-pi.sh linux-arm`. |
| Errore `Platform not supported` lanciando il server sul Pi | RID sbagliato: 64-bit → `linux-arm64`, 32-bit → `linux-arm`. |
| Client e server su reti diverse | Devono stare sulla stessa LAN (o configura port forwarding sul router verso il Pi). |

Per capire **cosa fa** il codice e **perché** le difese funzionano, leggi
[REPORT.md](REPORT.md).
