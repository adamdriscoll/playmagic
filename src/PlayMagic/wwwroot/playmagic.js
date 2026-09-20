window.playMagic = {
    preloadImage(url) {
        return new Promise(resolve => {
            const image = new Image();
            let settled = false;
            const finish = () => {
                if (settled) return;
                settled = true;
                clearTimeout(timeout);
                resolve();
            };
            const timeout = setTimeout(finish, 4000);
            image.onload = finish;
            image.onerror = finish;
            image.src = url;
            if (image.complete) finish();
        });
    },
    async post(path, payload) {
        const response = await fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        const result = await response.json();
        return response.ok
            ? { value: result.value, error: null }
            : { value: null, error: result.message || 'Please try again.' };
    },
    getToken(gameId) { return localStorage.getItem(`playmagic:${gameId}`); },
    setToken(gameId, token) { localStorage.setItem(`playmagic:${gameId}`, token); },
    getSavedDecks() {
        try {
            const decks = JSON.parse(localStorage.getItem('playmagic:saved-decks') || '[]');
            return Array.isArray(decks) ? decks : [];
        } catch { return []; }
    },
    saveDeck(name, text) {
        const label = name.trim() || 'Untitled deck';
        const decks = this.getSavedDecks().filter(deck => deck.name !== label);
        decks.unshift({ name: label, text });
        const saved = decks.slice(0, 10);
        try { localStorage.setItem('playmagic:saved-decks', JSON.stringify(saved)); }
        catch { return this.getSavedDecks(); }
        return saved;
    },
    removeSavedDeck(name) {
        const decks = this.getSavedDecks().filter(deck => deck.name !== name);
        try { localStorage.setItem('playmagic:saved-decks', JSON.stringify(decks)); }
        catch { return this.getSavedDecks(); }
        return decks;
    },
    async copyLink(url, detailsId) {
        await navigator.clipboard.writeText(url);
        if (detailsId) document.getElementById(detailsId)?.removeAttribute('open');
    },
    confirm(message) { return window.confirm(message); },
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
