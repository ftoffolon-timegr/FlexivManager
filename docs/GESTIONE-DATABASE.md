# Come FlexivManager gestisce il database SQLite

Spiegazione basata sul codice attuale nella cartella `FlexivManager` (5 novembre 2026) e sui due file `.db` presenti in `data\`, che ho aperto in sola lettura.

---

## 1. In due parole

- Il database è **un solo file** sul disco (SQLite non ha un server: niente installazioni, niente servizi).
- Lo usa **solo** il progetto `src/RobotGateway.Data`, tramite **Entity Framework Core** (EF Core 10, pacchetto `Microsoft.EntityFrameworkCore.Sqlite`). EF Core traduce le classi C# in tabelle e le query LINQ in SQL.
- Tutto il resto dell'applicazione parla con il database attraverso **una sola classe**, `SqlitePlanningStore`. Il thread del robot (`RobotWorker`) **non tocca mai il database**.

```
Pagine Blazor / API REST / import ERP / CycleRecorder
                     │
                     ▼
            SqlitePlanningStore   (unico punto di accesso)
                     │  EF Core
                     ▼
        data\planning.db  oppure  data\planning-flexiv.db
```

---

## 2. Dove sta il file e come si configura

In `appsettings.json`:

```json
"Planning": {
  "DatabasePath": "data/planning.db",
  "SeedDemoData": false
}
```

- Il percorso è **relativo alla cartella dell'applicazione** (la "content root"). Quando lanci da Visual Studio è la cartella del progetto, quindi oggi il file è `C:\Users\fabio\time\4_0\asp\FlexivManager\data\`.
- La cartella `data\` viene creata da sola se manca (`DataServiceCollectionExtensions.AddSqlitePlanningStore`).
- **Due file diversi a seconda del robot** (`GatewayComposition.DatabasePath`):

| `Robot:Backend` | File usato | Contenuto oggi |
|---|---|---|
| `Mock` (simulatore) | `data\planning.db` | 5 codici, 4 ordini, 4 cicli (dati demo) |
| `Flexiv` (robot vero) | `data\planning-flexiv.db` | 3 codici, 8 ordini, 31 cicli |

  Così i codici di prova del simulatore non finiscono mai davanti all'operatore sul robot vero.
- `SeedDemoData: true` (solo in `appsettings.Development.json`) riempie un database **vuoto** con codici e ordini di esempio, e **mai** quando il backend è `Flexiv`.
- `data/` è in `.gitignore`: il database **non va in git**, ed è giusto così (sono dati di produzione, non codice).

---

## 3. Le tabelle

Definite in `PlanningDbContext.cs`. Ogni classe `...Entity` è una tabella; ogni proprietà è una colonna.

| Tabella | A cosa serve | Colonne principali |
|---|---|---|
| **Jobs** | Associazione *codice prodotto → programma (plan) del robot* | `ProductCode` (unico), `PlanName`, `Description`, `Enabled`, `IsService`, `Version`, date e utente dell'ultima modifica |
| **JobHistory** | Storico di ogni creazione/modifica/cancellazione di un codice | `JobId`, `Version`, `Action` (Created/Updated/Deleted), `User`, `At`, `Json` (fotografia completa del codice in quel momento) |
| **Orders** | Ordini di produzione del giorno | `WorkDate` (giorno), `OrderNumber`, `ProductCode`, `Quantity`, `CompletedCount`, `Status` (Pending/Active/Completed/Cancelled/CarriedOver, salvato come testo), `Position` (ordine nella lista), `Version` |
| **Cycles** | Un record per ogni ciclo eseguito dal robot | `OrderId`, `ProductCode`, `PlanName`, `Source`, `StartedAt`, `DurationSeconds`, `Outcome`, `Message` |
| **Articles** | Codici articolo arrivati dal gestionale (ERP) | `Code` (unico, normalizzato), `RawCode` (come arrivato), prima/ultima importazione, file di origine |

Note utili:
- Non ci sono "foreign key" vere tra le tabelle (es. `Orders.ProductCode` non è legato a `Jobs`): i controlli li fa il codice C# (es. non puoi cancellare un codice se ha ordini aperti).
- Gli **indici** (es. unico su `Jobs.ProductCode`, su `Orders(WorkDate, Position)`) rendono veloci le ricerche e impediscono i doppioni.
- Le date (`DateTimeOffset`) sono salvate come **testo** in UTC. Conseguenza: SQLite non sa confrontarle direttamente, quindi alcuni filtri per data si fanno in memoria (vedi punto 9).

---

## 4. Come nasce il database e cosa succede all'avvio

All'avvio dell'applicazione (`GatewayComposition.InitializeDatabase`) viene chiamato `SqlitePlanningStore.EnsureCreated()`, che fa in ordine:

1. **`EnsureCreated()` di EF Core**: se il file non esiste, lo crea con tutte le tabelle. Se esiste già, **non tocca niente**.
2. **`DropObsoleteColumns`**: elimina colonne che il programma non usa più (la velocità `VelocityScale` tolta il 30/10 e le colonne di qualità OK/KO/rilavorazione tolte il 3/11).
3. **`AddDailyPlanColumns`**: aggiunge le colonne arrivate con il piano giornaliero (`Jobs.IsService`, `Orders.WorkDate`) se mancano; agli ordini vecchi assegna come giorno quello di creazione.
4. **`AddArticlesTable`**: crea la tabella `Articles` (ERP) se manca.
5. Se richiesto, carica i dati demo (solo simulatore, solo database vuoto).

Ogni passo **controlla prima** se la modifica serve (`HasColumn`, `CREATE TABLE IF NOT EXISTS`), quindi ripetere l'avvio 100 volte non fa danni.

Esempio concreto: oggi `planning.db` (simulatore) **non ha ancora** la tabella `Articles`, perché non hai più avviato in modalità Mock dopo l'integrazione ERP. Verrà aggiunta da sola al prossimo avvio in Mock. `planning-flexiv.db` invece ce l'ha già.

---

## 5. Cosa succede quando cambia lo schema (aggiungo/tolgo un campo)

Qui c'è la scelta più importante da capire.

**Come funziona oggi:** non uso le *migrations* di EF Core. Ogni volta che il modello cambia, aggiungo a mano in `SqlitePlanningStore` una piccola istruzione SQL "se manca, aggiungi" o "se c'è, togli". È quello che è successo per velocità, qualità, piano giornaliero e ERP.

**Perché:** in questa fase i requisiti cambiano spesso e le modifiche sono piccole (una colonna, una tabella). Le migrations aggiungerebbero file generati da gestire a ogni modifica.

**Il limite:** il database non sa "che versione" è (`PRAGMA user_version` è 0 in entrambi i file). Funziona finché le modifiche sono semplici e "aggiuntive". Non funziona bene per cambi complessi (rinominare una colonna, spostare dati tra tabelle) e ogni passo resta nel codice per sempre.

**Attenzione:** togliere una colonna cancella i dati di quella colonna. Per la velocità e la qualità era voluto, ma è un'operazione **irreversibile** senza un backup (vedi punto 8).

**Cosa ti consiglio prima della produzione vera:** passare alle **EF Core migrations** (comando `dotnet ef migrations add ...`, applicate all'avvio con `Database.Migrate()`), partendo dallo schema attuale come "versione 1". Da lì in poi ogni cambio di schema è un file numerato, tracciato in una tabella `__EFMigrationsHistory`, e si sa sempre a che versione è un database.

---

## 6. Più utenti insieme: concorrenza e thread

Nello stesso processo girano due siti (operatore 8081 e supervisione 8080/8090), l'import ERP e il registratore dei cicli. Tutti usano **la stessa istanza** di `SqlitePlanningStore`.

- **Una connessione per operazione.** Ogni metodo apre un `PlanningDbContext` nuovo (tramite `IDbContextFactory`), fa il suo lavoro e lo chiude. Le connessioni sono riusate da un pool (`Pooling = true`), quindi è veloce. Così due richieste parallele non si pestano i piedi.
- **Letture in parallelo, scritture in fila.** Tutte le scritture passano da `Write(...)`, che usa un semaforo (`SemaphoreSlim(1,1)`): **una scrittura alla volta**. SQLite accetta comunque un solo scrittore; mettendole in fila anche nel codice, i controlli tipo "questo numero ordine è già usato?" e "qual è la prossima posizione?" non possono essere falsati da un'altra scrittura a metà.
- **Modalità WAL.** Ho verificato l'intestazione dei due file: sono in modalità *WAL* (Write-Ahead Log, attivata da EF Core quando crea il database). In pratica chi legge non blocca chi scrive e viceversa: la pagina di supervisione può leggere mentre si registra un ciclo. Mentre l'applicazione gira vedrai accanto al `.db` due file temporanei, `-wal` e `-shm`: sono normali, non cancellarli.
- **Il robot non aspetta mai il disco.** Il `RobotWorker` non scrive nel database: alla fine di un ciclo pubblica un evento e il `CycleRecorder` lo salva in background. Se il disco è lento, il controllo del robot non ne risente.

### Modifiche contemporanee dallo stesso dato (concorrenza ottimistica)

`Jobs` e `Orders` hanno una colonna `Version` che sale di 1 a ogni modifica. Quando salvi da una pagina, il programma controlla che la versione sia ancora quella che avevi caricato. Se nel frattempo qualcun altro (un'altra finestra, o l'API REST) ha salvato prima di te, ricevi il messaggio *"modificato da ... nel frattempo: ricarica e riprova"* invece di sovrascrivere il suo lavoro senza saperlo.

---

## 7. Transazioni

- Ogni `SaveChanges` di EF Core è **una transazione**: o vengono salvate tutte le righe di quell'operazione, o nessuna. Per esempio "copia ordini mancanti a domani" crea le nuove righe e marca le vecchie come *riportate* in un colpo solo.
- Registrare un ciclo e aggiornare il contatore dell'ordine avviene nella stessa transazione: non può succedere che il ciclo sia salvato ma il conteggio no.
- **Un'eccezione da sistemare:** per i codici (Jobs) il salvataggio del codice e la riga di storico (`JobHistory`) sono **due salvataggi separati**. Se il programma si chiudesse esattamente tra i due, il codice sarebbe salvato ma mancherebbe la riga di storico. Il rischio è molto basso e non tocca la produzione, ma è facile chiuderlo con una transazione esplicita.

---

## 8. Backup: oggi non c'è

Questo è il punto più importante da sistemare prima di andare in produzione.

- L'applicazione **non fa nessun backup** del database.
- `data\` non è in git (giusto), quindi se il file si rovina o il disco si guasta, codici, ordini e storico cicli si perdono.
- **Non copiare il `.db` con Esplora risorse mentre l'applicazione gira**: in modalità WAL le ultime modifiche possono essere ancora nel file `-wal` e la copia sarebbe incompleta. A applicazione ferma (nessun file `-wal` presente, com'è adesso) la copia del solo `.db` è sicura.

**Proposta:** un backup automatico fatto dall'applicazione stessa, per esempio ogni notte e a ogni avvio, con il comando SQLite `VACUUM INTO 'data\backup\planning-flexiv-2026-11-05.db'`, che produce una copia coerente anche mentre il programma lavora; tenere gli ultimi 30 file, e meglio ancora copiarli anche su una cartella di rete. Lo posso aggiungere io se vuoi.

---

## 9. Altri rischi e miglioramenti (in ordine di importanza)

1. **Backup** (punto 8). Priorità alta.
2. **Passare alle migrations** EF Core (punto 5) prima di installare in produzione. Priorità media.
3. **Transazione per codice + storico** (punto 7). Priorità bassa, lavoro piccolo.
4. **Crescita della tabella `Cycles`.** Ogni ciclo aggiunge una riga e non si cancella mai. Anche con 500 cicli al giorno sono circa 150.000 righe l'anno: per SQLite è poco, e la lettura per data si ferma al giorno prima del periodo richiesto, quindi resta veloce. Tra qualche anno si potrà valutare un'archiviazione.
5. **Posizione del file con il servizio Windows.** Quando l'applicazione girerà come servizio, `data\` sarà accanto all'eseguibile installato, non più nella cartella del progetto. L'utente del servizio dovrà avere permessi di scrittura lì, e una nuova pubblicazione **non deve cancellare** quella cartella (attenzione all'opzione "elimina tutti i file esistenti" della pubblicazione in Visual Studio). In alternativa si può mettere un percorso assoluto in `Planning:DatabasePath`, per esempio `C:\ProgramData\FlexivManager\planning.db`.
6. **Programmi esterni sul database** (es. *DB Browser for SQLite*): vanno bene per **guardare**, meglio aprendo il file in sola lettura. Non modificare dati mentre l'applicazione gira: il semaforo protegge solo le scritture fatte dall'applicazione stessa, e una modifica esterna salta i controlli del programma (versioni, regole sugli ordini).
7. **Documentazione da aggiornare:** `docs/ARCHITETTURA.md` cita ancora gli "esiti qualità" nel `SqlitePlanningStore` e nei test, tolti il 3/11.

---

## 10. Come guardare dentro il database

- Installa **DB Browser for SQLite** (gratuito) e apri `data\planning-flexiv.db` con *Apri database in sola lettura*.
- Le tabelle utili da guardare sono `Orders` (ordini e conteggi), `Cycles` (storico cicli) e `Jobs` (codici → plan).
- Per i dati veri conviene comunque usare le pagine dell'applicazione o le API REST (`GET /api/...`), che applicano le regole del programma.
