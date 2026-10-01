# Changelog

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
