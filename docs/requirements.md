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
| Pannelli flottanti | Tinta traslucida del tema al 65% (due livelli sovrapposti) con l'ombra a due livelli; WPF non sa sfocare ciò che sta dietro un elemento della finestra, quindi niente sfocatura dello sfondo (blocco 4a; l'effetto Acrylic con sfocatura 30 px resta l'obiettivo se la piattaforma lo permetterà). Con "Effetti di trasparenza" disattivati in Windows i pannelli diventano opachi (NFR-32) |
| Tema | Chiaro e scuro, segue il tema di sistema, commutabile a mano |
| Colore accento | Colore accento di sistema, con override nelle impostazioni |
| Sfondo attorno alle pagine | Grigio neutro scuro in tema scuro, chiaro in tema chiaro |
| Pagine | Angoli dritti, senza ombra: in WPF un effetto ombra su ogni pagina costa latenza alla penna (NFR-04), quindi la pagina si stacca solo grazie al colore dello sfondo attorno, più scuro in tema chiaro e più chiaro del nero in tema scuro (blocco 4a; l'ombra morbida da 24 px resta un'idea solo se non pesa sul tratto) |

### Forme e tipografia

- Angoli arrotondati: 12 px per pannelli, pillola (fascia con i lati lunghi dritti e le estremità semicircolari: raggio pari a metà della dimensione minore, mai un'ellisse) per toolbar e pulsanti singoli, 8 px per elementi interni.
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
| Anteprima della barra di scorrimento (UI-07) | Dissolvenza in entrata e in uscita; ogni nuova pagina sfuma sopra la precedente | 150 ms in entrata, 120 ms in uscita, 140 ms di dissolvenza incrociata, tutte ease-out |

Tutte le animazioni si disattivano se l'utente ha attivato "Effetti di animazione: off" in Windows.

## 4. Struttura dell'interfaccia

L'interfaccia ha un solo livello di base, il canvas del documento, e quattro elementi flottanti sovrapposti. Nessuno di essi è visibile in permanenza tranne, opzionalmente, la toolbar.

| ID | Elemento | Posizione predefinita | Comportamento |
| --- | --- | --- | --- |
| UI-01 | Canvas documento | Tutta la finestra | Pagine in scorrimento verticale continuo, centrate |
| UI-02 | Toolbar strumenti | Bordo inferiore, centrata | Pillola flottante (materiale dei pannelli, sezione 3); si ritira durante la scrittura. Il trascinamento su un altro bordo arriva col blocco 4b (l'impostazione esiste già nel file delle impostazioni, FR-110) |
| UI-03 | Menu principale | Pulsante tondo in alto a sinistra, sotto la barra del titolo | Si espande in un pannello con Apri, Salva, Salva con nome, File recenti, Esporta, Segni (Ctrl+M), Pagine (Ctrl+Maiusc+P), Cerca (Ctrl+F), Impostazioni, con accanto le scorciatoie; nel blocco 4a File recenti, Esporta e Impostazioni sono visibili ma disattivati (arrivano col blocco 4b). Si chiude scegliendo una voce, con Esc o con un tocco fuori; compare come i pannelli opzioni (istantaneo se le animazioni di Windows sono disattivate). La penna preme il pulsante e le voci come un dito; fuori dal pannello aperto lo chiude e scrive sulla pagina |
| UI-04 | Pannello segnalibri e miniature | Bordo sinistro, a scomparsa | Si apre con swipe dal bordo (sull'ultima scheda mostrata) o dal menu; schede in alto al posto del titolo (nel blocco 3b: "Segni", l'elenco dei segni (FR-103), con Ctrl+M; "Pagine", con Ctrl+Maiusc+P (Ctrl+P resta libero per la stampa): miniature verticali di circa 140 px con il numero sotto, che includono i propri tratti disegnati sopra l'immagine della pagina, la pagina corrente cerchiata, una tacca dei colori della barra di scorrimento per le pagine con segni o note; nella scheda "Segni" ogni riga mostra la fascia di pagina intorno al segno (l'immagine della pagina a bassa risoluzione, caricata dopo, dalla stessa coda delle miniature) con sopra i tratti; un tocco porta alla pagina e il pannello resta aperto, Indietro torna dove si era prima di aprirlo; le miniature si caricano solo quando la scheda è visibile e mai prima delle pagine che si stanno leggendo; la scheda Segnalibri arriva con i segnalibri. La penna non usa mai il pannello: sul bordo scrive, sul pannello aperto lo chiude senza scrivere) |
| UI-05 | Indicatore pagina e zoom | In basso a destra, a sinistra della barra di scorrimento | Pillola fluttuante che mostra "12 / 48 · 125%"; compare e sparisce insieme alla barra di scorrimento (durante scroll e zoom, sparisce dopo 1,5 s; dissolvenza di 150 ms, istantanea se le animazioni di Windows sono disattivate) e resta visibile mentre la si usa (mouse sopra, campo aperto, tastiera). Un tocco apre il campo "vai a pagina" (FR-09); una pressione lunga, il passaggio del mouse (o della penna sospesa) o la tastiera mostrano i pulsanti zoom − e + e "Adatta larghezza" (FR-07). Sopra l'indicatore, piccoli pulsanti tondi da 40 px: Indietro (FR-09), segno precedente e successivo (FR-102), con accanto l'avviso "Nessun altro segno"; compaiono e spariscono con l'indicatore. La penna preme i pulsanti e scrive nel campo come un dito; intorno scrive sulla pagina; quando sono nascosti scrive ovunque (fase 4a) |
| UI-06 | Barra del titolo | In alto, trasparente | Due pillole tinte come le altre superfici fluttuanti (seguono il tema chiaro/scuro, opache con gli effetti di trasparenza disattivati): a sinistra il nome file in piccolo, con un asterisco (*) finché ci sono modifiche non salvate; a destra i pulsanti finestra (Riduci a icona, Ingrandisci, Chiudi, alti 40 px, con nome per l'accessibilità); subito sotto, mentre un salvataggio è in corso, una linea sottile di avanzamento nel colore di accento (FR-89; ferma e piena se le animazioni di Windows sono disattivate; la penna scrive sotto di essa); si nasconde a schermo intero |
| UI-07 | Barra di scorrimento con anteprima | Bordo destro, sottile, a scomparsa | Compare mentre si scorre e mostra la posizione; toccandola o trascinandola compare l'anteprima grande della pagina corrispondente, con sopra l'inchiostro del lettore (tratti e segni); l'anteprima compare e scompare con una breve dissolvenza morbida e ogni nuova pagina sfuma sopra la precedente, senza lampi bianchi (istantaneo se le animazioni di Windows sono disattivate); svanisce da sola dopo circa 1,5 s con dissolvenza morbida (istantanea se le animazioni di Windows sono disattivate). Mentre la barra è visibile, il cursore (il rettangolo della pagina corrente) si afferra e si trascina col dito, col mouse e anche con la penna; col dito e col mouse si afferra anche toccando un po' accanto (zona invisibile di almeno 56 × 56 DIP). La penna afferra solo il cursore: altrove sulla barra (tacche) scrive, e a barra nascosta scrive ovunque; l'indicatore pagina (UI-05) è una pillola a parte, a sinistra della barra. Sulla barra compaiono piccole tacche per i segni e, in rosso, per le note scritte a mano (FR-101), e per i risultati della ricerca (FR-105), con colori diversi (blocco 3b) |

### Toolbar strumenti (UI-02)

La toolbar contiene, da sinistra a destra: Penna, Evidenziatore, Gomma, Selezione (lazo), separatore, Undo, Redo, separatore, Segnalibro sulla pagina corrente. Selezione (lazo) e Segnalibro sono visibili ma disattivati finché non arrivano le loro fasi (lazo nella fase 5, segnalibri da decidere).

- Un tocco su uno strumento lo attiva. Un secondo tocco sullo strumento già attivo apre il suo **pannello opzioni** flottante sopra la toolbar.
- Il pannello opzioni della penna mostra: 8 colori preimpostati, selettore colore libero, la tavolozza personale, 3 spessori preimpostati, slider spessore, anteprima del tratto con pressione, curva di pressione (FR-21) e levigatura (FR-23); curva e levigatura passeranno anche nella finestra impostazioni (blocco 4b).
- Il pannello dell'evidenziatore mostra: 6 colori (giallo, verde, azzurro, rosa, arancio, viola), la stessa tavolozza personale, punta tonda o a scalpello, angolo dello scalpello (0–180°), modalità "linea dritta". Gli spessori sono fissi (FR-46). Nel blocco 3c lo strumento e le sue opzioni stanno nel pannello degli strumenti; la toolbar finale arriva con la fase 4.
- Il pannello della gomma mostra: cancella tratto intero / cancella parte del tratto, dimensione, "Cancella tutto nella pagina" (chiede conferma e si annulla con Undo). La modalità scelta qui vale anche per il retro della penna: una sola modalità per la gomma (FR-31).
- Ogni strumento ricorda colore e spessore scelti tra una sessione e l'altra.
- **Tavolozza personale (blocco 4a):** fino a 12 colori scelti dall'utente, condivisi da penna ed evidenziatore. Il "+" apre il selettore colore libero e il colore scelto entra nella tavolozza (se è piena esce il più vecchio; un colore già presente non si ripete); una pressione lunga (o Canc) su un colore personale lo toglie. Per l'evidenziatore un colore personale è solo una tinta: l'opacità resta 0,35 (FR-41).
- Il selettore colore libero (blocco 4a) è un piccolo selettore costruito nell'app: quadrato saturazione/luminosità, tonalità e codice esadecimale. Quello di WPF UI non è utilizzabile (non è pubblico nella versione 4.3).
- La penna usa i controlli della toolbar e dei pannelli opzioni come il dito e il mouse (anche il retro della penna, che sul pannello preme il controllo); una pressione della penna fuori dal pannello aperto lo chiude e scrive sulla pagina. È una differenza voluta rispetto al pannello laterale (UI-04), dove la penna sul pannello lo chiude senza scrivere. Sotto la toolbar e i pannelli l'inchiostro della pagina non parte mai, nemmeno per un istante. In una finestra bassa il pannello opzioni scorre e non finisce mai sotto la barra del titolo.

### Visibilità automatica

| Situazione | Toolbar | Altri pannelli |
| --- | --- | --- |
| Penna appoggiata e in scrittura | Si dissolve dopo 300 ms | Si chiudono |
| Penna sollevata da 600 ms | Riappare | Restano chiusi |
| Tocco con un dito su area vuota | Mostra / nasconde | Si chiudono |
| Modalità "Toolbar fissa" attiva | Sempre visibile | Invariato |

"In scrittura" vale per il tratto o la cancellazione in corso sulla pagina: conta anche il disegno col mouse (NFR-23) e la cancellazione col retro della penna.

"Tocco con un dito su area vuota" (blocco 4a): un tocco singolo di un dito che non si muove, sulla pagina o sullo sfondo attorno, fuori da ogni elemento flottante. Diventa un tocco singolo 300 ms dopo il sollevamento, se non arriva un secondo tocco: il doppio tocco resta lo zoom (FR-07) e non mostra né nasconde la toolbar. Non conta il tocco che chiude un pannello aperto toccando fuori (chiude soltanto), quello che ferma lo scorrimento per inerzia, quello sulla barra di scorrimento. La penna non mostra né nasconde mai la toolbar con un tocco: scrive.

### Avvisi

Gli avvisi del documento (modifiche ripristinate dal diario, PDF che non si può salvare, annotazioni di altri programmi non modificabili, diario disattivato: FR-84, FR-86) , l'avviso "le modifiche non salvate non vengono registrate" e l'avviso "Le impostazioni non sono state salvate" (finché il file delle impostazioni non si può scrivere, FR-110; sparisce da solo quando un salvataggio riesce) compaiono in alto al centro, sotto la barra di ricerca, in un piccolo pannello flottante con il pulsante Chiudi; chiuso, resta chiuso finché l'avviso non cambia. L'Assistente vocale li legge quando compaiono. La penna preme Chiudi e intorno scrive sulla pagina (blocco 4a).

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
| FR-25 | 8 colori preimpostati + selettore colore libero + tavolozza personale di fino a 12 colori, condivisa con l'evidenziatore (UI-02, blocco 4a) | Must |
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
| FR-40 | Tratto largo, semitrasparente, che non copre il testo sottostante. Sullo schermo l'evidenziatore sta sempre sotto l'inchiostro della penna. **Approssimazione (3c):** la trasparenza è l'opacità dell'annotazione (0,35, `/CA`), salvata così che anche gli altri lettori la mostrino; la fusione "moltiplica" non si può scrivere con l'API pubblica di PDFium, quindi il testo sotto un'evidenziazione resta leggibile ma appare leggermente tinto, non moltiplicato | Must |
| FR-41 | 6 colori preimpostati: giallo, verde, azzurro, rosa, arancio, viola, tutti con la stessa opacità (0,35) | Must |
| FR-42 | Spessore costante, non dipendente dalla pressione, sia mentre si traccia sia nel tratto salvato | Must |
| FR-43 | Sovrapporre due passate dello stesso colore non scurisce il risultato. **Rinviato (3c):** senza "moltiplica" (FR-40) le passate sovrapposte si scuriscono | Should, rinviato |
| FR-44 | Modalità "linea dritta": al sollevamento della penna, se la corda dal primo all'ultimo punto è entro 3° dall'orizzontale e ogni punto resta entro il 2% della lunghezza della corda da essa, il tratto diventa il segmento orizzontale tra i due estremi (si tengono le loro x e i loro tempi, all'altezza media dei due); altrimenti resta com'è. Il raddrizzamento si annulla come ogni altra modifica (un primo Annulla toglie il raddrizzamento, un secondo il tratto) | Should |
| FR-45 | Evidenziazione del testo selezionato (aggancio alle parole del PDF). **Rinviato (3c):** richiede un'interfaccia di selezione del testo | Could, rinviato |

#### Punta a scalpello

Oltre alla punta tonda, l'evidenziatore offre una punta a scalpello (rettangolare sottile), il cui segno cambia larghezza in base all'orientamento della punta rispetto alla direzione del tratto.

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-46 | Punta a scalpello con angolo fisso per tutto il tratto, regolabile nel pannello opzioni (0–180°, predefinito 45°). Punta lunga L = 22 pt, con proporzione spessore/lunghezza r = 0,25 (fissa nel blocco 3c: le proporzioni regolabili sono rinviate); la punta tonda è larga 16 pt. Per un tratto di direzione φ il segno è largo L·(r + (1 − r)·\|sin(θ − φ)\|), con θ l'angolo della punta: sottile (r·L) se il tratto è parallelo alla punta, pieno (L) se è perpendicolare. A 45° una passata orizzontale è alta circa 17,2 pt, quindi basta a coprire una riga di testo e a diventare un segno (FR-100) | Should |
| FR-47 | Modalità "segui l'impugnatura": l'orientamento della punta segue la direzione verso cui pende la penna (azimut dell'inclinazione), aggiornato punto per punto. **Rinviato (3c)** | Could, rinviato |
| FR-48 | Se la penna trasmette la rotazione attorno al proprio asse (barrel rotation), l'orientamento della punta segue la rotazione reale; altrimenti si applica FR-47 o FR-46. **Rinviato (3c):** la Slim Pen 2 non trasmette la rotazione (limite hardware, sotto) | Could, rinviato |

**Limite hardware.** Le specifiche della Surface Slim Pen 2 citano pressione e inclinazione, ma non la rotazione attorno al proprio asse: salvo verifica contraria (vedi Domande aperte), con questa penna FR-48 non è attivabile e ruotare la penna tra le dita non cambia il segno. App come Photoshop e Illustrator gestiscono la rotazione solo con penne che la trasmettono (es. Wacom Art Pen). FR-48 va comunque predisposto leggendo il campo di rotazione dell'input Windows quando presente, per penne future.

**Impatto tecnico.** L'`InkCanvas` di WPF supporta una punta rettangolare con orientamento fisso per tutto il tratto, sufficiente per FR-46. FR-47 e FR-48 richiedono un rendering personalizzato del tratto, con orientamento variabile punto per punto; il dato va salvato per ogni `InkPoint` (campo `TipAngle`). Il salvataggio come contorno vettoriale (sezione 7) copre già qualsiasi forma della punta.

**Limiti noti (blocco 3c, 02/11/2026).**

- Le passate sovrapposte si scuriscono, perché non c'è la fusione "moltiplica": FR-43 è rinviato.
- Il testo sotto un'evidenziazione è leggermente tinto, non moltiplicato (FR-40 approssimato).
- La rotazione della penna attorno al proprio asse non è usata (FR-48 rinviato, limite hardware); nemmeno l'inclinazione (FR-47 rinviato): la punta a scalpello segue solo l'angolo fisso di FR-46.
- L'evidenziazione agganciata alle parole del testo (FR-45) è rinviata.
- Ordine delle annotazioni negli altri lettori: il salvataggio non riscrive mai le annotazioni già presenti (NFR-10), quindi un'evidenziazione aggiunta dopo che un tratto a penna è già stato salvato viene scritta dopo quel tratto e, in un altro lettore, può passargli sopra (l'inchiostro resta visibile, leggermente tinto). Nello stesso salvataggio le nuove evidenziazioni sono scritte prima dei nuovi tratti a penna. Nell'app l'evidenziatore sta sempre sotto la penna.
- Mentre si traccia un'evidenziazione, il tratto vivo sta sopra l'inchiostro della penna; al sollevamento della penna passa sotto.
- Un lettore che applicasse l'opacità due volte (annotazione e aspetto) mostrerebbe un'evidenziazione più chiara (circa 0,12 invece di 0,35), con il testo comunque leggibile: da verificare aprendo il file salvato in Edge o Acrobat.

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
| FR-100 | Una linea laterale tracciata a penna (quasi verticale, anche leggermente inclinata, curva, ripassata più volte o tracciata in più tratti (un tratto corto e quasi verticale lungo la linea fa parte del segno e non diventa una nota), lunga almeno 2 righe di testo, nel margine esterno della pagina; tracciata a mano libera: il tremolio della penna non conta. Finché non si legge il testo della pagina (3b-2) una riga vale 15 pt, quindi almeno 30 pt, circa 1 cm) è riconosciuta come segno; anche un passaggio evidenziato è un segno (3c): un gruppo di tratti di evidenziatore che si toccano o si sovrappongono in verticale diventa un segno quando è alto almeno una riga (15 pt) e largo almeno 60 pt (circa tre parole brevi), soglie incluse; se il suo centro orizzontale cade nel 36% centrale della pagina il segno è "al centro" (così lo chiama l'elenco dei segni; ha la sua tacca sulla barra come gli altri e non ha nota a margine), altrimenti a sinistra o a destra secondo il centro; un gruppo più piccolo resta una nota (FR-101). Con gli spessori predefiniti (FR-46) una sola passata orizzontale su una riga basta, con entrambe le punte. L'evidenziatore non conta mai come linea a margine né come nota a margine. I segni sono ricavati dai tratti a ogni apertura e modifica: non si scrive nulla di nuovo nel PDF | Must |
| FR-101 | I segni compaiono come tacche sulla barra di scorrimento (UI-07); in un altro colore (rosso) compaiono anche le note scritte a mano: ogni gruppo di tratti che non sono linee laterali (scritte, disegni, sottolineature, note nel margine), dove i tratti distano meno di 4 righe in verticale. Punti e tocchi isolati non contano. Per ora elenco dei segni e Alt+Su/Giù restano solo per i segni | Must |
| FR-102 | Salto al segno precedente/successivo (pulsanti tondi sopra l'indicatore pagina, UI-05; scorciatoie Alt+Su e Alt+Giù), che registra la posizione nello storico del tasto Indietro (FR-09); agli estremi non salta e avvisa "Nessun altro segno" | Must |
| FR-103 | Elenco dei segni nel pannello laterale sinistro (UI-04; si apre con swipe dal bordo, dal menu o con Ctrl+M): miniatura del passaggio con le note scritte accanto, numero di pagina; un tocco porta al passaggio | Should |
| FR-104 | Più linee sullo stesso passaggio contano come un solo segno; un solo tipo di segno, senza livelli né colori | Must |
| FR-105 | I risultati della ricerca (FR-13) compaiono come tacche sulla barra e sono evidenziati sulla pagina senza coprire i tratti (il risultato corrente in un colore più forte); avanti/indietro tra i risultati con i pulsanti accanto al campo di ricerca, con Invio e Maiusc+Invio nel campo e con F3 e Maiusc+F3 ovunque; dopo l'ultimo riparte dal primo avvisando "Ripartito dall'inizio" (e "Ripartito dalla fine" all'indietro); il primo salto registra la posizione nello storico del tasto Indietro (FR-09). La ricerca è una pillola fluttuante in alto al centro, sotto la barra del titolo (campo, conteggio, precedente, successivo, chiudi; sotto, gli avvisi): Ctrl+F la apre e porta al campo, Esc (o il pulsante chiudi) annulla la ricerca e la chiude; la penna scrive nel campo e preme i pulsanti, intorno scrive sulla pagina (fase 4a). Se il PDF non ha testo lo dice una volta ("Nessun testo ricercabile in questo PDF"). Avanti/indietro premuti mentre la ricerca è ancora in corso non vanno persi: il salto avviene da solo appena il risultato è trovato. Se alcune pagine non si sono potute leggere, accanto al conteggio compare una nota ("N pagine non lette") | Should |

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

Gli evidenziatori seguono la stessa strategia: l'aspetto è il contorno della punta (tonda o a scalpello) riempito con il colore dell'evidenziatore, e l'annotazione ha opacità 0,35 (`/CA`), applicata dall'aspetto attraverso lo stato grafico che PDFium aggiunge. La modalità di fusione "Multiply" non è disponibile con l'API pubblica di PDFium (FR-40, approssimato). I dati privati ricordano che il tratto è un evidenziatore, con colore, opacità e punta, così che l'app lo riapra come tale.

### Impostazioni

| ID | Requisito | Priorità |
| --- | --- | --- |
| FR-110 | Le impostazioni dell'app sono in un file con numero di versione (`%LOCALAPPDATA%\DreamBooks\settings.json`, `"v":1`), scritto in modo atomico (file temporaneo e sostituzione) mezzo secondo dopo l'ultima modifica e all'uscita. Contiene: tema (sistema, chiaro, scuro; predefinito sistema), colore di accento personalizzato (predefinito nessuno), modalità della gomma (intero tratto o parte del tratto, predefinito intero tratto; vale per lo strumento gomma e per il retro della penna), curva di pressione (predefinita "decisa"), levigatura (predefinita "forte"), "Toolbar fissa" (predefinito no), bordo della toolbar (predefinito in basso), colore e spessore ricordati di penna, evidenziatore (colore, punta, angolo, linea dritta) e gomma (dimensione), tavolozza personale (fino a 12 colori). Non contiene mai contenuti né percorsi dei documenti. File mancante, illeggibile o di versione sconosciuta: valori predefiniti; un valore non valido torna al suo predefinito da solo; un errore di scrittura non blocca l'app e non è mai un crash. Nel blocco 4a i valori si cambiano dalla toolbar e dai suoi pannelli; la finestra delle impostazioni arriva col blocco 4b | Must |

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
| NFR-32 | Accessibilità | Rispetto dell'impostazione di sistema che riduce animazioni e trasparenze. Eccezione voluta: le evidenziazioni della ricerca (FR-105) restano semitrasparenti anche con gli effetti di trasparenza disattivati, perché un colore pieno coprirebbe il testo evidenziato (sono un colore di evidenziazione, non un effetto di trasparenza); lo stesso vale per le evidenziazioni dell'evidenziatore (FR-40) | Must |
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
4. **Interfaccia finale.** Toolbar flottante, pannelli a scomparsa, Mica/Acrylic, animazioni, temi, schermata iniziale, impostazioni. In due blocchi: **4a** finestra (Mica, barra del titolo trasparente), temi chiaro e scuro, file delle impostazioni (FR-110), toolbar flottante con i pannelli opzioni e la tavolozza personale, indicatore pagina, pulsanti di navigazione, pillola di ricerca, menu principale; il pannello di debug sparisce (**in prova** sulla Surface, non ancora approvato); **4b** schermata iniziale, file recenti, finestra delle impostazioni, trascinamento della toolbar sugli altri bordi.
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
