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
    async importGame(archive) {
        const response = await fetch('/api/games/import', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ archive })
        });
        const body = await response.json();
        return response.ok
            ? { result: body, error: null }
            : { result: null, error: body.message || 'Please try again.' };
    },
    downloadText(filename, content) {
        const blob = new Blob([content], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 0);
    },
    getToken(gameId) { return localStorage.getItem(`playmagic:${gameId}`); },
    setToken(gameId, token) { localStorage.setItem(`playmagic:${gameId}`, token); },
    setRestoredInvites(gameId, invites) {
        localStorage.setItem(`playmagic:restored-invites:${gameId}`, JSON.stringify(invites));
    },
    getRestoredInvites(gameId) {
        try {
            const invites = JSON.parse(localStorage.getItem(`playmagic:restored-invites:${gameId}`) || 'null');
            return Array.isArray(invites) ? invites : null;
        } catch { return null; }
    },
    removeRestoredInvites(gameId) {
        localStorage.removeItem(`playmagic:restored-invites:${gameId}`);
    },
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
