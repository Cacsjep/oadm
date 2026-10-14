// Writes the docs page Project > Changelog from the repository's CHANGELOG.md, so the page and the release notes are
// one text. Runs before every build and dev start (npm prebuild / predev); the page is git-ignored.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, '..', '..', 'CHANGELOG.md');
const target = join(here, '..', 'src', 'content', 'docs', 'project', 'changelog.md');
const releases = 'https://github.com/Cacsjep/oadm/releases/tag/v';

const lines = readFileSync(source, 'utf-8').split(/\r?\n/);
const out = [];
let started = false;
for (const line of lines) {
	// "## [3.0.3] - 2026-10-14": the version as heading (short anchor #303), the date and the release below it.
	const release = /^## \[(?<version>[^\]]+)\](?:\s*-\s*(?<date>\S+))?/.exec(line);
	if (release) {
		started = true;
		const { version, date } = release.groups;
		out.push(`## ${version}`, '', `${date ? `${date} · ` : ''}[Download](${releases}${version})`);
		continue;
	}

	// Everything before the first release (title, intro, Unreleased) and the link list at the end stay out.
	if (!started || /^\[[^\]]+\]:\s/.test(line)) {
		continue;
	}

	// "### Added" one level smaller, so the versions stand out.
	out.push(line.startsWith('### ') ? `#${line}` : line);
}

const page = [
	'---',
	'title: Changelog',
	'description: What changed in every OADM release, newest first.',
	'tableOfContents:',
	'  maxHeadingLevel: 2',
	'editUrl: https://github.com/Cacsjep/oadm/edit/main/CHANGELOG.md',
	'---',
	'',
	'What changed in every release, newest first. The downloads are on each release page.',
	'',
	...out,
].join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';

mkdirSync(dirname(target), { recursive: true });
writeFileSync(target, page);
console.log(`Changelog page: ${out.filter((l) => l.startsWith('## ')).length} releases`);
