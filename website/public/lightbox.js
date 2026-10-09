// Click a screenshot to see it at full size; click again, press Escape or use the close button to return.
(() => {
	const dialog = document.createElement('dialog');
	dialog.className = 'lightbox';
	dialog.innerHTML = '<button type="button" class="lightbox-close" aria-label="Close">×</button><img alt="">';
	const image = dialog.querySelector('img');

	const close = () => dialog.close();
	dialog.addEventListener('click', close);
	dialog.addEventListener('close', () => image.removeAttribute('src'));

	document.addEventListener('click', (event) => {
		const target = event.target;
		if (!(target instanceof HTMLImageElement) || !target.classList.contains('screen')) return;
		event.preventDefault();
		if (!dialog.isConnected) document.body.append(dialog);
		image.src = target.currentSrc || target.src;
		image.alt = target.alt;
		dialog.showModal();
	});
})();
