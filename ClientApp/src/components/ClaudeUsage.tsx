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
  limits: UsageLimit[];
}

/** Matches the server's poll interval - a faster one would only re-read the same cached snapshot. */
const POLL_MS = 60_000;

function useClaudeUsage() {
  const [usage, setUsage] = useState<Usage | null>(null);
  const [nudging, setNudging] = useState(false);

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

  return { usage, nudge, nudging };
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

/**
 * App-bar chip: the tightest window as a percent plus its reset, with every window in the tooltip.
 * Stays quiet when there is nothing useful to say - an unauthenticated or never-polled dashboard
 * renders nothing here rather than an error the user did not ask about.
 */
export function HeaderUsage() {
  const { usage, nudge, nudging } = useClaudeUsage();
  if (!usage) return null;
  // A different agent provider is selected, and this meter only measures one subscription. Nothing
  // is wrong and nothing is fixable, so there is nothing to say.
  if (usage.status === 'unsupported') return null;

  const worst = leading(usage.limits);
  if (!worst) {
    // No numbers yet. Only worth a word if something is actually wrong and fixable. A forbidden token
    // is in that set too: it is the one failure a user can stare at for a long time without guessing
    // that the credential is fine and simply not allowed to read this. The detail is in the tooltip.
    if (usage.status === 'forbidden' || nudgeable(usage.status)) {
      return (
        <span className="flex items-center gap-1.5 text-xs text-gray-500" title={usage.message ?? ''}>
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
    return null;
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
      <span className="h-1.5 w-10 rounded-full bg-gray-800 overflow-hidden">
        <span
          className={`block h-full rounded-full ${t.bar} ${stale ? 'opacity-50' : ''}`}
          style={{ width: `${Math.min(100, Math.max(2, worst.percent))}%` }}
        />
      </span>
      <span className={`font-medium ${stale ? 'text-gray-500' : t.text}`}>
        {Math.round(worst.percent)}%
      </span>
      {reset && <span className="text-gray-500">resets {reset}</span>}
      {stale && <span className="text-gray-600">·stale</span>}
      {/* The usual way to see a dead login is right here rather than on the empty chip above: a
          failed poll keeps the last good numbers, so the meter goes stale long before it goes
          blank. Offering the nudge only once every number is gone would be offering it last. */}
      {nudgeable(usage.status) && <NudgeButton onClick={nudge} busy={nudging} />}
    </span>
  );
}

/**
 * The same data given room to breathe, for the Metrics tab: one labelled meter per window, so the
 * weekly ceiling is visible next to the session one rather than hidden behind a tooltip.
 */
export function UsageMeters() {
  const { usage } = useClaudeUsage();
  if (!usage) return null;
  // See HeaderUsage: the panel would be a heading over a permanent apology.
  if (usage.status === 'unsupported') return null;

  return (
    <div className="bg-gray-900 border border-hair rounded-lg p-4">
      <div className="flex items-baseline justify-between mb-3">
        <h3 className="text-sm font-semibold text-gray-300">Rate-limit headroom</h3>
        <span className="text-xs text-gray-600">
          {usage.status === 'ok' && usage.fetchedAt
            ? `updated ${formatWakeTime(new Date(usage.fetchedAt))}`
            : usage.message}
        </span>
      </div>

      {usage.limits.length === 0 ? (
        <p className="text-xs text-gray-500">
          No usage data yet. This reads Claude Code's own login, which only stays fresh while somebody is
          running the CLI — run <code className="text-gray-400">claude</code> once if it stays empty.
        </p>
      ) : (
        <div className="space-y-3">
          {usage.limits.map(l => {
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
      )}
    </div>
  );
}
