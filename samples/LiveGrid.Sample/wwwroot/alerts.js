// The page's alerts in the browser (Components/AlertsBar.razor): its rules kept in local settings, and its alerts
// presented as Tapline delivers them — Tapline's AlertInbox (tapline-alerts.js) subscribes to the audience's inbox and to
// each rule's shape at its throttle, and sounds, marks and notifies each rule's alerts past what its audience presented;
// this says each back to the page. A browser makes sound and asks leave to notify only from a click itself, so those are
// on window.liveAlerts, for the page's onclick attributes.

const NAME = 'livegrid-alerts';
let alerts = null;   // tapline-alerts.js, as the page imports it (by its fingerprinted address)
let sounds = null;
let inbox = null;
let page = null;

/** The browser's audience (made once, kept), its rules, and whether it notifies (and may). */
export async function load(alertsUrl) {
    alerts ??= await import(alertsUrl);
    const saved = alerts.loadRules(NAME);
    return {
        audience: saved.audience,
        rules: saved.rules,
        notify: saved.notify && 'Notification' in window && Notification.permission === 'granted'
    };
}

/** The page's alerts, presented: the inbox and the shapes' bindings, the sounds by id (their addresses). */
export function start(dotnet, {inbox: binding, shapes, since, sounds: urls, notify}) {
    page = dotnet;
    sounds ??= new alerts.AlertSounds(sound => urls[sound]);
    inbox?.dispose();
    inbox = new alerts.AlertInbox({
        inbox: binding,
        shapes,
        since,
        notify,
        soundOn: sounds.unlocked,
        play: sound => sounds.play(sound).catch(failure => console.warn("an alert's sound couldn't be played", failure)),
        alert: a => page?.invokeMethodAsync('Alerted', a.rule, a.source, a.count, a.fired),
        away: list => page?.invokeMethodAsync('Away', list.map(a => ({name: a.name, count: a.count, summary: a.summary}))),
        error: failure => console.warn('an alert subscription ended', failure)
    });
}

/** The rules' shapes as they are now (rules changed): those new subscribed, those gone let go. */
export function setShapes(shapes) {
    inbox?.setShapes(shapes);
}

/** The rules kept in the browser's local settings (its audience's id kept with them). */
export function saveRules(rules) {
    alerts?.saveRules(rules, NAME);
}

export function stop() {
    inbox?.dispose();
    inbox = null;
    page = null;
}

window.liveAlerts = {
    // From the click itself: a browser lets a page make sound only once someone has interacted with it.
    enableSound() {
        if (!sounds) return;
        sounds.unlock().then(() => {
            if (inbox) inbox.soundOn = true;
            page?.invokeMethodAsync('SoundEnabled', true);
        });
    },

    // From the click itself too: a browser asks for leave to notify only when someone has asked.
    setNotify(on) {
        if (alerts) alerts.saveRules(alerts.loadRules(NAME).rules, NAME, null, {notify: on});
        const told = allowed => {
            if (inbox) inbox.notify = on && allowed;
            page?.invokeMethodAsync('NotifyAllowed', on && allowed);
        };
        if (on && 'Notification' in window && Notification.permission === 'default')
            Notification.requestPermission().then(answer => told(answer === 'granted'));
        else told('Notification' in window && Notification.permission === 'granted');
    },

    play(sound) {
        if (!sounds?.unlocked) return false;
        sounds.play(sound).catch(failure => console.warn("an alert's sound couldn't be played", failure));
        return true;
    }
};
