// The page's own, in the browser (Components/Home.razor), as the ChalkQL blotter's: Escape closes what's open over the
// page — the filter editor, the alerts, the order opened and the splash that asks for sound (the page's, told it), the
// link menu (the browser's alone); a column chooser closes itself (tapline-grid.js). And the bar's copy and share, which
// run in the click itself: a clipboard takes nothing written later.

let page = null;   // the page's DotNetObjectReference (Home)

document.addEventListener('keydown', event => {
    // An editor that took the key, closing its list of suggestions, leaves the rest open.
    if (event.key !== 'Escape' || event.defaultPrevented) return;
    document.querySelectorAll('.share-wrap[data-open]').forEach(wrap => wrap.removeAttribute('data-open'));
    page?.invokeMethodAsync('OnEscape').catch(() => {});
});

export function listen(dotnet) {
    page = dotnet;
}

export function stop() {
    page = null;
}

window.liveGrid = {
    // Copies the rows in view, at least half of each, under the headings of the columns shown — as text, a tab between
    // values, which a sheet takes as cells, and as a table, for what pastes one; each value whole, as the grid copies a
    // row (a number's every digit, a time's instant), where its cell may show it rounded.
    copyRows(button) {
        const grid = document.querySelector('.frame .tg-grid')?.gridBody;
        const {headings, rows} = grid?.rowsInView() ?? {headings: [], rows: []};
        if (rows.length === 0) {
            this.say(button, 'No rows in view to copy');
            return;
        }
        const text = [headings, ...rows].map(row => row.join('\t')).join('\n');
        const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        const html = '<table><thead><tr>' + headings.map(h => `<th>${escape(h)}</th>`).join('') + '</tr></thead><tbody>' +
            rows.map(row => '<tr>' + row.map(v => `<td>${escape(v)}</td>`).join('') + '</tr>').join('') + '</tbody></table>';
        navigator.clipboard.write([new ClipboardItem({
            'text/plain': new Blob([text], {type: 'text/plain'}),
            'text/html': new Blob([html], {type: 'text/html'})
        })]).then(
            () => this.say(button, `${rows.length} ${rows.length === 1 ? 'row' : 'rows'} copied`),
            failure => this.say(button, `Not copied: ${failure.message}`));
    },

    // Opens or closes the link menu under the link icon; a click anywhere else closes it.
    toggleShare(button) {
        const wrap = button.closest('.share-wrap');
        if (wrap.toggleAttribute('data-open')) {
            const close = event => {
                if (!wrap.contains(event.target)) {
                    wrap.removeAttribute('data-open');
                    document.removeEventListener('click', close, true);
                }
            };
            document.addEventListener('click', close, true);
        }
    },

    // Copies a link to the view, private (its data shrouded) or readable: the page writes it, and the clipboard is handed
    // the promise of it in the click itself, as Safari asks.
    shareView(item, shrouded) {
        const wrap = item.closest('.share-wrap');
        const icon = wrap.querySelector('.icon-button');
        wrap.removeAttribute('data-open');
        if (!page) return;
        const link = page.invokeMethodAsync('ShareLink', shrouded);
        navigator.clipboard.write([new ClipboardItem({
            'text/plain': link.then(url => new Blob([url], {type: 'text/plain'}))
        })]).then(
            () => this.say(icon, shrouded ? 'Private link copied' : 'Readable link copied'),
            failure => this.say(icon, `Not copied: ${failure.message}`));
    },

    // A word under a button, for a moment.
    say(button, words) {
        button.dataset.said = words;
        clearTimeout(button.saying);
        button.saying = setTimeout(() => delete button.dataset.said, 1800);
    }
};
