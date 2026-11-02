import { MONO } from '../orbit';
import type { ArmRow, ExperimentRow } from './types';
import { Empty, NUM, Section, Sheet, TD, TH, compact, day, mins, pct, useJson } from './parts';
import type { RunFilter } from './PhaseDetail';

/**
 * A/B experiments on a phase's skill, model, effort or provider, read from the marts: per arm, what
 * a ticket costs to get to Done and how often it went wrong, the guardrails' tests, and the verdict
 * the board would suggest. The same numbers as the board's own experiment view, which is where an
 * experiment is started, stopped and decided; this page only reads them.
 *
 * Beside the arm's cost, its cost over only the tickets that never left it: a ticket whose later runs
 * ran as configured (the experiment was stopped, or an override was not honoured) is not evidence
 * of what the arm costs.
 */

const STAGE: Record<string, string> = {
  enrolling: 'Enrolling', landing: 'Landing', waiting: 'Waiting for bugs', ready: 'Ready for a verdict',
  stopped: 'Stopped', concluded: 'Concluded',
};

export function ExperimentResults({ project, bump, onPick }: {
  project: string | null; bump: number; onPick: (f: RunFilter) => void;
}) {
  const { data } = useJson<ExperimentRow[]>(`/api/analytics/experiments${project ? `?project=${encodeURIComponent(project)}` : ''}`, bump);
  const list = data ?? [];
  if (data && list.length === 0) {
    return (
      <Section title="Experiments">
        <Empty>No experiment has run{project ? ' on this project' : ''}. One is started from the board, on a phase's skill, model, effort or provider.</Empty>
      </Section>
    );
  }
  return (
    <Section title="Experiments" meta="per ticket to Done, from its enrolment on, across every skill">
      <div className="space-y-4">
        {list.map(e => <Experiment key={e.experimentId} e={e} onPick={onPick} />)}
      </div>
    </Section>
  );
}

function Experiment({ e, onPick }: { e: ExperimentRow; onPick: (f: RunFilter) => void }) {
  const guard = [
    { name: 'QA failure', p: e.qaFailureP, breaks: e.qaFailureBreaks, of: (a: ArmRow) => [a.qaFailureBad, a.qaFailureN] },
    { name: 'Rework', p: e.reworkP, breaks: e.reworkBreaks, of: (a: ArmRow) => [a.reworkBad, a.reworkN] },
    { name: 'Escaped bug', p: e.escapedP, breaks: e.escapedBreaks, of: (a: ArmRow) => [a.escapedBad, a.escapedN] },
  ];
  const leftAny = e.arms.some(a => a.ticketsLeftArm > 0);
  return (
    <Sheet pad={false}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 border-b border-hair px-4 py-3">
        <span className="text-[11px] text-gray-500" style={{ fontFamily: MONO }}>#{e.experimentId} · {e.skill}</span>
        <span className="min-w-0 flex-1 truncate text-sm font-medium text-gray-100" title={e.hypothesis ?? ''}>{e.name}</span>
        <span className={`text-[11px] ${e.stage === 'stopped' ? 'text-red-300' : e.stage === 'ready' ? 'text-amber-300' : 'text-gray-400'}`}>
          {STAGE[e.stage] ?? e.stage}
          {e.stage === 'waiting' && e.verdictFrom ? ` until ${day(e.verdictFrom)}` : ''}
          {e.stage === 'enrolling' ? ` · ${e.controlTickets}/${e.minTicketsPerArm} and ${e.variantTickets}/${e.minTicketsPerArm}` : ''}
        </span>
      </div>

      <table className="w-full table-fixed text-xs">
        <thead>
          <tr className="border-b border-hair text-gray-500">
            <th className={`${TH} w-[26%] text-left`}>Arm</th>
            <th className={`${TH} ${NUM}`}>Done</th>
            <th className={`${TH} ${NUM}`}>Minutes</th>
            <th className={`${TH} ${NUM} max-sm:hidden`}>Fresh tokens</th>
            {guard.map(g => <th key={g.name} className={`${TH} ${NUM} max-sm:hidden`}>{g.name}</th>)}
            {leftAny && <th className={`${TH} ${NUM} max-sm:hidden`} title="median minutes over Done tickets that never left the arm">In arm only</th>}
          </tr>
        </thead>
        <tbody className="divide-y divide-hairsoft">
          {e.arms.map(a => (
            <tr key={a.armKey} className="orbit-pick cursor-pointer" tabIndex={0} title="List this arm's runs"
              onClick={() => onPick({ name: 'experiment_arm', value: `#${e.experimentId} ${a.armKey}`, label: `#${e.experimentId} ${a.armName}` })}
              onKeyDown={ev => { if (ev.key === 'Enter') onPick({ name: 'experiment_arm', value: `#${e.experimentId} ${a.armKey}`, label: `#${e.experimentId} ${a.armName}` }); }}>
              <td className={TD}>
                <div className={`truncate ${e.winner === a.armKey ? 'text-amber-300' : 'text-gray-200'}`}>{a.armName}</div>
                <div className="truncate text-[10.5px] text-gray-500">
                  {[a.skillVariant && `skill ${a.skillVariant}`, a.model, a.effort, a.provider].filter(Boolean).join(' · ') || 'as configured'}
                </div>
              </td>
              <td className={`${TD} ${NUM} text-gray-400`} style={{ fontFamily: MONO }}>{a.done}/{a.tickets}</td>
              <td className={`${TD} ${NUM} text-gray-100`} style={{ fontFamily: MONO }}>{mins(a.medianMinutes)}</td>
              <td className={`${TD} ${NUM} text-gray-300 max-sm:hidden`} style={{ fontFamily: MONO }}>{compact(a.medianFreshTokens)}</td>
              {guard.map(g => {
                const [bad, n] = g.of(a);
                return <td key={g.name} className={`${TD} ${NUM} text-gray-300 max-sm:hidden`} style={{ fontFamily: MONO }}
                  title={`${bad} of ${n} tickets with an outcome`}>{n ? pct(bad / n) : '—'}</td>;
              })}
              {leftAny && <td className={`${TD} ${NUM} text-gray-400 max-sm:hidden`} style={{ fontFamily: MONO }}
                title={`${a.appliedDone} Done tickets never left the arm; ${a.ticketsLeftArm} did`}>{mins(a.appliedMedianMinutes)}</td>}
            </tr>
          ))}
          <tr className="text-[10.5px] text-gray-500 max-sm:hidden">
            <td className={TD} colSpan={4}>Variant worse than control, one-sided Fisher p</td>
            {guard.map(g => (
              <td key={g.name} className={`${TD} ${NUM} ${g.breaks ? 'text-red-300' : g.p < 0.1 ? 'text-amber-300' : ''}`} style={{ fontFamily: MONO }}
                title={g.breaks ? 'enough to stop the experiment' : g.p < 0.1 ? 'fails the verdict' : 'passes'}>{g.p.toFixed(3)}</td>
            ))}
            {leftAny && <td />}
          </tr>
        </tbody>
      </table>

      <div className="border-t border-hair px-4 py-2.5 text-xs">
        {e.winner
          ? <span className="text-gray-300">Decided: <span className="text-amber-300">{e.winner}</span></span>
          : <span className="text-gray-300">{e.suggestion ? <>Suggested: <span className="text-amber-300">{e.suggestion}</span>. </> : null}{e.suggestionWhy}</span>}
        {e.stopReason && <div className="mt-1 text-[11px] text-gray-500">Stopped{e.stopKind === 'Guardrail' ? ' by a guardrail' : ''}: {e.stopReason}</div>}
      </div>
    </Sheet>
  );
}
