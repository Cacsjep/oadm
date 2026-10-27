// The offline docs of the client's Help (docs/spec/help.md "Docs build"): OADM_DOCS_OFFLINE=1 (npm run build:offline)
// builds the user docs only. The developer docs, the SDK reference, the landing page with the intro video, Get started
// (install and first start: read before OADM runs) and Activate stay on oadm.app: they are not built, and links to them go to oadm.app. Without the flag every export changes nothing.
import { readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join, posix, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const offline = process.env.OADM_DOCS_OFFLINE === '1';

const online = 'https://oadm.app';

/** The pages left out, as patterns of the docs loader (src/content.config.ts). */
export const offlineExcluded = ['dev/**', 'api/**', 'start/**', 'index.mdx', 'activate.mdx'];

/** Paths of the online site that are not built offline: links to them go to oadm.app. */
const onlineOnly = /^\/(dev|api|start|activate)(\/|$)/;

/** The start page "/" without the landing page and Get started: the first page of "Devices" (a plain redirect page, no script). */
export const offlineRedirects = offline ? { '/': '/devices/add/' } : {};

/** Offline the sidebar has no "Get started", and "Developers" becomes one link to the developer docs on oadm.app. */
export function offlineSidebar(sidebar) {
	if (!offline) return sidebar;
	return sidebar.filter((group) => group.label !== 'Get started').map((group) =>
		group.label === 'Developers' ? { label: 'Developers', link: `${online}/dev/plugin-sdk/` } : group,
	);
}

// Links of the built pages to dev/, api/ and activate/ (relative or from the root) become links to oadm.app; the pages
// are changed after the build, so Markdown, components and the sidebar are all covered.
async function linkOnline(out) {
	let changed = 0;
	const files = (await readdir(out, { recursive: true })).filter((f) => f.endsWith('.html'));
	for (const f of files) {
		const path = join(out, f);
		const base = `/${posix.dirname(f.split(sep).join('/'))}/`.replace(/^\/\.\//, '/');
		const html = await readFile(path, 'utf-8');
		const next = html.replace(/(\shref=")([^"]*)"/g, (all, attr, href) => {
			if (!href || /^([a-z][a-z0-9+.-]*:|#|\/\/)/i.test(href)) return all;
			const url = new URL(href, `${online}${base}`);
			if (!onlineOnly.test(url.pathname)) return all;
			changed++;
			return `${attr}${url.href}"`;
		});
		if (next !== html) await writeFile(path, next);
	}
	return changed;
}

// Files of public/ only the landing page uses (the intro video and its poster; the shared-link picture is read from
// oadm.app): removed from the offline output, public/ itself stays as it is.
const landingFiles = ['intro.mp4', 'intro-poster.jpg', 'og-image.png'];

export const offlineIntegrations = offline
	? [
			{
				name: 'oadm-offline-docs',
				hooks: {
					'astro:build:done': async ({ dir, logger }) => {
						const out = fileURLToPath(dir);
						for (const f of landingFiles) await rm(join(out, f), { force: true });
						const changed = await linkOnline(out);
						logger.info(`removed ${landingFiles.join(', ')}; ${changed} links to oadm.app`);
					},
				},
			},
		]
	: [];
