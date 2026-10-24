# Report: come funziona l'anti-cheat di questo gioco (e perché)

Questo documento spiega il progetto a chi vuole **imparare a costruire anti-cheat
moderni**. Parte dal principio fondamentale, mostra ogni difesa con il codice che
la implementa, spiega *perché* funziona e *dove* ha dei limiti, e collega ogni
difesa al simulatore di cheat che la mette alla prova.

Indice:
1. [L'idea centrale: il server ha sempre ragione](#1-lidea-centrale-il-server-ha-sempre-ragione)
2. [Il modello a messaggi e il ciclo di gioco](#2-il-modello-a-messaggi-e-il-ciclo-di-gioco)
3. [Modello delle minacce: che tipi di cheat esistono](#3-modello-delle-minacce-che-tipi-di-cheat-esistono)
4. [Le difese, una per una](#4-le-difese-una-per-una)
5. [La verità scomoda: non puoi nascondere i dati che il client deve disegnare](#5-la-verità-scomoda-non-puoi-nascondere-i-dati-che-il-client-deve-disegnare)
6. [Lag compensation: utilità e rischio](#6-lag-compensation-utilità-e-rischio)
7. [Difese lato client (kernel anti-cheat): cosa sono e perché non bastano](#7-difese-lato-client-kernel-anti-cheat-cosa-sono-e-perché-non-bastano)
8. [Mappa cheat → difesa → codice](#8-mappa-cheat--difesa--codice)
9. [Come tarare e come estendere](#9-come-tarare-e-come-estendere)
10. [Glossario](#10-glossario)

---

## 1. L'idea centrale: il server ha sempre ragione

La regola numero uno dell'anti-cheat moderno è una sola:

> **Non fidarti mai del client. Il client è in mano all'avversario.**

Qualunque cosa giri sul PC del giocatore può essere letta, modificata, falsificata.
La memoria si può editare, il traffico si può intercettare, il tempo si può
manipolare, il binario si può disassemblare. Quindi **nessuna decisione che conta
può essere presa dal client**.

In questo progetto il client fa solo due cose oneste:
- **disegna** quello che il server gli dice (gli *snapshot* dei piattelli);
- **dichiara l'intenzione** del giocatore: "ho sparato *qui* a *questo istante*".

Tutto il resto vive nel **server** (`src/Server`):
- genera i piattelli e la loro traiettoria;
- decide se uno sparo ha colpito;
- tiene il punteggio;
- applica l'anti-cheat.

Questo si chiama **modello server-authoritative** (il server è l'autorità). È il
modello di Valorant, CS2, Overwatch, Fortnite e praticamente ogni sparatutto
competitivo serio. Lo vedi concretamente in
[`src/Server/GameSession.cs`](src/Server/GameSession.cs): il metodo
`HandleFireAsync` riceve *dove* e *quando* il client dice di aver sparato, ma è il
**server** a calcolare la posizione reale del bersaglio in quell'istante e a
decidere `hit` / non `hit`. Il client non manda mai "ho fatto 10 punti": riceve il
punteggio dal server tramite `ShotResultMsg`.

**Perché funziona:** se il punteggio e i colpi li calcola il server con i *suoi*
dati, un cheat sul client può al massimo cambiare ciò che il giocatore *vede*, non
ciò che *conta*. Il contorno di questo principio — "e allora cosa può ancora fare
un cheat?" — è il tema del resto del report.

---

## 2. Il modello a messaggi e il ciclo di gioco

Client e server parlano con messaggi incapsulati in un *envelope*
([`src/Shared/Protocol.cs`](src/Shared/Protocol.cs)). Formato sul filo:

```
[4 byte: lunghezza][ JSON dell'envelope ]
envelope = { type, seq, ts, mac, payload }
```

- `type` — tipo di messaggio (es. `Fire`, `Snapshot`).
- `seq` — numero di sequenza crescente (anti-replay).
- `ts` — timestamp del mittente (controllo del clock).
- `mac` — firma HMAC-SHA256 del messaggio (autenticità/integrità).
- `payload` — il messaggio vero, serializzato come stringa JSON.

I tipi di messaggio sono in [`src/Shared/Messages.cs`](src/Shared/Messages.cs).

**Ciclo di una partita** (vedi `GameSession`):
1. **Handshake**: il client manda `Hello`, il server risponde `Welcome` e si deriva
   una **chiave di sessione** condivisa (vedi §4.1).
2. Il client manda `JoinRound { difficoltà, arma }`.
3. Il server avvia la prova (`RoundStarted`), poi in un **tick loop** a 60 Hz
   simula i piattelli e manda **snapshot** dello stato a 30 Hz.
4. Il giocatore spara → `Fire { aimX, aimY, clientTime }`; il server valida e
   risponde `ShotResult { hit, punteggio, ... }`.
5. Dopo 20 bersagli → `RoundEnded` con statistiche; si può rigiocare.

Il server avanza la fisica a **passi fissi** e con tempo **monotòno**
([`src/Server/ServerClock.cs`](src/Server/ServerClock.cs)): questo rende la
simulazione **deterministica e verificabile**, requisito per poter giudicare se un
comportamento è plausibile.

---

## 3. Modello delle minacce: che tipi di cheat esistono

Per difendersi bisogna prima classificare l'avversario. I cheat si dividono per
**dove** agiscono:

**A. Cheat di rete (dall'esterno del gioco)** — l'attaccante non modifica il gioco,
ma manomette il traffico:
- **Injection / forgery**: costruisce pacchetti falsi ("ho colpito tutti").
- **Replay**: ri-manda pacchetti validi già catturati.
- **Tampering**: altera i valori nei pacchetti legittimi.

**B. Cheat lato client (dentro/accanto al gioco)** — l'attaccante esegue codice sul
proprio PC:
- **Speedhack / auto-fire**: accelera il tempo o automatizza il grilletto per
  sparare più in fretta.
- **Aimbot**: mira automaticamente (perfetta, istantanea).
- **Triggerbot**: spara da solo quando il mirino è sul bersaglio.
- **Wallhack / ESP**: rende visibili informazioni che il giocatore non dovrebbe
  vedere (qui poco rilevante: un piattello è sempre visibile).
- **Memory editing**: cambia punteggio/munizioni/stato in RAM.
- **Clock manipulation**: falsa il tempo dichiarato al server.

Questo progetto **neutralizza o rileva tutta la categoria A** (con autorità del
server + crittografia) e **la maggior parte degli effetti della categoria B** (con
autorità del server + rilevamento comportamentale). Ciò che resta della categoria B
— avere comunque codice ostile sul PC — è il dominio dell'**anti-cheat lato client**
(§7), che spieghiamo a parole.

---

## 4. Le difese, una per una

Tutte le regole stanno in [`src/Server/AntiCheat.cs`](src/Server/AntiCheat.cs) e
vengono invocate da `GameSession`. Ogni difesa ha: *cosa fa*, *come*, *perché
funziona*, *limiti*, e *quale scenario del CheatSim la dimostra*.

### 4.1 Autenticità dei messaggi (HMAC) — contro forgery e tampering

**Cosa.** Ogni messaggio (dopo l'handshake) porta un **MAC** = HMAC-SHA256 calcolato
su `type|seq|ts|payload` con una **chiave di sessione**. Il server ricalcola il MAC e
confronta ([`Security.VerifyMac`](src/Shared/Security.cs),
`AntiCheat.CheckEnvelope`). Confronto a **tempo costante** per non trapelare
informazioni via timing.

**Come nasce la chiave.** Nell'handshake client e server si scambiano due *nonce*
casuali; la chiave di sessione è `HMAC(rootKey, sessionId|clientNonce|serverNonce)`
([`Security.DeriveSessionKey`](src/Shared/Security.cs)). Legandola ai nonce, ogni
sessione ha una chiave diversa: un pacchetto di una sessione non vale in un'altra.

**Perché funziona.** Chi non possiede la chiave non può produrre un MAC valido:
qualunque pacchetto iniettato o alterato "da fuori" viene rifiutato. Alterare anche
un solo byte del payload cambia il MAC.

**Limiti (importante!).** La `rootKey` vive nel binario del client. Chi fa reverse
engineering del client può estrarla e **firmare pacchetti validi**. Quindi l'HMAC
ferma gli attaccanti *esterni*, **non** un cheat che gira dentro il client ufficiale.
È una difesa necessaria ma non sufficiente: la vera barriera contro i cheat interni
è l'autorità del server (§1) + il rilevamento comportamentale (§4.5). In un gioco
reale la chiave non sarebbe una costante nel sorgente ma consegnata in modo
offuscato/attestato e ruotata — resta comunque estraibile in linea di principio.

**Dimostrazione.** `CheatSim` scenario **`forge`**: invia un `Fire` firmato con la
chiave **sbagliata** → il server risponde `packet_auth` → **kick** immediato.

### 4.2 Anti-replay e ordine (numeri di sequenza)

**Cosa.** Ogni messaggio ha un `seq` che deve **crescere sempre**. Il server
memorizza l'ultimo `seq` visto e scarta quelli ≤ (`AntiCheat.CheckEnvelope`). Pochi
fuori-sequenza → warning; troppi → kick.

**Perché funziona.** Un attacco di **replay** ri-manda un pacchetto valido già
usato: ha un `seq` già visto, quindi viene ignorato. Protegge anche dal riordino
malevolo.

**Limiti.** Da solo non basta: va sempre abbinato al MAC (altrimenti l'attaccante
potrebbe rifare il pacchetto cambiando solo il `seq`). Insieme, MAC + `seq` chiudono
forgery e replay.

**Dimostrazione.** Scenario **`replay`**: cattura un `Fire` valido e lo re-invia
identico (stesso `seq`/MAC) più volte → `replay` warn ripetuti → **kick**.

### 4.3 Coerenza del tempo (anti clock-manipulation)

**Cosa.** Il tempo dichiarato dal client (`ts`) deve essere **monotòno** (non torna
indietro) e non deve **divergere** troppo dal tempo reale misurato dal server
(`AntiCheat.CheckClock`). Il server misura gli intervalli col proprio orologio
monotòno (`ServerClock`), mai con l'ora di sistema.

**Perché funziona.** Molti **speedhack** funzionano ingannando il concetto di tempo
del gioco. Se il client prova a dire "sono passati 10 ms" quando lato server ne sono
passati 100 (o viceversa), la deriva cresce e viene colta. Un tempo che torna
indietro è palesemente falso.

**Limiti.** È un indizio, non una prova assoluta: la rete introduce jitter, quindi
si usano tolleranze. Per questo lo abbiniamo al limite di cadenza (§4.4), che non si
basa sul tempo *dichiarato* dal client ma su quello *misurato* dal server.

**Dimostrazione.** Scenario **`clock`**: manda heartbeat con `ts` che tornano
indietro → `clock_tamper` warn → **kick**.

### 4.4 Limite di cadenza di fuoco (anti speedhack / auto-fire)

**Cosa.** Ogni arma ha una cadenza massima
([`GameConfig.For(Weapon)`](src/Shared/GameConfig.cs)): pistola ~230 ms tra i colpi,
fucile ~110 ms. Il server rifiuta gli spari troppo ravvicinati rispetto al **proprio
orologio** (`AntiCheat.RegisterShot`): lo sparo in eccesso **non può colpire** e
conta come violazione (warn, poi kick).

**Perché funziona.** Questo è il punto chiave contro lo speedhack: la cadenza è
imposta **lato server, col tempo del server**. Non importa quanto velocemente il
cheat prema il grilletto o acceleri il client: gli spari oltre il limite vengono
semplicemente buttati. Il cheat non ottiene alcun vantaggio, e per di più si
autodenuncia.

**Limiti.** La soglia va tarata con un margine (`tolerancePerShotMs`) per non punire
il jitter di rete di un giocatore onesto. Troppo stretta = falsi positivi; troppo
larga = lascia spazio a micro-vantaggi.

**Dimostrazione.** Scenario **`firerate`**: pistola che spara ogni ~15 ms → quasi
tutti gli spari rifiutati → `fire_rate` warn → **kick**.

### 4.5 Validazione dei confini (anti coordinate impossibili)

**Cosa.** Le coordinate di mira di un `Fire` devono stare dentro l'area di gioco (più
un piccolo margine) — `AntiCheat.CheckAimBounds`. Fuori da lì: kick.

**Perché funziona.** Un pacchetto costruito a mano o un client manomesso può mandare
coordinate assurde (es. `99999`). Un giocatore con un mouse e uno schermo reale non
può. È un controllo di **plausibilità dell'input**: barato e a basso costo.

**Limiti.** Coglie solo l'assurdo. Un aimbot manda coordinate perfettamente valide
(sono dentro lo schermo!), quindi qui non scatta: serve il rilevamento
comportamentale.

**Dimostrazione.** Scenario **`bounds`**: `Fire` a `(99999, -99999)` → `bounds`
→ **kick**.

### 4.6 Rilevamento comportamentale (anti aimbot/triggerbot)

Questa è la difesa concettualmente più importante, perché colpisce i cheat che
**non violano nessuna regola "dura"**: mandano input validi, firmati, in sequenza,
con cadenza corretta. Sembrano legittimi. L'unica cosa che li tradisce è *come*
giocano. Il server misura — con i **propri** dati — tre segnali
(`AntiCheat.RegisterHit` + `EvaluateRound`):

1. **Tempo di reazione.** Da quando compare il bersaglio a quando arriva il colpo che
   lo centra. Gli esseri umani migliori stanno sui ~150–250 ms; sotto ~120 ms in modo
   ripetuto non è fisiologicamente plausibile (`HumanReactionFloorMs`).
2. **Precisione della mira.** Distanza del punto di mira dal centro del bersaglio sui
   colpi andati a segno. Un aimbot centra quasi sempre il pixel esatto: errore
   mediano ≈ 0 è un'impronta fortissima (`SuspiciousAimErrorPx`).
3. **Accuratezza complessiva.** Percentuale di colpi a segno, specie sulle prove
   difficili.

**Come decide.** A fine prova incrocia i tre segnali. Un umano può eccellere in
*uno* (c'è gente molto precisa, o molto veloce). Ma avere **tutti e tre** al livello
"macchina" sulle prove non facili, o una reazione sotto la soglia fisica umana unita
a precisione chirurgica, è il profilo dell'aimbot → **kick**. Due segnali su tre →
**warn** (sorveglianza). La logica è in `EvaluateRound`.

**Perché funziona.** Non cerca di *impedire* l'aimbot (impossibile lato server, vedi
§5): lo **riconosce dalle statistiche**. È esattamente ciò che fanno i sistemi
moderni, oggi spesso con modelli di machine learning addestrati su milioni di
sessioni, che valutano non solo i colpi ma l'intera **traiettoria del mouse** (uno
snap istantaneo e poi fermo è innaturale; un umano fa micro-correzioni).

**Limiti e cautele.**
- **Falsi positivi**: è il rischio numero uno. Punire un giocatore bravo è peggio che
  lasciar passare un cheater occasionale. Per questo qui serve un **minimo di dati**
  (≥10 spari) prima di giudicare, e le soglie sono prudenti. In produzione si
  accumulano prove su molte partite prima di bannare.
- **Aimbot "umanizzati"**: i cheat avanzati aggiungono ritardo casuale ed errore
  artificiale per sembrare umani. Allora la partita si sposta sulla statistica fine e
  sul confronto con il comportamento storico del giocatore.
- La soglia va calibrata per difficoltà: a "Facile" si è più tolleranti.

**Dimostrazione.** Scenario **`aimbot`**: mira esatta al centro con reazione ~0 → a
fine prova `aimbot_pattern` → **kick**. In contrasto, scenario **`honest`** (reazione
260–420 ms, errore 8–14 px) **non** viene segnalato: è la prova che le difese non
colpiscono i giocatori bravi ma onesti.

---

## 5. La verità scomoda: non puoi nascondere i dati che il client deve disegnare

Domanda naturale: *"Se il server non mandasse la posizione del piattello, l'aimbot
non potrebbe mirare, giusto?"*

Sbagliato, ed è una delle lezioni più importanti dell'anti-cheat:

> **Tutto ciò che il client deve disegnare, il client lo conosce. E ciò che il
> client conosce, un cheat può leggerlo.**

Per mostrarti il piattello sullo schermo, il gioco *deve* avere in memoria la sua
posizione. Un aimbot legge quella posizione (dalla rete o dalla RAM) ed estrapola la
traiettoria. Nel nostro `CheatSim`, lo scenario `aimbot` fa esattamente questo:
legge gli `Snapshot` (gli stessi che servono a disegnare) e calcola il punto di
impatto. **Non c'è modo di impedirlo lato server**, perché quei dati servono a
giocare.

Questo spiega l'intera strategia moderna, e perché le difese sono "stratificate":

1. **Autorità del server** → il cheat non può *falsificare l'esito* (punteggio,
   colpi). Al massimo gioca "troppo bene".
2. **Minimizzazione dei dati** → mandare *solo* ciò che serve, il più tardi
   possibile. (Esempio classico: contro il wallhack non mandi al client i nemici che
   non può vedere. Nel tiro al piattello il bersaglio è sempre visibile, quindi qui
   c'è poco da nascondere — ma in questo progetto il server manda comunque **snapshot
   periodici**, non l'intera traiettoria futura in un colpo solo: è la stessa
   filosofia.)
3. **Rilevamento comportamentale** (§4.6) → poiché non puoi *impedire* all'aimbot di
   mirare, lo **riconosci** da come si comporta.
4. **Integrità del client** (§7) → rendi *difficile* eseguire il cheat sul PC.

Nessuno di questi strati è perfetto da solo. Messi insieme alzano enormemente il
costo di barare: è così che si vince, non con una pallottola d'argento.

---

## 6. Lag compensation: utilità e rischio

In un gioco d'azione il giocatore vede lo stato con qualche decina di ms di ritardo
(latenza di rete). Se il server valutasse lo sparo sullo stato "adesso", un
giocatore onesto con 80 ms di ping mancherebbe un bersaglio che *vedeva* centrato. La
**lag compensation** risolve così: il server **riavvolge** lo stato al momento in cui
il client ha realmente visto la scena, e valuta lì il colpo.

Nel progetto: `GameSession.HandleFireAsync` stima una latenza e calcola
`shotServerMs = recv - latency` (limitata da `GameConfig.MaxRewindMs`), poi usa
`ActiveTarget.StateAt(shotServerMs)` per sapere **dove era** il piattello in
quell'istante. Il moto è analitico, quindi il server può valutare qualunque istante
passato senza ambiguità.

**Il rischio didattico.** La lag compensation è anche una **superficie d'attacco**:
chi dichiara un tempo di sparo falso prova a "barare sul passato". Per questo:
- il riavvolgimento è **limitato** (`MaxRewindMs`): non puoi riavvolgere all'infinito;
- il tempo dichiarato è controllato dall'anti-clock (§4.3);
- tutto resta ancorato all'**orologio del server**.

È l'esempio perfetto di come una feature pensata per l'esperienza di gioco vada
sempre progettata *insieme* all'anti-cheat.

---

## 7. Difese lato client (kernel anti-cheat): cosa sono e perché non bastano

Finora tutto gira lato server. Ma i cheat della categoria B (§3) eseguono comunque
codice sul PC del giocatore. Per alzare il costo, i giochi moderni aggiungono un
**anti-cheat lato client** (Vanguard di Valorant, Easy Anti-Cheat, BattlEye, ...).
In questo progetto **non** lo implementiamo (richiederebbe codice specifico per
sistema operativo e privilegi elevati), ma devi sapere **cosa fa e perché**, perché
è parte dell'architettura moderna.

**Cosa cerca di fare, concettualmente:**
- **Integrità del processo**: verificare che il binario del gioco e la sua memoria non
  siano stati modificati (anti memory-editing/patching).
- **Rilevare strumenti ostili**: debugger agganciati, moduli iniettati, driver noti di
  cheat, overlay sospetti.
- **Attestazione dell'ambiente**: dare al server un minimo di garanzia che il client è
  "quello vero" e non manomesso.
- Spesso gira con **privilegi di kernel** per vedere più in basso di un cheat che gira
  in user-space.

**Perché "non basta" (ed è un punto cruciale da capire):**
- È una **corsa agli armamenti**: chi ha il controllo della macchina può, in linea di
  principio, ingannare chi gira sulla stessa macchina. Un cheat in kernel può nascondersi
  da un anti-cheat in kernel.
- Solleva problemi reali di **privacy, stabilità e fiducia** (un driver di kernel sul PC
  dei giocatori).
- Per questo l'anti-cheat lato client è un **moltiplicatore di costo**, non una garanzia.
  La difesa che *non si può aggirare stando sulla macchina del giocatore* resta l'**autorità
  del server**: se l'esito lo decide una macchina che l'attaccante non controlla, il cheat
  non può cambiarlo, può solo provare a sembrare umano — e lì lo aspetta il rilevamento
  comportamentale.

**Takeaway.** L'ordine di importanza didattico è: (1) autorità del server, (2)
rilevamento comportamentale, (3) integrità del canale (crypto), (4) integrità del
client. I primi due reggono anche se il quarto cade.

---

## 8. Mappa cheat → difesa → codice

| Cheat | Difesa principale | Dove nel codice | Scenario CheatSim | Esito |
|---|---|---|---|---|
| Pacchetto falso / tampering | HMAC per messaggio | `Security.VerifyMac`, `AntiCheat.CheckEnvelope` | `forge` | kick `packet_auth` |
| Replay | Numeri di sequenza | `AntiCheat.CheckEnvelope` | `replay` | kick `replay` |
| Clock/speed via tempo falso | Coerenza del clock | `AntiCheat.CheckClock` + `ServerClock` | `clock` | kick `clock_tamper` |
| Speedhack / auto-fire | Limite di cadenza lato server | `AntiCheat.RegisterShot` + `GameConfig` | `firerate` | kick `fire_rate` |
| Coordinate impossibili | Validazione confini | `AntiCheat.CheckAimBounds` | `bounds` | kick `bounds` |
| Aimbot / triggerbot | Rilevamento comportamentale | `AntiCheat.RegisterHit` + `EvaluateRound` | `aimbot` | kick `aimbot_pattern` |
| (manipolazione dell'esito/punteggio) | Autorità del server | tutto `GameSession` (calcola lui hit e score) | — | strutturalmente impossibile |
| Giocatore bravo e onesto | Soglie prudenti + dati minimi | `EvaluateRound` | `honest` | nessun kick ✔ |

---

## 9. Come tarare e come estendere

**Tarare le soglie.** Sono tutte costanti commentate in
[`src/Server/AntiCheat.cs`](src/Server/AntiCheat.cs):
- `HumanReactionFloorMs`, `SuspiciousReactionMs` — quanto "veloce" è sospetto.
- `SuspiciousAimErrorPx` — quanto "preciso" è sospetto.
- soglie di `fire_rate`, `replay`, `clock`.
Le cadenze delle armi e i parametri delle difficoltà sono in
[`src/Shared/GameConfig.cs`](src/Shared/GameConfig.cs).

**Metodo giusto per tarare:** raccogli prima dati di giocatori **onesti** (lo scenario
`honest` è un punto di partenza), guarda le distribuzioni di reazione/precisione, e
metti le soglie **oltre** la coda umana, con margine. L'obiettivo è *zero falsi
positivi* prima ancora di massimizzare le catture.

**Idee di estensione** (buoni esercizi per imparare):
1. **Analisi della traiettoria del mouse**: far mandare al client la traccia del
   movimento tra gli spari e cercare "snap" innaturali (grande spostamento in un
   frame seguito da immobilità). È la differenza tra un triggerbot e un umano.
2. **Punteggio di sospetto cumulativo**: invece di decidere a fine prova, accumulare
   un "trust score" tra più partite e agire solo con prove solide.
3. **Più bersagli contemporanei** e lag compensation con storico vero (ring buffer di
   stati) per avvicinarsi a un netcode da sparatutto.
4. **Server-side replay**: salvare gli input per rivedere una partita sospetta
   (strumento investigativo molto usato nei giochi reali).
5. **Telemetria**: loggare i flag anti-cheat su file/database per analisi offline.

---

## 10. Glossario

- **Server-authoritative**: il server è l'unica autorità sullo stato del gioco; il
  client propone input, il server decide.
- **HMAC**: codice di autenticazione del messaggio basato su hash + chiave segreta;
  prova che il messaggio viene da chi ha la chiave e non è stato alterato.
- **Nonce**: numero casuale usato una volta, per rendere unica una sessione/chiave.
- **Replay attack**: ri-usare un messaggio valido già inviato.
- **Snapshot**: fotografia dello stato di gioco inviata periodicamente dal server.
- **Lag compensation**: riavvolgere lo stato del server al momento percepito dal
  client, per valutare correttamente gli spari in presenza di latenza.
- **Aimbot / triggerbot**: cheat che mirano/sparano automaticamente.
- **Speedhack**: cheat che accelera il gioco (spesso manipolando il tempo).
- **Rilevamento comportamentale**: riconoscere un cheat dalle statistiche di gioco
  anziché impedirne l'azione.
- **Falso positivo**: un giocatore onesto erroneamente segnalato/punito — l'errore
  più grave per un anti-cheat.
- **Anti-cheat lato client (kernel)**: software sul PC del giocatore che verifica
  l'integrità del processo e cerca strumenti ostili; alza il costo di barare ma non è
  inviolabile.

---

### In una frase

Costruisci l'anti-cheat **attorno** a questo principio: *decide il server, con i
suoi dati e il suo orologio*. La crittografia chiude gli attacchi di rete, le regole
"dure" (cadenza, confini, tempo) chiudono l'assurdo, e il **rilevamento
comportamentale** si occupa di ciò che non puoi impedire ma puoi riconoscere. Le
difese sul client aggiungono costo, non certezze. Stratifica, misura, e proteggi
prima di tutto i giocatori onesti.
