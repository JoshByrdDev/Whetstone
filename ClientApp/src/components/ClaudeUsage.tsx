import { useEffect, useState } from 'react';
import { formatWakeTime } from '../hooks/useTabStatus';

/**
 * How much of the Claude subscription's rate limit is spent, and when it comes back.
 *
 * This is the fleet's real budget. The Metrics tab counts what the pipeline burned; this counts what
 * is left, which is the number that decides whether to set three more agents going or wait for the
 * window to roll. It is account-wide, so the human's own sessions are in it too.
 *
 * Rendered twice from one poll: a chip in the app bar (always visible, one line) and a full set of
 * meters on the Metrics tab. Both read the dashboard's cached copy - no browser ever calls Anthropic.
 */

interface UsageLimit {
  kind: string;
  group: string;
  label: string;
  percent: number;
  severity: string;
  resetsAt: string | null;
  isActive: boolean;
}

interface Usage {
  status: string;
  message: string | null;
  fetchedAt: string | null;
  /** Whose numbers these are, and every login the fleet could be switched to. */
  account: string;
  accounts: string[];
  limits: UsageLimit[];
}

/** Matches the server's poll interval - a faster one would only re-read the same cached snapshot. */
const POLL_MS = 60_000;

function useClaudeUsage() {
  const [usage, setUsage] = useState<Usage | null>(null);
  const [nudging, setNudging] = useState(false);
  const [switching, setSwitching] = useState(false);

  /**
   * Point the fleet at another Claude login. The server polls the new account before it answers, so
   * the snapshot read straight afterwards is already that account's - never the old numbers under
   * the new name.
   */
  const switchAccount = async (name: string) => {
    setSwitching(true);
    try {
      await fetch('/api/usage/account', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }),
      });
      const fresh = await fetch('/api/usage');
      if (fresh.ok) setUsage(await fresh.json());
    } catch { /* the picker snaps back to the account still in force, which is the truth */ }
    finally { setSwitching(false); }
  };

  useEffect(() => {
    let active = true;
    const load = () =>
      fetch('/api/usage')
        .then(r => (r.ok ? r.json() : null))
        .then(d => { if (active) setUsage(d); })
        .catch(() => { /* the chip keeps its last value; the next tick is the retry */ });

    load();
    const timer = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(timer); };
  }, []);

  /**
   * Ask the server to run the CLI once so it renews the login this meter reads. Slow by nature —
   * it is a whole process start — so the button says so rather than looking broken for ten seconds.
   */
  const nudge = async () => {
    setNudging(true);
    try {
      const r = await fetch('/api/usage/nudge', { method: 'POST' });
      if (r.ok) {
        const d = await r.json();
        setUsage(u => (u ? { ...u, status: d.status, message: d.message } : u));
      }
      // Either way, re-read the real snapshot: the nudge response carries no limits.
      const fresh = await fetch('/api/usage');
      if (fresh.ok) setUsage(await fresh.json());
    } catch { /* leave the chip as it was; the message already says what is wrong */ }
    finally { setNudging(false); }
  };

  return { usage, nudge, nudging, switchAccount, switching };
}

/**
 * Which Claude login the fleet is spending. Drawn only when there is a second one to choose: with a
 * single account it would be a dropdown of one. A switch reaches the next run to start - agents
 * already out finish on the account they began with.
 */
function AccountPicker({ usage, onSwitch, busy }: {
  usage: Usage; onSwitch: (name: string) => void; busy: boolean;
}) {
  const accounts = usage.accounts ?? [];
  if (accounts.length < 2) return null;
  return (
    <select
      aria-label="Claude account"
      title="Which Claude login new agent runs spend. Runs already out finish on the one they started with."
      value={usage.account}
      disabled={busy}
      onChange={e => onSwitch(e.target.value)}
      className="orbit-tap bg-gray-900 border border-hair rounded px-1.5 py-0.5 text-xs text-gray-300 focus:outline-none focus:border-gray-500 disabled:opacity-50 max-sm:max-w-[120px]"
    >
      {accounts.map(a => <option key={a} value={a}>{a}</option>)}
    </select>
  );
}

/**
 * Whether running the CLI once could plausibly fix what the meter is reporting.
 *
 * Every status here means the same thing underneath: the login on disk is spent or missing, and a
 * `claude` process is what puts a live one back. `renew-blocked` is in the set precisely because the
 * dashboard's own renewal is the thing that failed - the CLI renews as a different caller and is not
 * subject to the backoff that produced it. Deliberately not `error` (the usage API misbehaving, which
 * a nudge cannot touch), `rate-limited` (the same, for the poll), or `forbidden` (a real token of the
 * wrong kind, which will still be the wrong kind afterwards).
 */
function nudgeable(status: string): boolean {
  return status === 'expired' || status === 'no-credentials' || status === 'renew-blocked';
}

/**
 * Colour by how close the window is to full, but let the API's own severity override it - the server
 * knows about warning bands we would otherwise have to guess at from the percentage alone.
 */
function tone(l: UsageLimit) {
  if (l.severity === 'critical' || l.percent >= 90) return { text: 'text-red-300', bar: 'bg-red-400' };
  if (l.severity === 'warning' || l.percent >= 70) return { text: 'text-amber-300', bar: 'bg-amber-400' };
  return { text: 'text-emerald-300', bar: 'bg-emerald-400' };
}

/** "resets 6:10pm" for today, "resets Thu 3:00pm" once it is far enough out to be ambiguous. */
function formatReset(iso: string | null): string | null {
  if (!iso) return null;
  const when = new Date(iso);
  if (Number.isNaN(when.getTime())) return null;
  const sameDay = when.toDateString() === new Date().toDateString();
  return sameDay
    ? formatWakeTime(when)
    : `${when.toLocaleDateString([], { weekday: 'short' })} ${formatWakeTime(when)}`;
}

/** The window the chip leads with: the fullest one, since that is the one that will stop the fleet. */
function leading(limits: UsageLimit[]): UsageLimit | null {
  if (limits.length === 0) return null;
  return limits.reduce((worst, l) => (l.percent > worst.percent ? l : worst));
}

function summary(usage: Usage): string {
  const lines = usage.limits.map(l => {
    const reset = formatReset(l.resetsAt);
    return `${l.label}: ${Math.round(l.percent)}%${reset ? ` - resets ${reset}` : ''}`;
  });
  if (usage.message) lines.push(usage.message);
  if (usage.fetchedAt) lines.push(`Updated ${formatWakeTime(new Date(usage.fetchedAt))}`);
  return lines.join('\n');
}

/**
 * The one control on the chip. Only appears when the meter is dark, because it is not a refresh —
 * it starts a real `claude` process to make the CLI renew the login, which takes seconds and spends
 * a little of the budget being measured.
 */
function NudgeButton({ onClick, busy }: { onClick: () => void; busy: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      title="Run `claude` once so it renews the login this meter reads"
      className="px-1.5 py-0.5 rounded border border-hair text-gray-400 hover:bg-gray-800 hover:text-gray-200 disabled:opacity-50"
    >
      {busy ? 'nudging…' : 'nudge'}
    </button>
  );
}

// ---------- Which meters to draw ------------------------------------------------

/** Which providers some role runs on - one meter each for Claude and Codex, none for Gemini. */
interface UsageProviders { claude: boolean; codex: boolean; gemini: boolean }

function useUsageProviders(): UsageProviders | null {
  const [providers, setProviders] = useState<UsageProviders | null>(null);
  useEffect(() => {
    let active = true;
    const load = () =>
      fetch('/api/usage/providers')
        .then(r => (r.ok ? r.json() : null))
        .then(d => { if (active && d) setProviders(d); })
        .catch(() => { /* keep the last answer; the next tick is the retry */ });
    load();
    const timer = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(timer); };
  }, []);
  return providers;
}

// ---------- Codex ----------------------------------------------------------------

/**
 * Codex's windows, as Codex last reported them. Not a live reading: Codex writes its limits down only
 * while a session runs, and the dashboard reads that report (see CodexUsageService). `fetchedAt` is
 * when the report was made, so the meter can say how old it is.
 */
function useCodexUsage(enabled: boolean): Usage | null {
  const [usage, setUsage] = useState<Usage | null>(null);
  useEffect(() => {
    if (!enabled) { setUsage(null); return; }
    let active = true;
    const load = () =>
      fetch('/api/usage/codex')
        .then(r => (r.ok ? r.json() : null))
        .then(d => { if (active) setUsage(d ? { ...d, account: '', accounts: [] } : null); })
        .catch(() => { /* keep the last value */ });
    load();
    const timer = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(timer); };
  }, [enabled]);
  return usage;
}

/** "as of 2:14pm", or "as of Thu 2:14pm" once it is not today's. */
function asOf(iso: string | null): string | null {
  const when = formatReset(iso);
  return when ? `as of ${when}` : null;
}

// ---------- The app bar ------------------------------------------------------------

/** The provider's name ahead of its meter - only when two meters sit side by side. */
function Tag({ children }: { children: string }) {
  return <span className="text-gray-600">{children}</span>;
}

/**
 * App-bar chip: the tightest window as a percent plus its reset, with every window in the tooltip.
 * Stays quiet when there is nothing useful to say - an unauthenticated or never-polled dashboard
 * renders nothing here rather than an error the user did not ask about.
 */
function ClaudeChip({ tag }: { tag: boolean }) {
  const { usage, nudge, nudging, switchAccount, switching } = useClaudeUsage();
  if (!usage) return null;
  // No role runs on Claude, and this meter only measures Claude's subscription.
  if (usage.status === 'unsupported') return null;

  // On every branch below, including the ones that otherwise say nothing: an account with no
  // numbers is exactly the one you need to be able to switch away from.
  const picker = <AccountPicker usage={usage} onSwitch={switchAccount} busy={switching} />;
  const label = tag ? <Tag>Claude</Tag> : null;

  const worst = leading(usage.limits);
  if (!worst) {
    // No numbers yet. Only worth a word if something is actually wrong and fixable. A forbidden token
    // is in that set too: it is the one failure a user can stare at for a long time without guessing
    // that the credential is fine and simply not allowed to read this. The detail is in the tooltip.
    if (usage.status === 'forbidden' || nudgeable(usage.status)) {
      return (
        <span className="flex items-center gap-1.5 text-xs text-gray-500" title={usage.message ?? ''}>
          {label}
          {picker}
          {/* Three different problems, so three different words. "usage expired" over a renewal the
              dashboard is merely waiting to retry would send the user looking for a dead login. */}
          {usage.status === 'forbidden'
            ? 'usage not permitted'
            : usage.status === 'renew-blocked'
              ? 'login stale'
              : 'usage expired'}
          {/* A forbidden token is the one case running the CLI cannot fix - it will still be the
              wrong KIND of credential afterwards - so the button is not offered there. */}
          {nudgeable(usage.status) && <NudgeButton onClick={nudge} busy={nudging} />}
        </span>
      );
    }
    return picker;
  }

  const t = tone(worst);
  const reset = formatReset(worst.resetsAt);
  // Anything other than a clean poll means the numbers on screen are the previous ones.
  const stale = usage.status !== 'ok';

  return (
    <span
      className="flex items-center gap-1.5 text-xs"
      title={summary(usage)}
    >
      {label}
      {/* The picker, the bar and the reset time stay at the desk. On a phone the percent is the
          whole glance - the bar it shares with a full fleet's pulse has no room for more - and the
          Metrics tab has every window spelled out. The picker still shows there whenever there
          are no numbers, which is when switching away is the point. */}
      <span className="max-sm:hidden">{picker}</span>
      <span className="h-1.5 w-10 rounded-full bg-gray-800 overflow-hidden max-sm:hidden">
        <span
          className={`block h-full rounded-full ${t.bar} ${stale ? 'opacity-50' : ''}`}
          style={{ width: `${Math.min(100, Math.max(2, worst.percent))}%` }}
        />
      </span>
      <span className={`font-medium ${stale ? 'text-gray-500' : t.text}`}>
        {Math.round(worst.percent)}%
      </span>
      {reset && <span className="text-gray-500 whitespace-nowrap max-sm:hidden">resets {reset}</span>}
      {stale && <span className="text-gray-600">·stale</span>}
      {/* The usual way to see a dead login is right here rather than on the empty chip above: a
          failed poll keeps the last good numbers, so the meter goes stale long before it goes
          blank. Offering the nudge only once every number is gone would be offering it last. */}
      {nudgeable(usage.status) && <NudgeButton onClick={nudge} busy={nudging} />}
    </span>
  );
}

/** Codex's chip: the same glance as Claude's, from Codex's last report, with its age in the tooltip. */
function CodexChip({ tag }: { tag: boolean }) {
  const usage = useCodexUsage(true);
  if (!usage || usage.status === 'unsupported') return null;
  const label = tag ? <Tag>Codex</Tag> : null;
  const worst = leading(usage.limits);
  if (!worst) {
    return (
      <span className="flex items-center gap-1.5 text-xs text-gray-500" title={usage.message ?? ''}>
        {label}codex usage unknown
      </span>
    );
  }
  const t = tone(worst);
  const reset = formatReset(worst.resetsAt);
  const age = asOf(usage.fetchedAt);
  return (
    <span className="flex items-center gap-1.5 text-xs" title={[summary({ ...usage, fetchedAt: null }), age && `Reported ${age}`].filter(Boolean).join('\n')}>
      {label}
      <span className="h-1.5 w-10 rounded-full bg-gray-800 overflow-hidden max-sm:hidden">
        <span className={`block h-full rounded-full ${t.bar}`} style={{ width: `${Math.min(100, Math.max(2, worst.percent))}%` }} />
      </span>
      <span className={`font-medium ${t.text}`}>{Math.round(worst.percent)}%</span>
      {reset && <span className="text-gray-500 whitespace-nowrap max-sm:hidden">resets {reset}</span>}
    </span>
  );
}

const GEMINI_UNKNOWN = "Gemini CLI doesn't report its usage, so there is nothing to show for it.";

/**
 * Up to two chips: Claude's and Codex's, each only while some role runs on that provider. A board
 * running only Gemini gets a quiet "usage unknown" instead of nothing, so an empty bar is never
 * mistaken for a meter that failed to load.
 */
export function HeaderUsage() {
  const providers = useUsageProviders();
  // Until the first answer, Claude's chip alone: it hides itself when no role runs on Claude.
  if (!providers) return <ClaudeChip tag={false} />;
  const both = providers.claude && providers.codex;
  if (!providers.claude && !providers.codex) {
    return providers.gemini
      ? <span className="text-xs text-gray-500" title={GEMINI_UNKNOWN}>usage unknown</span>
      : null;
  }
  return (
    <span className="flex items-center gap-3">
      {providers.claude && <ClaudeChip tag={both} />}
      {providers.codex && <CodexChip tag={both} />}
    </span>
  );
}

// ---------- The Metrics tab --------------------------------------------------------

/** One labelled bar per window. */
function MeterRows({ limits }: { limits: UsageLimit[] }) {
  return (
    <div className="space-y-3">
      {limits.map(l => {
        const t = tone(l);
        const reset = formatReset(l.resetsAt);
        return (
          <div key={`${l.kind}-${l.label}`}>
            <div className="flex items-baseline justify-between text-xs mb-1">
              <span className="text-gray-400">{l.label}</span>
              <span className="text-gray-600">
                <span className={`font-medium ${t.text}`}>{Math.round(l.percent)}%</span>
                {reset && ` · resets ${reset}`}
              </span>
            </div>
            <div className="h-1.5 rounded-full bg-gray-800 overflow-hidden">
              <div
                className={`h-full rounded-full ${t.bar}`}
                style={{ width: `${Math.min(100, Math.max(1, l.percent))}%` }}
              />
            </div>
          </div>
        );
      })}
    </div>
  );
}

function MeterCard({ title, note, children }: { title: string; note: string | null; children: React.ReactNode }) {
  return (
    <div className="bg-gray-900 border border-hair rounded-lg p-4 min-w-0">
      <div className="flex items-baseline justify-between gap-3 mb-3">
        <h3 className="text-sm font-semibold text-gray-300">{title}</h3>
        {note && <span className="text-xs text-gray-600 truncate">{note}</span>}
      </div>
      {children}
    </div>
  );
}

function ClaudeMeters() {
  const { usage } = useClaudeUsage();
  if (!usage || usage.status === 'unsupported') return null;
  return (
    <MeterCard
      title="Claude headroom"
      note={usage.status === 'ok' && usage.fetchedAt ? `updated ${formatWakeTime(new Date(usage.fetchedAt))}` : usage.message}
    >
      {usage.limits.length === 0 ? (
        <p className="text-xs text-gray-500">
          No usage data yet. This reads Claude Code's own login, which only stays fresh while somebody is
          running the CLI — run <code className="text-gray-400">claude</code> once if it stays empty.
        </p>
      ) : <MeterRows limits={usage.limits} />}
    </MeterCard>
  );
}

function CodexMeters() {
  const usage = useCodexUsage(true);
  if (!usage || usage.status === 'unsupported') return null;
  return (
    <MeterCard title="Codex headroom" note={asOf(usage.fetchedAt)}>
      {usage.limits.length === 0 ? (
        <p className="text-xs text-gray-500">
          {usage.message ?? 'No Codex usage reported yet.'} It updates each time a Codex run finishes.
        </p>
      ) : <MeterRows limits={usage.limits} />}
    </MeterCard>
  );
}

/**
 * The same data given room to breathe, for the Metrics tab: one card per provider in use, side by
 * side, each with every window spelled out rather than hidden behind a tooltip.
 */
export function UsageMeters() {
  const providers = useUsageProviders();
  if (!providers) return null;
  if (!providers.claude && !providers.codex) {
    if (!providers.gemini) return null;
    return (
      <MeterCard title="Usage unknown" note={null}>
        <p className="text-xs text-gray-500">{GEMINI_UNKNOWN} Every role runs on Gemini, so no headroom is shown here.</p>
      </MeterCard>
    );
  }
  return (
    <div className="space-y-2">
      <div className={`grid gap-4 ${providers.claude && providers.codex ? 'sm:grid-cols-2' : ''}`}>
        {providers.claude && <ClaudeMeters />}
        {providers.codex && <CodexMeters />}
      </div>
      {providers.gemini && (
        <p className="text-[11px] text-gray-600">Roles on Gemini are not counted above: Gemini CLI doesn't report its usage.</p>
      )}
    </div>
  );
}
