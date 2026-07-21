// opentype.js glue: font parsing and text layout. Geometry (flattening,
// holes, extrusion) all happens on the F# side — this module only produces
// raw path commands in millimetre coordinates (y-down).
import { parse } from 'opentype.js';
import scriptFontUrl from '@fontsource/pacifico/files/pacifico-latin-400-normal.woff?url';
import serifFontUrl from '@fontsource/playfair-display/files/playfair-display-latin-700-normal.woff?url';
import roundFontUrl from '@fontsource/baloo-2/files/baloo-2-latin-700-normal.woff?url';

async function fetchFont(url) {
  const res = await fetch(url);
  return parse(await res.arrayBuffer());
}

/**
 * The bundled fonts, in dropdown order: a connected script (for the name),
 * a high-contrast serif (for the big monogram letter) and a chunky round
 * one as an alternative. Returns [{ name, font }].
 */
export async function loadBundledFonts() {
  const [script, serif, round] = await Promise.all([
    fetchFont(scriptFontUrl),
    fetchFont(serifFontUrl),
    fetchFont(roundFontUrl),
  ]);
  return [
    { name: 'Pacifico (script, built-in)', font: script },
    { name: 'Playfair Display (serif, built-in)', font: serif },
    { name: 'Baloo 2 (round, built-in)', font: round },
  ];
}

export function parseFontBuffer(buffer) {
  return parse(buffer);
}

export function fontName(font) {
  try {
    return font.names.fullName.en || font.names.fontFamily.en || 'font';
  } catch {
    return 'font';
  }
}

/**
 * Text laid out with the font's own metrics — advance widths and kerning —
 * so connected script fonts keep their letter joins intact (per-glyph
 * optical spacing would tear cursive strokes apart). `letterSpacingMm` is
 * extra advance per glyph in mm; opentype.js takes it as a fraction of the
 * font size, hence the division.
 *
 * Returns one command array PER GLYPH (positioned, mm, y-down, baseline at
 * y=0) rather than one merged outline: hole classification is containment
 * based, so overlapping glyphs must be classified separately — an 'm'
 * starting inside a script E would otherwise be mistaken for a hole of the
 * E and carved away. The glyph shapes are unioned after classification.
 */
export function textCommands(font, text, sizeMm, letterSpacingMm) {
  const paths = font.getPaths(text, 0, 0, sizeMm, {
    kerning: true,
    letterSpacing: sizeMm > 0 ? letterSpacingMm / sizeMm : 0,
  });
  return paths.map((p) => p.commands);
}
