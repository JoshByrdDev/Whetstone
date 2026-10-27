import { useEffect, useState } from 'react';
import { getStewardPlan, setTicketPlan } from '../api/steward';
import type { StewardPlanView } from '../api/steward';
import type { TicketDetail } from '../types/tickets';

/**
 * Which unit of which plan a ticket belongs to, and which of that unit's outcomes it carries - the
 * link every count of a unit's progress is taken from. A steward sets it when it files a ticket;
 * here the owner sets it on one made by hand, moves it, or takes it out.
 *
 * The plan is read from the project's repo as committed, the same view the Steward page shows, so
 * the picker only offers units and outcomes that are really there. The server checks it again.
 */
export function TicketPlanLink({ ticket, disabled, onChanged }: {
  ticket: TicketDetail; disabled?: boolean; onChanged: () => void;
}) {
  const link = ticket.plan ?? null;
  const [editing, setEditing] = useState(false);
  // The plan being read: the ticket's own when it has one, else the one being worked (null).
  const [planKey, setPlanKey] = useState<string | null>(link?.key ?? null);
  const [view, setView] = useState<StewardPlanView | null>(null);
  const [unit, setUnit] = useState<number | null>(link?.unit ?? null);
  const [picked, setPicked] = useState<string[]>(link?.outcomes ?? []);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  // A key of "" is a plan never given one; the server reads null as the one being worked, so the
  // plan named on the link is passed whenever there is a link.
  useEffect(() => {
    let live = true;
    getStewardPlan(ticket.projectKey, planKey)
      .then(v => { if (live) setView(v); })
      .catch(() => { if (live) setView({ why: 'The plan could not be read just now.' }); });
    return () => { live = false; };
  }, [ticket.projectKey, planKey]);

  const plans = view?.plan?.plans ?? [];
  const units = view?.plan?.units ?? [];
  const thisPlan = view?.plan?.plan;
  const shownUnit = units.find(u => u.unit === (editing ? unit : link?.unit));

  const open = () => {
    setUnit(link?.unit ?? null); setPicked(link?.outcomes ?? []); setPlanKey(link?.key ?? null);
    setFailed(null); setEditing(true);
  };
  const save = async (next: { unit: number | null; outcomes?: string[] }) => {
    setBusy(true); setFailed(null);
    try {
      await setTicketPlan(ticket.id, { plan: next.unit == null ? null : thisPlan?.key ?? planKey, ...next });
      setEditing(false);
      onChanged();
    } catch (err) {
      setFailed(err instanceof Error ? err.message : String(err));
    } finally { setBusy(false); }
  };
  const toggle = (o: string) => setPicked(p => p.includes(o) ? p.filter(x => x !== o) : [...p, o]);

  const off = disabled || busy;
  const pad = (n: number) => String(n).padStart(2, '0');

  if (!editing) {
    return (
      <div key="read" className="orbit-swap">
      {failed && <div className="mb-1.5 text-[11px] text-red-400">{failed}</div>}
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          {link ? (
            <>
              <div className="text-[12.5px] text-gray-200 leading-snug">
                <span className="text-gray-500">{thisPlan && thisPlan.key === link.key ? thisPlan.name : link.key || 'The plan'} › </span>
                <span className="font-mono text-gray-400">{pad(link.unit)}</span>{' '}
                {shownUnit?.title ?? `Unit ${link.unit}`}
              </div>
              <div className="mt-1.5 flex flex-wrap gap-1">
                {link.outcomes.length > 0
                  ? link.outcomes.map(o => (
                    <span key={o} className="rounded border border-hair px-1.5 py-0.5 font-mono text-[10.5px] text-gray-300">{o}</span>
                  ))
                  : <span className="text-[11px] text-gray-600">No outcome named - it counts toward the unit, not toward any one outcome.</span>}
              </div>
            </>
          ) : (
            <div className="text-[12.5px] text-gray-500">
              Not in a plan.{view?.why ? <span className="text-gray-600"> {view.why}</span> : null}
            </div>
          )}
        </div>
        <div className="flex flex-none items-center gap-1.5">
          {link && (
            <button type="button" disabled={off} onClick={() => save({ unit: null })}
              className="orbit-tap rounded border border-hair px-2 py-1 text-[11px] text-gray-400 disabled:opacity-40"
              title="Take this ticket out of the plan">
              Unassign
            </button>
          )}
          {(units.length > 0 || link) && (
            <button type="button" disabled={off} onClick={open}
              className="orbit-tap rounded border border-hair px-2 py-1 text-[11px] text-gray-300 disabled:opacity-40">
              {link ? 'Change' : 'Link to a plan'}
            </button>
          )}
        </div>
      </div>
      </div>
    );
  }

  return (
    <div key="edit" className="orbit-swap rounded-lg border border-hair bg-black/20 p-2.5">
      {plans.length > 1 && (
        <div className="mb-2.5 flex flex-wrap gap-1">
          {plans.map(p => {
            const on = (thisPlan?.key ?? '') === p.key;
            return (
              <button key={p.key} type="button" disabled={off} aria-pressed={on}
                onClick={() => { setPlanKey(p.key); setUnit(null); setPicked([]); }}
                className={`orbit-tap rounded border px-2 py-1 text-[11px] ${on ? 'border-gray-400 text-gray-100 bg-white/[.06]' : 'border-hair text-gray-500'}`}>
                {p.name}{p.working ? '' : ' (finished)'}
              </button>
            );
          })}
        </div>
      )}

      {units.length === 0
        ? <div className="text-[12px] text-gray-500">{view ? view.why ?? 'This plan has no units yet.' : 'Reading the plan'}</div>
        : (
          <div className="flex flex-col gap-1">
            {units.map(u => {
              const on = u.unit === unit;
              return (
                <div key={u.unit}>
                  <button type="button" disabled={off} aria-current={on ? 'true' : undefined}
                    onClick={() => { if (!on) { setUnit(u.unit); setPicked([]); } }}
                    className={`orbit-pick flex w-full min-w-0 items-baseline gap-2 rounded border px-2 py-1.5 text-left ${on ? 'border-gray-400 bg-white/[.05]' : 'border-hair'}`}>
                    <span className="font-mono text-[11px] text-gray-500">{pad(u.unit)}</span>
                    <span className={`min-w-0 flex-1 truncate text-[12.5px] ${on ? 'text-gray-100' : 'text-gray-300'}`}>{u.title}</span>
                    {!u.agreed && <span className="flex-none text-[10.5px] text-gray-600">{u.draft ? 'draft' : 'not agreed'}</span>}
                  </button>
                  {on && (
                    <div key={`o-${u.unit}`} className="orbit-swap px-2 pb-1 pt-2">
                      {u.groups.length === 0 && <div className="text-[11px] text-gray-600">Its spec names no outcomes.</div>}
                      {u.groups.map(g => (
                        <div key={g.heading} className="mb-2">
                          <div className="mb-1 text-[10.5px] uppercase tracking-wide text-gray-600">{g.heading}</div>
                          <div className="flex flex-col gap-1">
                            {g.outcomes.map(o => {
                              const ticked = picked.includes(o.outcome);
                              return (
                                <button key={o.outcome} type="button" disabled={off} aria-pressed={ticked}
                                  onClick={() => toggle(o.outcome)}
                                  className={`orbit-tap flex min-w-0 items-baseline gap-2 rounded border px-2 py-1 text-left ${ticked ? 'border-gray-400 bg-white/[.06]' : 'border-hair'}`}>
                                  <span className={`flex-none font-mono text-[11px] ${ticked ? 'text-gray-100' : 'text-gray-500'}`}>
                                    {ticked ? '■' : '□'} {o.outcome}
                                  </span>
                                  {/* A spec's outcome can run to a paragraph; two lines are enough to tell them apart. */}
                                  <span title={o.text} className={`min-w-0 line-clamp-2 text-[11.5px] leading-snug ${ticked ? 'text-gray-200' : 'text-gray-500'}`}>{o.text}</span>
                                </button>
                              );
                            })}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}

      {failed && <div className="mt-2 text-[11px] text-red-400">{failed}</div>}
      <div className="mt-2.5 flex items-center justify-end gap-1.5">
        <button type="button" disabled={busy} onClick={() => setEditing(false)}
          className="orbit-tap rounded border border-hair px-2 py-1 text-[11px] text-gray-400">
          Cancel
        </button>
        <button type="button" disabled={off || unit == null} onClick={() => save({ unit, outcomes: picked })}
          className="orbit-tap rounded border border-gray-400 bg-white/[.06] px-2.5 py-1 text-[11px] text-gray-100 disabled:opacity-40">
          {busy ? 'Saving' : 'Save'}
        </button>
      </div>
    </div>
  );
}
