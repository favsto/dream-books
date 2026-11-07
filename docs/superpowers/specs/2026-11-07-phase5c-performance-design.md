# Phase 5c: performance on large PDFs (NFR-01, NFR-02, NFR-03, NFR-06)

The requirement of the block was "measure the 300-page reference file, then fix what the numbers show". The numbers
below show nothing that needs fixing on the dev Surface, so the block adds a way to measure the two things this machine
cannot measure for itself (frame rate while scrolling, cold start), and a budget test for the cost of a lot of ink.

## What was measured (dev Surface, 8 GB RAM; headless, so no screen refresh)

| Requirement | Measure | Result |
|---|---|---|
| NFR-02 first page within 1 s (300 pages) | 300 generated pages: open + layout, first bitmap | 155 ms, 156 ms |
| NFR-02, harder | real e-book, 1,076 pages | open 962 ms, first bitmap 1,028 ms |
| NFR-05 bounded retention | scrolling the e-book end to end (2,134 steps) | at most 6 page surfaces, bitmap cache at most 247 MB in 18 bitmaps |
| NFR-06 under 600 MB | e-book scrolled end to end | retained memory +304 MB at the peak, private bytes +380 MB |
| NFR-06 with 2,000 strokes | `LargeDocumentTests`: 2,000 strokes (80 points) added to the e-book and saved | the save grew the process by 61 MB; reading every page's strokes back 0.9 s |
| Cost of saving (FR-89) | same test | first save with 2,000 strokes 19 s; saving one more stroke afterwards 6.4 s; file 6.5 MB → 31.6 MB (about 12.6 KB per stroke) |

## Decisions

- **No change to the saver.** A save is a full rewrite followed by two verifications and a page-by-page comparison
  (NFR-10, the riskiest feature of the app), in short slices so that page renders are served meanwhile, and it runs in
  the background with a progress line (FR-89). 6 s on a 1,000-page book is the price of that safety; a 300-page
  document is several times quicker. Making it faster by verifying less would trade away the guarantee against losing
  data. `LargeDocumentTests` now fixes budgets with room (90 s, 40 s, 10 s) so a change that makes a long book several
  times slower is noticed.
- **A device probe for what cannot be measured here.** `FramePerformanceProbe` (App) counts frames from
  `CompositionTarget.Rendering` and writes a short report when the app exits. It is off unless the environment variable
  `DREAMBOOKS_PERF=1` is set. The report holds numbers only: no path, document name or content (NFR-12), and nothing
  goes over the network (NFR-40). WPF draws only when something changes, so a gap longer than 250 ms between two frames
  is idle time, not a slow frame, and is left out (`Core/Rendering/FrameTimeStats`, tested).

## Using the probe on the Surface

In PowerShell, from the folder of the published exe:

```
$env:DREAMBOOKS_PERF = "1"
.\DreamBooks.App.exe
```

Open the 300-page PDF, scroll it from first to last page with a finger swipe, zoom with a pinch, close the app. Read
`%LocalAppData%\DreamBooks\perf.txt`. At 60 fps the median is about 16.7 ms; the numbers that matter for NFR-03 are the
95th percentile and the counts of frames over 25 ms and over 50 ms.
