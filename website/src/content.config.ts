import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';
import { offline, offlineExcluded } from './offline.mjs';

// The offline docs of the client's Help (OADM_DOCS_OFFLINE=1, src/offline.mjs): Starlight's docs loader without the
// pages that stay on oadm.app.
const loader = offline
	? glob({
			base: './src/content/docs',
			pattern: ['**/[^_]*.{markdown,mdown,mkdn,mkd,mdwn,md,mdx}', ...offlineExcluded.map((p) => `!${p}`)],
		})
	: docsLoader();

export const collections = {
	docs: defineCollection({ loader, schema: docsSchema() }),
};
