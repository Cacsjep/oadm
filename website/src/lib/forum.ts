// Whether the support forum answers: one check per page view, shared by the top bar and the buttons below the downloads.
// `no-cors`: any answer counts (the check needs no CORS entry), only a failed connection or 6 s without one is "down".
export const forumUrl = 'https://support.oadm.app/';

let check: Promise<boolean> | undefined;

export function forumAvailable(): Promise<boolean> {
	check ??= (async () => {
		const abort = new AbortController();
		const timer = setTimeout(() => abort.abort(), 6000);
		try {
			await fetch(`${forumUrl}api/health`, { mode: 'no-cors', cache: 'no-store', signal: abort.signal });
			return true;
		} catch {
			return false;
		} finally {
			clearTimeout(timer);
		}
	})();
	return check;
}

/** Greys out a link to the forum: not clickable, not reachable with Tab. */
export function disableForumLink(link: HTMLElement) {
	link.classList.add('forum-unavailable');
	link.setAttribute('aria-disabled', 'true');
	link.setAttribute('tabindex', '-1');
	link.addEventListener('click', (event) => event.preventDefault());
}
