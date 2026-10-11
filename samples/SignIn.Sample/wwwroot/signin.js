// /signin: passkeys and passwords (OPAQUE) through Tapline's sign-in — the page's side. The form isn't bound to Blazor:
// what's typed stays here; the password is stretched here (Argon2id) and proved by OPAQUE, never sent. `passwords` is
// Tapline.Authentication.Opaque's tapline-opaque.mjs.

const REALM = 'demo';

export function createSignIn(tapline, passwords) {
    let members = null;                                            // the members subscription shown: its tap id
    let log = null;
    const say = line => {
        if (log) log.textContent = `${new Date().toLocaleTimeString()}  ${line}\n` + log.textContent;
    };
    tapline.onIdentity = identity => say(identity
        ? `the router says: ${identity.userId}, by ${identity.module}${identity.insecure ? ' (insecure)' : ''}`
        : 'the router says: anonymous');
    tapline.onSessionExpiring = notice => say(`the session lapses at ${notice.expiresAt?.toLocaleTimeString()} (renewal: ${notice.renewal})`);

    return {
        attach(form) {
            const user = form.querySelector('[name=user]'), password = form.querySelector('[name=password]');
            log = form.querySelector('.log');
            const name = () => user.value.trim() || null;
            const run = (what, act) => async () => {
                say(`${what}…`);
                try {
                    const r = await act();
                    say(r.accepted
                        ? `${what}: signed in as ${r.identity.userId} (${r.identity.module})`
                        : `${what}: ${r.reason}`);
                    if (r.accepted) password.value = '';
                } catch (e) {
                    say(`${what}: ${e?.name === 'NotAllowedError' ? 'cancelled, or not allowed by the browser' : e?.message ?? e}`);
                }
            };
            const actions = {
                'passkey': run('sign in with a passkey', () => tapline.signInWithPasskey(REALM, {userId: name()})),
                'passkey-register': run('create a passkey', () => tapline.registerPasskey(REALM, {enrollment: name()})),
                'password': run('sign in with the password', () => {
                    if (!name() || !password.value) return {accepted: false, reason: 'a name and a password, please'};
                    return passwords.signInWithPassword(tapline, REALM, name(), password.value);
                }),
                'password-register': run('set the password', () => {
                    if (!password.value) return {accepted: false, reason: 'a password, please'};
                    return passwords.registerPassword(tapline, REALM, password.value, {enrollment: name()});
                }),
            };
            for (const button of form.querySelectorAll('button[data-action]'))
                button.addEventListener('click', actions[button.dataset.action]);
            form.addEventListener('submit', e => e.preventDefault());
        },
        attachMembers(binding, el) {                               // a greeting, while the subscription lasts
            if (members !== null) tapline.drop(members);
            members = binding.tapId;
            tapline.subscribe(binding,
                value => el.textContent = new TextDecoder().decode(value),
                error => {
                    el.textContent = `ended (code ${error.code}): sign in and subscribe again`;
                    if (members === binding.tapId) members = null;
                });
        },
        signedOut() {                                              // the app signed this connection out
            tapline.identity = null;
            tapline.ticket = null;
            say('signed out');
        },
    };
}
