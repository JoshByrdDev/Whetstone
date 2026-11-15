import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import type { TicketDetail } from '../types/tickets';
import * as api from '../api/discovery';
import type { Discovery, DiscoveryObservation, DiscoveryProposal, DiscoveryRoute, GoalCheck, GoalOutcome, ProposalKind, RejectReason } from '../api/discovery';
import { transitionTicket, updateTicket } from '../api/tickets';

/**
 * A discovery's drawer (docs/discovery-track-plan.md): the route it walked, what it saw, and the
 * verdict on what it proposes.
 *
 * Everything a run wrote is a proposal until the person reading this says otherwise. Each one can
 * be reworded, re-kinded, approved, rejected with a reason or snoozed, and Complete files every
 * approved one at once - as Delivery tickets that link back here. There is no tool behind any of
 * these buttons: no run can approve, file or answer anything.
 *
 * The long tail (step 2): a route can wait at its gate for the owner's look; once the verdict files
 * tickets the discovery waits for them, and says which one holds it; then a recheck drives the
 * product again and says, goal by goal, whether what was approved landed - and its own proposals
 * come back here for a verdict, a round.
 */

const OUTCOME_STYLE: Record<GoalOutcome, { label: string; cls: string }> = {
  Met: { label: 'met', cls: 'text-emerald-300' },
  NotMet: { label: 'not met', cls: 'text-rose-300' },
  Unclear: { label: 'could not tell', cls: 'text-gray-400' },
};

/** The phases before a scout has walked the route, when whether the owner looks first can still change. */
const BEFORE_SCOUT = ['Backlog', 'NeedsCharting', 'Charting', 'NeedsRouteApproval'];

const KIND_STYLE: Record<ProposalKind, { label: string; chip: string }> = {
  Ticket: { label: 'Ticket', chip: 'text-sky-300 border-sky-800/70 bg-sky-950/30' },
  Bug: { label: 'Bug', chip: 'text-rose-300 border-rose-800/70 bg-rose-950/30' },
  Slice: { label: 'Slice', chip: 'text-violet-300 border-violet-800/70 bg-violet-950/30' },
  Decide: { label: 'Decide', chip: 'text-amber-300 border-amber-800/70 bg-amber-950/30' },
};

const REJECT_LABEL: Record<RejectReason, string> = {
  Generic: 'generic',
  AlreadyDecided: 'already decided',
  NotNow: 'not now',
  Wrong: 'wrong',
};

const OBS_LABEL: Record<DiscoveryObservation['kind'], string> = {
  Wall: 'wall', Judgement: 'judgement', Idea: 'idea', RoughEdge: 'rough edge', Bug: 'bug',
};

function Label({ children }: { children: ReactNode }) {
  return (
    <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 mb-2.5">
      {children}
    </div>
  );
}

const btn = 'orbit-tap text-[11px] px-2 py-1 rounded border border-hair text-gray-300 hover:border-gray-600 disabled:opacity-40';

export function DiscoveryPanel({ t, onChanged }: { t: TicketDetail; onChanged: () => void }) {
  const [d, setD] = useState<Discovery | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = () => api.getDiscovery(t.id).then(setD).catch(e => setError(e instanceof Error ? e.message : String(e)));
  // Reloaded whenever the ticket itself changes, so a run's new observations appear as it logs them.
  useEffect(() => { load(); }, [t.id, t.phase, t.updatedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const obsById = useMemo(() => new Map((d?.observations ?? []).map(o => [o.id, o])), [d]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      await load();
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That did not work.');
    } finally {
      setBusy(false);
    }
  }

  if (!d) return error ? <p className="text-[12px] text-rose-300">{error}</p> : null;

  // A verdict is open while the discovery waits for one - and, at the sign-off, on what a recheck
  // past the rounds proposed, which went there instead of to a verdict.
  const atSignOff = t.phase === 'NeedsReview';
  const openFor = (p: DiscoveryProposal) => t.phase === 'NeedsVerdict' || (atSignOff && (p.round ?? 0) > 0);
  const proposals = d.proposals.filter(p => p.status !== 'Dropped');
  const lateOpen = atSignOff && proposals.some(p => (p.round ?? 0) > 0 && (p.status === 'Proposed' || p.status === 'Approved'));
  const verdictOpen = t.phase === 'NeedsVerdict' || lateOpen;
  const rounds = [...new Set(proposals.map(p => p.round ?? 0))].sort((a, b) => b - a);
  const recheck = d.recheck ?? { round: 0, roundsWithVerdict: 2, goals: [] };
  const checks = d.goalChecks ?? [];
  const goalIds = [...new Set([...checks.map(c => c.goalId), ...(['NeedsRecheck', 'Rechecking'].includes(t.phase) ? recheck.goals : [])])];
  const goals = goalIds.map(id => d.proposals.find(p => p.id === id)).filter(Boolean) as DiscoveryProposal[];
  const waitingOn = d.filed.filter(f => f.phase !== 'Done');
  const dropped = d.proposals.filter(p => p.status === 'Dropped');
  const undecided = proposals.filter(p => p.status === 'Proposed').length;
  const approved = proposals.filter(p => p.status === 'Approved');
  // What Complete will do, in its own words: a ticket or bug is filed, a slice becomes a briefing,
  // a decision is answered - three different things, so they are counted apart.
  const outcome = [
    plural(approved.filter(p => p.kind === 'Ticket' || p.kind === 'Bug').length, 'ticket', 'filed'),
    plural(approved.filter(p => p.kind === 'Slice').length, 'slice briefing', 'written'),
    plural(approved.filter(p => p.kind === 'Decide').length, 'answer', 'recorded'),
  ].filter(Boolean).join(', ');
  const filesTickets = approved.some(p => p.kind === 'Ticket' || p.kind === 'Bug');
  const then = atSignOff
    ? 'the discovery stays at your sign-off: the recheck rounds are spent.'
    : filesTickets
    ? 'then this discovery waits for them to be built, and is rechecked.'
    : 'then this discovery moves to your sign-off.';

  return (
    <div className="space-y-4">
      {d.route ? (
        <RouteBox ticketId={t.id} route={d.route} gate={t.phase === 'NeedsRouteApproval'} busy={busy} run={run} />
      ) : (
        <p className="text-[12px] text-gray-500">No route yet: the direction is charted into one before anything is scouted.</p>
      )}

      {BEFORE_SCOUT.includes(t.phase) && (
        <label className="flex items-center gap-2 text-[12px] text-gray-400 cursor-pointer">
          <input type="checkbox" checked={!!t.requiresRouteReview} disabled={busy}
            onChange={e => run(() => updateTicket(t.id, { requiresRouteReview: e.target.checked }))} />
          Review the route before it is scouted
        </label>
      )}

      {t.phase === 'AwaitingWork' && (
        <div className="rounded-lg border border-hair bg-black/25 px-3 py-2.5 text-[12px] space-y-1.5">
          <div className="text-gray-300">
            {waitingOn.length === 0
              ? 'Everything it filed is settled; the recheck is queued.'
              : `Waiting on ${waitingOn.length} ticket${waitingOn.length === 1 ? '' : 's'} it filed. Once each is done (or deleted), the product is rechecked against what you approved.`}
          </div>
          {waitingOn.map(f => (
            <div key={f.id} className="flex items-baseline gap-2 min-w-0">
              <span className="font-mono text-gray-500">#{f.id}</span>
              <span className="text-gray-400 truncate flex-1 min-w-0">{f.title}</span>
              <span className={`text-[11px] ${f.phase === 'Blocked' ? 'text-rose-300' : 'text-gray-600'}`}>{f.phase === 'Blocked' ? 'blocked - holding this discovery' : f.phase}</span>
            </div>
          ))}
          <button type="button" disabled={busy} onClick={() => run(() => transitionTicket(t.id, 'NeedsReview', 'the owner skipped the recheck'))}
            className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">skip the recheck and sign off</button>
        </div>
      )}

      {goals.length > 0 && (
        <div>
          <Label>
            {['NeedsRecheck', 'Rechecking'].includes(t.phase)
              ? `Recheck ${Math.max(recheck.round, 1)} · did each approved goal land`
              : 'Did each approved goal land'}
          </Label>
          <ul className="space-y-2 text-[12px]">
            {goals.map(g => <GoalRow key={g.id} goal={g} checks={checks.filter(c => c.goalId === g.id)} />)}
          </ul>
          <div className="mt-1.5 text-[11px] text-gray-600">
            {recheck.roundsWithVerdict === 0
              ? 'Rechecks on this project report to your sign-off.'
              : `${recheck.roundsWithVerdict} recheck${recheck.roundsWithVerdict === 1 ? '' : 's'} on this project come back to you for a verdict; after that, what they find waits at your sign-off.`}
          </div>
        </div>
      )}

      {rounds.map(r => (
        <div key={r}>
          <Label>{r === 0 ? (rounds.length > 1 ? 'Proposals from the sift' : 'Proposals') : `Proposals from recheck ${r}`}</Label>
          <div className="space-y-2.5">
            {proposals.filter(p => (p.round ?? 0) === r).map(p => (
              <ProposalCard key={p.id} ticketId={t.id} p={p} obs={p.observationIds.map(i => obsById.get(i)).filter(Boolean) as DiscoveryObservation[]}
                goal={p.goalId ? d.proposals.find(x => x.id === p.goalId) : undefined}
                open={openFor(p)} busy={busy} run={run} />
            ))}
          </div>
        </div>
      ))}

      {verdictOpen && (
        <div className="rounded-lg border border-hair bg-black/25 px-3 py-2.5 flex items-center gap-3 flex-wrap">
          <span className="text-[12px] text-gray-400 flex-1 min-w-0">
            {undecided > 0
              ? `${undecided} still to decide. Complete files every approved proposal at once.`
              : outcome
              ? `Complete: ${outcome}, ${then}`
              : atSignOff
              ? 'Nothing approved. Complete records that; the discovery stays at your sign-off.'
              : 'Nothing approved. Complete moves this discovery to your sign-off with nothing filed.'}
          </span>
          <button type="button" disabled={busy || undecided > 0} onClick={() => run(() => api.complete(t.id))}
            className="orbit-primary btn-act px-3 py-1.5 text-[12px] bg-blue-500 text-void disabled:bg-gray-700 disabled:text-gray-500">
            Complete
          </button>
        </div>
      )}

      {error && <p className="text-[12px] text-rose-300">{error}</p>}

      {d.filed.length > 0 && (
        <div>
          <Label>Filed from this discovery</Label>
          <ul className="space-y-1 text-[12px]">
            {d.filed.map(f => (
              <li key={f.id} className="flex items-baseline gap-2 min-w-0">
                <span className="font-mono text-gray-500">#{f.id}</span>
                <span className="text-gray-300 truncate flex-1 min-w-0">{f.title}</span>
                <span className="text-[11px] text-gray-600">{f.phase}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {dropped.length > 0 && (
        <details>
          <summary className="cursor-pointer text-[11px] text-gray-500 hover:text-gray-300 transition-colors">
            Dropped ({dropped.length})
          </summary>
          <ul className="mt-2 space-y-1 text-[12px]">
            {dropped.map(p => (
              <li key={p.id} className="flex items-baseline gap-2 text-gray-400 leading-snug">
                <span className="flex-1 min-w-0">{p.title} <span className="text-gray-600">· {p.dropReason}</span></span>
                {t.phase === 'NeedsVerdict' && (
                  <button type="button" disabled={busy} onClick={() => run(() => api.restore(p.id))}
                    title="Promote it to a proposal, to decide with the rest" className={`${btn} flex-none`}>Restore</button>
                )}
              </li>
            ))}
          </ul>
        </details>
      )}

      {d.observations.length > 0 && (
        <details>
          <summary className="cursor-pointer text-[11px] text-gray-500 hover:text-gray-300 transition-colors">
            Everything {recheck.round > 0 ? 'the scout and rechecks' : 'the scout'} logged ({d.observations.length} observations, {d.visits.length} pages)
          </summary>
          <ul className="mt-2 space-y-2">
            {d.observations.map(o => <ObservationRow key={o.id} ticketId={t.id} o={o} />)}
          </ul>
        </details>
      )}
    </div>
  );
}

function plural(n: number, noun: string, verb: string) {
  return n === 0 ? '' : `${n} ${noun}${n === 1 ? '' : 's'} ${verb}`;
}

function RouteBox({ ticketId, route, gate, busy, run }: {
  ticketId: number;
  route: DiscoveryRoute;
  gate: boolean;
  busy: boolean;
  run: (fn: () => Promise<unknown>) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [persona, setPersona] = useState(route.persona);
  const [jobs, setJobs] = useState(route.jobs.join('\n'));
  const [signedInAs, setSignedInAs] = useState(route.signedInAs ?? '');
  const [widths, setWidths] = useState(route.widths.join(', '));
  useEffect(() => {
    setPersona(route.persona); setJobs(route.jobs.join('\n')); setSignedInAs(route.signedInAs ?? ''); setWidths(route.widths.join(', '));
  }, [route.persona, route.jobs, route.signedInAs, route.widths]);
  const field = 'w-full text-[12px] bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600';

  const save = () => run(async () => {
    await api.editRoute(ticketId, {
      persona: persona.trim(), jobs: jobs.split('\n').map(j => j.trim()).filter(Boolean),
      signedInAs: signedInAs.trim(), widths: widths.split(',').map(w => w.trim()).filter(Boolean),
    });
    setEditing(false);
  });

  return (
    <div>
      <Label>{gate ? 'Route · waiting for your look' : 'Route'}</Label>
      <div className={`rounded-lg border px-3 py-2.5 space-y-2 text-[12px] ${gate ? 'border-violet-800/60 bg-violet-950/10' : 'border-hair bg-black/20'}`}>
        {editing ? (
          <div className="space-y-1.5">
            <textarea value={persona} onChange={e => setPersona(e.target.value)} rows={2} className={field} placeholder="Who the person is" />
            <textarea value={jobs} onChange={e => setJobs(e.target.value)} rows={4} className={field} placeholder="Jobs, one per line" />
            <div className="flex gap-1.5 max-sm:flex-col">
              <input value={signedInAs} onChange={e => setSignedInAs(e.target.value)} className={`${field} min-w-0`} placeholder="Signed in as" />
              <input value={widths} onChange={e => setWidths(e.target.value)} className={`${field} min-w-0`} placeholder="Widths: 1440x900, 390x844" />
            </div>
            <div className="flex items-center gap-2">
              <button type="button" disabled={busy || !persona.trim() || !jobs.trim()} onClick={save} className={btn}>Save</button>
              <button type="button" onClick={() => setEditing(false)} className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">Cancel</button>
            </div>
          </div>
        ) : (
          <>
            <p className="text-gray-200 leading-snug">{route.persona}</p>
            <div className="flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-gray-500">
              {route.signedInAs && <span>as <span className="text-gray-400">{route.signedInAs}</span></span>}
              {route.widths.length > 0 && <span>at {route.widths.join(', ')}</span>}
              {route.startCases.length > 0 && <span>from {route.startCases.join(', ')}</span>}
              {route.areas.length > 0 && <span>areas {route.areas.join(', ')}</span>}
              {route.ownerEdited && <span>edited by you</span>}
            </div>
            <ol className="list-decimal pl-5 space-y-0.5 text-gray-300">
              {route.jobs.map((j, i) => <li key={i}>{j}</li>)}
            </ol>
            {route.notes && <p className="text-[11px] text-gray-500 leading-snug">{route.notes}</p>}
          </>
        )}
        {gate && !editing && (
          <div className="flex items-center gap-2 flex-wrap pt-1">
            <button type="button" disabled={busy} onClick={() => run(() => api.approveRoute(ticketId))}
              className="orbit-primary btn-act px-3 py-1.5 text-[12px] bg-blue-500 text-void disabled:bg-gray-700 disabled:text-gray-500">
              Approve route
            </button>
            <button type="button" disabled={busy} onClick={() => setEditing(true)} className={btn}>Edit</button>
            <button type="button" disabled={busy} onClick={() => run(() => transitionTicket(ticketId, 'NeedsCharting', 'the owner asked for the direction to be charted again'))}
              className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">chart again</button>
          </div>
        )}
      </div>
    </div>
  );
}

function GoalRow({ goal, checks }: { goal: DiscoveryProposal; checks: GoalCheck[] }) {
  const latest = checks[checks.length - 1];
  return (
    <li className="rounded-lg border border-hair bg-black/20 px-3 py-2">
      <div className="flex items-baseline gap-2 min-w-0">
        <span className="text-gray-200 leading-snug flex-1 min-w-0">{goal.title}</span>
        {goal.filedTicketId && <span className="font-mono text-[11px] text-gray-600">#{goal.filedTicketId}</span>}
        <span className={`text-[11px] ${latest ? OUTCOME_STYLE[latest.outcome].cls : 'text-gray-600'}`}>
          {latest ? OUTCOME_STYLE[latest.outcome].label : 'not checked yet'}
        </span>
      </div>
      {checks.map(c => (
        <div key={c.id} className="mt-1 text-[11px] text-gray-500 leading-snug">
          <span className="text-gray-600">recheck {c.round} · </span>
          <span className={OUTCOME_STYLE[c.outcome].cls}>{OUTCOME_STYLE[c.outcome].label}</span>
          <span> - {c.note}</span>
        </div>
      ))}
    </li>
  );
}

function ObservationRow({ ticketId, o }: { ticketId: number; o: DiscoveryObservation }) {
  return (
    <li className="flex gap-2.5 items-start text-[12px]">
      {o.shot && (
        <a href={api.shotUrl(ticketId, o.shot)} target="_blank" rel="noreferrer" className="orbit-tap flex-none">
          <img src={api.shotUrl(ticketId, o.shot)} alt="" className="w-16 h-11 object-cover object-top rounded border border-hair" />
        </a>
      )}
      <div className="min-w-0 flex-1">
        <div className="text-gray-300 leading-snug">
          <span className="text-[10px] uppercase tracking-wide text-gray-500 mr-1.5">{OBS_LABEL[o.kind]}</span>
          {o.title}
        </div>
        <div className="text-gray-500 leading-snug">{o.saw}</div>
        {(o.url || o.width) && (
          <div className="text-[11px] text-gray-600 truncate">{[o.width, o.url].filter(Boolean).join(' · ')}</div>
        )}
      </div>
    </li>
  );
}

function ProposalCard({ ticketId, p, obs, goal, open, busy, run }: {
  ticketId: number;
  p: DiscoveryProposal;
  obs: DiscoveryObservation[];
  /** For a recheck's fix-up: the approved goal it fixes up. */
  goal?: DiscoveryProposal;
  open: boolean;
  busy: boolean;
  run: (fn: () => Promise<unknown>) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(p.title);
  const [body, setBody] = useState(p.body);
  const [items, setItems] = useState(p.workItems.join('\n'));
  const [answer, setAnswer] = useState(p.answer ?? '');
  const [rejecting, setRejecting] = useState(false);
  const decidable = open && p.status !== 'Filed';
  const style = KIND_STYLE[p.kind];

  useEffect(() => {
    setTitle(p.title); setBody(p.body); setItems(p.workItems.join('\n')); setAnswer(p.answer ?? '');
  }, [p.title, p.body, p.workItems, p.answer]);

  const save = () => run(async () => {
    await api.decide(p.id, {
      title: title.trim(), body: body.trim(),
      workItems: items.split('\n').map(s => s.trim()).filter(Boolean),
    });
    setEditing(false);
  });

  const statusWord = p.status === 'Rejected'
    ? `rejected · ${p.rejectReason ? REJECT_LABEL[p.rejectReason] : ''}`
    : p.status === 'Snoozed' ? `snoozed${p.snoozeUntil ? ` until ${p.snoozeUntil.slice(0, 10)}` : ''}`
    : p.status === 'Filed' ? (p.filedNote ?? 'filed')
    : p.status === 'Approved' ? 'approved'
    : 'to decide';

  return (
    <div className={`rounded-lg border px-3 py-2.5 ${p.status === 'Rejected' ? 'border-hair bg-black/10 opacity-70' : 'border-hair bg-black/25'}`}>
      <div className="flex items-baseline gap-2 flex-wrap">
        <span className="font-mono text-[11px] text-gray-600">{p.rank}</span>
        <span className={`text-[10px] uppercase tracking-wide rounded border px-1.5 ${style.chip}`}>{style.label}</span>
        {p.recheckKind && (
          <span className={`text-[10px] uppercase tracking-wide rounded border px-1.5 ${p.recheckKind === 'FixUp' ? 'text-amber-300 border-amber-800/70' : 'text-gray-400 border-hair'}`}
            title={p.recheckKind === 'FixUp' && goal ? `Fixes up: ${goal.title}` : undefined}>
            {p.recheckKind === 'FixUp' ? 'fix-up' : 'new'}
          </span>
        )}
        {p.size && <span className="text-[11px] text-gray-600">{p.size}</span>}
        {p.edited && <span className="text-[11px] text-gray-600">edited</span>}
        <span className="flex-1" />
        <span className={`text-[11px] ${p.status === 'Approved' || p.status === 'Filed' ? 'text-emerald-300' : 'text-gray-500'}`}>{statusWord}</span>
      </div>

      {editing ? (
        <div className="mt-2 space-y-1.5">
          <input value={title} onChange={e => setTitle(e.target.value)}
            className="w-full text-[13px] bg-black/40 border border-hair rounded px-2 py-1 text-gray-100 focus:outline-none focus:border-gray-600" />
          <textarea value={body} onChange={e => setBody(e.target.value)} rows={5}
            className="w-full text-[12px] bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600" />
          {(p.kind === 'Ticket' || p.kind === 'Bug') && (
            <textarea value={items} onChange={e => setItems(e.target.value)} rows={3} placeholder="Work items, one per line"
              className="w-full text-[12px] bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600" />
          )}
          <div className="flex items-center gap-2 flex-wrap">
            <button type="button" disabled={busy || !title.trim() || !body.trim()} onClick={save} className={btn}>Save</button>
            <button type="button" onClick={() => setEditing(false)} className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">Cancel</button>
            <span className="flex-1" />
            {(Object.keys(KIND_STYLE) as ProposalKind[]).map(k => (
              <button key={k} type="button" disabled={busy || k === p.kind} onClick={() => run(() => api.decide(p.id, { kind: k }))}
                className={`orbit-tap text-[10px] uppercase tracking-wide rounded border px-1.5 ${KIND_STYLE[k].chip} ${k === p.kind ? '' : 'opacity-40 hover:opacity-80'}`}>
                {KIND_STYLE[k].label}
              </button>
            ))}
          </div>
        </div>
      ) : (
        <>
          <div className="mt-1.5 text-[13px] text-gray-100 leading-snug">{p.title}</div>
          {p.recheckKind === 'FixUp' && goal && (
            <div className="mt-0.5 text-[11px] text-gray-500 leading-snug">fixes up: {goal.title}</div>
          )}
          {p.why && <div className="mt-1 text-[12px] text-gray-400 leading-snug">{p.why}</div>}
          <div className="mt-1.5 text-[12px] text-gray-300 leading-snug whitespace-pre-wrap">{p.body}</div>
          {p.workItems.length > 0 && (
            <ul className="mt-1.5 list-disc pl-5 text-[12px] text-gray-400 space-y-0.5">
              {p.workItems.map((w, i) => <li key={i}>{w}</li>)}
            </ul>
          )}
          {p.confidence && <div className="mt-1.5 text-[11px] text-gray-600">confidence: {p.confidence}</div>}
        </>
      )}

      {obs.some(o => o.shot) && (
        <div className="mt-2 flex gap-1.5 flex-wrap">
          {obs.filter(o => o.shot).map(o => (
            <a key={o.id} href={api.shotUrl(ticketId, o.shot!)} target="_blank" rel="noreferrer" title={o.title} className="orbit-tap">
              <img src={api.shotUrl(ticketId, o.shot!)} alt={o.title} className="w-24 h-16 object-cover object-top rounded border border-hair" />
            </a>
          ))}
        </div>
      )}

      {p.kind === 'Decide' && decidable && (
        <textarea value={answer} onChange={e => setAnswer(e.target.value)} rows={2} placeholder="Your answer - what you decide"
          className="mt-2 w-full text-[12px] bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600" />
      )}

      {decidable && !editing && (
        <div className="mt-2 flex items-center gap-1.5 flex-wrap">
          <button type="button" disabled={busy || p.status === 'Approved' || (p.kind === 'Decide' && !answer.trim())}
            onClick={() => run(() => api.decide(p.id, { status: 'Approved', ...(p.kind === 'Decide' ? { answer: answer.trim() } : {}) }))}
            className={`${btn} text-emerald-300`}>Approve</button>
          {rejecting ? (
            (Object.keys(REJECT_LABEL) as RejectReason[]).map(r => (
              <button key={r} type="button" disabled={busy}
                onClick={() => { setRejecting(false); run(() => api.decide(p.id, { status: 'Rejected', rejectReason: r })); }}
                className={btn}>{REJECT_LABEL[r]}</button>
            ))
          ) : (
            <button type="button" disabled={busy} onClick={() => setRejecting(true)} className={btn}>Reject…</button>
          )}
          <button type="button" disabled={busy || p.status === 'Snoozed'} onClick={() => run(() => api.decide(p.id, { status: 'Snoozed' }))}
            title="Not now: it comes back to a later discovery's sift" className={btn}>Snooze</button>
          {p.status !== 'Proposed' && (
            <button type="button" disabled={busy} onClick={() => run(() => api.decide(p.id, { status: 'Proposed' }))}
              className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">undo</button>
          )}
          <span className="flex-1" />
          <button type="button" disabled={busy} aria-pressed={!!p.newToMe}
            onClick={() => run(() => api.decide(p.id, { newToMe: !p.newToMe }))}
            title="Something you had not already thought of"
            className={`orbit-tap text-[11px] px-2 py-1 rounded border ${p.newToMe ? 'border-amber-700/70 text-amber-300 bg-amber-950/30' : 'border-hair text-gray-500 hover:text-gray-300'}`}>
            new to me
          </button>
          <button type="button" onClick={() => setEditing(true)} className="text-[11px] text-gray-500 hover:text-gray-300 transition-colors">edit</button>
        </div>
      )}
    </div>
  );
}
