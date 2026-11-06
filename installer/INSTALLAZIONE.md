# Installazione di FlexivManager sul PC del cliente

> Lo STOP software della pagina operatore **non sostituisce l'arresto di emergenza hardware** del robot.

FlexivManager gira come **servizio Windows** (`FlexivManager`), parte da solo all'accensione del PC e **non avvia mai movimenti del robot all'avvio**.

| Pagina | Indirizzo | Chi |
|---|---|---|
| Postazione operatore | `http://127.0.0.1:8081/operator` (solo dal PC stesso) | operatore alla cella |
| Monitoraggio e pianificazione | `http://<IP-aziendale-del-PC>:8090/` | ufficio, gestionale |

## 1. Creare il pacchetto (PC di sviluppo)

Da PowerShell, nella cartella della soluzione:

```powershell
cd C:\Users\fabio\time\4_0\asp\FlexivManager
powershell -ExecutionPolicy Bypass -File .\installer\build-package.ps1 -VcRedist C:\setup\vc_redist.x64.exe
```

- esegue i test, pubblica in modalità *self-contained* win-x64 (sul PC del cliente **non serve installare .NET**), controlla che ci siano la DLL nativa Flexiv (`native\rdk-1.8\`) e SQLite, ed esclude `appsettings.Development.json` e la cartella `data\` di sviluppo;
- crea `bin\package\FlexivManager-<data>-<commit>-win-x64.zip`.

`-VcRedist` è facoltativo: è il programma di installazione del **runtime Visual C++ 2015-2022 x64** (<https://aka.ms/vs/17/release/vc_redist.x64.exe>), necessario alla DLL Flexiv. Se lo includi, `install.ps1` lo installa da solo quando manca.

In Visual Studio lo stesso publish c'è come profilo **win-x64** (Pubblica), con output in `bin\publish\win-x64\`.

## 2. Prerequisiti del PC del cliente

- Windows 11 x64, un utente amministratore.
- Due schede di rete consigliate: una sulla **rete aziendale** (monitoraggio, gestionale) e una sulla **rete del robot** (IP statico, es. `192.168.2.150`).
- Robot: in Flexiv Elements abilitare il **controllo remoto (RDK)** prima di usare il robot reale.
- La versione RDK deve corrispondere al software del robot: 3.10.x → `1.8`, 3.11.3 → `1.9.4.1`. Nel pacchetto entrano le cartelle `native\rdk-*\` presenti al momento della creazione.

## 3. Prima installazione

1. Copia lo zip sul PC del cliente ed estrailo (es. in `C:\setup\FlexivManager-...`).
2. Apri **PowerShell come amministratore** e vai nella cartella estratta.
3. Lancia l'installazione:

```powershell
cd C:\setup\FlexivManager-2026.11.05-abc1234-win-x64

# Robot reale
powershell -ExecutionPolicy Bypass -File .\install.ps1 `
    -MonitoringAddress 10.0.0.25 `
    -RobotSerial "Rizon10-012345" -RdkVersion 1.8 -RobotNetworkInterface 192.168.2.150 `
    -ErpImportFolder "\\server\gestionale\flexiv"

# Solo prova con il simulatore (nessun parametro: chiede l'indirizzo)
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Lo script chiede la **password del pianificatore** (utente `planner` del monitoraggio) e alla fine mostra la **chiave API** generata per il gestionale: annotala, compare una volta sola.

Parametri principali:

| Parametro | Default | Significato |
|---|---|---|
| `-MonitoringAddress` | chiesto | IP della scheda **aziendale** su cui ascolta il monitoraggio. `0.0.0.0` = tutte le schede (vedi §7). |
| `-MonitoringPort` | `8090` | porta del monitoraggio |
| `-FirewallRemoteAddress` | `Any` | chi può collegarsi: `LocalSubnet`, oppure IP/subnet separati da virgola |
| `-RobotSerial` | vuoto | numero di serie del robot. **Vuoto = simulatore.** |
| `-RdkVersion` | `1.8` | `1.8` (robot 3.10.x) o `1.9.4.1` (3.11.3) |
| `-RobotNetworkInterface` | vuoto | IP di questo PC sulla rete del robot |
| `-ErpImportFolder` | `data\erp-import` | cartella in cui il gestionale scrive i file dei codici |
| `-ApiKey` | generata | chiave `X-Api-Key` dell'API REST di scrittura |
| `-InstallDir` | `C:\Program Files\FlexivManager` | cartella del programma |
| `-NoEditorPassword`, `-NoShortcut`, `-NoStart` | | salta password, collegamento sul desktop, avvio |

Cosa fa:

- copia il programma in `C:\Program Files\FlexivManager\`;
- crea `data\` (database), `data\backup\` e, se non indichi una cartella del gestionale, `data\erp-import\`;
- scrive **`appsettings.Production.json`** con le impostazioni del sito (leggibile solo da SYSTEM e Administrators, perché contiene chiave API e hash della password);
- registra il servizio con **avvio automatico ritardato** e **riavvio automatico** dopo un errore;
- apre nel firewall **solo** la porta del monitoraggio, profili Dominio e Privato. La porta 8081 non viene aperta: la postazione operatore ascolta solo su `127.0.0.1`;
- crea sul desktop il collegamento **"Postazione operatore robot"**;
- avvia il servizio e controlla che le due pagine rispondano.

## 4. Configurazione

Le impostazioni si leggono in quest'ordine, l'ultima vince: `appsettings.json` (valori di fabbrica, **sostituito a ogni aggiornamento**) → `appsettings.Production.json` (**sito, mai toccato dagli aggiornamenti**).
Modifica quindi solo `appsettings.Production.json` (Blocco note **come amministratore**), poi riavvia il servizio:

```powershell
Restart-Service FlexivManager
```

Esempio completo:

```json
{
  "Hosts": {
    "Monitoring": {
      "Address": "10.0.0.25",
      "Port": 8090,
      "Editor": { "PasswordHash": "PBKDF2-SHA256$210000$..." },
      "Api": { "Key": "..." }
    }
  },
  "Erp": { "ImportFolder": "\\\\server\\gestionale\\flexiv" },
  "Robot": {
    "Backend": "Flexiv",
    "Flexiv": { "SerialNumber": "Rizon10-012345", "RdkVersion": "1.8", "NetworkInterfaces": [ "192.168.2.150" ] }
  }
}
```

- **Nuova password del pianificatore**: `& "C:\Program Files\FlexivManager\FlexivManager.exe" hash-password <password>` e copia il risultato in `PasswordHash`.
- **Passare dal simulatore al robot reale**: `Robot:Backend` = `Flexiv` e `SerialNumber`. Il database del robot reale è un file diverso (`data\planning-flexiv.db`): i codici creati col simulatore non compaiono.
- **Porta del monitoraggio cambiata**: dopo il riavvio aggiorna anche il firewall rilanciando `update.ps1` dal pacchetto, oppure a mano in "Windows Defender Firewall".
- Nel JSON le barre rovesciate vanno raddoppiate (`\\\\server\\share`).

### Cartella condivisa del gestionale

Il servizio gira come **LocalSystem**: sulla rete si presenta come l'account computer `DOMINIO\NOMEPC$`. Due possibilità:

1. dare all'account computer i permessi di **lettura, scrittura e cancellazione** sulla condivisione (più semplice in dominio);
2. usare un **account di servizio** dedicato: `services.msc` → FlexivManager → Proprietà → Connessione → "Account", poi dare a quell'account i permessi sulla condivisione e sulla cartella dati:

```powershell
icacls "C:\Program Files\FlexivManager\data" /grant "DOMINIO\svc-flexiv:(OI)(CI)M"
icacls "C:\Program Files\FlexivManager\appsettings.Production.json" /grant "DOMINIO\svc-flexiv:R"
Restart-Service FlexivManager
```

## 5. Verifica dopo l'installazione

- [ ] `Get-Service FlexivManager` → `Running`, tipo di avvio automatico.
- [ ] Sul PC: il collegamento "Postazione operatore robot" apre la pagina operatore.
- [ ] Da un PC dell'ufficio: `http://<IP-aziendale>:8090/` si apre; il login `planner` funziona.
- [ ] Da un PC dell'ufficio `http://<IP-aziendale>:8081/` **non** risponde (atteso).
- [ ] `http://<IP-aziendale>:8090/api/status` mostra `"backend"` corretto (Mock o Flexiv) e, col robot reale, `"connected": true`.
- [ ] Gestionale: un file `.txt` nella cartella di import sparisce entro un paio di minuti e i codici compaiono nell'editor dei codici.
- [ ] Riavvio del PC: il servizio riparte da solo e il robot **non si muove** finché l'operatore non preme Start.
- [ ] Robot reale: STOP software ferma il piano; l'arresto d'emergenza hardware è stato provato.

## 6. Aggiornamento

1. Estrai il nuovo zip, PowerShell come amministratore nella cartella estratta.
2. Con il robot fermo (nessun ciclo in corso):

```powershell
powershell -ExecutionPolicy Bypass -File .\update.ps1
```

Lo script si rifiuta se il robot sta lavorando (`-Force` per procedere comunque: fermare il servizio **ferma il robot** e chiude il ciclo come fermato). Poi: ferma il servizio, copia i database in `data\backup\pre-update-<data-ora>\`, salva il programma attuale in `C:\Program Files\FlexivManager.previous\`, sostituisce i file **tranne `data\` e `appsettings.Production.json`**, riallinea il firewall, riavvia e verifica.

**Tornare alla versione precedente:**

```powershell
Stop-Service FlexivManager
robocopy "C:\Program Files\FlexivManager.previous" "C:\Program Files\FlexivManager" /MIR /XD "C:\Program Files\FlexivManager\data"
Start-Service FlexivManager
```

Se la nuova versione aveva già modificato il database, ripristina anche i file da `data\backup\pre-update-...` (a servizio fermo).

## 7. Disinstallazione

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1              # tiene data\ e appsettings.Production.json
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData  # cancella tutto (chiede conferma)
```

## 8. Rete e sicurezza

- La **postazione operatore** (che può muovere il robot) ascolta solo su `127.0.0.1`: il programma rifiuta di partire con un altro indirizzo.
- Il **monitoraggio** non può muovere il robot. Con `0.0.0.0` ascolta anche sulla scheda del robot: la regola firewall vale solo per i profili Dominio/Privato, quindi la rete del robot deve restare **Pubblica** (o "Rete non identificata"). Meglio indicare l'IP della scheda aziendale.
- Per limitare chi raggiunge il monitoraggio: `-FirewallRemoteAddress "10.0.0.0/24"` (o modificare la regola "FlexivManager - monitoraggio").
- Tutto in **HTTP** (niente HTTPS per ora): non esporre il PC su Internet.

## 9. Diagnostica

```powershell
Get-Service FlexivManager
sc.exe qc FlexivManager                          # percorso, tipo di avvio, account
Get-NetTCPConnection -State Listen -LocalPort 8081, 8090
Get-WinEvent -LogName Application -MaxEvents 30 | Where-Object ProviderName -like 'FlexivManager*'
Invoke-RestMethod http://127.0.0.1:8081/api/status
```

Con il servizio in esecuzione, gli avvisi e gli errori finiscono nel **Visualizzatore eventi** → Registri di Windows → Applicazione (origine `FlexivManager`). Per vedere tutti i messaggi a video: ferma il servizio e lancia `FlexivManager.exe` da una console amministratore nella cartella di installazione (Ctrl+C per uscire), poi riavvia il servizio.

Errori tipici:

| Sintomo | Causa probabile |
|---|---|
| Il servizio si ferma subito | `appsettings.Production.json` non valido (JSON) o porta già occupata: vedi Visualizzatore eventi |
| Monitoraggio non raggiungibile dall'ufficio | IP sbagliato in `Hosts:Monitoring:Address`, firewall, profilo di rete Pubblico sulla scheda aziendale |
| "versione non installata" / "Wrapper nativo RDK non trovato" / "Impossibile caricare" | manca `native\rdk-<versione>\` per la `RdkVersion` scelta, oppure manca il runtime Visual C++ x64 |
| Robot non connesso | controllo remoto non abilitato in Elements, numero di serie, versione RDK, scheda di rete del robot |
| File del gestionale non letti | permessi dell'account del servizio sulla condivisione (§4) |
