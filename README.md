# 3DSideHustle Door Sign Designer

Type a name and a big monogram letter, get a 3D-printable layered door sign:
a large capital (serif) with the full name (connected script) laid across it,
live 3D preview, and binary STL export — assembled, or letter/name as
separate pieces.

Built on the text→STL engine of the companion
[keychain designer](https://github.com/notschizo91/slibitydibity) (same
theme, same geometry core: opentype.js outlines → Clipper 2D booleans →
earcut extrusion → binary STL).

## The assembly trick

The two pieces are designed to be **printed separately and glued**:

- The **letter piece** gets the name's outline *cut all the way through*,
  dilated by the **Fit Gap** slider (default 0.2 mm) — a true
  outline-to-outline clearance on every side.
- The **name piece** is the plain script name.
- Both print flat on the plate. Slide the name into the letter's cutout and
  glue it — the gap makes it an easy slip fit; ~0.15–0.3 mm suits most
  printers. Make the name a little thicker than the letter and it stands
  proud, like the classic Etsy look.

Leave the **letter empty** for a plain script name sign, or the **name
empty** for a monogram-only sign — every combination exports cleanly.

## Features

- **Fonts** — bundled Pacifico (connected script, for the name), Playfair
  Display 700 (high-contrast serif, for the letter) and Baloo 2 (round);
  separate font pickers for name and letter; upload your own
  `.ttf`/`.otf`/`.woff` fonts.
- **Name layout** — uses the font's own advance widths + kerning so cursive
  joins stay connected, then unions overlapping strokes into one clean
  printable solid. Letter-spacing slider adds/removes advance in mm.
- **Placement** — offset the name across the letter with X/Y sliders, or
  drag it in the 3D preview (the sliders follow).
- **Fit** — Fit Gap (cutout clearance) and hole-fill threshold (letter
  counters below the area are filled in).
- **Sizes & heights** — independent letter/name sizes (mm) and thicknesses;
  both pieces rest on z=0.
- **Save Folder** — pick a folder once (Chrome/Edge) and exports are written
  straight into it; other browsers fall back to normal downloads.
- **Export** — one assembled STL, or `{name}-letter.stl` + `{name}-name.stl`
  for printing the pieces in different colors. Binary STL, watertight,
  outward-facing normals, Z-up, resting on z=0.

## Run it

Prerequisites: Node 18+, .NET SDK 8.

```bash
npm install
dotnet tool restore   # installs the Fable compiler (pinned in .config/dotnet-tools.json)
npm run dev           # Fable watch + Vite dev server on http://localhost:5173
npm run build         # production build into dist/ (static, host anywhere)
npm run test:e2e      # headless end-to-end test with STL validation
```

The e2e test builds the default Emma/E sign, exports assembled + separate
STLs and validates the binaries: byte layout, z-ranges, watertightness,
outward orientation, volume bookkeeping — plus the door-sign specifics: the
cutout removes letter material, the fit-gap slider widens it, the offset
slider moves the name, and the letter-less / name-less modes export single
clean pieces.

## How it works

- **Text → outlines**: `opentype.js` renders the whole string with kerning
  (`src/text-helpers.js`); bezier commands are adaptively flattened and
  classified into shapes-with-holes in F# (`src/TextShapes.fs`).
- **Assembly cutout**: the placed name's outer rings are unioned and offset
  outward by the fit gap with round joins via Clipper
  (`src/clipper-helpers.js`, `src/Clipper.fs`), then subtracted from the
  letter shape — all booleans are 2D, no 3D CSG needed.
- **Extrusion**: the engine's pure `Geometry.extrude` (earcut caps + wall
  quads, outward winding) builds each piece from z=0; the identical
  triangles feed the three.js preview and the STL writer (`src/Stl.fs`).
- Text layout + flattening are cached, so offset drags, gap, and height
  changes only re-run the cheap Clipper/extrude step (debounced ~40 ms).

## Project layout

```
index.html               app shell (topbar, canvas, card sidebar)
styles.css               theme
src/App.fsproj           F# project (compile order matters)
src/Types.fs             domain types            ┐
src/PathParser.fs        curve flattening        │ engine, shared with
src/Rings.fs             hole classification     │ the slibitydibity
src/Geometry.fs          extrusion (caps+walls)  │ keychain designer
src/Stl.fs               binary STL writer       ┘
src/TextShapes.fs        opentype commands -> shapes
src/Clipper.fs           offset/boolean bindings
src/Viewer.fs            viewer bindings
src/text-helpers.js      opentype.js layout glue (metric layout, kerning)
src/clipper-helpers.js   clipper-lib glue
src/fs-helpers.js        File System Access (save folder)
src/viewer.js            three.js scene + name drag
src/Main.fs              state, pipeline, UI wiring
tests/e2e.mjs            headless e2e + STL validation
```
