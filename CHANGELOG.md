# Changelog

## Unreleased

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
