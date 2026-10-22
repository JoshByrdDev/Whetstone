import { useCallback, useEffect, useState } from 'react';
import { GhostButton } from './ui';
import { chamfer, MONO } from './orbit';

/** One dependency and whether this machine has it. Mirrors SetupItem in Services/SetupCheck.cs. */
interface SetupItem {
  key: string;
  label: string;
  /** ok; missing (it will fail); warn (it works less well, or needs a restart to be seen). */
  state: 'ok' | 'missing' | 'warn';
  required: boolean;
  detail: string;
  fix: string | null;
}

const DOT: Record<SetupItem['state'], string> = {
  ok: 'bg-emerald-400',
  warn: 'bg-amber-400',
  missing: 'bg-rose-400',
};

/**
 * What a new machine is missing, at the top of the System page. Every item here is one that
 * otherwise fails quietly - a heartbeat that flips back to Stopped, a QA ticket that blocks, a
 * commit git refuses - so it is shown open whenever something is wrong, and folds to one line
 * when nothing is. docs/setup.md is the same list for reading before the first run.
 */
export function SetupCheck() {
  const [items, setItems] = useState<SetupItem[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [open, setOpen] = useState(false);

  const check = useCallback(async () => {
    setChecking(true);
    try {
      const res = await fetch('/api/setup-check');
      if (!res.ok) throw new Error();
      setItems(await res.json());
    } catch {
      // keep what was shown; a check that cannot run says nothing new
    } finally {
      setChecking(false);
    }
  }, []);

  useEffect(() => { check(); }, [check]);

  // Not loaded, or a server without the route: say nothing rather than something wrong.
  if (!items) return null;

  const problems = items.filter(i => i.state !== 'ok');
  const missing = problems.filter(i => i.state === 'missing').length;
  // Problems first, worst first; then what is fine.
  const order = { missing: 0, warn: 1, ok: 2 } as const;
  const sorted = [...items].sort((a, b) => order[a.state] - order[b.state]);
  const shown = problems.length > 0 || open ? sorted : [];

  const summary = missing > 0
    ? `${missing} missing${problems.length > missing ? `, ${problems.length - missing} to look at` : ''}`
    : problems.length > 0
      ? `${problems.length} to look at`
      : `All ${items.length} checks pass`;

  return (
    <section
      className={`mb-6 border p-4 ${missing > 0 ? 'border-rose-700/50 bg-rose-950/15' : problems.length > 0 ? 'border-amber-700/40 bg-amber-950/10' : 'border-hair bg-gray-800'}`}
      style={{ clipPath: chamfer(10) }}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wider text-gray-500">Setup check</h3>
        <span className={`text-sm ${missing > 0 ? 'text-rose-300' : problems.length > 0 ? 'text-amber-300' : 'text-emerald-400'}`}>
          {summary}
        </span>
        <div className="ml-auto flex items-center gap-2">
          {problems.length === 0 && (
            <GhostButton onClick={() => setOpen(o => !o)}>{open ? 'Hide' : 'Details'}</GhostButton>
          )}
          <GhostButton onClick={check} disabled={checking}>{checking ? 'Checking…' : 'Check again'}</GhostButton>
        </div>
      </div>

      {shown.length > 0 && (
        <ul className="mt-3 divide-y divide-hairsoft">
          {shown.map(i => (
            <li key={i.key} className="flex gap-3 py-2.5">
              <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${DOT[i.state]}`} aria-hidden />
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-baseline gap-x-2">
                  <span className="text-sm font-medium text-gray-200">{i.label}</span>
                  <span className="text-[11px] text-gray-500">
                    {i.state === 'ok' ? 'ok' : i.state === 'missing' ? 'missing' : 'check'}
                    {!i.required && ' · optional'}
                  </span>
                </div>
                <div className="mt-0.5 break-words text-xs text-gray-400">{i.detail}</div>
                {i.fix && (
                  <div className="mt-1 break-words text-[11px] text-gray-300" style={{ fontFamily: MONO }}>
                    {i.fix}
                  </div>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      {problems.length > 0 && (
        <div className="mt-2 text-[11px] text-gray-500">
          Installed something? Whetstone and the heartbeat only see a new program after Whetstone restarts.
          The full list is <span style={{ fontFamily: MONO }}>docs/setup.md</span>.
        </div>
      )}
    </section>
  );
}
