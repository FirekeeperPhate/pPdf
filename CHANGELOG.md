# Changelog

## Unreleased

- pPdf remembers where each document was left: the exact point in the page (not just the page), the zoom and the rotation, for the last 200 documents. The position is also written a few seconds after you stop scrolling, so even a crash does not lose it. Positions saved by older versions (page only) are still used.

## 0.1.4

- The pointer now switches by itself: over text it is the I-beam and dragging selects; over empty page or the grey background it is an open hand and dragging scrolls the page (a plain click there still clears the selection). The Hand tool remains for dragging even over text.

## 0.1.3

- The scroll bar now spans the whole document in the single-page and two-page views too (it only covered the current page). The wheel, the arrows and PgUp / PgDn stay inside a page and then turn it; dragging the scroll bar settles on one page.
- One pPdf process for everything: starting pPdf again (for example a double-click on another PDF) hands the file to the running one, which opens it in a new window. Three documents now take about 215 MB in total instead of about 450 MB. Ctrl+N or More > New window opens an empty window.
- Lower memory: the page cache is sized on your screen (about three screens of bitmaps, between 64 and 192 MB) instead of a fixed 384 MB and is shared by all windows; after 20 seconds minimized the bitmaps and caches are released and the working set drops to a few MB.
- Very big PDFs (over 64 MB) are read from disk when needed instead of being loaded into memory; while one is open it cannot be overwritten.
- Less rendering work: only a third of a screen is prefetched above and below, a blank page first shows a quick half-size draft, thumbnails are only rendered while the panel is open.
- Runtime tuning: no background GC thread, no dynamic PGO.

## 0.1.2

- Fixed the drop-down menus (page layout, theme, More): their items were drawn as empty squares because they inherited the icon font of the toolbar button. Tooltips now have an explicit text font too.

## 0.1.1

- Automatic updates: pPdf checks GitHub once a day for a newer release (More menu: Check for updates / automatic check on or off), downloads the installer of the same edition, verifies size and SHA-256, installs it and reopens the same document. Version 0.1.0 cannot do this: install 0.1.1 by hand once.
- Text being typed in an annotation is never lost when saving, printing, closing or opening another file.
- Images that are not PNG or JPEG (GIF, TIFF, BMP, WebP...) are converted once when added, so saving a copy always works.
- A font that cannot be embedded falls back to Arial instead of making the save fail.
- Protected PDFs that do not allow changes: a clear message instead of a technical error.
- The page you were reading is restored when opening from the command line or the recent list.
- Left / Right arrows turn the page in the one-page and two-page views.
- The keyboard goes back to the document after clicking a thumbnail; the find bar re-runs when another document is opened; the annotation bar closes when the text tool is cancelled.
- Lower memory peak when opening big files.

## 0.1.0

First release: continuous / single / two-page views, zoom and fit modes, rotation, thumbnails and outline, links, text selection and copy, search, text and image annotations saved into a copy of the PDF, printing, light and dark theme, installers (Full and Light).
