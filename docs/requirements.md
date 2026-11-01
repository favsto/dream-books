# Requisiti – App di annotazione PDF per Surface

Oct 29, 2026 · @vorrei sviluppare una app in flutter per gestire in modo collabo

## 1. Introduzione

L'app è un lettore e annotatore PDF per Windows, pensato per Microsoft Surface con Surface Slim Pen 2. L'interfaccia è ridotta al minimo: il documento occupa tutto lo schermo e i comandi compaiono solo quando servono, in forma di pannelli flottanti semitrasparenti.

**Obiettivo.** Permettere di leggere un PDF e scriverci sopra a mano con la stessa naturalezza della carta, sfruttando pressione e gomma della penna, e salvare un PDF standard leggibile da qualsiasi altro programma.

**Destinatari del documento.** Lo sviluppatore (umano o assistito da AI) che costruirà l'app da zero. Il documento descrive *cosa* deve fare l'app e *come* deve apparire; le scelte tecniche indicate sono vincolanti salvo diversa decisione esplicita.

**Convenzioni.** Codice, nomi di classi, file, impostazioni e chiavi di configurazione sono in inglese. I testi visibili all'utente sono in italiano, predisposti per la localizzazione.

I requisiti sono numerati con prefisso: **FR** funzionali, **NFR** non funzionali, **UI** interfaccia. Le priorità sono *Must* (indispensabile per la v1), *Should* (atteso in v1 se possibile), *Could* (successivo).

### Glossario

| Termine | Significato |
| --- | --- |
| Tratto (stroke) | Linea continua disegnata dalla penna tra appoggio e sollevamento |
| Pressione | Forza applicata dalla punta, 4096 livelli sulla Slim Pen 2, normalizzata 0–1 |
| Dorso / gomma | Estremità superiore della penna, rilevata da Windows come penna invertita |
| Evidenziatore | Strumento a tratto largo, semitrasparente, che non copre il testo |
| Annotazione | Qualsiasi oggetto aggiunto dall'utente sopra il PDF originale |
| Segnalibro | Voce dell'indice del PDF (outline) che punta a una pagina |
| Toolbar flottante | Barra comandi sospesa sopra il documento, spostabile e a scomparsa |
| Acrylic / Mica | Materiali di trasparenza sfocata di Windows 11 |

## 2. Piattaforma e stack tecnologico

L'app è un'applicazione desktop nativa per Windows 11, scritta in C# su .NET (ultima versione LTS) con WPF. WPF è scelto perché offre un `InkCanvas` maturo con pressione e gomma integrate; l'aspetto moderno si ottiene con i materiali di Windows 11 e uno stile grafico personalizzato.

| Componente | Scelta | Motivo |
| --- | --- | --- |
| Sistema operativo | Windows 11 (22H2 o successivo) | Necessario per Mica, Acrylic e angoli arrotondati nativi |
| Dispositivo di riferimento | Surface Pro / Laptop con Slim Pen 2 | Target principale di test |
| Linguaggio | C# su .NET LTS | Ecosistema maturo, supporto Ink nativo |
| UI framework | WPF | `InkCanvas` con pressione, gomma invertita, highlighter |
| Stile moderno | Libreria WPF UI (stile Fluent) o stili custom equivalenti | Mica, Acrylic, controlli Fluent senza riscrivere tutto |
| Rendering PDF | PDFium tramite wrapper .NET | Rendering veloce e fedele delle pagine |
| Scrittura PDF | PDFium (lo stesso motore della lettura) | Salvataggio annotazioni e dati privati con riscrittura completa del file; la scrittura dei segnalibri si decide nella fase 3. PDFsharp non è usato (decisione del 30/10/2026: dopo una prova, PDFium legge e scrive senza un secondo parser che potrebbe rifiutare file che PDFium apre) |
| Architettura | MVVM (CommunityToolkit.Mvvm) | Separazione UI e logica, testabilità |
| Distribuzione | MSIX, con opzione installer classico | Installazione pulita e aggiornamenti |

**Vincoli di licenza.** Sono ammesse solo librerie con licenza permissiva (MIT, BSD, Apache 2.0). Librerie AGPL (es. iText) sono escluse.

**Ambiente di sviluppo.** Visual Studio Community con carico di lavoro "Sviluppo desktop .NET", Git, Claude Code. Le dipendenze si gestiscono solo via NuGet.

**Alternativa valutata.** WinUI 3 avrebbe un aspetto più nativo, ma l'inchiostro su WinUI 3 va verificato prima di adottarlo. La scelta si può rivalutare se, all'avvio del progetto, WinUI 3 offre un controllo Ink equivalente.

## 3. Principi di UI/UX e linguaggio visivo

Il documento è sempre protagonista: nessuna barra fissa, nessuna cornice, nessun menu tradizionale. Ogni comando vive in un elemento flottante che compare quando serve e si ritira quando l'utente scrive.

### Principi

1. **Contenuto a tutto schermo.** Il PDF occupa l'intera finestra, anche sotto la barra del titolo (barra del titolo personalizzata e trasparente).
2. **Comandi effimeri.** Toolbar e pannelli si nascondono automaticamente durante la scrittura e riappaiono al sollevamento della penna o con un tocco.
3. **Una sola mano, un solo gesto.** Le azioni frequenti (cambio strumento, colore, undo) si raggiungono in un tocco.
4. **Profondità, non bordi.** Gli elementi si distinguono per trasparenza, sfocatura e ombra, non per linee e riquadri.
5. **Movimento con significato.** Ogni animazione indica da dove viene e dove va un elemento; nessuna animazione decorativa.

### Materiali e colori

| Elemento | Specifica |
| --- | --- |
| Sfondo finestra | Mica (Windows 11), visibile attorno alle pagine |
| Pannelli flottanti | Acrylic: sfocatura 30 px, opacità tinta 60–70% |
| Tema | Chiaro e scuro, segue il tema di sistema, commutabile a mano |
| Colore accento | Colore accento di sistema, con override nelle impostazioni |
| Sfondo attorno alle pagine | Grigio neutro scuro in tema scuro, chiaro in tema chiaro |
| Pagine | Ombra morbida (blur 24 px, opacità 15%), angoli dritti |

### Forme e tipografia

- Angoli arrotondati: 12 px per pannelli, 999 px (pillola) per toolbar e pulsanti singoli, 8 px per elementi interni.
- Ombre: due livelli, uno diffuso (blur 32 px, 12%) e uno di contatto (blur 4 px, 8%).
- Font: Segoe UI Variable; icone Segoe Fluent Icons, dimensione 20 px.
- Area di tocco minima 40 × 40 px anche se l'icona è più piccola.

### Animazioni

| Evento | Animazione | Durata |
| --- | --- | --- |
| Comparsa pannello flottante | Dissolvenza + scala da 0,95 a 1 | 180 ms, ease-out |
| Scomparsa pannello flottante | Dissolvenza + scala a 0,97 | 120 ms, ease-in |
| Comparsa e scomparsa pannello laterale (ancorato al bordo dello schermo, es. UI-04) | Scorrimento dal bordo e ritorno al bordo | 180 ms ease-out in entrata, 120 ms ease-in in uscita |
| Ritiro toolbar durante la scrittura | Dissolvenza a 0% | 200 ms dopo 300 ms di scrittura |
| Cambio strumento | Indicatore che scorre sotto l'icona attiva | 150 ms |
| Undo / redo | Breve evidenziazione dell'area modificata | 250 ms |

Tutte le animazioni si disattivano se l'utente ha attivato "Effetti di animazione: off" in Windows.

## 4. Struttura dell'interfaccia

L'interfaccia ha un solo livello di base, il canvas del documento, e quattro elementi flottanti sovrapposti. Nessuno di essi è visibile in permanenza tranne, opzionalmente, la toolbar.

| ID | Elemento | Posizione predefinita | Comportamento |
| --- | --- | --- | --- |
| UI-01 | Canvas documento | Tutta la finestra | Pagine in scorrimento verticale continuo, centrate |
| UI-02 | Toolbar strumenti | Bordo inferiore, centrata | Pillola Acrylic; trascinabile su qualunque bordo; si ritira durante la scrittura |
| UI-03 | Menu principale | Pulsante tondo in alto a sinistra | Si espande in un pannello con Apri, Salva, Salva con nome, File recenti, Esporta, Impostazioni |
| UI-04 | Pannello segnalibri e miniature | Bordo sinistro, a scomparsa | Si apre con swipe dal bordo o dal menu; due schede: Segnalibri e Pagine (nel blocco 3b: in 3b-1 il pannello laterale ospita l'elenco dei segni (FR-103), con Ctrl+M per aprirlo e chiuderlo; la scheda Pagine, con le miniature che includono i propri tratti, arriva in 3b-2; la scheda Segnalibri arriva con i segnalibri. La penna non usa mai il pannello: sul bordo scrive, sul pannello aperto lo chiude senza scrivere) |
| UI-05 | Indicatore pagina e zoom | In basso a destra | Mostra "12 / 48 · 125%"; compare durante scroll e zoom, sparisce dopo 1,5 s |
| UI-06 | Barra del titolo | In alto, trasparente | Solo i pulsanti finestra e il nome file in piccolo; si nasconde a schermo intero |
| UI-07 | Barra di scorrimento con anteprima | Bordo destro, sottile, a scomparsa | Compare mentre si scorre e mostra la posizione; toccandola o trascinandola compare l'anteprima grande della pagina corrispondente; svanisce da sola dopo circa 1,5 s con dissolvenza morbida (istantanea se le animazioni di Windows sono disattivate). Sulla barra compaiono piccole tacche per i segni e, in rosso, per le note scritte a mano (FR-101), e per i risultati della ricerca (FR-105), con colori diversi (blocco 3b) |

### Toolbar strumenti (UI-02)

La toolbar contiene, da sinistra a destra: Penna, Evidenziatore, Gomma, Selezione (lazo), separatore, Undo, Redo, separatore, Segnalibro sulla pagina corrente.

- Un tocco su uno strumento lo attiva. Un secondo tocco sullo strumento già attivo apre il suo **pannello opzioni** flottante sopra la toolbar.
- Il pannello opzioni della penna mostra: 8 colori preimpostati, selettore colore libero, 3 spessori preimpostati, slider spessore, anteprima del tratto con pressione.
- Il pannello dell'evidenziatore mostra: 6 colori (giallo, verde, azzurro, rosa, arancio, viola), 3 spessori, modalità "tratto dritto".
- Il pannello della gomma mostra: cancella tratto intero / cancella parte del tratto, dimensione, "Cancella tutto nella pagina".
- Ogni strumento ricorda colore e spessore scelti tra una sessione e l'altra.

### Visibilità automatica

| Situazione | Toolbar | Altri pannelli |
| --- | --- | --- |
| Penna appoggiata e in scrittura | Si dissolve dopo 300 ms | Si chiudono |
| Penna sollevata da 600 ms | Riappare | Restano chiusi |
| Tocco con un dito su area vuota | Mostra / nasconde | Si chiudono |
| Modalità "Toolbar fissa" attiva | Sempre visibile | Invariato |

### Schermata iniziale

Senza documento aperto, il canvas mostra al centro un pannello Acrylic con: pulsante "Apri PDF", elenco degli ultimi 8 file con miniatura, area di trascinamento file.

## 5. Requisiti funzionali: apertura, navigazione, zoom

L'utente deve poter aprire qualsiasi PDF standard e muoversi nel documento con dita, penna, tastiera e mouse senza cambiare modalità.

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-01 | Apertura PDF da menu, trascinamento sulla finestra, "Apri con" da Esplora file e associazione al tipo .pdf | Must |
| FR-02 | Apertura di PDF protetti da password, con richiesta della password | Should |
| FR-03 | Elenco degli ultimi 8 file aperti, con miniatura della prima pagina | Should |
| FR-04 | Riapertura del documento alla pagina, allo zoom e alla posizione dell'ultima sessione, anche per i documenti senza modifiche non salvate; la posizione si conserva fuori dal PDF, in un piccolo file locale per documento (il PDF non viene toccato) | Should |
| FR-05 | Scorrimento verticale continuo con un dito, rotellina o barra di scorrimento sottile a scomparsa. Lo scorrimento col dito ha inerzia: lanciata, la pagina prosegue e rallenta progressivamente, tanto più lontano quanto più veloce è il lancio; si ferma subito a inizio e fine documento (senza rimbalzo), a un nuovo tocco, quando la penna si avvicina o scrive, con tastiera, rotellina, barra, salti e zoom. Il pinch non ha inerzia | Must |
| FR-06 | Zoom con pinch a due dita, Ctrl + rotellina, Ctrl + / Ctrl −; range 25%–800% | Must |
| FR-07 | Doppio tocco con un dito: alterna "adatta larghezza" e zoom 200% sul punto toccato | Should |
| FR-08 | Modalità di visualizzazione: pagina singola continua (predefinita), due pagine affiancate | Could |
| FR-09 | Vai a pagina: tocco sull'indicatore pagina (UI-05) apre un campo numerico | Should |
| FR-10 | Navigazione da tastiera: PagSu, PagGiù, Home, Fine | Should |
| FR-11 | Schermo intero con F11 o dal menu | Should |
| FR-12 | Rotazione della vista di una pagina di 90° (solo visualizzazione, non modifica il file) | Could |
| FR-13 | Ricerca di testo nel PDF con evidenziazione dei risultati (solo dove il PDF ha un livello di testo) | Should |

**Separazione dita / penna.** Il dito non disegna mai: serve solo a scorrere, zoomare e toccare i comandi. La penna scrive sempre con lo strumento attivo. Il mouse si comporta come la penna se lo strumento attivo è di disegno, altrimenti scorre. Questo comportamento si può cambiare nelle impostazioni ("Disegna anche con il dito").

**Rifiuto del palmo.** Mentre la penna è in prossimità dello schermo, i tocchi del palmo vengono ignorati, sfruttando il rilevamento di Windows Ink.

## 6. Requisiti funzionali: inchiostro, gomma, evidenziatori

La scrittura è la funzione centrale: deve essere immediata, fluida e fedele alla pressione su tutti i 4096 livelli della Slim Pen 2.

### Penna

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-20 | Il tratto varia di spessore in base alla pressione della punta, usando il valore continuo fornito da Windows Ink senza quantizzazione | Must |
| FR-21 | Curva di pressione regolabile nelle impostazioni (lineare, morbida, decisa) con anteprima | Should |
| FR-22 | Spessore base da 0,25 a 5 mm; a pressione massima il tratto raggiunge 1,5× lo spessore base, a pressione minima 0,3× | Must |
| FR-23 | Tratti levigati (curve di Bézier) senza perdere precisione nei dettagli piccoli | Must |
| FR-24 | Latenza percepita minima: il tratto appare sotto la punta durante il movimento, non al sollevamento | Must |
| FR-25 | 8 colori preimpostati modificabili + selettore colore libero | Must |
| FR-26 | Le annotazioni seguono la pagina con zoom e scroll, restando ancorate al punto del PDF | Must |
| FR-27 | Ogni tratto appartiene a una sola pagina e viene ritagliato al suo bordo | Must |
| FR-28 | Il pulsante laterale della penna, tenuto premuto, attiva temporaneamente lo strumento Selezione (lazo) | Should |

**Decisione (30/10/2026).** Dopo un confronto su dispositivo con l'inchiostro standard di WPF, è stato adottato il renderer personalizzato come unico motore del tratto. Impostazioni predefinite: curva di pressione "decisa" (FR-21) e levigatura "forte" (FR-23).

### Gomma

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-30 | Usando il dorso della penna si cancella, qualunque sia lo strumento attivo; al sollevamento torna lo strumento precedente | Must |
| FR-31 | Due modalità: cancella tratto intero (predefinita) e cancella parte del tratto | Must |
| FR-32 | Un cerchio semitrasparente mostra l'area della gomma sotto il dorso | Should |
| FR-33 | La gomma cancella solo annotazioni, mai il contenuto originale del PDF | Must |
| FR-34 | Lo strumento Gomma della toolbar permette di cancellare anche con la punta | Must |

### Evidenziatore

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-40 | Tratto largo, semitrasparente, che non copre il testo sottostante (fusione tipo "moltiplica") | Must |
| FR-41 | 6 colori preimpostati: giallo, verde, azzurro, rosa, arancio, viola | Must |
| FR-42 | Spessore costante, non dipendente dalla pressione | Must |
| FR-43 | Sovrapporre due passate dello stesso colore non scurisce il risultato | Should |
| FR-44 | Modalità "tratto dritto": il tratto si raddrizza in orizzontale se resta quasi rettilineo | Should |
| FR-45 | Evidenziazione del testo selezionato (aggancio alle parole del PDF) | Could |

#### Punta a scalpello

Oltre alla punta tonda, l'evidenziatore offre una punta a scalpello (rettangolare sottile), il cui segno cambia larghezza in base all'orientamento della punta rispetto alla direzione del tratto.

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-46 | Punta a scalpello con angolo fisso, regolabile nel pannello opzioni (0–180°, predefinito 45°); proporzioni della punta regolabili | Should |
| FR-47 | Modalità "segui l'impugnatura": l'orientamento della punta segue la direzione verso cui pende la penna (azimut dell'inclinazione), aggiornato punto per punto | Could |
| FR-48 | Se la penna trasmette la rotazione attorno al proprio asse (barrel rotation), l'orientamento della punta segue la rotazione reale; altrimenti si applica FR-47 o FR-46 | Could |

**Limite hardware.** Le specifiche della Surface Slim Pen 2 citano pressione e inclinazione, ma non la rotazione attorno al proprio asse: salvo verifica contraria (vedi Domande aperte), con questa penna FR-48 non è attivabile e ruotare la penna tra le dita non cambia il segno. App come Photoshop e Illustrator gestiscono la rotazione solo con penne che la trasmettono (es. Wacom Art Pen). FR-48 va comunque predisposto leggendo il campo di rotazione dell'input Windows quando presente, per penne future.

**Impatto tecnico.** L'`InkCanvas` di WPF supporta una punta rettangolare con orientamento fisso per tutto il tratto, sufficiente per FR-46. FR-47 e FR-48 richiedono un rendering personalizzato del tratto, con orientamento variabile punto per punto; il dato va salvato per ogni `InkPoint` (campo `TipAngle`). Il salvataggio come contorno vettoriale (sezione 7) copre già qualsiasi forma della punta.

### Selezione

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-50 | Strumento lazo per selezionare uno o più tratti | Should |
| FR-51 | Sui tratti selezionati: sposta, ridimensiona, cambia colore, elimina, copia/incolla | Should |

## 7. Requisiti funzionali: undo/redo, segnalibri, salvataggio

Il salvataggio è il requisito più delicato: il file prodotto deve essere un PDF standard, apribile ovunque con le annotazioni visibili, e riapribile nell'app con le annotazioni ancora modificabili.

### Undo e redo

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-60 | Pulsanti Undo e Redo nella toolbar, sempre raggiungibili con un tocco | Must |
| FR-61 | Scorciatoie Ctrl+Z e Ctrl+Y (anche Ctrl+Maiusc+Z) | Must |
| FR-62 | Cronologia di almeno 200 operazioni per documento | Must |
| FR-63 | Ogni operazione è annullabile: tratto aggiunto, cancellazione (intera o parziale), spostamento, cambio colore, segnalibro aggiunto/rimosso/rinominato | Must |
| FR-64 | Pulsanti disattivati e semitrasparenti quando non c'è nulla da annullare/ripristinare | Must |
| FR-65 | Un nuovo tratto dopo un undo svuota la pila del redo | Must |
| FR-66 | Gesto a due dita con tocco = undo, tre dita = redo | Could |

### Segnalibri

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-70 | Aggiunta di un segnalibro sulla pagina corrente con un tocco dalla toolbar; nome predefinito "Pagina N" | Must |
| FR-71 | Rinomina, eliminazione e riordino (trascinamento) dal pannello segnalibri | Must |
| FR-72 | Tocco su un segnalibro porta alla pagina con animazione di scorrimento | Must |
| FR-73 | Il pannello mostra anche i segnalibri già presenti nel PDF originale | Must |
| FR-74 | I segnalibri sono salvati come indice standard del PDF (outline), visibile in qualsiasi lettore | Must |
| FR-75 | Segnalibri annidati (sottovoci) | Could |
| FR-76 | Indicatore discreto sul bordo della pagina che ha un segnalibro | Should |

**Rinviati (decisione del 31/10/2026).** FR-70–FR-76 non fanno parte del blocco 3b: Fausto preferisce segni solo visivi, riconosciuti dai suoi tratti (FR-100–FR-104), e non vuole dipendere dai segnalibri nativi del PDF. La scrittura dell'indice (FR-74) resta un requisito Must della v1 ma non è pianificata; è una decisione aperta (PDFium non sa scrivere i segnalibri).

### Segni e ricerca (blocco 3b)

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-100 | Una linea laterale tracciata a penna (quasi verticale, anche leggermente inclinata, curva o ripassata più volte, lunga almeno 2 righe di testo, nel margine esterno della pagina; tracciata a mano libera: il tremolio della penna non conta. Finché non si legge il testo della pagina (3b-2) una riga vale 15 pt, quindi almeno 30 pt, circa 1 cm) è riconosciuta come segno; anche un passaggio evidenziato diventa un segno quando esiste l'evidenziatore (3c). I segni sono ricavati dai tratti a ogni apertura e modifica: non si scrive nulla di nuovo nel PDF | Must |
| FR-101 | I segni compaiono come tacche sulla barra di scorrimento (UI-07); in un altro colore (rosso) compaiono anche le note scritte a mano: ogni gruppo di tratti che non sono linee laterali (scritte, disegni, sottolineature, note nel margine), dove i tratti distano meno di 4 righe in verticale. Punti e tocchi isolati non contano. Per ora elenco dei segni e Alt+Su/Giù restano solo per i segni | Must |
| FR-102 | Salto al segno precedente/successivo (pulsanti nel menu, scorciatoie Alt+Su e Alt+Giù), che registra la posizione nello storico del tasto Indietro (FR-09); agli estremi non salta e avvisa "Nessun altro segno" | Must |
| FR-103 | Elenco dei segni nel pannello laterale sinistro (UI-04; si apre con swipe dal bordo, dal menu o con Ctrl+M): miniatura del passaggio con le note scritte accanto, numero di pagina; un tocco porta al passaggio | Should |
| FR-104 | Più linee sullo stesso passaggio contano come un solo segno; un solo tipo di segno, senza livelli né colori | Must |
| FR-105 | I risultati della ricerca (FR-13) compaiono come tacche sulla barra e sono evidenziati sulla pagina senza coprire i tratti; avanti/indietro tra i risultati | Should |

### Salvataggio

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-80 | Salva (Ctrl+S) sovrascrive il file aperto; Salva con nome (Ctrl+Maiusc+S) crea un nuovo file | Must |
| FR-81 | Il PDF salvato mostra tutte le annotazioni in qualsiasi lettore (Edge, Acrobat, browser), con lo spessore variabile della pressione preservato | Must |
| FR-82 | Il contenuto originale del PDF (testo selezionabile, link, moduli) resta intatto | Must |
| FR-83 | Riaprendo nell'app un PDF salvato da essa, i tratti tornano oggetti modificabili (cancellabili, selezionabili) | Must |
| FR-84 | Diario locale delle modifiche non salvate (tratti aggiunti, cancellazioni, annulla/ripristina, posizione di lettura), scritto al termine di ogni gesto nei dati dell'app, mai sul file originale; non c'è un salvataggio automatico a tempo: salva l'utente. Il diario si svuota al salvataggio, ma conserva le modifiche fatte mentre il salvataggio era in corso; si cancella se l'utente rifiuta di salvare alla chiusura e quando il documento viene chiuso senza modifiche non salvate | Must |
| FR-85 | Alla chiusura con modifiche non salvate: dialogo Salva / Non salvare / Annulla | Must |
| FR-86 | Recupero dopo crash: all'avvio, o aprendo il file, l'app ripristina da sola le modifiche dal diario (stessi tratti, stessa pagina, zoom e posizione), con un avviso, e il documento risulta modificato e non salvato | Should |
| FR-87 | Esportazione "appiattita": PDF con annotazioni fuse nella pagina, non più modificabili | Should |
| FR-88 | Esportazione di una pagina come immagine PNG | Could |
| FR-89 | Il salvataggio avviene in background, senza bloccare la scrittura; un indicatore discreto ne mostra l'avanzamento | Should |

### Strategia di salvataggio

Le annotazioni Ink standard del PDF hanno spessore costante e non possono rappresentare la pressione. Per questo ogni tratto viene salvato in due forme:

1. **Forma visibile:** il tratto è convertito in un contorno vettoriale pieno (poligono che segue lo spessore variabile) e scritto come annotazione PDF con aspetto personalizzato (appearance stream). Qualsiasi lettore lo mostra esattamente come nell'app.
2. **Forma modificabile:** i dati originali del tratto (punti, pressione, colore, strumento) sono serializzati in JSON e salvati nello stesso PDF come dati privati dell'app, collegati a ciascuna annotazione tramite un identificatore.

All'apertura, se i dati privati sono presenti e coerenti, l'app ricostruisce i tratti modificabili; altrimenti mostra le annotazioni come oggetti di sola lettura.

Gli evidenziatori seguono la stessa strategia, con opacità e modalità di fusione "Multiply" nell'aspetto dell'annotazione.

## 8. Requisiti non funzionali

I valori sono misurati su una Surface Pro di fascia media (Intel Core i5 o Snapdragon X, 16 GB RAM) con un PDF di riferimento di 300 pagine.

| ID | Area | Requisito | Priorità |
| --- | --- | --- | --- |
| NFR-01 | Prestazioni | Avvio a freddo fino alla schermata iniziale in meno di 1,5 s | Should |
| NFR-02 | Prestazioni | Prima pagina visibile entro 1 s dall'apertura di un PDF di 300 pagine | Must |
| NFR-03 | Prestazioni | Scorrimento e zoom a 60 fps; le pagine non ancora renderizzate mostrano un segnaposto, mai uno schermo bianco | Must |
| NFR-04 | Prestazioni | Tratto visualizzato sotto la punta senza ritardo percepibile durante la scrittura | Must |
| NFR-05 | Prestazioni | Rendering solo delle pagine visibili più 2 prima e 2 dopo; cache limitata in memoria | Must |
| NFR-06 | Prestazioni | Memoria inferiore a 600 MB con un PDF di 300 pagine e 2.000 tratti | Should |
| NFR-07 | Prestazioni | Ripetizione del rendering in alta risoluzione dopo lo zoom, senza attesa visibile per l'inchiostro | Must |
| NFR-10 | Affidabilità | Nessuna perdita di dati: il salvataggio scrive su file temporaneo e sostituisce l'originale solo a scrittura riuscita | Must |
| NFR-11 | Affidabilità | Un PDF danneggiato o non supportato mostra un messaggio chiaro senza chiudere l'app | Must |
| NFR-12 | Affidabilità | Log degli errori in file locale, senza dati del contenuto dei documenti | Should |
| NFR-20 | Compatibilità | PDF salvati validi secondo lo standard PDF 1.7 e apribili senza avvisi in Acrobat Reader, Edge e Chrome | Must |
| NFR-21 | Compatibilità | Funziona su processori x64 e ARM64 (Surface con Snapdragon) | Must |
| NFR-22 | Compatibilità | Supporto di schermi ad alta densità e scala di sistema da 100% a 250% | Must |
| NFR-23 | Compatibilità | Funziona anche senza penna, con mouse e tastiera | Should |
| NFR-30 | Accessibilità | Contrasto testi e icone conforme a WCAG AA in entrambi i temi | Should |
| NFR-31 | Accessibilità | Tutti i comandi hanno etichetta per gli screen reader e sono raggiungibili da tastiera | Should |
| NFR-32 | Accessibilità | Rispetto dell'impostazione di sistema che riduce animazioni e trasparenze | Must |
| NFR-40 | Privacy | Nessuna connessione di rete; nessun dato inviato all'esterno | Must |
| NFR-50 | Manutenibilità | Codice, commenti e nomi in inglese; test automatici per modello dati, undo/redo e salvataggio/riapertura | Must |

## 9. Architettura e modello dati

L'app segue il pattern MVVM su quattro livelli: ogni livello parla solo con quello sotto, così la UI resta indipendente dalle librerie PDF e la logica è testabile senza interfaccia.

&#91;embedded content: architettura · 4 livelli, 12 componenti\]

Le frecce indicano le dipendenze: la View osserva i ViewModel, che usano i servizi Core, che a loro volta usano l'infrastruttura. Nessun componente della View importa PDFium.

### Modello dati

| Entità | Campi principali | Note |
| --- | --- | --- |
| `Document` | `FilePath`, `Pages`, `Bookmarks`, `IsDirty` | Radice della sessione |
| `Page` | `Index`, `WidthPt`, `HeightPt`, `Strokes` | Dimensioni in punti PDF (1/72 di pollice) |
| `InkStroke` | `Id` (GUID), `Tool` (Pen/Highlighter), `Color`, `BaseWidth`, `Points` | Appartiene a una sola pagina |
| `InkPoint` | `X`, `Y`, `Pressure` (0–1), `TiltX`, `TiltY`, `TipAngle`, `Timestamp` | Coordinate nello spazio della pagina, non dello schermo |
| `Bookmark` | `Id`, `Title`, `PageIndex`, `Order`, `Children` | Mappato sull'outline del PDF |
| `IUndoableCommand` | `Do()`, `Undo()`, `Description` | Una classe per ogni tipo di operazione |

### Regole di architettura

- Le coordinate dei tratti sono sempre salvate in punti PDF della pagina; la conversione verso lo schermo dipende solo da zoom e scroll. Così le annotazioni restano ancorate a qualunque zoom (FR-26).
- Ogni modifica al modello passa da un `IUndoableCommand` eseguito dallo `UndoManager`; nessun componente modifica il modello direttamente.
- Rendering PDF e salvataggio girano su thread in background; il thread della UI gestisce solo input penna e disegno.
- L'input penna usa la pipeline di Windows Ink per avere pressione completa, rifiuto del palmo e rilevamento del dorso.
- I dati privati dell'app nel PDF hanno un numero di versione dello schema, per poter evolvere il formato senza rompere i file già salvati.

## 10. Fasi di sviluppo, accettazione e fuori ambito

Lo sviluppo procede in cinque fasi; ognuna termina con una build installabile da provare sulla Surface prima di passare alla successiva.

1. **Prototipo inchiostro.** Una pagina PDF renderizzata, penna con pressione, gomma col dorso, undo/redo in memoria, zoom minimo (parte di FR-06) per verificare FR-26. Serve a validare la sensazione di scrittura.
2. **Salvataggio e riapertura.** Salvataggio nel PDF con le due forme (visibile e modificabile), riapertura, diario delle modifiche non salvate e recupero dopo crash; i tratti di tutte le pagine restano quando si cambia pagina.
3. **Documento completo**, in tre blocchi da provare uno alla volta: **3a** documento a più pagine (scorrimento continuo con rendering virtualizzato, zoom, vai a pagina, torna indietro, ripresa dall'ultima posizione, barra di scorrimento con anteprima); **3b** aiuti per ritrovare le cose, in due passi (3b-1: riconoscimento dei segni dalla linea laterale, tacche sulla barra, salto tra segni, elenco dei segni; 3b-2: pannello di miniature con i propri tratti, ricerca di testo); i segnalibri e la scrittura dell'indice del PDF sono rinviati; **3c** evidenziatori (tondo e a scalpello, salvati trasparenti nel PDF).
4. **Interfaccia finale.** Toolbar flottante, pannelli a scomparsa, Mica/Acrylic, animazioni, temi, schermata iniziale, impostazioni.
5. **Rifinitura.** Selezione con lazo, prestazioni su PDF grandi, accessibilità, pacchetto MSIX.

### Criteri di accettazione della v1

- [ ] Tutti i requisiti *Must* sono soddisfatti
- [ ] Un PDF annotato nell'app si apre in Acrobat Reader, Edge e Chrome mostrando le annotazioni identiche, spessore variabile compreso
- [ ] Lo stesso PDF, riaperto nell'app, permette di cancellare e modificare i tratti salvati
- [ ] Il testo originale del PDF annotato resta selezionabile e ricercabile in altri lettori
- [ ] Scrivendo velocemente per 60 s su una pagina non si notano scatti né ritardi del tratto
- [ ] La gomma col dorso funziona con qualunque strumento attivo e ripristina lo strumento precedente
- [ ] Chiudendo forzatamente l'app durante la scrittura, al riavvio si ritrovano tutti i tratti e le cancellazioni completati, nella stessa posizione di lettura
- [ ] Un PDF di 300 pagine scorre fluido da inizio a fine senza schermate bianche
- [ ] Undo e redo ripercorrono correttamente una sequenza mista di 50 operazioni

### Fuori ambito per la v1

Inserimento di caselle di testo digitato, forme geometriche, immagini e note adesive; riconoscimento della scrittura a mano; sincronizzazione cloud e collaborazione; modifica del contenuto originale del PDF (testo, pagine, riordino); compilazione e firma di moduli; versioni per altre piattaforme.

### Domande aperte

- [ ] Nome dell'app e icona
- [ ] Distribuzione: solo uso personale, Microsoft Store o download diretto?
- [ ] Serve la ricerca di testo (FR-13) già nella v1?
- [ ] Lingue dell'interfaccia oltre all'italiano
- [ ] La Slim Pen 2 trasmette la rotazione attorno al proprio asse? Test: in Photoshop, pennello con Shape Dynamics → Angle Jitter → Control: Rotation, penna tenuta perpendicolare e ruotata solo tra le dita. Se il segno ruota, FR-48 passa da Could a Should e la nota sul limite hardware va rimossa
