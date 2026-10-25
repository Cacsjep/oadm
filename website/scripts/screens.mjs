// Copies the screenshots of the headless UI tests to src/assets/screens/ (docs/spec/packaging.md "Website").
//
//   OADM_SCREENSHOT_DIR=<dir> dotnet test ...   (the tests write their PNG files there, subfolders allowed)
//   npm run screens -- <dir>
//
// screens.json maps each website image to the file a test writes ("website name": "test output name"). Every image
// of src/assets/screens/ must be in the map; the script names the ones it could not find and exits with 1.

import { copyFileSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const target = join(here, '..', 'src', 'assets', 'screens');
const source = process.argv[2] ?? process.env.OADM_SCREENSHOT_DIR;
if (!source) {
  console.error('Usage: npm run screens -- <folder the tests wrote their screenshots to>');
  process.exit(2);
}

const map = JSON.parse(readFileSync(join(here, 'screens.json'), 'utf8'));

// Every PNG below the source folder by its file name; a name found twice is ambiguous.
const found = new Map();
const twice = new Set();
const walk = (folder) => {
  for (const entry of readdirSync(folder)) {
    const path = join(folder, entry);
    if (statSync(path).isDirectory()) {
      walk(path);
    } else if (entry.toLowerCase().endsWith('.png')) {
      if (found.has(entry)) twice.add(entry);
      found.set(entry, path);
    }
  }
};
walk(source);

let missing = 0;
for (const [website, output] of Object.entries(map)) {
  if (website.startsWith('//')) continue;
  const from = found.get(output);
  if (!from) {
    console.error(`missing: ${output} (for ${website})`);
    missing++;
    continue;
  }
  if (twice.has(output)) console.warn(`${output} was written more than once; using ${from}`);
  copyFileSync(from, join(target, website));
  console.log(`${website} <- ${from}`);
}

const unmapped = readdirSync(target).filter((f) => f.endsWith('.png') && !(f in map));
for (const file of unmapped) console.error(`not in screens.json: ${basename(file)}`);
process.exit(missing > 0 || unmapped.length > 0 ? 1 : 0);
