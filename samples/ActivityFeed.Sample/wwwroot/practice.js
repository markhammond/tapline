// The practice's feeds, in the page. The library knows nothing of practices: this decodes a person's topic — its keys
// (a part byte, and a sequence number) and rows (fixed-width, as FeedRow lays them out: the offsets come from the app) —
// from the replica tapline keeps, its checksum checked against the router's on every record, and shows it as its person
// may see it. A topic holds the schedule's activities (who, when, with whom, and a note) and the visits' clinical sections
// the person may read (what for, and what was found, one per problem). Each row says what the practice's policy disclosed
// of the patient and of its content: a withheld value is its placeholder, or nothing, and is shown as withheld. A visit
// with no section the person may read shows why not: reception reads none ("Consultation"); a GP reads only what they
// attended and what the patient shares with them ("Not shared with you").
//
// A feed is the person's activity, one row an activity; the page groups them by visit (the booking they share), with its
// sections, and shows each visit's latest step, the latest activity first — the order held while the pointer is over the feed, so a row
// doesn't move from under it (new visits wait at the end until it leaves). Reception may check a booked patient in, a GP call their own patient in: the button makes a
// correlation id, awaits its rendezvous (tapline.rendezvous: before the request goes, so nothing can come first), then
// sends the command through the page's component to the practice service. The rendezvous comes once the change is in
// front of this page, after it, in order: the panel says how long that took from the click.
//
// Each row also says why its patient was disclosed to the reader (its basis): care, for a GP's own; scheduling, need to
// know, for reception, the manager and the rest of a GP's site, shown as such; emergency, for a GP who broke the glass.
//
// A manager has their own topic (the appointment schedule) and a second feed, "stats": their sites' statistics (rows as
// StatsRow lays them out), each site's hours and its visits by reason, a count under the policy's floor withheld.

const utf8 = new TextDecoder();
const KINDS = ['', 'booked', 'arrived', 'called', 'seen', 'reminded', 'messaged', 'result', 'replied'];
const DISCLOSED = ['full', 'masked', 'aggregate', 'redacted'];
const BASES = ['', 'care', 'scheduling', 'own record', 'carer', 'emergency'];   // (DisclosureBasis)
const PART_ACTIVITY = 0, PART_TASKS = 1, PART_STATUS = 2, PART_CLINICAL = 3, PART_ACCESS = 4, PART_OUTBOX = 8;
const BOARD_VISITS = 40, ALONE_VISITS = 400, BOARD_MESSAGES = 4, ALONE_MESSAGES = 40, LOG_ROWS = 120, RENDEZVOUS_MS = 10000;
const BOARD_HOURS = 3, ALONE_HOURS = 12;
const time = new Intl.DateTimeFormat(undefined, {hour: '2-digit', minute: '2-digit'});
const plural = (n, one, many = `${one}s`) => `${n.toLocaleString()} ${n === 1 ? one : many}`;
const esc = s => String(s).replace(/[&<>"']/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'})[c]);

/** Zero-padded UTF-8 text, through its first zero byte. */
function text(bytes, at, length) {
    let end = at;
    while (end < at + length && bytes[end] !== 0) end++;
    return utf8.decode(bytes.subarray(at, end));
}

/**
 * A person's topic, decoded as FeedRow lays it out: its activities (the schedule, each with its acknowledgements), its
 * clinical sections, the person's own tasks (the messages they've yet to read) and their status row (the badge).
 */
function decodeFeed(state, o) {
    const rows = [], sections = [], tasks = new Map(), access = [], outbox = new Map();
    let badge = 0;
    for (const {key, row} of state.rows.values()) {
        const part = key[o.key.section];
        const k = new DataView(key.buffer, key.byteOffset, key.byteLength);
        const v = new DataView(row.buffer, row.byteOffset, row.byteLength);
        const f = o.feed;
        if (part === PART_TASKS) {                                  // (a receptionist's messages to read; a GP's results to release)
            tasks.set(Number(k.getBigInt64(o.key.seq, true)), {
                kind: KINDS[row[f.kind]] ?? 'unknown', patient: v.getInt32(f.patient, true),
                name: text(row, f.patient_name, 48), reason: text(row, f.reason, 48), detail: text(row, f.detail, 120),
                at: Number(v.getBigInt64(f.at_us, true) / 1000n)
            });
            continue;
        }
        if (part === PART_STATUS) {
            badge = v.getInt32(f.count, true);
            continue;
        }
        if (part === PART_OUTBOX) {                                 // (a receptionist's replies: how each one's notification went)
            outbox.set(Number(k.getBigInt64(o.key.seq, true)), {
                status: text(row, f.reason, 48), devices: v.getInt32(f.count, true), at: Number(v.getBigInt64(f.at_us, true) / 1000n)
            });
            continue;
        }
        if (part === PART_ACCESS) {                                 // (a patient's consents; a GP's emergency access)
            access.push({
                seq: Number(k.getBigInt64(o.key.seq, true)),
                gp: v.getInt32(f.clinician, true),
                patient: v.getInt32(f.patient, true),
                name: text(row, f.patient_name, 48),
                label: text(row, f.reason, 48),
                detail: text(row, f.detail, 120),
                at: Number(v.getBigInt64(f.at_us, true) / 1000n),
                everything: v.getInt32(f.count, true) === 1,
                carer: v.getInt32(f.count, true) === 2             // (a designation: a patient's carer, or whom a carer cares for)
            });
            continue;
        }
        if (part !== PART_ACTIVITY && part !== PART_CLINICAL) continue;
        if (part === PART_CLINICAL) {
            sections.push({
                seq: Number(k.getBigInt64(o.key.seq, true)),
                kind: KINDS[row[f.kind]] || 'visit',
                released: row[f.released] === 1,
                cut: row[f.detail_cut] === 1,                       // (its note's longer than the row holds: the rest on demand)
                patient: v.getInt32(f.patient, true),
                booking: Number(v.getBigInt64(f.booking, true)),
                attending: v.getInt32(f.clinician, true),
                problem: v.getInt32(f.problem, true),
                at: Number(v.getBigInt64(f.at_us, true) / 1000n),
                reason: text(row, f.reason, 48),
                detail: text(row, f.detail, 120),
                reasonDisclosed: DISCLOSED[row[f.reason_disclosed]] ?? 'redacted',
                detailDisclosed: DISCLOSED[row[f.detail_disclosed]] ?? 'redacted'
            });
            continue;
        }
        rows.push({
            seq: Number(k.getBigInt64(o.key.seq, true)),
            kind: KINDS[row[f.kind]] ?? 'unknown',
            site: row[f.site],
            clinician: v.getInt32(f.clinician, true),
            booking: Number(v.getBigInt64(f.booking, true)),
            slot: Number(v.getBigInt64(f.slot_us, true) / 1000n),
            at: Number(v.getBigInt64(f.at_us, true) / 1000n),
            ticket: v.getInt16(f.ticket, true),
            room: text(row, f.room, 8),
            patient: v.getInt32(f.patient, true),
            name: text(row, f.patient_name, 48),
            born: v.getInt16(f.born, true),
            sex: row[f.sex] ? String.fromCharCode(row[f.sex]) : '',
            note: text(row, f.detail, 120),
            patientDisclosed: DISCLOSED[row[f.patient_disclosed]] ?? 'redacted',
            noteDisclosed: DISCLOSED[row[f.detail_disclosed]] ?? 'redacted',
            acked: Number(v.getBigInt64(f.acked_us, true) / 1000n),
            ackedBy: text(row, f.acked_by, 48),
            read: Number(v.getBigInt64(f.read_us, true) / 1000n),
            readBy: text(row, f.read_by, 48),
            readers: row[f.readers],
            staffDisclosed: DISCLOSED[row[f.staff_disclosed]] ?? 'redacted',
            basis: BASES[row[f.basis]] ?? ''
        });
    }
    return {rows, sections, tasks, badge, access, outbox};
}

/** A screen's rows: tickets, waiting or called. */
function decodeScreen(state, o) {
    const rows = [];
    for (const {key, row} of state.rows.values()) {
        const k = new DataView(key.buffer, key.byteOffset, key.byteLength);
        const v = new DataView(row.buffer, row.byteOffset, row.byteLength);
        rows.push({
            seq: Number(k.getBigInt64(o.key.seq, true)),
            kind: KINDS[row[o.screen.kind]] ?? 'unknown',
            ticket: v.getInt16(o.screen.ticket, true),
            room: text(row, o.screen.room, 8),
            at: Number(v.getBigInt64(o.screen.at_us, true) / 1000n)
        });
    }
    return rows;
}

/**
 * A manager's statistics: each site's hours (checked in, called in, seen; the mean wait of those called) and its visits
 * by reason, each count as the policy disclosed it (an aggregate, or withheld under the floor).
 */
function decodeStats(state, o, reasons) {
    const hours = [], byReason = [], s = o.stats;
    for (const {row} of state.rows.values()) {
        const v = new DataView(row.buffer, row.byteOffset, row.byteLength);
        const site = row[s.site], at = Number(v.getBigInt64(s.at_us, true) / 1000n);
        if (row[s.hourly]) hours.push({site, at, arrivals: v.getInt32(s.arrivals, true), called: v.getInt32(s.called, true),
            seen: v.getInt32(s.seen, true), wait: v.getInt32(s.mean_wait_s, true)});
        else byReason.push({site, since: at, counts: reasons.map((name, i) => ({name, count: v.getInt32(s.by_reason + 4 * i, true),
            disclosed: DISCLOSED[row[s.reasons_disclosed + i]] ?? 'redacted'}))});
    }
    return {hours, byReason};
}

/** The activity grouped by visit (the booking its rows share): each visit's rows, oldest first, its latest, its sections. */
function visitsOf(rows, sections) {
    const visits = new Map();
    for (const row of rows) {
        if (row.kind === 'messaged' || row.kind === 'replied') continue;   // (a message is its own thread, not a visit's)
        let visit = visits.get(row.booking);
        if (!visit) visits.set(row.booking, visit = {booking: row.booking, rows: [], sections: []});
        visit.rows.push(row);
    }
    for (const section of sections) visits.get(section.booking)?.sections.push(section);   // (a visit out of the schedule's reach: not shown)
    for (const visit of visits.values()) {
        visit.sections.sort((a, b) => a.seq - b.seq);
        visit.rows.sort((a, b) => a.seq - b.seq);
        visit.latest = visit.rows[visit.rows.length - 1];
        visit.ticket = visit.rows.reduce((t, r) => r.ticket || t, 0);
        visit.room = visit.rows.reduce((t, r) => r.room || t, '');
        visit.reminder = visit.rows.find(r => r.kind === 'reminded');
        visit.step = visit.rows.filter(r => r.kind !== 'reminded').at(-1) ?? visit.latest;   // (a reminder isn't a step)
    }
    return [...visits.values()].sort((a, b) => b.latest.seq - a.latest.seq);   // (the latest activity first)
}

/**
 * Why a staff member was shown a patient's name, where it isn't their care: scheduling, need to know (the appointment and
 * who attends, nothing clinical); emergency, the glass broken. Care says nothing.
 */
function basisOf(row) {
    if (row.basis === 'scheduling')
        return ' <span class="basis" title="Disclosed for scheduling, on a need-to-know basis: the appointment and who attends, not the visit\'s content">need to know</span>';
    if (row.basis === 'emergency')
        return ' <span class="basis emergency" title="Disclosed for your emergency access to their record, while it lasts">emergency</span>';
    return '';
}

/** A value as the policy disclosed it: itself, or withheld (its placeholder, if it has one), saying so. */
function shown(value, disclosed, what, fallback = 'withheld') {
    if (disclosed === 'full') return esc(value);
    const why = `${disclosed.toUpperCase()}: what the practice's policy disclosed of ${what}, for this row`;
    return `<span class="withheld" title="${esc(why)}">${esc(value || fallback)}</span>`;
}

/** No section a person may read, said as their role has it: why the reason isn't shown. */
function unread(role) {
    const [text, why] = role === 'reception' || role === 'manager'
        ? ['Consultation', `The policy shows ${role === 'manager' ? 'the manager' : 'reception'} no clinical section: who and when, never what for`]
        : role === 'clinician'
            ? ['Not shared with you', 'No section of this visit is yours to read: you didn\'t attend it, and the patient hasn\'t shared it with you']
            : ['withheld', 'The policy shows you no clinical section'];
    return `<span class="withheld" title="${esc(why)}">${esc(text)}</span>`;
}

/** What a visit was for, from its sections the person may read (its own, not its results'): each problem's reason. */
function reasonsOf(visit, role) {
    const own = visit.sections.filter(s => s.kind !== 'result');
    return own.length === 0 ? (visit.sections.length ? '' : unread(role))
        : own.map(s => `<span title="${sectionTitle(s)}">${shown(s.reason, s.reasonDisclosed, 'the reason')}</span>`).join(' · ');
}

/** A section's number, as the audit page looks it up: who was shown it. */
const sectionTitle = s => `clinical section ${s.seq} (the audit page looks it up: who was shown it)`;

/** A visit's numbers, as the audit page looks them up: the visit (its booking), and its activities. */
function visitTitle(visit, first) {
    return `visit ${visit.booking}, booked at ${time.format(first.at)} · activities ${visit.rows.map(r => r.seq).join(', ')}` +
        `${visit.sections.length ? ` · clinical sections ${visit.sections.map(s => s.seq).join(', ')}` : ''} (the audit page looks each up: who was shown it)`;
}

/** A visit's results the person may read: each test, and its finding (or, for its patient, that it awaits review). */
function resultsOf(visit) {
    return visit.sections.filter(s => s.kind === 'result').map(s => `<div class="result" title="${sectionTitle(s)}">Result: ${esc(s.reason)} — ` +
        `${shown(s.detail, s.detailDisclosed, 'the finding')}${s.released ? '' : ' <span class="unread">(to review)</span>'}</div>`).join('');
}

/** A section's note: in full once asked for, else as the row holds it, with a button for the rest if it's cut. */
function noteOf(view, s) {
    const asked = view.notes.get(s.seq);
    if (asked?.text) return `<span title="${sectionTitle(s)}">${esc(asked.text)}</span>`;
    const more = s.cut ? ` <button class="act small" data-note="${s.seq}"${asked?.loading ? ' disabled' : ''}>` +
        `${asked?.loading ? '…' : asked?.refused ? esc(asked.refused) : 'Full note'}</button>` : '';
    return `<span title="${sectionTitle(s)}">${esc(s.detail)}${s.cut ? '…' : ''}</span>${more}`;
}

/** What was found, from the visit's sections (once seen), or else its latest step's note. */
function detailOf(view, visit) {
    const found = visit.sections.filter(s => s.kind !== 'result' && s.detail && s.detailDisclosed === 'full').map(s => noteOf(view, s));
    if (found.length) return found.join(' · ');
    const r = visit.step;
    return r.noteDisclosed === 'full' && r.note ? esc(r.note) : '';
}

/** A reminder's state: acknowledged (by whom, for whom, when), or not yet. */
function reminderOf(visit, mine) {
    const r = visit.reminder;
    if (!r) return '';
    if (!r.acked) return '<div class="ack">Reminder sent · not acknowledged yet</div>';
    const named = r.staffDisclosed === 'full' && r.ackedBy;
    const by = mine ? 'you' : named ? esc(r.ackedBy) : 'the patient or their carer';
    const forWhom = named && r.ackedBy !== r.name && r.name ? `, for ${esc(r.name)}` : '';
    return `<div class="ack done">Acknowledged by ${by}${forWhom}, ${time.format(r.acked)}</div>`;
}

/** A message's read state: by whom first, and how many at reception; or the practice, not who, for its patient. */
function readOf(m, patient) {
    if (!m.read) return '<span class="unread">Not read yet</span>';
    if (patient) return `<span class="read">Read by the practice, ${time.format(m.read)}</span>`;
    const by = m.staffDisclosed === 'full' && m.readBy ? esc(m.readBy) : 'the practice';
    return `<span class="read">Read by ${by}, ${time.format(m.read)}${m.readers > 1 ? ` · ${m.readers} at reception` : ''}</span>`;
}

function stepOf(visit) {
    switch (visit.step.kind) {
        case 'booked':
            return '<span class="step booked">Booked</span>';
        case 'arrived':
            return `<span class="step arrived">Checked in${visit.ticket ? ` · ticket ${visit.ticket}` : ''}</span>`;
        case 'called':
            return `<span class="step called">Called → ${esc(visit.room)}</span>`;
        case 'seen':
            return '<span class="step seen">Seen</span>';
        default:
            return esc(visit.step.kind);
    }
}

export function createPracticeView(tapline) {
    const views = new Map();                                     // the feed's element → its view
    // An instance of the practice service draining (blue/green): each feed bound through it is asked for again by its
    // panel (step 1 goes to another instance), and moved there make before break (move, below).
    tapline.onServiceDraining = node => {
        for (const view of views.values())
            if (view.binding.nodeId === node) view.component.invokeMethodAsync('Move', view.binding.tapId).catch(() => {});
    };
    // One feed asked to move (its instance rebalancing): asked for again by its panel — placed by load — and moved there,
    // make before break, as for a drain.
    tapline.onMove = tapId => {
        for (const view of views.values())
            if (view.binding.tapId === tapId) view.component.invokeMethodAsync('Move', tapId).catch(() => {});
    };

    function schedule(view) {
        if (view.frame) return;
        view.frame = true;
        requestAnimationFrame(() => {
            view.frame = false;
            render(view);
        });
    }

    /**
     * A person's feed (their topic, bound on this page's circuit: step 1 was the component's), shown in `element`, its
     * counts and state in `meta`; `component` sends the commands. Subscribes (step 2): the router sends its state.
     */
    function attach(binding, element, meta, options, component) {
        views.get(element)?.off();
        const view = {binding, element, meta, options, component, state: null, banner: '', note: null, seen: new Map(), busy: new Set(), order: [], frame: false, reported: new Set(), draft: '', replies: new Map(), replying: 0,
            notes: new Map(), why: {open: false, loading: false, explanation: null, error: ''}};
        view.shown = state => {
            view.state = state;
            view.banner = state.degraded || state.stale ? 'Stale: the practice service is late or out of reach — the last state it sent, kept' : '';
            schedule(view);
        };
        view.ended = (error, last) => {
            view.state = last ?? view.state;
            view.banner = `${utf8.decode(error.payload) || 'The feed ended'} (code ${error.code}) — the last state kept`;
            schedule(view);
        };
        const off = tapline.subscribe(binding, view.shown, view.ended);
        view.off = () => {
            off();
            tapline.drop(binding.tapId);
        };
        views.set(element, view);
        if (!element.dataset.acts) {                              // (once per element: the view's looked up as it's used)
            element.dataset.acts = 'on';
            element.addEventListener('click', e => {
                const button = e.target.closest?.('button[data-act]');
                const current = views.get(element);
                if (button && current) act(current, button);
                const open = e.target.closest?.('button[data-reply]');   // (reception: a reply's form opened, or closed)
                if (open && current) {
                    current.replying = current.replying === Number(open.dataset.reply) ? 0 : Number(open.dataset.reply);
                    schedule(current);
                }
                const note = e.target.closest?.('button[data-note]');     // (a section's note in full, asked for)
                if (note && current) fullNote(current, Number(note.dataset.note));
                const why = e.target.closest?.('summary[data-why]');      // (why: ChalkQL's explanation, asked for once opened)
                if (why && current) {
                    e.preventDefault();
                    explain(current);
                }
            });
            element.addEventListener('input', e => {                 // (a draft survives the feed's re-renders)
                const current = views.get(element);
                if (current && e.target.name === 'message') current.draft = e.target.value;
                if (current && e.target.name === 'reply') current.replies.set(Number(e.target.dataset.thread), e.target.value);
            });
            element.addEventListener('submit', e => {                // (a patient's message to the practice)
                e.preventDefault();
                const button = e.target.querySelector?.('button[data-act]');
                const current = views.get(element);
                if (button && current) act(current, button);
            });
            element.addEventListener('pointerenter', () => element.dataset.hovered = 'on');
            element.addEventListener('pointerleave', () => {
                delete element.dataset.hovered;
                const current = views.get(element);
                if (current) schedule(current);                    // (the order held meanwhile catches up)
            });
        }
        schedule(view);
    }

    function detach(element) {
        views.get(element)?.off();
        views.delete(element);
    }

    /**
     * A feed moved make before break (its instance draining): `binding`, the panel's step 1 again, in place of the tap
     * it held. The old one shows on until the new one's first state, which lets it go. Resolves then; or once the new
     * one ends first.
     */
    function move(element, oldTapId, binding) {
        const view = views.get(element);
        if (!view || view.binding.tapId !== oldTapId) return Promise.resolve();
        return new Promise(resolve => {
            const old = view.off;
            const off = tapline.replace(oldTapId, binding,
                state => {
                    if (view.binding !== binding) {
                        view.binding = binding;              // (the old one let go: this one's from here)
                        old();
                        resolve();
                    }
                    view.shown(state);
                },
                (error, last) => {
                    view.ended(error, last);
                    resolve();
                });
            view.off = () => {
                off();
                tapline.drop(binding.tapId);
            };
        });
    }

    /** A command: its rendezvous awaited first, then sent; the panel says what came of it, and how soon it was in front of the page. */
    async function act(view, button) {
        const {act: kind, id: target, patient, tag} = button.dataset;
        const message = kind === 'message' ? view.draft.trim() : kind === 'reply' ? (view.replies.get(Number(target)) ?? '').trim()
            : kind === 'share' || kind === 'notice' ? tag : '';
        if ((kind === 'message' || kind === 'reply') && !message) return;
        const id = tapline.correlation(), abort = new AbortController();
        const arrived = tapline.rendezvous(id, {tapId: view.binding.tapId, signal: abort.signal});
        arrived.catch(() => {
        });
        const clicked = performance.now();
        const verb = {arrive: `Checked ${patient} in`, call: `Called ${patient} in`, acknowledge: 'Acknowledged the reminder',
            message: 'Sent your message', reply: `Replied to ${patient}`, share: `Shared “${tag}”`, withdraw: 'Withdrew it',
            breakglass: `Broke the glass for ${patient}`, release: `Released the result to ${patient}`,
            revokecarer: 'Revoked the designation', notice: `Posted “${button.textContent}”`}[kind] ?? kind;
        const busy = kind === 'share' || kind === 'notice' ? `${target}:${tag}` : Number(target ?? 0) || kind;
        view.busy.add(busy);                                       // (its button disabled through re-renders, until it's done)
        button.disabled = true;
        button.textContent = '…';
        try {
            let answer;
            try {
                answer = await view.component.invokeMethodAsync('Act', kind, Number(target ?? 0), id, message,
                    Number(button.dataset.patientId ?? 0));   // (a reply's thread: its message's patient)
            } catch (e) {
                abort.abort();
                return note(view, `${verb}: not sent (${e.message ?? e})`, 'refused');
            }
            if (!answer?.done) {
                abort.abort();
                return note(view, `Refused: ${answer?.refused ?? 'no reason given'}`, 'refused');
            }
            const timeout = new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), RENDEZVOUS_MS));
            try {
                await Promise.race([arrived, timeout]);
                const what = {share: 'consent', withdraw: 'consent', breakglass: '', release: 'section', revokecarer: '', notice: ''}[kind] ?? 'activity';
                note(view, `${verb} — in front of you ${Math.round(performance.now() - clicked)} ms after the click` +
                    (what ? ` (${what} ${answer.seq})` : ''), 'ok');
            } catch {
                abort.abort();
                note(view, `${verb} (activity ${answer.seq}), but its rendezvous didn't come within ${RENDEZVOUS_MS / 1000} s`, 'refused');
            }
        } finally {
            view.busy.delete(busy);
            if (kind === 'message' && view.note?.kind === 'ok') view.draft = '';
            if (kind === 'reply' && view.note?.kind === 'ok') {
                view.replies.delete(Number(target));
                view.replying = 0;
            }
            schedule(view);
        }
    }

    /** A section's note in full, as the person may read it: asked of the service (an entitled read, as them). */
    async function fullNote(view, seq) {
        if (view.notes.get(seq)?.loading) return;
        view.notes.set(seq, {loading: true});
        schedule(view);
        try {
            const answer = await view.component.invokeMethodAsync('Note', seq);
            view.notes.set(seq, answer?.refused ? {refused: answer.refused} : {text: answer.text, disclosed: answer.disclosed});
        } catch (e) {
            view.notes.set(seq, {refused: `not asked (${e.message ?? e})`});
        }
        schedule(view);
    }

    /** Why the person sees what they see: opened, ChalkQL's explanation of their statements, asked once; closed again. */
    async function explain(view) {
        const why = view.why;
        why.open = !why.open;
        schedule(view);
        if (!why.open || why.explanation || why.loading) return;
        why.loading = true;
        try {
            why.explanation = await view.component.invokeMethodAsync('Explain');
        } catch (e) {
            why.error = `not asked (${e.message ?? e})`;
        }
        why.loading = false;
        schedule(view);
    }

    /** The explanation, folded for this person: per statement, each table's rows and its columns not shown in full. */
    function renderWhy(view) {
        const {open, loading, explanation: x, error} = view.why;
        let body = '';
        if (open && loading) body = '<p class="empty">Asking ChalkQL…</p>';
        else if (open && (error || x?.refused)) body = `<p class="note refused">${esc(error || x.refused)}</p>`;
        else if (open && x) body = `<p class="counted">As <code>${esc(x.principal)}</code>, for ${esc(x.purpose)}: what ChalkQL's ` +
            'policy folds to for your statements, nothing run.</p>' + x.statements.map(st => `<h5>${esc(st.name)}</h5>` + st.tables.map(t => {
                const listed = t.columns.filter(c => c.disclosure !== 'FULL' || c.placeholder);
                const full = t.columns.length - listed.length;
                return `<div class="why-table"><strong>${esc(t.table)}</strong>: ${t.rows ? `rows where <code>${esc(t.rows)}</code>` : 'every row'}<ul>` +
                    listed.map(c => `<li><code>${esc(c.column)}</code>: ${esc(c.disclosure)}${c.placeholder ? ` · placeholder ${esc(c.placeholder)}` : ''}` +
                        `${c.mask ? ` · mask ${esc(c.mask)}` : ''}${c.floor && c.disclosure.includes('AGGREGATE') ? ` · a count under ${c.floor} withheld` : ''}</li>`).join('') +
                    (full ? `<li>${full} more, in full</li>` : '') + '</ul></div>';
            }).join('')).join('');
        return `<details class="why"${open ? ' open' : ''}><summary data-why="1">Why you see what you see</summary>${body}</details>`;
    }

    /**
     * What the page has shown its person, reported once each: at reception, the messages now in front of them; for a
     * patient or carer, each visit's step and reminder, so a notification waiting on what they've seen in the app isn't
     * pushed to them too (the service records only those).
     */
    function report(view, shown) {
        const fresh = shown.filter(seq => !view.reported.has(seq));
        if (fresh.length === 0) return;
        for (const seq of fresh) view.reported.add(seq);
        view.component.invokeMethodAsync('Seen', fresh, tapline.correlation()).then(answer => {
            if (!answer?.done) {                                    // (refused: tried again at the next render)
                console.warn(`practice: reading ${fresh} refused: ${answer?.refused}`);
                for (const seq of fresh) view.reported.delete(seq);
            }
        }, e => {
            console.warn(`practice: reading ${fresh} not sent: ${e?.message ?? e}`);
            for (const seq of fresh) view.reported.delete(seq);
        });
    }

    function note(view, message, kind) {
        view.note = {message, kind};
        schedule(view);
    }

    function render(view) {
        const {options: o, state} = view;
        if (!state) {
            view.element.innerHTML = '<p class="empty">Waiting for the feed…</p>';
            return;
        }
        const screen = o.role === 'screen', stats = o.feed === 'stats' ? decodeStats(state, o.offsets, o.reasons) : null;
        const {rows, sections, tasks, badge, access, outbox} = screen
            ? {rows: decodeScreen(state, o.offsets), sections: [], tasks: new Set(), badge: 0, access: [], outbox: new Map()}
            : stats ? {rows: [], sections: [], tasks: new Set(), badge: 0, access: [], outbox: new Map()}
                : decodeFeed(state, o.offsets);
        const messages = rows.filter(r => r.kind === 'messaged').sort((a, b) => b.seq - a.seq);
        const replies = rows.filter(r => r.kind === 'replied').sort((a, b) => a.seq - b.seq);   // (the practice's, oldest first)
        let body;
        if (screen) body = renderScreen(rows);
        else if (stats) body = renderStats(view, stats);
        else if (o.role === 'patient') body = renderSharing(view, access) + renderPatient(view, visitsOf(rows, sections), messages, replies);
        else if (o.role === 'carer') body = renderCaring(access) + renderPatient(view, visitsOf(rows, sections), messages, replies);
        else body = renderEmergencies(access) + renderResults(view, tasks) + renderUndelivered(tasks) +
            renderMessages(view, messages, tasks, replies, outbox) + renderStaff(view, visitsOf(rows, sections));
        if (o.alone && !screen && !stats) body += renderLog(rows, sections);
        const banner = view.banner ? `<p class="banner">${esc(view.banner)}</p>` : '';
        const note = view.note ? `<p class="note ${view.note.kind}">${esc(view.note.message)}</p>` : '';
        const focused = view.element.contains(document.activeElement) ? document.activeElement : null;
        const scroll = view.element.scrollTop, typing = focused?.name === 'message' ? [focused.selectionStart, focused.selectionEnd] : null,
            replyTyping = focused?.name === 'reply' ? [focused.selectionStart, focused.selectionEnd] : null;
        view.element.innerHTML = banner + note + (screen || stats ? '' : renderWhy(view)) + body;   // (why: first, folded until asked; a manager's, with their schedule)
        view.element.scrollTop = scroll;
        const input = view.element.querySelector('input[name=message]');
        if (input) {
            input.value = view.draft;
            if (typing) {                                          // (the person was typing: still are)
                input.focus();
                input.setSelectionRange(...typing);
            }
        }
        const reply = view.element.querySelector('input[name=reply]');
        if (reply) {
            reply.value = view.replies.get(Number(reply.dataset.thread)) ?? '';
            if (replyTyping) {
                reply.focus();
                reply.setSelectionRange(...replyTyping);
            }
        }
        if (view.meta) {
            const live = view.banner ? 'stale' : 'live';
            const what = {patient: 'to acknowledge', carer: 'to acknowledge', clinician: 'to review'}[o.role] ?? 'unread';
            view.meta.innerHTML = `<span class="dot ${live}" title="${live}"></span>${esc(view.binding.topic ?? '')} · ` +
                (screen ? plural(rows.length, 'ticket') : stats ? `${plural(new Set(stats.hours.map(h => h.site)).size, 'site')}, ` +
                    `${plural(stats.hours.length, 'hour')}, counts and means only` : `${plural(new Set(rows.map(r => r.booking)).size, 'visit')}, ` +
                    `${plural(rows.length, 'activity', 'activities')}, ${plural(sections.length, 'section')}` +
                    (badge > 0 ? ` · <span class="badge" title="your status row: what's open">${badge} ${what}</span>` : ''));
        }
    }

    /**
     * The visits in the order shown: the latest activity first, or — the pointer over the feed — as last shown, those
     * new since at the end.
     */
    function ordered(view, visits) {
        if (view.element.dataset.hovered && view.order.length) {
            const at = new Map(view.order.map((booking, i) => [booking, i]));
            visits = [...visits].sort((a, b) => (at.get(a.booking) ?? Infinity) - (at.get(b.booking) ?? Infinity));
        }
        view.order = visits.map(v => v.booking);
        return visits;
    }

    /** Which visits changed since last shown (their latest activity): those flash. */
    function changed(view, visit) {
        const was = view.seen.get(visit.booking);
        view.seen.set(visit.booking, visit.latest.seq);
        return was !== undefined && was !== visit.latest.seq;
    }

    /**
     * Patients' messages, the latest first: each with who read it first and how many at reception. Reception's own
     * unread ones (their tasks) are marked, and reported read once shown.
     */
    function renderMessages(view, messages, tasks, replies, outbox) {
        if (messages.length === 0) return '';
        const o = view.options, listed = messages.slice(0, o.alone ? ALONE_MESSAGES : BOARD_MESSAGES);
        const thread = m => replies.filter(r => r.booking === m.seq).map(r => `<div class="reply-line">↳ The practice, ` +
            `${time.format(r.at)}: “${shown(r.note, r.noteDisclosed, 'the reply')}”${outbox.has(r.seq)
                ? ` — <span class="${outbox.get(r.seq).devices > 0 ? 'read' : 'unread'}">${esc(outbox.get(r.seq).status)}</span>` : ''}</div>`).join('');
        const form = m => o.role !== 'reception' ? ''
            : view.replying === m.seq
                ? `<form class="write reply"><input name="reply" data-thread="${m.seq}" maxlength="100" autocomplete="off" ` +
                `placeholder="Reply to ${esc(m.name || 'the patient')} (one line)"><button class="act small" data-act="reply" ` +
                `data-id="${m.seq}" data-patient="${esc(m.name || 'the patient')}" data-patient-id="${m.patient}"${view.busy.has(m.seq) ? ' disabled' : ''}>Send</button></form>`
                : ` <button class="act small" data-reply="${m.seq}" type="button">Reply</button>`;
        const lines = listed.map(m => `<li class="${tasks.has(m.seq) ? 'task' : ''}"><span class="time">${time.format(m.at)}</span> ` +
            `${m.patientDisclosed === 'full' ? `<strong>${esc(m.name)}</strong>` : shown('', m.patientDisclosed, 'the patient', 'a patient')}: ` +
            `${shown(m.note, m.noteDisclosed, 'the message')} — ${tasks.has(m.seq) ? '<span class="unread">New to you</span>' : readOf(m, false)}` +
            `${view.replying === m.seq ? '' : form(m)}${thread(m)}${view.replying === m.seq ? form(m) : ''}</li>`);
        if (o.role === 'reception') report(view, listed.filter(m => tasks.has(m.seq)).map(m => m.seq));
        return `<ul class="messages"><li class="head">Messages from patients</li>${lines.join('')}</ul>`;
    }

    /** Reception's tasks for the practice's messages that weren't delivered by their deadline: each desk's to follow up. */
    function renderUndelivered(tasks) {
        const undelivered = [...tasks.values()].filter(t => t.kind === 'replied');
        return undelivered.length === 0 ? '' : `<ul class="messages"><li class="head">Messages not delivered</li>${undelivered
            .map(t => `<li class="task">To <strong>${esc(t.name)}</strong>: ${esc(t.reason)}</li>`).join('')}</ul>`;
    }

    /** A carer's designations: whom they care for, in what scope. */
    function renderCaring(access) {
        const lines = access.map(d => `<li>You care for <strong>${esc(d.name)}</strong> (${esc(d.label)})</li>`);
        return lines.length ? `<ul class="messages"><li class="head">Whom you care for</li>${lines.join('')}</ul>`
            : '<p class="empty">You care for nobody now: no designation stands.</p>';
    }

    /** A GP's results to review and release: their tasks. */
    function renderResults(view, tasks) {
        const results = [...tasks.entries()].filter(([, t]) => t.kind === 'result').sort((a, b) => b[1].at - a[1].at);
        if (results.length === 0) return '';
        const shown = results.slice(0, view.options.alone ? ALONE_MESSAGES : BOARD_MESSAGES);
        const more = results.length > shown.length ? `<li class="head">…and ${results.length - shown.length} more</li>` : '';
        const lines = shown.map(([seq, t]) => `<li class="task"><span class="time">${time.format(t.at)}</span> ` +
            `<strong>${esc(t.name)}</strong>: ${esc(t.reason)} — ${esc(t.detail)} ` +
            `<button class="act small" data-act="release" data-id="${seq}" data-patient="${esc(t.name)}"${view.busy.has(seq) ? ' disabled' : ''}>` +
            'Release to patient</button></li>');
        return `<ul class="messages"><li class="head">Results to review</li>${lines.join('')}${more}</ul>`;
    }

    /** A patient's consents and carers: who reads what, since when, each to withdraw; their tags, each to share with a GP. */
    function renderSharing(view, access) {
        const o = view.options;
        const busy = id => view.busy.has(id) ? ' disabled' : '';
        const carers = access.filter(c => c.carer), consents = access.filter(c => !c.carer);
        const cares = carers.map(c => `<li><strong>${esc(c.name)}</strong>, your carer (${esc(c.label)}) ` +
            `<button class="act small" data-act="revokecarer" data-id="${c.patient}"${busy(c.patient)}>Revoke</button></li>`);
        const lines = consents.map(c => `<li><strong>${esc(o.names[c.gp] ?? `GP ${c.gp}`)}</strong> reads ` +
            `${c.everything ? 'everything (sensitive problems named)' : `“${esc(c.label)}” (${esc(c.detail)})`} · ` +
            `${c.at ? `since ${time.format(c.at)}` : 'since registering'} ` +
            `<button class="act small" data-act="withdraw" data-id="${c.seq}"${busy(c.seq)}>Withdraw</button></li>`);
        const offers = [];
        for (const tag of o.tags ?? [])
            for (const gp of o.gps ?? [])
                if (!access.some(c => c.gp === gp.id && (c.everything || c.label === tag)))
                    offers.push(`<button class="act small" data-act="share" data-id="${gp.id}" data-tag="${esc(tag)}"${busy(`${gp.id}:${tag}`)}>` +
                        `Share “${esc(tag)}” with ${esc(gp.name)}</button>`);
        return `<details class="sharing" open><summary>Who reads what of your record</summary><ul>${lines.join('') ||
            '<li>Nobody but the GPs who see you</li>'}${cares.join('')}</ul>${offers.length ? `<div class="offers">${offers.join(' ')}</div>` : ''}</details>`;
    }

    /** A GP's emergency access, while it lasts: whose record, until when. */
    function renderEmergencies(access) {
        return access.map(a => `<p class="banner emergency">Emergency access to ${esc(a.name)}'s whole record, until ` +
            `${time.format(a.at)}: the reason recorded</p>`).join('');
    }

    function renderStaff(view, visits) {
        visits = ordered(view, visits);
        const o = view.options, limit = o.alone ? ALONE_VISITS : BOARD_VISITS;
        const lines = visits.slice(0, limit).map(visit => {
            const r = visit.latest, first = visit.rows[0];
            const who = r.patientDisclosed === 'full'
                ? `${esc(r.name)} <span class="born">${r.born || ''}${esc(r.sex)}</span>${basisOf(r)}`
                : shown('', r.patientDisclosed, 'the patient', 'a patient');
            const why = reasonsOf(visit, o.role);
            const found = detailOf(view, visit);
            const detail = (found ? `<div class="detail">${found}</div>` : '') + resultsOf(visit) + reminderOf(visit, false);
            const by = r.clinician === o.person ? 'you' : esc(o.names[r.clinician] ?? `clinician ${r.clinician}`);
            let button = '';
            const now = visit.step;
            if (view.busy.has(visit.booking)) button = '<button class="act" disabled>…</button>';
            else if (o.role === 'reception' && now.kind === 'booked' && r.site === o.site)
                button = `<button class="act" data-act="arrive" data-id="${visit.booking}" data-patient="${esc(r.name || 'the patient')}">Check in</button>`;
            else if (o.role === 'clinician' && now.kind === 'arrived' && r.clinician === o.person)
                button = `<button class="act" data-act="call" data-id="${visit.booking}" data-patient="${esc(r.name || 'the patient')}" title="to ${esc(o.room ?? '')}">Call in</button>`;
            else if (o.role === 'clinician' && visit.sections.length === 0 && r.patient)
                button = `<button class="act glass" data-act="breakglass" data-id="${r.patient}" data-patient="${esc(r.name || 'the patient')}" ` +
                    `title="Emergency access to this patient's whole record, for a while: the reason recorded">Break glass</button>`;
            const action = button ? `<div class="action">${button}</div>` : '';
            return `<tr class="${changed(view, visit) ? 'changed' : ''}" title="${visitTitle(visit, first)}">` +
                `<td class="time">${time.format(r.slot)}</td><td class="who">${who}</td>` +
                `<td class="why">${why}${detail}<div class="by">with ${by}</div></td>` +
                `<td class="state">${stepOf(visit)}${action}</td></tr>`;
        });
        const more = visits.length > limit ? `<p class="more">…and ${visits.length - limit} earlier visits</p>` : '';
        return visits.length === 0 ? '<p class="empty">No activity yet.</p>'
            : `<table class="visits"><tbody>${lines.join('')}</tbody></table>${more}`;
    }

    function renderPatient(view, visits, messages, replies) {
        visits = ordered(view, visits);
        const o = view.options;
        report(view, replies.map(r => r.seq));                      // (the practice's messages, in front of them)
        const form = o.role !== 'patient' ? '' : `<form class="write"><input name="message" maxlength="100" placeholder="Write to the practice (one line)" autocomplete="off">` +
            `<button class="act" data-act="message"${view.busy.has('message') ? ' disabled' : ''}>Send</button></form>`;
        const notes = messages.slice(0, o.alone ? ALONE_MESSAGES : BOARD_MESSAGES)
            .map(m => `<li><span class="time">${time.format(m.at)}</span> ${o.role === 'carer' ? `${esc(m.name)} wrote` : 'You wrote'}: “${esc(m.note)}” — ${readOf(m, true)}` +
                replies.filter(r => r.booking === m.seq).map(r => `<div class="reply-line">↳ Northgate Practice, ${time.format(r.at)}: “${esc(r.note)}”</div>`).join('') + '</li>');
        const written = notes.length ? `<ul class="messages mine-messages">${notes.join('')}</ul>` : '';
        if (visits.length === 0) return form + written + '<p class="empty">No visits yet.</p>';
        report(view, visits.flatMap(v => [v.step.seq, ...(v.reminder ? [v.reminder.seq] : [])]));
        const lines = visits.map(visit => {
            const r = visit.step;
            const step = {
                booked: 'booked',
                arrived: `checked in${visit.ticket ? `, ticket ${visit.ticket}` : ''}: please wait`,
                called: `called in to ${esc(visit.room)}`,
                seen: 'seen'
            }[r.kind] ?? r.kind;
            const found = visit.sections.filter(s => s.detail && s.detailDisclosed === 'full').map(s => noteOf(view, s)).join(' · ');
            const detail = r.kind === 'seen' && found ? `<div class="detail">${found}</div>` : '';
            const carer = o.role === 'carer', whose = carer ? `${esc(r.name)}'s reminder` : 'Your reminder';
            const reminder = visit.reminder && !visit.reminder.acked && r.kind === 'booked'
                ? `<div class="ack">${whose}: ${view.busy.has(visit.reminder.seq) ? '<button class="act" disabled>…</button>'
                    : `<button class="act" data-act="acknowledge" data-id="${visit.reminder.seq}">Acknowledge${carer ? ` for ${esc(r.name.split(' ')[0])}` : ''}</button>`}</div>`
                : reminderOf(visit, !carer);
            return `<li class="${changed(view, visit) ? 'changed' : ''}" title="${visitTitle(visit, visit.rows[0])}"><span class="time">${time.format(r.slot)}</span> ` +
                `${carer ? `<strong>${esc(r.name)}</strong> ` : ''}with ${esc(o.names[r.clinician] ?? `clinician ${r.clinician}`)} · ` +
                `${reasonsOf(visit, o.role)} — <strong>${step}</strong>${detail}${resultsOf(visit)}${reminder}</li>`;
        });
        return form + written + `<ul class="mine">${lines.join('')}</ul>`;
    }

    function renderScreen(rows) {
        const called = rows.filter(r => r.kind === 'called').sort((a, b) => b.at - a.at);
        const waiting = rows.filter(r => r.kind === 'arrived').sort((a, b) => a.ticket - b.ticket);
        const tickets = called.map(r => `<div class="ticket called"><span>Ticket ${r.ticket}</span><span>→ ${esc(r.room)}</span></div>`)
            .concat(waiting.map(r => `<div class="ticket waiting"><span>Ticket ${r.ticket}</span><span>please wait</span></div>`));
        return tickets.length === 0 ? '<p class="empty">Nobody waiting.</p>' : `<div class="tickets">${tickets.join('')}</div>`;
    }

    /**
     * A manager's statistics, a site at a time: its latest hours (who was checked in, called in and seen, and the mean
     * wait), and its visits by reason over the full hours counted, a count under the floor withheld (as the policy has it).
     */
    function renderStats(view, {hours, byReason}) {
        const o = view.options, limit = o.alone ? ALONE_HOURS : BOARD_HOURS;
        const sites = [...new Set([...hours.map(h => h.site), ...byReason.map(r => r.site)])].sort((a, b) => a - b);
        if (sites.length === 0) return '<p class="empty">No figures yet.</p>';
        const upTo = new Date(Math.floor(Date.now() / 3600000) * 3600000);
        return sites.map(site => {
            const latest = hours.filter(h => h.site === site).sort((a, b) => b.at - a.at).slice(0, limit);
            const lines = latest.map(h => `<tr><td class="time">${time.format(h.at)}</td><td>${h.arrivals}</td><td>${h.called}</td>` +
                `<td>${h.seen}</td><td>${h.wait < 0 ? '—' : `${(h.wait / 60).toFixed(1)} min`}</td></tr>`);
            const by = byReason.find(r => r.site === site);
            const counts = (by?.counts ?? []).map(c => c.disclosed === 'aggregate'
                ? `<li>${esc(c.name)} <strong>${c.count}</strong></li>`
                : `<li><span class="withheld" title="${esc(`${c.disclosed.toUpperCase()}: fewer than five visits, so the count is withheld`)}">` +
                `${esc(c.name)} &lt;5</span></li>`);
            const notices = [['phones', 'Phone lines down (urgent)'], ['hours', 'Opening hours changing'], ['flu', 'Flu vaccinations']]
                .map(([notice, label]) => `<button class="act small" data-act="notice" data-id="${site}" data-tag="${notice}"` +
                    `${view.busy.has(`${site}:${notice}`) ? ' disabled' : ''}>${esc(label)}</button>`).join(' ');
            return `<section class="stats"><h4>${esc(o.sites?.[site] ?? `Site ${site}`)}</h4>` +
                '<table class="hours"><thead><tr><th>hour</th><th>checked in</th><th>called in</th><th>seen</th><th>mean wait</th></tr></thead>' +
                `<tbody>${lines.join('')}</tbody></table>` +
                (by ? `<p class="counted">Visits by reason, booked ${time.format(by.since)}–${time.format(upTo)} (full hours)</p>` +
                    `<ul class="reasons">${counts.join('')}</ul>` : '') +
                `<p class="counted">A notice to the site's patients, as each one's limits allow:</p><div class="offers">${notices}</div></section>`;
        }).join('');
    }

    /** A person's page: every row as it was sent, newest first, each with what it disclosed: activities, then sections. */
    function renderLog(rows, sections) {
        const flag = d => `<span class="flag ${d}" title="${d.toUpperCase()}">${d[0].toUpperCase()}</span>`;
        const latest = [...rows].sort((a, b) => b.seq - a.seq).slice(0, LOG_ROWS);
        const acks = r => [r.acked ? `acknowledged ${time.format(r.acked)}${r.ackedBy ? ` by ${esc(r.ackedBy)}` : ''}` : '',
            r.read ? `read ${time.format(r.read)}${r.readBy ? ` by ${esc(r.readBy)}` : ''} (${r.readers} at reception)` : '']
            .filter(Boolean).join('; ');
        const lines = latest.map(r => `<tr><td class="time">${time.format(r.at)}</td><td>${r.seq}</td><td>${r.kind}</td>` +
            `<td>${r.booking}</td><td>${flag(r.patientDisclosed)} ${r.patientDisclosed === 'full' ? esc(r.name) : ''}</td>` +
            `<td>${flag(r.noteDisclosed)} ${esc(r.note)}</td><td>${r.acked || r.read ? `${flag(r.staffDisclosed)} ${acks(r)}` : ''}</td></tr>`);
        const clinical = [...sections].sort((a, b) => b.seq - a.seq).slice(0, LOG_ROWS);
        const notes = clinical.map(s => `<tr><td class="time">${time.format(s.at)}</td><td>${s.seq}</td><td>${s.booking}</td>` +
            `<td>${s.problem}</td><td>${s.attending}</td><td>${flag(s.reasonDisclosed)} ${esc(s.reason)}</td>` +
            `<td>${flag(s.detailDisclosed)} ${esc(s.detail)}</td></tr>`);
        return `<details class="log" open><summary>Every activity, as it was sent (the latest ${latest.length})</summary>` +
            '<table class="log"><thead><tr><th>at</th><th>seq</th><th>kind</th><th>booking</th><th>patient</th><th>note</th><th>acknowledgements</th></tr></thead>' +
            `<tbody>${lines.join('')}</tbody></table></details>` +
            `<details class="log" open><summary>Every clinical section, as it was sent (the latest ${clinical.length})</summary>` +
            '<table class="log"><thead><tr><th>written</th><th>section</th><th>booking</th><th>problem</th><th>attended by</th><th>reason</th><th>detail</th></tr></thead>' +
            `<tbody>${notes.join('')}</tbody></table></details>`;
    }

    /**
     * Signs this page's connection in to `realm` as `user` by passthrough — the id taken as given: this demo's — unless
     * it's signed in as them already. Resolves { accepted, reason }.
     */
    async function signIn(realm, user) {
        if (tapline.identity?.realm === realm && tapline.identity?.userId === user) return {accepted: true, reason: null};
        const r = await tapline.signIn(realm, 'passthrough', user);
        return {accepted: r.accepted, reason: r.reason};
    }

    /** In a frame on the board: the page's margins go. */
    function embedded() {
        document.body.classList.add('embed');
    }

    return {attach, detach, move, signIn, embedded};
}
