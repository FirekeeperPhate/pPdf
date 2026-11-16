# Changelog

## 0.3.3

- **Faster wheel scrolling**: a notch moves 120 px (following the Windows "lines to scroll" setting) instead of 96, up to three times as far when the wheel is spun fast, and the glide settles in about 0.15 s at any refresh rate. The one-page and two-page views move twice as far per notch. Touchpads are unchanged.

## 0.3.2

- **Security (EPUB)**: a book can no longer name a file outside itself. A crafted book could have rewritten the start of one of your files, shown a local text file inside the book, or made Windows connect to a network share (sending your sign-in hash) just by being opened. Books that unpack into far more than they weigh are refused, and the hidden browser cannot even look up a server name.
- **Annotations are safer**: a save that fails (full disk, antivirus) is tried again instead of being forgotten; logging off or shutting Windows down with pPdf open now keeps the last changes; a damaged or locked store is never written over (a damaged one is kept aside); pictures half written by a crash are written again; annotations on pages a file no longer has are kept; opening a document counts as using it, so the housekeeping no longer forgets annotations of documents you open often (and waits three years, not one).
- **A file that changed is noticed**: when the file at that path was replaced (a new download with the same name, a scanner's daily file) or a book was laid out again, pPdf asks before showing the annotations and form entries made on the old one.
- **Save a copy** writes beside the target and then replaces it, so a full disk or a dropped network share never leaves half a file; the window cannot be closed while it writes; saving over another file forgets the annotations kept for that file.
- Copying a long selection (Ctrl+A, Ctrl+C) and highlighting it no longer freeze the window; a page that failed to draw or to read its text is tried again; a search jump no longer lands in a document opened meanwhile; in the two-page views a page beside a shorter one shows up as soon as it is on screen; documents with no pages are refused cleanly; closed documents no longer stay in memory through the page cache.
- Forms: check boxes of one name with states of their own ("Yes" / "No") are separate choices, on screen, printed and in the saved copy; text in a field on a turned page is upright in the saved copy; punctuation such as … • ™ is kept, and text WinAnsi cannot hold (Polish, Greek, Cyrillic) asks other viewers to draw the field themselves instead of showing "?".
- Updates are offered only with a checksum to verify the download; the same user signed in twice (console and remote desktop) gets a pPdf in each session; the installer sees pPdf running in another session; two pPdf processes no longer wipe each other's recent files and positions.

## 0.3.1

- EPUB: a book with one odd file inside (a name Windows refuses, a damaged picture) still opens; a picture listed as a page of its own is shown as one; two windows converting the same book at once no longer collide; a layout that takes more than ten minutes gives up with a message.
- **Opening an EPUB is faster** (about 25 to 40 % on the first open of a book): the browser that lays the pages out starts while the book is being unpacked, and the chapters are prepared on all cores at once. Reopening a book was already instant.
- **The side panel opens on the Outline**: the Outline tab now comes first and is selected. A document without an outline shows its pages instead (without changing your choice); once you pick a tab yourself, pPdf remembers it.

## 0.3.0

- **EPUB books**: pPdf now opens `.epub` files. The book is unpacked and laid out into pages (5.5 x 8.5 in) by the Edge WebView2 engine already present in Windows, with a real outline from its headings and working internal and external links; the result is cached, so reopening is instant. Search, selection, highlights, notes, printing, night mode, position memory and *Save a copy* (as a PDF) all work on books. Scripts and online content in a book never run, DRM-protected books are refused with a clear message, scrambled embedded fonts are restored. "Open with" is registered for `.epub` too (never as the default).

## 0.2.1

- Changing the theme (or the system switching between light and dark) keeps the document where it was instead of jumping back to the first page.

## 0.2.0

- **Annotations are kept**: text boxes, images and markups you add are saved automatically (a moment after each change, and when you close) in `%AppData%\pPdf\annotations` and come back, still editable, the next time you open the same file. Closing no longer asks to save: *Save a copy* is only for writing them into a PDF. The PDF itself is never modified.
- **Highlight, underline and strike-through** of the selected text (toolbar, with a color picker). Click marked text to select the markup and change its kind or color, or delete it. Saved into the copy, printed and exported.
- **Fillable PDF forms** (AcroForm): text fields (single and multi-line), check boxes, radio buttons, combo boxes and lists are shown as controls on the page and can be filled in. The values are kept like annotations, written into the copy you save (with appearances, so any viewer shows them), and printed / exported.
- **Night mode keeps the colors**: it flips the lightness only (white paper becomes soft dark gray), so photos and charts no longer turn into negatives.
- **Back / forward**: after a link, an outline entry, a thumbnail, "go to page" or a search jump you can return with Alt+Left (and forward with Alt+Right, or the mouse back / forward buttons, or the toolbar arrows). Ctrl+G jumps to the page box. F11 shows the document full screen.
- **Document properties**, **Show in folder**, **Copy file path** and **Export this page as an image** (PNG or JPEG, 150 / 300 / 600 dpi, with annotations and form values) in the More menu.
- Smoother wheel scrolling (a short glide).
- The toolbar wraps onto a second row when the window is narrow instead of hiding buttons.
- Shift + wheel scrolls sideways when the page is wider than the window. Arrow keys, Page Up / Down and the wheel over an open list now work inside form fields instead of also moving the page. Saving a copy over the open file reloads it without drawing the annotations twice.
- Very large images added as annotations are shown at a reduced size in memory (the original file is still the one embedded), and files over 4 GB give a clear message instead of an arithmetic error.


## 0.1.5

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
