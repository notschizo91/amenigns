// End-to-end test: serve the built app, design a door sign (big letter +
// script name), export combined + separate STLs, and validate the binaries —
// including the assembly cutout that the name slides into.
//
// Prereqs: `npm run build` (dist/ must exist), a Chromium binary
// (CHROMIUM_PATH env var, default /opt/pw-browsers/chromium).
import http from 'node:http';
import path from 'node:path';
import { createReadStream, existsSync, readFileSync, mkdtempSync } from 'node:fs';
import os from 'node:os';
import { chromium } from 'playwright-core';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
const DIST = path.join(ROOT, 'dist');
const PORT = 4193;

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff': 'font/woff',
};

let failures = 0;
const check = (cond, msg) => {
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`);
  if (!cond) failures++;
};

const server = http.createServer((req, res) => {
  const url = req.url === '/' ? '/index.html' : req.url.split('?')[0];
  const file = path.join(DIST, decodeURIComponent(url));
  if (!file.startsWith(DIST) || !existsSync(file)) {
    res.writeHead(404).end('not found');
    return;
  }
  res.writeHead(200, { 'content-type': MIME[path.extname(file)] ?? 'application/octet-stream' });
  createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(PORT, r));

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium',
});
const context = await browser.newContext({ acceptDownloads: true, viewport: { width: 1400, height: 900 } });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(String(e)));
page.on('console', (m) => {
  if (m.type() === 'error') pageErrors.push(m.text());
});

const tmp = mkdtempSync(path.join(os.tmpdir(), '3dsh-'));

const parseStl = (buf) => {
  const triCount = buf.readUInt32LE(80);
  let minZ = Infinity, maxZ = -Infinity, minX = Infinity, maxX = -Infinity, volume = 0;
  let minY = Infinity, maxY = -Infinity;
  const edges = new Map();
  const key = (x, y, z) => `${x.toFixed(4)},${y.toFixed(4)},${z.toFixed(4)}`;
  for (let t = 0; t < triCount; t++) {
    const o = 84 + 50 * t + 12;
    const v = [];
    for (let k = 0; k < 3; k++) {
      const x = buf.readFloatLE(o + 12 * k);
      const y = buf.readFloatLE(o + 12 * k + 4);
      const z = buf.readFloatLE(o + 12 * k + 8);
      v.push([x, y, z]);
      if (z < minZ) minZ = z;
      if (z > maxZ) maxZ = z;
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    }
    const [a, b, c] = v;
    volume +=
      (a[0] * (b[1] * c[2] - c[1] * b[2]) -
        a[1] * (b[0] * c[2] - c[0] * b[2]) +
        a[2] * (b[0] * c[1] - c[0] * b[1])) / 6;
    for (let k = 0; k < 3; k++) {
      const e = key(...v[k]) + '|' + key(...v[(k + 1) % 3]);
      edges.set(e, (edges.get(e) ?? 0) + 1);
    }
  }
  let badEdges = 0;
  for (const [e, n] of edges) {
    const rev = e.split('|').reverse().join('|');
    if (n !== 1 || (edges.get(rev) ?? 0) !== 1) badEdges++;
  }
  return { triCount, minZ, maxZ, minX, maxX, minY, maxY, volume, badEdges, bytesOk: buf.length === 84 + 50 * triCount };
};

const download = async (buttonSel) => {
  const [dl] = await Promise.all([page.waitForEvent('download'), page.click(buttonSel)]);
  const p = path.join(tmp, dl.suggestedFilename());
  await dl.saveAs(p);
  return { name: dl.suggestedFilename(), buf: readFileSync(p) };
};

// Click a multi-file export button and collect all resulting downloads.
const downloadAll = async (buttonSel, expected) => {
  const queue = [];
  const onDl = (d) => queue.push(d);
  page.on('download', onDl);
  await page.click(buttonSel);
  const deadline = Date.now() + 10000;
  while (queue.length < expected && Date.now() < deadline) await page.waitForTimeout(100);
  page.off('download', onDl);
  const named = {};
  for (const dl of queue) {
    const p = path.join(tmp, dl.suggestedFilename());
    await dl.saveAs(p);
    named[dl.suggestedFilename()] = readFileSync(p);
  }
  return named;
};

const setSlider = async (id, value) => {
  await page.fill(`#${id}`, String(value));
  await page.dispatchEvent(`#${id}`, 'input');
  await page.waitForTimeout(300);
};

try {
  await page.goto(`http://127.0.0.1:${PORT}/`);
  // Bundled fonts load async; the export button enables after the first build.
  await page.waitForSelector('#export-btn:not([disabled])', { timeout: 15000 });
  check(true, 'bundled fonts loaded and initial Emma/E model built');

  // --- Default state: name "Emma" over letter "E", gap 0.2mm ---
  const combined = await download('#export-combined');
  check(combined.name === 'Emma.stl', `combined filename is the name (${combined.name})`);
  const c = parseStl(combined.buf);
  check(c.bytesOk, `byte length matches triangle count (${c.triCount} tris)`);
  check(c.triCount > 1000, 'non-trivial triangle count');
  check(Math.abs(c.minZ) < 1e-4, `model sits on z=0 (minZ=${c.minZ})`);
  check(Math.abs(c.maxZ - 6) < 1e-3, `total height is the name thickness 6mm (maxZ=${c.maxZ})`);
  check(c.volume > 0, `outward-facing normals (volume=${c.volume.toFixed(0)} mm³)`);
  check(c.badEdges === 0, `watertight (${c.badEdges} bad edges)`);

  // --- Separate export: the two printable pieces ---
  const named = await downloadAll('#export-separate', 2);
  check(
    'Emma-letter.stl' in named && 'Emma-name.stl' in named,
    `separate export names (${Object.keys(named).join(', ')})`
  );
  const letter = parseStl(named['Emma-letter.stl']);
  const name = parseStl(named['Emma-name.stl']);
  check(Math.abs(letter.maxZ - 5) < 1e-3 && Math.abs(letter.minZ) < 1e-4, `letter spans 0..5mm (${letter.minZ}..${letter.maxZ})`);
  check(Math.abs(name.maxZ - 6) < 1e-3 && Math.abs(name.minZ) < 1e-4, `name spans 0..6mm (${name.minZ}..${name.maxZ})`);
  check(letter.badEdges === 0 && name.badEdges === 0, 'both separate STLs watertight');
  check(
    Math.abs(letter.volume + name.volume - c.volume) < 1,
    'separate volumes sum to the combined volume'
  );
  // The script name is wider than the big letter (overhangs both sides).
  check(name.maxX - name.minX > letter.maxX - letter.minX, 'name overhangs the letter');

  // --- The assembly cutout ---
  // Move the name far away: no overlap -> the letter loses its cutout and
  // its volume grows back to the full monogram.
  await page.evaluate(() => window.__setNameOffset({ x: 500, y: 0 }));
  await page.waitForTimeout(200);
  const solidLetter = parseStl((await downloadAll('#export-separate', 2))['Emma-letter.stl']);
  check(
    solidLetter.volume > letter.volume + 100,
    `name cutout removes material from the letter (${letter.volume.toFixed(0)} vs solid ${solidLetter.volume.toFixed(0)} mm³)`
  );
  check(solidLetter.badEdges === 0, 'solid letter watertight');

  // Back to center; widen the fit gap -> bigger pocket -> smaller letter.
  await page.evaluate(() => window.__setNameOffset({ x: 0, y: 0 }));
  await page.waitForTimeout(200);
  await setSlider('fit-gap', '1');
  const wideGap = parseStl((await downloadAll('#export-separate', 2))['Emma-letter.stl']);
  check(
    wideGap.volume < letter.volume - 50,
    `fit gap widens the cutout (${letter.volume.toFixed(0)} -> ${wideGap.volume.toFixed(0)} mm³ at 1mm gap)`
  );
  check(wideGap.badEdges === 0, 'wide-gap letter watertight');
  await setSlider('fit-gap', '0.2');

  // Offset slider shifts the name piece in the export.
  await setSlider('off-x', '20');
  const shifted = parseStl((await downloadAll('#export-separate', 2))['Emma-name.stl']);
  check(
    Math.abs(shifted.minX - (name.minX + 20)) < 0.5,
    `offset X slider moves the name (+20mm: minX ${name.minX.toFixed(1)} -> ${shifted.minX.toFixed(1)})`
  );
  await setSlider('off-x', '0');

  // --- Door sign without the large letter ---
  await page.fill('#letter-input', '');
  await page.dispatchEvent('#letter-input', 'input');
  await page.waitForTimeout(300);
  const nameOnlyFiles = await downloadAll('#export-separate', 1);
  check(
    Object.keys(nameOnlyFiles).length === 1 && 'Emma-name.stl' in nameOnlyFiles,
    `empty letter -> name-only sign, single STL (${Object.keys(nameOnlyFiles).join(', ')})`
  );
  const nameOnly = parseStl(nameOnlyFiles['Emma-name.stl']);
  check(nameOnly.badEdges === 0, 'name-only sign watertight');
  check(Math.abs(nameOnly.volume - name.volume) < 1, 'name piece identical with or without the letter');

  // --- Monogram-only sign (letter back, name cleared) ---
  await page.fill('#letter-input', 'E');
  await page.dispatchEvent('#letter-input', 'input');
  await page.fill('#name-input', '');
  await page.dispatchEvent('#name-input', 'input');
  await page.waitForTimeout(300);
  const letterOnlyFiles = await downloadAll('#export-separate', 1);
  check(
    Object.keys(letterOnlyFiles).length === 1 && 'E-letter.stl' in letterOnlyFiles,
    `empty name -> monogram only, single STL named after the letter (${Object.keys(letterOnlyFiles).join(', ')})`
  );
  const letterOnly = parseStl(letterOnlyFiles['E-letter.stl']);
  check(letterOnly.badEdges === 0, 'monogram-only sign watertight');
  check(
    Math.abs(letterOnly.volume - solidLetter.volume) < 1,
    `monogram-only letter has no cutout (${letterOnly.volume.toFixed(0)} vs ${solidLetter.volume.toFixed(0)} mm³)`
  );

  // --- Sliders still drive the pipeline: letter size shrinks the monogram ---
  await setSlider('letter-size', '60');
  const smallLetter = parseStl((await downloadAll('#export-separate', 1))['E-letter.stl']);
  check(
    smallLetter.maxX - smallLetter.minX < (letterOnly.maxX - letterOnly.minX) * 0.75,
    `letter size slider scales the monogram (${(letterOnly.maxX - letterOnly.minX).toFixed(1)} -> ${(smallLetter.maxX - smallLetter.minX).toFixed(1)}mm wide)`
  );

  // Restore the full design for the screenshot.
  await page.fill('#name-input', 'Emma');
  await page.dispatchEvent('#name-input', 'input');
  await setSlider('letter-size', '100');
  await page.waitForTimeout(400);
  await page.screenshot({ path: path.join(ROOT, 'e2e-screenshot.png') });
  check(pageErrors.length === 0, `no console/page errors${pageErrors.length ? ': ' + pageErrors.join(' | ') : ''}`);
} finally {
  await browser.close();
  server.close();
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
