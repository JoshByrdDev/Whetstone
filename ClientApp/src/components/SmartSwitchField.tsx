import { useEffect, useState } from 'react';

/**
 * The smart switch: an ordered list of Claude accounts, each with how full it may get before the
 * next one takes over. The server applies it after every usage poll (ClaudeAccountSwitcher) - the
 * first account on the list under both of its limits is the one new runs start on. Like the header's
 * picker, a switch never touches a run already out.
 */

export interface SmartSwitchRung { account: string; weekly: number; session: number }
export interface SmartSwitchSetting { enabled: boolean; chain: SmartSwitchRung[] }

interface AccountSeen {
  name: string;
  signedIn: boolean;
  known: boolean;
  fetchedAt: string | null;
  session: number;
  weekly: number;
  blockedUntil: string | null;
  over: string | null;
}

interface Seen {
  active: string;
  smartSwitch: boolean;
  lastSwitch: { from: string; to: string; reason: string; at: string; manual: boolean } | null;
  accounts: AccountSeen[];
}

const WEEKLY = 95;
const SESSION = 90;

const input =
  'w-16 bg-gray-900 border border-hair rounded px-2 py-1 text-sm text-gray-200 focus:outline-none focus:border-gray-500';
const small = 'orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800 disabled:opacity-40';

function when(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' });
}

export function SmartSwitchField({ value, accounts, onChange, dirty }: {
  value: SmartSwitchSetting;
  /** Every account name on offer, Default first - including ones typed but not saved yet. */
  accounts: string[];
  onChange: (v: SmartSwitchSetting) => void;
  /** Unsaved edits on the form: the live numbers below describe what is saved, not what is typed. */
  dirty: boolean;
}) {
  const [seen, setSeen] = useState<Seen | null>(null);

  useEffect(() => {
    let live = true;
    const load = () =>
      fetch('/api/usage/accounts')
        .then(r => (r.ok ? r.json() : null))
        .then(d => { if (live && d) setSeen(d); })
        .catch(() => { /* the numbers are a guide; the next tick is the retry */ });
    load();
    const t = setInterval(load, 60_000);
    return () => { live = false; clearInterval(t); };
  }, []);

  const names = accounts.filter(Boolean);
  const chain = value.chain;
  const unused = names.filter(n => !chain.some(r => r.account.toLowerCase() === n.toLowerCase()));

  const set = (chain: SmartSwitchRung[]) => onChange({ ...value, chain });
  const patch = (i: number, p: Partial<SmartSwitchRung>) => set(chain.map((r, j) => (j === i ? { ...r, ...p } : r)));
  const move = (i: number, by: number) => {
    const next = [...chain];
    const [r] = next.splice(i, 1);
    next.splice(i + by, 0, r);
    set(next);
  };

  const toggle = (enabled: boolean) => {
    // Switched on with nothing listed: every account, in the order they were added, at the usual limits.
    const filled = enabled && chain.length === 0
      ? names.map(account => ({ account, weekly: WEEKLY, session: SESSION }))
      : chain;
    onChange({ enabled, chain: filled });
  };

  if (names.length < 2) {
    return (
      <p className="text-xs text-gray-500">
        Add a second account above and save, and this can move the fleet onto it by itself as the first one fills.
      </p>
    );
  }

  return (
    <div className="space-y-3">
      <label className="flex items-start gap-2.5 group cursor-pointer">
        <input
          type="checkbox"
          checked={value.enabled}
          onChange={e => toggle(e.target.checked)}
          className="mt-[3px] w-3.5 h-3.5 flex-none rounded accent-blue-500 cursor-pointer"
        />
        <span className="min-w-0">
          <span className="block text-sm text-gray-300 group-hover:text-gray-100 transition-colors">Switch accounts by usage</span>
          <span className="block text-xs text-gray-500 mt-1">
            New runs start on the first account below that is under both of its limits, checked every minute. When one
            fills, the next takes over; when an earlier one's window resets, it comes back. Runs already going finish
            on the account they started with - nothing is stopped. Picking an account by hand in the header turns this off.
          </span>
        </span>
      </label>

      <div className={`space-y-2 ${value.enabled ? '' : 'opacity-50'}`}>
        {chain.map((r, i) => {
          const s = seen?.accounts.find(a => a.name.toLowerCase() === r.account.toLowerCase());
          const active = seen?.active.toLowerCase() === r.account.toLowerCase();
          return (
            <div key={`${r.account}-${i}`} className="rounded border border-hair px-3 py-2 space-y-1.5">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
                <span className="text-xs text-gray-500 w-4">{i + 1}.</span>
                <select
                  aria-label="Account"
                  value={r.account}
                  onChange={e => patch(i, { account: e.target.value })}
                  className="bg-gray-900 border border-hair rounded px-2 py-1 text-sm text-gray-200 focus:outline-none focus:border-gray-500 min-w-0 max-w-[160px]"
                >
                  {[r.account, ...unused].map(n => <option key={n} value={n}>{n}</option>)}
                </select>
                <label className="flex items-center gap-1.5 text-xs text-gray-400">
                  until weekly
                  <input
                    type="number" min={1} max={100} aria-label="Weekly limit percent" value={r.weekly}
                    onChange={e => patch(i, { weekly: Number(e.target.value) || 0 })} className={input}
                  />%
                </label>
                <label className="flex items-center gap-1.5 text-xs text-gray-400">
                  or session
                  <input
                    type="number" min={1} max={100} aria-label="Session limit percent" value={r.session}
                    onChange={e => patch(i, { session: Number(e.target.value) || 0 })} className={input}
                  />%
                </label>
                <span className="flex items-center gap-1 ml-auto">
                  <button type="button" className={small} disabled={i === 0} onClick={() => move(i, -1)} title="Earlier in the list" aria-label="Move up">↑</button>
                  <button type="button" className={small} disabled={i === chain.length - 1} onClick={() => move(i, 1)} title="Later in the list" aria-label="Move down">↓</button>
                  <button type="button" className={small} onClick={() => set(chain.filter((_, j) => j !== i))} title="Take it off the list">Remove</button>
                </span>
              </div>
              <AccountLine seen={s} active={active} dirty={dirty} />
            </div>
          );
        })}

        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            className={small}
            disabled={unused.length === 0}
            onClick={() => set([...chain, { account: unused[0], weekly: WEEKLY, session: SESSION }])}
          >
            Add to list
          </button>
          <span className="text-xs text-gray-600">
            When every account is at its limit, the fleet stays where it is and waits for a reset as it does today.
          </span>
        </div>
      </div>

      {seen?.lastSwitch && (
        <p className="text-xs text-gray-500">
          Last switch: {seen.lastSwitch.from} → {seen.lastSwitch.to}, {when(seen.lastSwitch.at)}
          {seen.lastSwitch.manual ? ' (by hand)' : ` - ${seen.lastSwitch.reason}`}
        </p>
      )}
    </div>
  );
}

/** What the switch currently sees for one account: how full, whether it is the one in use, why not. */
function AccountLine({ seen, active, dirty }: { seen: AccountSeen | undefined; active: boolean; dirty: boolean }) {
  if (!seen) return <p className="text-[11px] text-gray-600">{dirty ? 'Save to start tracking this account.' : 'No numbers yet.'}</p>;
  if (!seen.signedIn) return <p className="text-[11px] text-amber-400">Not signed in yet - skipped until it is.</p>;
  const numbers = seen.known
    ? `session ${seen.session}% · weekly ${seen.weekly}%${seen.fetchedAt ? `, read ${when(seen.fetchedAt)}` : ''}`
    : 'no reading yet';
  return (
    <p className="text-[11px] text-gray-500 flex flex-wrap gap-x-2">
      {active && <span className="text-emerald-400">in use</span>}
      <span>{numbers}</span>
      {seen.over && <span className="text-amber-400">- {seen.over}</span>}
    </p>
  );
}
