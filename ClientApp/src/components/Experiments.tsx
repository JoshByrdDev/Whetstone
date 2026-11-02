import { useCallback, useEffect, useState } from 'react';
import {
  listExperiments, getExperimentOptions, createExperiment, stopExperiment, declareExperiment,
} from '../api/experiments';
import type { ExperimentView, ExperimentOptions, ArmResult, ArmSpec, Rate, Stage } from '../api/experiments';
import { Chip, GhostButton, PrimaryButton, SectionRule } from './ui';

/**
 * Experiments (docs/bugs-and-experiments-plan.md, section 2): two arms of one phase's skill or agent
 * settings, measured per ticket to Done, with quality as a guardrail. Created, stopped and declared
 * only here; the board's one move of its own is stopping a variant that clearly breaks a guardrail.
 */

const caps = 'text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500';

const STAGE: Record<Stage, { label: string; colour: string }> = {
  enrolling: { label: 'Enrolling', colour: 'var(--w-active)' },
  landing: { label: 'Landing', colour: 'var(--w-queue)' },
  waiting: { label: 'Waiting for escaped bugs', colour: 'var(--w-gate)' },
  ready: { label: 'Ready for a verdict', colour: 'var(--w-accent)' },
  stopped: { label: 'Stopped', colour: 'var(--w-blocked)' },
  concluded: { label: 'Concluded', colour: 'var(--w-done)' },
};

const GUARD: Record<string, string> = { 'qa-failure': 'QA failure', rework: 'Rework', 'escaped-bug': 'Escaped bug' };

const pct = (r: Rate) => (r.value == null ? '—' : `${Math.round(r.value * 100)}%`);
const mins = (m: number | null) => (m == null ? '—' : m < 60 ? `${Math.round(m)}m` : `${(m / 60).toFixed(1)}h`);
const tok = (n: number | null) => (n == null ? '—' : n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${Math.round(n / 1e3)}k` : `${Math.round(n)}`);

function overrides(a: { skillVariant: string | null; model: string | null; effort: string | null; provider: string | null }) {
  const parts = [
    a.skillVariant && `skill: ${a.skillVariant}.md`,
    a.provider && `provider: ${a.provider}`,
    a.model && `model: ${a.model}`,
    a.effort && `effort: ${a.effort}`,
  ].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'as configured';
}

function ArmColumn({ arm, suggested }: { arm: ArmResult; suggested: boolean }) {
  return (
    <div className="min-w-0 flex-1 rounded-lg border border-hair px-3 py-2.5"
         style={suggested ? { borderColor: 'rgb(var(--w-accent-rgb) / .5)' } : undefined}>
      <div className="flex items-baseline gap-2 min-w-0">
        <span className="text-[13px] text-gray-100 truncate">{arm.name}</span>
        <span className={caps}>{arm.key}</span>
      </div>
      <div className="text-[11px] text-gray-500 break-words">{overrides(arm)}</div>
      <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-[12px]">
        <dt className="text-gray-500">Tickets</dt><dd className="font-mono text-gray-200 text-right">{arm.done}/{arm.tickets} done</dd>
        <dt className="text-gray-500">Worker time</dt><dd className="font-mono text-gray-200 text-right">{mins(arm.medianMinutes)}</dd>
        <dt className="text-gray-500">Fresh tokens</dt><dd className="font-mono text-gray-200 text-right">{tok(arm.medianFreshTokens)}</dd>
        <dt className="text-gray-500">QA failure</dt><dd className="font-mono text-gray-200 text-right">{pct(arm.qaFailure)} <span className="text-gray-600">of {arm.qaFailure.n}</span></dd>
        <dt className="text-gray-500">Rework</dt><dd className="font-mono text-gray-200 text-right">{pct(arm.rework)} <span className="text-gray-600">of {arm.rework.n}</span></dd>
        <dt className="text-gray-500">Escaped bug</dt><dd className="font-mono text-gray-200 text-right">{pct(arm.escapedBug)} <span className="text-gray-600">of {arm.escapedBug.n}</span></dd>
      </dl>
      <div className="mt-1 text-[10px] text-gray-600">medians per ticket to Done</div>
    </div>
  );
}

function ExperimentCard({ e, onChanged }: { e: ExperimentView; onChanged: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [note, setNote] = useState('');
  const [showTickets, setShowTickets] = useState(false);
  const stage = STAGE[e.stage];
  const canDeclare = e.stage === 'ready' || e.stage === 'stopped';

  const act = (f: () => Promise<unknown>) => {
    setBusy(true); setError(null);
    f().then(onChanged).catch(err => setError(String(err?.message ?? err))).finally(() => setBusy(false));
  };

  return (
    <div className="rounded-lg border border-hair bg-panel px-3 sm:px-4 py-3">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="text-[14px] text-gray-100 min-w-0 break-words">{e.name}</span>
        <span className="text-[11px] text-gray-500">{e.projectKey} · {e.skill}</span>
        <span className={caps} style={{ color: stage.colour }}>{stage.label}</span>
      </div>
      {e.hypothesis && <p className="mt-1 text-[12px] text-gray-400 break-words">{e.hypothesis}</p>}
      <p className="mt-1 text-[11px] text-gray-500">
        Started {new Date(e.startedAt).toLocaleDateString()} · {e.minTicketsPerArm} tickets an arm · {e.waitDays} days' wait
        {e.stage === 'waiting' && e.verdictFrom && <> · verdict from {new Date(e.verdictFrom).toLocaleDateString()}</>}
      </p>
      {e.stopReason && (
        <p className="mt-1 text-[12px] break-words" style={{ color: e.stopKind === 'Guardrail' ? 'var(--w-blocked)' : 'var(--w-t400)' }}>
          {e.stopKind === 'Guardrail' ? 'Stopped by the board: ' : 'Stopped: '}{e.stopReason}
        </p>
      )}

      <div className="mt-3 flex flex-col sm:flex-row gap-2">
        {e.arms.map(a => <ArmColumn key={a.key} arm={a} suggested={e.suggestion === a.key && e.stage !== 'concluded'} />)}
      </div>

      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-gray-500">
        {e.checks.map(c => (
          <span key={c.name} style={{ color: c.breaks ? 'var(--w-blocked)' : c.passes ? undefined : 'var(--w-gate)' }}>
            {GUARD[c.name] ?? c.name}: p = {c.p.toFixed(3)}{c.breaks ? ' · breaks' : c.passes ? '' : ' · worse'}
          </span>
        ))}
      </div>

      {e.stage === 'concluded'
        ? <p className="mt-2 text-[12px] text-gray-300">Declared: <span className="text-gray-100">{e.winner}</span>{e.conclusionNote && <span className="text-gray-500"> · {e.conclusionNote}</span>}</p>
        : <p className="mt-2 text-[12px] text-gray-400 break-words">
            {e.suggestion ? <>Suggests <span className="text-gray-100">{e.suggestion}</span>. </> : 'No suggestion yet. '}{e.suggestionWhy}
          </p>}

      {e.status !== 'Concluded' && (
        <div className="mt-3 flex flex-wrap gap-2 items-center">
          {e.status === 'Running' && <GhostButton tone="danger" disabled={busy} onClick={() => act(() => stopExperiment(e.id))}>Stop</GhostButton>}
          {canDeclare && !open && <GhostButton tone="go" disabled={busy} onClick={() => setOpen(true)}>Declare a winner</GhostButton>}
        </div>
      )}
      {open && canDeclare && (
        <div key="declare" className="orbit-swap mt-2 flex flex-col gap-2">
          <input value={note} onChange={ev => setNote(ev.target.value)} placeholder="Why, in a line"
                 className="w-full min-w-0 rounded border border-hair bg-transparent px-2 py-1 text-[12px] text-gray-200 placeholder:text-gray-600" />
          <div className="flex flex-wrap gap-2">
            {['control', 'variant', 'none'].map(w => (
              <GhostButton key={w} tone={w === 'none' ? 'plain' : 'go'} disabled={busy}
                           onClick={() => act(() => declareExperiment(e.id, w, note))}>{w}</GhostButton>
            ))}
            <GhostButton disabled={busy} onClick={() => setOpen(false)}>Cancel</GhostButton>
          </div>
        </div>
      )}
      {e.tickets.length > 0 && (
        <div className="mt-2">
          <button className="orbit-tap text-[11px] text-gray-500 hover:text-gray-300 transition-colors" onClick={() => setShowTickets(s => !s)}>
            {showTickets ? 'Hide' : 'Show'} {e.tickets.length} enrolled ticket{e.tickets.length === 1 ? '' : 's'}
          </button>
          {showTickets && (
            <ul key="tickets" className="orbit-swap mt-1 flex flex-col gap-0.5 text-[11px]">
              {e.tickets.map(t => (
                <li key={t.ticketId} className="flex gap-2 min-w-0">
                  <span className="font-mono text-gray-500 flex-none">#{t.ticketId}</span>
                  <span className="text-gray-300 truncate min-w-0 flex-1">{t.title ?? ''}</span>
                  <span className={`${caps} flex-none`}>{t.arm}</span>
                  <span className="text-gray-500 flex-none">{t.doneAt ? 'done' : t.phase}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {error && <p className="mt-2 text-[12px] text-[color:var(--w-blocked)]">{error}</p>}
    </div>
  );
}

function ArmFields({ label, value, onChange, options, skill }: {
  label: string; value: ArmSpec; onChange: (v: ArmSpec) => void; options: ExperimentOptions; skill: string;
}) {
  const variants = options.variants[skill] ?? [];
  const field = 'w-full min-w-0 rounded border border-hair bg-transparent px-2 py-1 text-[12px] text-gray-200 placeholder:text-gray-600';
  return (
    <div className="min-w-0 flex-1 rounded-lg border border-hair px-3 py-2.5 flex flex-col gap-1.5">
      <div className={caps}>{label}</div>
      <input className={field} placeholder="Name" value={value.name ?? ''} onChange={e => onChange({ ...value, name: e.target.value })} />
      <select className={field} value={value.skillVariant ?? ''} onChange={e => onChange({ ...value, skillVariant: e.target.value || undefined })}>
        <option value="">skill.md as it is</option>
        {variants.map(v => <option key={v} value={v}>variants/{v}.md</option>)}
      </select>
      <select className={field} value={value.provider ?? ''} onChange={e => onChange({ ...value, provider: e.target.value || undefined })}>
        <option value="">provider as configured</option>
        {options.providers.map(p => <option key={p} value={p}>{p}</option>)}
      </select>
      <input className={field} placeholder="model as configured" value={value.model ?? ''} onChange={e => onChange({ ...value, model: e.target.value || undefined })} />
      <select className={field} value={value.effort ?? ''} onChange={e => onChange({ ...value, effort: e.target.value || undefined })}>
        <option value="">effort as configured</option>
        {options.efforts.map(x => <option key={x} value={x}>{x}</option>)}
      </select>
    </div>
  );
}

function CreateForm({ project, onDone }: { project: string; onDone: () => void }) {
  const [options, setOptions] = useState<ExperimentOptions | null>(null);
  const [skill, setSkill] = useState('implement');
  const [name, setName] = useState('');
  const [hypothesis, setHypothesis] = useState('');
  const [min, setMin] = useState(8);
  const [wait, setWait] = useState(21);
  const [control, setControl] = useState<ArmSpec>({ name: 'Control' });
  const [variant, setVariant] = useState<ArmSpec>({ name: 'Variant' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { getExperimentOptions(project).then(setOptions).catch(e => setError(String(e?.message ?? e))); }, [project]);
  if (!options) return error ? <p className="text-[12px] text-[color:var(--w-blocked)]">{error}</p> : null;

  const role = options.roles[skill];
  const field = 'w-full min-w-0 rounded border border-hair bg-transparent px-2 py-1 text-[12px] text-gray-200 placeholder:text-gray-600';
  const submit = () => {
    setBusy(true); setError(null);
    createExperiment({ projectKey: project, skill, name, hypothesis, minTicketsPerArm: min, waitDays: wait, control, variant })
      .then(onDone).catch(e => setError(String(e?.message ?? e))).finally(() => setBusy(false));
  };

  return (
    <div key="create" className="orbit-swap rounded-lg border border-hair bg-panel px-3 sm:px-4 py-3 flex flex-col gap-2.5">
      <div>
        <div className={`${caps} mb-1`}>Phase</div>
        <div className="flex flex-wrap gap-1.5">
          {options.skills.map(s => <Chip key={s} on={skill === s} onClick={() => setSkill(s)}>{s}</Chip>)}
        </div>
        {role && <div className="mt-1 text-[11px] text-gray-600">Configured: {role.provider} · {role.model} · effort {role.effort}</div>}
      </div>
      <input className={field} placeholder="Name, e.g. a terser implement skill" value={name} onChange={e => setName(e.target.value)} />
      <input className={field} placeholder="What you expect the variant to change" value={hypothesis} onChange={e => setHypothesis(e.target.value)} />
      <div className="flex flex-wrap gap-3 text-[12px] text-gray-400">
        <label className="flex items-center gap-2">Tickets an arm
          <input type="number" min={2} max={200} value={min} onChange={e => setMin(+e.target.value)} className={`${field} w-16`} />
        </label>
        <label className="flex items-center gap-2">Days to wait for escaped bugs
          <input type="number" min={0} max={365} value={wait} onChange={e => setWait(+e.target.value)} className={`${field} w-16`} />
        </label>
      </div>
      <div className="flex flex-col sm:flex-row gap-2">
        <ArmFields label="Control" value={control} onChange={setControl} options={options} skill={skill} />
        <ArmFields label="Variant" value={variant} onChange={setVariant} options={options} skill={skill} />
      </div>
      <p className="text-[11px] text-gray-600 leading-snug">
        A variant skill is a file in this project's folder, at skills/{skill}/variants/&lt;name&gt;.md. Tickets new to {skill} are
        enrolled in pairs, one per arm, and keep their arm for every later pass.
      </p>
      <div className="flex flex-wrap gap-2 items-center">
        <PrimaryButton disabled={busy || !name.trim()} onClick={submit}>Start it</PrimaryButton>
        <GhostButton disabled={busy} onClick={onDone}>Cancel</GhostButton>
      </div>
      {error && <p className="text-[12px] text-[color:var(--w-blocked)]">{error}</p>}
    </div>
  );
}

export function Experiments({ project }: { project: string | null }) {
  const [list, setList] = useState<ExperimentView[] | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(() => { listExperiments(project).then(setList).catch(() => setList([])); }, [project]);
  useEffect(() => { setList(null); setCreating(false); load(); }, [load]);

  const running = list?.filter(e => e.status === 'Running').length ?? 0;
  return (
    <div>
      <SectionRule title="Experiments" colour="var(--w-queue)"
                   meta={list ? `${running} running · ${list.length} in all` : undefined}
                   right={project && !creating ? <GhostButton onClick={() => setCreating(true)}>New experiment</GhostButton> : undefined} />
      <div className="mt-3 flex flex-col gap-3">
        {creating && project && <CreateForm project={project} onDone={() => { setCreating(false); load(); }} />}
        {!project && !creating && list?.length === 0 && (
          <p className="text-xs text-gray-600">Pick a project to start an experiment on one of its phases.</p>
        )}
        {project && list?.length === 0 && !creating && (
          <p className="text-xs text-gray-600">No experiments yet. One compares two versions of a phase's skill, model, effort or provider, ticket by ticket.</p>
        )}
        {list?.map(e => <ExperimentCard key={e.id} e={e} onChanged={load} />)}
      </div>
    </div>
  );
}
