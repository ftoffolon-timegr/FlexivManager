# FlexivManager – API REST

Tutte le interazioni della supervisione (codici e piano di lavoro giornaliero) sono disponibili anche via API REST,
oltre che dalle pagine web. L'API passa dagli stessi controlli delle pagine (`PlanningService`): stessa validazione,
stesso log eventi, stesso controllo di versione.

- **Lettura** (`GET`): aperta, senza chiave, su entrambi gli host (come le pagine di monitoraggio).
- **Scrittura** (`POST`, `PUT`, `DELETE`): solo sull'host remoto (Monitoring, porta 8080, 8090 in sviluppo), con
  l'header `X-Api-Key` uguale a `Hosts:Monitoring:Api:Key`. Chiave vuota in configurazione = scrittura disattivata.
  Le modifiche risultano fatte da `Hosts:Monitoring:Api:ClientName` (default `api`).
- L'API **non comanda mai il robot**: avvio, stop ed esito qualità restano solo sulla postazione locale.
- JSON con nomi in camelCase; enum come testo (`"Pending"`, `"Ok"`…); date nel formato `yyyy-MM-dd`.
  Senza `date`, il giorno è **oggi** secondo l'orologio del PC del gateway.

## Lettura

| Metodo e percorso | Risposta |
|-------------------|----------|
| `GET /api/plan?date=2026-10-31` | `{ date, rows: [riga…] }` tutte le righe del giorno, in ordine |
| `GET /api/plan/rows/{id}` | una riga, 404 se non esiste |
| `GET /api/report?date=2026-10-31` | report del giorno (vedi sotto) |
| `GET /api/jobs`, `GET /api/jobs/{id}` | codici → plan, con `planOnRobot` e `version` |
| `GET /api/cycles?max=50` | ultimi cicli con esito (`Completed`, `Stopped`, `Failed`) |
| `GET /api/status`, `/api/telemetry`, `/api/events`, `/api/plans` | stato robot (invariati) |

Riga del piano (campi principali): `id`, `workDate`, `productCode`, `orderNumber` (vuoto se non indicato), `quantity`,
`completedCount`, `remaining`, `status`
(`Pending`, `Active`, `Completed`, `Cancelled`, `CarriedOver`), `notes`, `position`, `version`, `updatedBy`, `updatedAt`.

Report:

```json
{
  "date": "2026-10-31",
  "rows": [ { "row": { "...": "riga" }, "progress": "InProgress" } ],
  "totals": {
    "rows": 4, "rowsDone": 1, "rowsInProgress": 1, "rowsMissing": 2,
    "planned": 120, "done": 47, "missing": 73
  }
}
```

`progress`: `Done`, `InProgress`, `Missing`, `Cancelled`, `CarriedOver`. I pezzi delle righe annullate non sono
né pianificati né mancanti; quelli delle righe riportate a un altro giorno restano mancanti nel giorno originale.

## Scrittura (header `X-Api-Key`)

| Metodo e percorso | Corpo | Risposta |
|-------------------|-------|----------|
| `POST /api/jobs` | `{ productCode, planName, description?, enabled?, isService? }` | 201 + codice |
| `PUT /api/jobs/{id}` | come sopra + `version` | 200 + codice |
| `DELETE /api/jobs/{id}?version=N` | – | 204 |
| `POST /api/plan/rows` | `{ productCode, quantity, date?, orderNumber?, notes? }` | 201 + riga |
| `PUT /api/plan/rows/{id}` | come sopra + `version` (solo righe non iniziate) | 200 + riga |
| `POST /api/plan/rows/{id}/cancel` | – | 200 + riga |
| `POST /api/plan/rows/{id}/move` | `{ "direction": "up" }` o `"down"` (nello stesso giorno) | 200 + piano del giorno |
| `POST /api/plan/copy-missing` | `{ from, to? }` (senza `to`: giorno dopo `from`, o oggi se è già passato) | 200 + piano del giorno di destinazione |

`version` è quella letta con `GET`: se nel frattempo qualcuno ha modificato il dato, la richiesta è rifiutata con 409
invece di sovrascrivere.

Errori (formato *problem details*):

| Codice | Quando |
|--------|--------|
| 400 | dati non validi; `errors` contiene i messaggi (es. quantità fuori limite, codice senza associazione, giorno passato) |
| 401 | chiave mancante o errata (il tentativo è registrato nel log eventi, sorgente `Api`) |
| 409 | versione non aggiornata, riga non più modificabile, codice duplicato |

## Gestionale (ERP)

Dettagli, esempi e regole di calcolo in [INTEGRAZIONE-GESTIONALE.md](INTEGRAZIONE-GESTIONALE.md).

| Metodo e percorso | Accesso | Risposta |
|-------------------|---------|----------|
| `GET /api/erp/production?date=yyyy-MM-dd` | aperto, entrambi gli host | per ogni codice articolo del giorno (default oggi): `startedAt`, `endedAt`, `durationSeconds`, `quantity`, `robotProgram`, `status` |
| `GET /api/erp/articles` | aperto, entrambi gli host | codici ricevuti dal gestionale, programma associato, ultima lettura della cartella |
| `POST /api/erp/articles/import` | `X-Api-Key`, solo host remoto | legge subito la cartella dei file del gestionale; 503 se non raggiungibile |

## Esempi (PowerShell)

```powershell
$base = "http://127.0.0.1:8090"          # host remoto in sviluppo
$h = @{ "X-Api-Key" = "dev-api-key" }   # chiave di sviluppo

# Piano e report di oggi
Invoke-RestMethod "$base/api/plan"
Invoke-RestMethod "$base/api/report?date=2026-10-31"

# Nuova riga per domani
$body = @{ productCode = "A100"; quantity = 20; date = "2026-10-31"; orderNumber = "ERP-123" } | ConvertTo-Json
Invoke-RestMethod "$base/api/plan/rows" -Method Post -Headers $h -ContentType "application/json" -Body $body

# Copia i mancanti di oggi a domani
$body = @{ from = "2026-10-30" } | ConvertTo-Json
Invoke-RestMethod "$base/api/plan/copy-missing" -Method Post -Headers $h -ContentType "application/json" -Body $body
```

Con `curl`:

```bash
curl -H "X-Api-Key: dev-api-key" -H "Content-Type: application/json" \
     -d '{"productCode":"A100","quantity":20}' http://127.0.0.1:8090/api/plan/rows
```
