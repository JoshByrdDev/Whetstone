import { useCallback, useEffect, useState } from 'react';

/**
 * Says out loud that the CLI's login is dead, because every other symptom of it is silent.
 *
 * A `claude` process handed a spent token does not fail fast: it retries the 401 for about three
 * minutes and then exits 1 with nothing on stderr. So a stale login shows up as the Classroom
 * spinning and giving up, heartbeat workers failing their tickets, and `claude auth status` still
 * cheerfully reporting `loggedIn: true` throughout. Nothing anywhere points at the credential.
 *
 * The banner is deliberately hard to trigger. The dashboard renews the token itself on a timer
 * (see ClaudeUsageService), and anything it can still repair - a rate-limited renewal, a dropped
 * request, a token that merely aged out - says nothing here. This appears only once a renewal has
 * genuinely been refused, which is the one case that needs a person.
 */

interface SignIn {
  needsSignIn: boolean;
  message: string | null;
  /** Whether the server can open a terminal for the interactive flow, or can only name the command. */
  canLaunch: boolean;
}

/** Matches the server's own poll: this state only ever changes on the back of one. */
const POLL_MS = 60_000;

export function AuthBanner() {
  const [state, setState] = useState<SignIn | null>(null);
  const [launching, setLaunching] = useState(false);
  const [launched, setLaunched] = useState<string | null>(null);

  const load = useCallback(
    () =>
      fetch('/api/usage/signin')
        .then(r => (r.ok ? r.json() : null))
        .then(d => { if (d) setState(d); })
        .catch(() => { /* the banner keeps its last answer; the next tick is the retry */ }),
    [],
  );

  useEffect(() => {
    load();
    const timer = setInterval(load, POLL_MS);
    return () => clearInterval(timer);
  }, [load]);

  if (!state?.needsSignIn) return null;

  /**
   * Signing in is an interactive OAuth flow the CLI drives itself, so the server opens a real
   * terminal rather than pretending it can do this in the browser. The banner stays up until a poll
   * proves the login works - finishing in the terminal is what clears it, not clicking here.
   */
  const signIn = async () => {
    setLaunching(true);
    try {
      const r = await fetch('/api/usage/signin', { method: 'POST' });
      const d = r.ok ? await r.json() : null;
      setLaunched(d?.message ?? 'Could not open a terminal.');
      await load();
    } catch {
      setLaunched('Could not reach the dashboard to open a terminal.');
    } finally {
      setLaunching(false);
    }
  };

  return (
    <div className="max-w-7xl mx-auto px-3 sm:px-6 pt-4">
      <div className="rounded-lg border border-amber-700/60 bg-amber-950/40 px-4 py-3 flex flex-wrap items-center gap-x-4 gap-y-2">
        <span className="text-amber-300 text-sm font-semibold">Claude sign-in needed</span>
        <span className="text-amber-200/80 text-sm flex-1 min-w-[16rem]">
          {state.message ?? "The dashboard's Claude login has expired."} Agent runs will fail until
          this is fixed.
        </span>

        {state.canLaunch ? (
          <button
            type="button"
            onClick={signIn}
            disabled={launching}
            title="Open a terminal on the dashboard's machine and run `claude auth login`"
            className="btn-act px-3.5 py-1.5 bg-amber-500 text-void disabled:opacity-50"
          >
            {launching ? 'Opening…' : 'Sign in'}
          </button>
        ) : (
          // No terminal to open - the dashboard is not on this user's desktop - so the honest
          // offer is the command itself rather than a button that cannot work.
          <code className="text-amber-200 text-xs bg-black/30 rounded px-2 py-1">claude auth login</code>
        )}
      </div>

      {launched && (
        <p className="text-xs text-amber-200/70 mt-1.5 px-1">
          {launched} This banner clears on its own once the login works.
        </p>
      )}
    </div>
  );
}
