<h1 align="center">AppSys</h1>

<p align="center">
  Monitor del sistema per Windows, in stile <i>conky</i>: CPU, RAM, GPU, dischi, rete, temperatura, batteria e processi,
  con avvisi, analisi approfondita del PC e assistente AI.<br>
  Fratello di <a href="https://github.com/corbolma/AppIP">AppIP</a>: stessa struttura, stesso assistente AI.<br>
  <a href="#english">English below</a>
</p>

<p align="center">
  <a href="https://github.com/corbolma/AppSys/releases/latest"><b>⬇ Scarica l'ultima versione</b></a> ·
  <a href="https://github.com/corbolma/AppSys/wiki">Wiki</a> ·
  <a href="https://github.com/corbolma/AppSys/issues">Segnala un problema</a>
</p>

---

## Cosa fa

### Widget sul desktop
- **CPU**, **RAM** (usata / totale) e **GPU** con barre e grafico degli ultimi 60 secondi
- **Dischi**: lettura/scrittura, attività e spazio di ogni volume
- **Rete**: download e upload
- **Temperatura** (zone termiche ACPI, se il PC le espone) e **batteria**
- **Processi più pesanti** (raggruppati per nome: CPU e memoria privata)
- Nome del PC, modello della CPU e da quanto è acceso
- Il widget resta **incollato al desktop** (visibile anche con Win+D), si trascina dove vuoi; **doppio clic** apre l'analisi
- Fissata alla barra delle applicazioni (dal menu Start), il tasto destro sull'icona offre *Analisi approfondita*,
  *Mostra in primo piano*, *Sempre in primo piano* ed *Esci* (solo mentre l'app è in esecuzione)
- L'icona nella tray mostra l'**uso della CPU** in tempo reale; il tooltip riassume CPU, RAM e spazio libero

### Avvisi
Un fumetto quando qualcosa resta al limite: CPU sopra il 90% per 2 minuti, RAM sopra il 92%, PC molto caldo,
disco quasi pieno, batteria quasi scarica. Indica il processo responsabile; cliccandolo si apre l'analisi.

### Cronologia
Ogni minuto AppSys salva un riassunto (medie, picchi, processo più pesante): 24 ore in memoria e, se attiva,
7 giorni in `%APPDATA%\AppSys\history.csv` (solo sul PC). Serve all'analisi e all'assistente per capire
*quando* il PC è stato lento e *per colpa di chi*.

### Analisi approfondita
17 controlli in sola lettura, senza permessi di amministratore, in 10-40 secondi:

| Controllo | Cosa guarda |
|---|---|
| Sistema | versione di Windows, modello, CPU, RAM, GPU e driver, da quanto è acceso |
| Processore / Memoria | carico attuale e delle ultime ore, memoria impegnata, file di paging, processi più pesanti |
| Spazio su disco | spazio libero di ogni volume (soglie più severe per il disco di sistema) |
| Salute dei dischi | stato SSD/HDD/NVMe; come amministratore anche usura, temperatura, ore di accensione, errori |
| Errori dei dischi | blocchi danneggiati, errori di I/O, reset del controller nel registro eventi |
| Arresti anomali | schermate blu e spegnimenti improvvisi degli ultimi 30 giorni |
| Errori hardware | segnalazioni WHEA (processore, memoria, PCIe) |
| Programmi bloccati | programmi che si chiudono o non rispondono, con il modulo responsabile |
| Temperatura, Batteria | temperatura attuale e massima; salute della batteria e cicli di carica |
| Avvio automatico | programmi che partono con Windows (anche quelli disattivati) |
| Aggiornamenti di Windows | data dell'ultimo aggiornamento, riavvio in sospeso |
| Antivirus | antivirus attivo e aggiornato |
| Dispositivi e driver | dispositivi con errori in Gestione dispositivi |
| File temporanei | cartella temporanea e Cestino |
| Indice di affidabilità | indice di stabilità di Windows (Monitoraggio affidabilità) |

Il risultato è un **punteggio su 100** con i consigli più importanti. *Copia report* e *Salva report...* producono
un testo da mandare all'assistenza.

### Assistente AI
Lo stesso di AppIP, facoltativo (*Assistente AI...* nel menu):
- **Assistente locale** (Qwen3.5 con llama.cpp sul PC): nessuna chiave e i dati non escono. Se AppIP o AppPad hanno
  già scaricato modello e llama.cpp, AppSys usa quei file
- **Claude** con la tua chiave API, **Claude Code** con il tuo abbonamento (solo uso personale), oppure un servizio
  **compatibile OpenAI** (OpenAI, OpenRouter, Ollama, LM Studio)

Al primo avvio AppSys **riprende le impostazioni dell'assistente da AppIP** (le chiavi restano cifrate con DPAPI per il tuo utente).

Nell'analisi:
- **Spiega con l'AI**: cosa significano i risultati e cosa fare, partendo dal più importante
- **Chiedi**: una conversazione sul PC, per esempio *"perché il PC è lento la mattina?"* o *"cosa occupa spazio sul disco C?"*
- Con Claude e i servizi compatibili OpenAI l'assistente può **fare da sé fino a 8 controlli in sola lettura** per
  approfondire: processi per CPU/memoria/disco, dettagli di un processo sconosciuto (percorso, produttore), registro
  eventi, cronologia delle risorse, cartelle più grandi, servizi automatici fermi, programmi installati di recente,
  programmi all'avvio. Ogni controllo compare nel riquadro mentre viene eseguito
- **Cosa viene inviato** mostra il testo esatto. Di default nome del PC, dominio e nome utente (anche nei percorsi)
  diventano segnaposti, e la risposta torna in chiaro solo sul PC

## Installazione
1. Scarica `AppSys-Setup-X.Y.Z.exe` dalle [Releases](https://github.com/corbolma/AppSys/releases/latest).
2. Avvialo. **Non servono permessi di amministratore**: installa solo per l'utente corrente in `%LOCALAPPDATA%\Programs\AppSys`.
3. Se compare *"Windows ha protetto il PC"*, clicca **Ulteriori informazioni → Esegui comunque** (l'eseguibile non è firmato digitalmente).

Installazione silenziosa (per l'IT): `AppSys-Setup-X.Y.Z.exe /silent` · Disinstallazione: Impostazioni → App, oppure
`"%LOCALAPPDATA%\Programs\AppSys\Uninstall.exe" /uninstall /silent` (rimuove anche impostazioni, cronologia e file
dell'assistente locale scaricati da AppSys; quelli di AppIP e AppPad restano).

## Aggiornamenti
*Aggiornamenti → Controlla ora* confronta la versione con l'ultima release su GitHub e, se ce n'è una nuova, scarica
l'installer, ne verifica lo SHA-256 e lo avvia. Il controllo automatico (una volta al giorno) è disattivato di default.

## Riga di comando
```bat
AppSys.exe                    :: avvia il widget (o lo porta in primo piano)
AppSys.exe /analysis          :: apre l'analisi approfondita
AppSys.exe /topmost           :: attiva/disattiva "Sempre in primo piano"
AppSys.exe /exit              :: chiude AppSys
AppSys.exe /report file.txt   :: analisi completa salvata su file, senza interfaccia (per l'IT o gli script)
```

## Compilare dal sorgente
Non serve installare nulla: si usa il compilatore C# incluso in Windows (.NET Framework 4.x).
```bat
build.bat                      :: crea AppSys.exe (e appsys.ico la prima volta)
build-installer.bat            :: crea dist\AppSys-Setup-X.Y.Z.exe e stampa il suo SHA-256
build-installer.bat patch      :: incrementa la versione (1.0.0 -> 1.0.1) e crea l'installer
```

**Pubblicare una versione:** crea su GitHub una release con tag `vX.Y.Z`, allega `dist\AppSys-Setup-X.Y.Z.exe` e scrivi
nelle note la riga `SHA-256: ...` stampata da `build-installer.bat` (la trovi anche in `dist\AppSys-Setup-X.Y.Z.sha256.txt`):
l'aggiornamento automatico la usa per verificare il file scaricato.

## Lingue
Interfaccia in **italiano e inglese**: segue la lingua di Windows e si cambia dal menu *Lingua / Language*. Anche
l'assistente AI risponde nella lingua scelta. Altre lingue si aggiungono con un semplice file di testo, senza
programmare: vedi [lang/README.md](lang/README.md) e la pagina [Lingue e traduzioni](https://github.com/corbolma/AppSys/wiki/Lingue-e-traduzioni)
della wiki. `lang-check.ps1` rigenera il modello `lang/_template.txt` e controlla le traduzioni.

## Privacy
AppSys non raccoglie dati e non fa connessioni di rete per il monitoraggio. Il controllo degli aggiornamenti
(`api.github.com`) parte solo da *Controlla ora* o se attivi quello automatico. Cronologia e impostazioni restano in
`%APPDATA%\AppSys`. L'assistente locale scarica modello e llama.cpp da `huggingface.co` e `github.com` solo quando premi
*Scarica*. Un assistente remoto, solo se lo scegli e solo quando premi *Spiega* o *Chiedi*, riceve il report dell'analisi
e i risultati dei controlli che chiede.

---

## English

**AppSys** is a small *conky*-style desktop widget and tray icon for Windows 10/11 that shows CPU, RAM, GPU, disk
activity and free space, network speed, temperature, battery and the heaviest processes, with a 60-second graph and the
CPU load in the tray icon. It warns you when something stays at the limit (CPU, memory, heat, full disk, low battery)
and records a per-minute summary (24 hours in memory, optionally 7 days on disk) to tell *when* the PC was slow and *why*.

- **In-depth analysis**: 17 read-only checks without administrator rights (system, CPU, memory, disk space and health,
  disk errors, blue screens and unexpected shutdowns, WHEA hardware errors, crashing programs, temperature, battery
  health, startup programs, Windows Update, antivirus, devices and drivers, temporary files, reliability index), with a
  score out of 100, plain-language advice and a report to copy or save.
- **AI assistant** (optional, same as AppIP): local model (Qwen3.5 via llama.cpp, data stays on the PC; reuses the files
  already downloaded by AppIP or AppPad), Claude (API key), Claude Code (your own subscription, personal use) or any
  OpenAI-compatible service. *Explain with AI* and a chat about your PC; with Claude and OpenAI-compatible services the
  assistant can run up to 8 read-only checks by itself (processes, process details, event log, resource history, largest
  folders, stopped services, recently installed programs, startup programs). Computer name, domain and user name are
  replaced by placeholders by default, and *What is sent* shows the exact text.
- **Install:** download `AppSys-Setup-X.Y.Z.exe` from [Releases](https://github.com/corbolma/AppSys/releases/latest).
  Per-user install, no admin rights required; silent install with `/silent`. **Updates** from GitHub Releases with
  SHA-256 verification (automatic check off by default).
- **Build:** `build.bat` / `build-installer.bat`, with the C# compiler bundled with Windows (.NET Framework 4.x).
- **Translations are welcome**: the interface is in English and Italian (it follows the Windows display language and can
  be changed from *Lingua / Language*); adding a language is just a text file, no coding required — see [lang/README.md](lang/README.md).
- **Privacy:** no telemetry; monitoring makes no network connections. Data goes out only to the AI service you choose,
  when you press *Explain* or *Ask*, and to `api.github.com` when checking for updates.

## Componenti di terze parti / Third-party components
AppSys non include componenti di terze parti. L'assistente locale, solo se lo attivi e premi *Scarica* (e se AppIP o
AppPad non li hanno già scaricati), scarica sul PC:

| Componente | Licenza | Da dove |
|---|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) (`llama-server`), di ggml-org | [MIT](https://github.com/ggml-org/llama.cpp/blob/master/LICENSE) | release ufficiali su GitHub |
| Modelli [Qwen3.5](https://huggingface.co/Qwen) 2B o 4B, di Alibaba (quantizzazione GGUF di unsloth) | [Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0) | Hugging Face |

Ogni file è controllato con il suo SHA-256. / Each file is verified with its SHA-256.

## Licenza / License
[MIT](LICENSE) © 2026 Mauro Corboli
