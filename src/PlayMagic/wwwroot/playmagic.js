window.playMagic = {
    async post(path, payload) {
        const response = await fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        const result = await response.json();
        return response.ok
            ? { value: result.value, error: null, importFailed: false }
            : { value: null, error: result.message || 'Please try again.', importFailed: !!result.importFailed };
    },
    getToken(gameId) { return localStorage.getItem(`playmagic:${gameId}`); },
    setToken(gameId, token) { localStorage.setItem(`playmagic:${gameId}`, token); },
    copyLink(url) { return navigator.clipboard.writeText(url); },
    draggedCardId: null,
    getDraggedCardId() { return this.draggedCardId; },
    clearDraggedCardId() { this.draggedCardId = null; }
};

document.addEventListener('dragstart', event => {
    const card = event.target.closest('[data-card-id]');
    if (!card || card.getAttribute('draggable') !== 'true') return;
    window.playMagic.draggedCardId = card.dataset.cardId;
    event.dataTransfer.setData('text/plain', card.dataset.cardId);
});
document.addEventListener('dragover', event => {
    if (event.target.closest('.drop-zone')) event.preventDefault();
});
