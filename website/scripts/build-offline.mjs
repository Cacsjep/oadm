// The offline docs of the client's Help (docs/spec/help.md "Docs build"): the site with OADM_DOCS_OFFLINE=1 (src/offline.mjs)
// into dist-offline/, then checked: no page that stays on oadm.app (dev/, api/, start/, Activate), no intro video, Pagefind
// indexes none of them, and every link to a local path has its file. "npm run build:offline"; pages.yml runs it on every
// deploy, release.yml for the installers. "--check" checks an existing dist-offline/ only.
import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, posix, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(site, 'dist-offline');

if (!process.argv.includes('--check')) {
	const run = (args) => {
		const result = spawnSync(process.execPath, args, {
			cwd: site,
			stdio: 'inherit',
			env: { ...process.env, OADM_DOCS_OFFLINE: '1' },
		});
		if (result.status !== 0) process.exit(result.status ?? 1);
	};
	run([join('scripts', 'changelog.mjs')]);
	run([join('node_modules', 'astro', 'bin', 'astro.mjs'), 'build', '--outDir', 'dist-offline']);
}

const errors = [];
const files = [];
const walk = (dir) => {
	for (const name of readdirSync(dir)) {
		const path = join(dir, name);
		if (statSync(path).isDirectory()) walk(path);
		else files.push(relative(out, path).split(sep).join('/'));
	}
};
walk(out);
const fileSet = new Set(files);
const onlineOnly = /^\/?(dev|api|start|activate)(\/|$)/;

if (!fileSet.has('index.html')) errors.push('index.html missing');
for (const f of files) {
	if (onlineOnly.test(f)) errors.push(`left-out page built: ${f}`);
	if (/(^|\/)intro\.mp4$/.test(f)) errors.push(`intro video copied: ${f}`);
}

// Pagefind: one fragment per indexed page ("pagefind_dcd" + JSON, gzip).
let indexed = 0;
for (const f of files.filter((f) => f.startsWith('pagefind/fragment/'))) {
	const text = gunzipSync(readFileSync(join(out, f))).toString('utf-8').replace(/^pagefind_dcd/, '');
	const { url } = JSON.parse(text);
	indexed++;
	if (onlineOnly.test(url)) errors.push(`search indexes a left-out page: ${url}`);
}
if (indexed === 0) errors.push('search index empty');

// Links and sources to local paths in every page.
const missing = new Map();
for (const f of files.filter((f) => f.endsWith('.html'))) {
	const html = readFileSync(join(out, f), 'utf-8');
	const base = `/${posix.dirname(f)}/`.replace(/^\/\.\//, '/');
	for (const m of html.matchAll(/\s(?:href|src)="([^"]*)"/g)) {
		const ref = m[1].replace(/&amp;/g, '&');
		if (!ref || /^([a-z][a-z0-9+.-]*:|#|\/\/)/i.test(ref)) continue;
		const path = decodeURIComponent(new URL(ref, `http://docs${base}`).pathname).replace(/^\//, '');
		const target = path === '' || path.endsWith('/') ? `${path}index.html` : path;
		if (fileSet.has(target) || fileSet.has(`${target}/index.html`)) continue;
		if (!missing.has(ref)) missing.set(ref, f);
	}
}
for (const [ref, page] of missing) errors.push(`broken link ${ref} in ${page}`);

if (errors.length > 0) {
	console.error(`offline docs check failed (${errors.length}):`);
	for (const e of errors.slice(0, 50)) console.error(`  ${e}`);
	process.exit(1);
}
const bytes = files.reduce((sum, f) => sum + statSync(join(out, f)).size, 0);
console.log(`offline docs ok: ${files.length} files, ${(bytes / 1048576).toFixed(1)} MB, ${indexed} pages in search, in ${out}`);
