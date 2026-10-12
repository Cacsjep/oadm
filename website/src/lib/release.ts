// The latest GitHub release, read once per build and shared by the download buttons, the footer and the structured
// data. The Pages workflow passes its token, so the build is not held up by the limit for anonymous requests.

export const repo = 'Cacsjep/oadm';
export const releasePage = `https://github.com/${repo}/releases/latest`;

export type Asset = { name: string; browser_download_url: string };
export type Release = { version: string; url: string; assets: Asset[] };

let cached: Promise<Release> | undefined;

export function latestRelease(): Promise<Release> {
	cached ??= load();
	return cached;
}

async function load(): Promise<Release> {
	try {
		const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
			headers: {
				Accept: 'application/vnd.github+json',
				...(process.env.GITHUB_TOKEN ? { Authorization: `Bearer ${process.env.GITHUB_TOKEN}` } : {}),
			},
		});
		if (response.ok) {
			const release = await response.json();
			return { version: release.tag_name ?? '', url: release.html_url ?? releasePage, assets: release.assets ?? [] };
		}
	} catch {
		// Offline build: links open the release page, no version is shown.
	}

	return { version: '', url: releasePage, assets: [] };
}
