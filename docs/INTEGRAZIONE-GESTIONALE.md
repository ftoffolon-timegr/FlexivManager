# FlexivManager – Interfaccia con il gestionale

Accordo del 2026-11-05 con i responsabili del gestionale (fornitore esterno al cliente).

```
 Gestionale                          FlexivManager
 ──────────                          ─────────────
 file .txt (1 codice per riga)  ──►  cartella condivisa, letta ogni minuto
                                     codici salvati → file CANCELLATO (= conferma)
                                     pianificatore: codice articolo → programma robot (pagina Codici lavoro)
 GET /api/erp/production        ◄──  per ogni codice di oggi: inizio, fine, durata, quantità, programma
 POST /api/erp/articles/import  ──►  (facoltativo) "verifica subito la cartella"
```

L'interfaccia **non comanda mai il robot**: avvio e stop restano solo alla postazione operatore.

## 1. Codici articolo: gestionale → FlexivManager

**File**

- Testo semplice, **un codice articolo per riga**. Estensione `.txt` (configurabile, `Erp:FilePattern`).
- Il codice è una stringa qualsiasi: non viene verificato il formato. Si tolgono solo spazi iniziali/finali e
  caratteri di controllo; le righe vuote sono ignorate; i doppioni sono uniti.
- Maiuscole e minuscole sono equivalenti (come per il barcode). La scrittura originale del gestionale viene
  conservata e usata nell'esportazione.
- Una riga viene **scartata** (e segnalata nel log eventi) solo se non potrebbe mai essere un codice barcode:
  più di 64 caratteri, oppure caratteri non ASCII (es. lettere accentate).
- Codifica: UTF-8 (con o senza BOM) o UTF-16 con BOM; se non è UTF-8 valido viene letta come Latin-1 (ANSI).
- Nome del file libero. Più file nella cartella sono letti tutti, dal più vecchio.
- Esempio: [esempi/codici-articolo.txt](esempi/codici-articolo.txt).

**Ciclo di lettura**

1. Ogni `Erp:PollInterval` (default 1 minuto) il servizio legge la cartella `Erp:ImportFolder`.
2. Un file modificato da meno di `Erp:MinFileAge` (5 s) o ancora aperto in scrittura dal gestionale viene
   lasciato al giro successivo.
3. I codici vengono salvati nel database (tabella `Articles`): i nuovi aggiunti, quelli già noti aggiornati.
   **Ogni file aggiunge codici, non ne toglie**: un codice assente da un file nuovo resta disponibile.
4. **Solo dopo il salvataggio** il file viene cancellato. La cancellazione è la conferma al gestionale che i
   codici sono stati caricati. Se il salvataggio fallisce, il file resta e viene riletto al giro dopo
   (rileggere un codice non crea doppioni). Se la cancellazione fallisce (permessi), l'errore è nel log eventi.
5. Ogni lettura è registrata nel log eventi (sorgente `Erp`): file, codici, nuovi, righe scartate.

**Cartella**

- In produzione: cartella di rete condivisa indicata dal fornitore del gestionale, es. `\\server\share\flexiv`
  (percorso UNC, non una lettera di unità: un servizio Windows non vede le unità mappate dall'utente).
- L'account con cui gira FlexivManager deve avere **lettura, scrittura e cancellazione** su quella cartella.
  Da definire con l'IT del cliente quando il servizio Windows sarà installato (account di dominio dedicato).
- Per ora (sviluppo e prove): cartella locale `data/erp-import` sotto la cartella del programma, creata
  all'avvio. Un percorso assoluto o UNC non viene mai creato dal programma.
- Se la cartella non è raggiungibile l'errore è registrato una volta (non a ogni minuto) e di nuovo quando
  torna raggiungibile.

**Uso dei codici**

Sulla postazione remota (porta 8080, 8090 in sviluppo), pagina **Codici lavoro**:

- il riquadro "Codici articolo dal gestionale" mostra i codici ricevuti **senza programma robot**: un clic apre
  il "Nuovo codice" già compilato, si sceglie il plan e si salva;
- nel "Nuovo codice" il codice si sceglie **solo da un menù a tendina** con i codici ricevuti che non hanno
  ancora un programma (`Erp:OnlyImportedCodes = true`, default). La stessa regola vale per `POST/PUT /api/jobs`
  (400 se il codice non è stato ricevuto);
- eccezioni: i **codici di servizio** (es. HOME, casella "Codice di servizio") si scrivono a mano; un codice
  creato prima dell'interfaccia può restare com'è, ma non può essere cambiato in un codice non ricevuto;
- il pulsante **Verifica nuovo file** (dopo l'accesso) legge subito la cartella.

## 2. Dati di produzione: FlexivManager → gestionale

```
GET http://<pc-robot>:8080/api/erp/production
GET http://<pc-robot>:8080/api/erp/production?date=2026-11-05
```

- Senza chiave, come tutte le letture. Disponibile su entrambi gli host.
- Senza `date`: la **giornata in corso** secondo l'orologio del PC del robot. Con `date` (`yyyy-MM-dd`) si può
  rileggere un giorno passato (es. se il gestionale ha perso una lettura).
- Esempio completo: [esempi/erp-production.json](esempi/erp-production.json).

```json
{
  "date": "2026-11-05",
  "generatedAt": "2026-11-05T15:42:10.512+01:00",
  "articles": [
    {
      "articleCode": "A100-45",
      "status": "Completed",
      "startedAt": "2026-11-05T07:58:12.304+01:00",
      "endedAt": "2026-11-05T10:31:47.918+01:00",
      "durationSeconds": 8640,
      "quantity": 48,
      "robotProgram": "PLAN-A100"
    },
    {
      "articleCode": "C300",
      "status": "InProgress",
      "startedAt": "2026-11-05T13:15:40.002+01:00",
      "endedAt": null,
      "durationSeconds": null,
      "quantity": null,
      "robotProgram": "PLAN-C300"
    }
  ]
}
```

Un elemento per ogni codice articolo lavorato nel giorno, in ordine di inizio. I codici di servizio (es. HOME)
non compaiono.

| Campo | Significato |
|-------|-------------|
| `articleCode` | Codice come scritto dal gestionale (come scritto nell'editor se non è mai arrivato dal gestionale) |
| `status` | `InProgress` in lavorazione, `Completed` lavorazione terminata |
| `startedAt` | Inizio del primo ciclo del giorno (ISO 8601, ora locale con fuso) |
| `endedAt` | Fine dell'ultimo ciclo del giorno. `null` se in lavorazione |
| `durationSeconds` | Somma delle durate dei cicli completati (tempo macchina del robot), secondi interi. `null` se in lavorazione |
| `quantity` | Pezzi prodotti = cicli completati. `null` se in lavorazione |
| `robotProgram` | Programma robot dell'ultimo ciclo |

**Quando un codice è "in lavorazione"**: c'è un ciclo in esecuzione per quel codice, oppure uno degli ordini di
produzione di oggi per quel codice è iniziato e non ancora finito (tra un ciclo e il successivo). In tutti gli
altri casi è `Completed` e i valori sono quelli finali. Se lo stesso codice viene ripreso più tardi nella
giornata, torna `InProgress` e poi i valori comprendono tutti i cicli del giorno.

**Cicli fermati o in errore**: non contano nella quantità né nella durata; il loro orario conta per inizio e fine.

`endedAt - startedAt` è il tempo di presenza del codice sul robot (comprese le pause tra i cicli);
`durationSeconds` è il solo tempo di lavorazione. Tempo medio per pezzo = `durationSeconds / quantity`.

## 3. Verifica immediata della cartella (facoltativa)

```
POST http://<pc-robot>:8080/api/erp/articles/import
X-Api-Key: <chiave>
```

- Solo sull'host remoto, con la stessa chiave dell'API di scrittura (`Hosts:Monitoring:Api:Key`).
- Corpo vuoto. Legge subito la cartella invece di aspettare il giro successivo; non aspetta `MinFileAge`, ma un
  file ancora aperto in scrittura viene comunque saltato (`skipped`).
- Non serve al funzionamento: la lettura automatica avviene comunque ogni minuto.

Risposta `200`:

```json
{
  "at": "2026-11-05T09:30:00.120+00:00",
  "trigger": "API api",
  "folder": "\\\\server\\share\\flexiv",
  "files": [
    { "fileName": "codici_20261105.txt", "codes": 3, "added": 2, "alreadyKnown": 1,
      "rejected": [], "deleted": true, "error": null }
  ],
  "skipped": [],
  "error": null,
  "codesAdded": 2,
  "success": true
}
```

| Codice | Quando |
|--------|--------|
| 200 | Cartella letta. `success` è `false` se un file ha avuto un errore (dettaglio in `files[].error`) |
| 401 | Chiave mancante o errata |
| 503 | Cartella non configurata o non raggiungibile (`detail` con il motivo) |

## 4. Consultazione

`GET /api/erp/articles` (aperto): codici ricevuti, con il programma associato (`planName` `null` se manca),
la cartella configurata e l'esito dell'ultima lettura (`lastScan`).

## 5. Configurazione (`appsettings.json`, sezione `Erp`)

| Chiave | Default | Note |
|--------|---------|------|
| `Enabled` | `true` | Lettura automatica periodica. Con `false` resta la lettura manuale |
| `ImportFolder` | `data/erp-import` | Cartella del gestionale. Vuoto = importazione disattivata |
| `FilePattern` | `*.txt` | File letti (sottocartelle escluse) |
| `PollInterval` | `00:01:00` | Intervallo della lettura automatica (minimo 5 s) |
| `MinFileAge` | `00:00:05` | Età minima di un file per la lettura automatica |
| `DeleteAfterImport` | `true` | Cancella il file dopo il salvataggio. Lasciare `true`: è la conferma per il gestionale |
| `OnlyImportedCodes` | `true` | Editor codici e `/api/jobs` accettano solo codici ricevuti dal gestionale (servizio esclusi). `false` = codice libero, per prove senza gestionale |

## 6. Prova in locale (PowerShell)

```powershell
# programma avviato in Development: cartella data\erp-import, host remoto su 8090
Set-Content -Path .\data\erp-import\codici.txt -Value "A100-45","b-200/7","C300"
Start-Sleep 65                                             # oppure la verifica immediata:
Invoke-RestMethod http://127.0.0.1:8090/api/erp/articles/import -Method Post -Headers @{ "X-Api-Key" = "dev-api-key" }
Test-Path .\data\erp-import\codici.txt                     # False: file letto e cancellato
Invoke-RestMethod http://127.0.0.1:8090/api/erp/articles   # codici ricevuti
Invoke-RestMethod http://127.0.0.1:8090/api/erp/production | ConvertTo-Json -Depth 5
```

## 7. Da definire con il fornitore del gestionale

- Percorso della cartella condivisa e account con i permessi.
- Se `robotProgram` serve davvero (oggi è sempre presente).
- Orario e frequenza della lettura di `GET /api/erp/production` (es. a fine turno, o ogni N minuti).
- Raggiungibilità: `Hosts:Monitoring:Address` deve essere l'indirizzo della scheda di rete aziendale (oggi è
  `127.0.0.1`, raggiungibile solo dal PC stesso) e il PC del robot deve accettare connessioni dal server del gestionale sulla porta 8080
  (regola firewall di Windows limitata all'IP del server del gestionale).
