import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { Chip, GhostButton, PrimaryButton } from './ui';
import { BODY, MONO, TIER, chamfer, fade } from './orbit';
import { AnalysisDoc } from './AnalysisBlocks';
import { WalkthroughCard } from './WalkthroughPlayer';
import type { DeckSubject } from './WalkthroughPlayer';
import { Bounces } from './Classroom';
import { useShotLightbox } from './ShotLightbox';
import { ago, label } from './stewardParts';
import { PHASE_LABEL } from '../types/tickets';
import type { Phase } from '../types/tickets';
import {
  cancelUnitReport, getUnitReport, getUnitReportView, getUnitWalkthrough, queueUnitReport, unitReportDiffUrl,
  writeUnitWalkthrough,
} from '../api/unitReports';
import type { UnitReport as Report, UnitReportTicket, UnitReportView } from '../api/unitReports';

/**
 * A unit, read as one piece of work: what its owner agreed and what its tickets delivered against
 * it, how the product works now, what was decided for them, and how the building went.
 *
 * The document is a Classroom document and renders as one - the coverage map first, the flows with
 * their screenshots, the diffs one click from a stop. What the server knows outright (which tickets,
 * how often each was sent back, what it cost) is drawn from the rows, never from the model's words.
 */
export function UnitReport({ project, plan, unit, title, onNotes }: {
  project: string; plan: string | null | undefined; unit: number; title: string; onNotes: (unit: number) => void;
}) {
  const [view, setView] = useState<UnitReportView | null>(null);
  const [picked, setPicked] = useState<Report | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => getUnitReportView(project, plan, unit)
    .then(v => { setView(v); setFailed(null); })
    .catch(e => setFailed(e instanceof Error ? e.message : String(e))), [project, plan, unit]);
  useEffect(() => { setView(null); setPicked(null); load(); }, [load]);

  // A report takes minutes and waits behind the tickets' own analyses: poll while it is in the line.
  const waiting = view?.queue != null;
  useEffect(() => {
    if (!waiting) return;
    const t = window.setInterval(load, 5000);
    return () => window.clearInterval(t);
  }, [waiting, load]);

  const ask = async (act: () => Promise<unknown>) => {
    setBusy(true);
    try { await act(); await load(); }
    catch (e) { setFailed(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  const write = () => ask(() => queueUnitReport(project, plan, unit));

  if (!view) return <Note>{failed ?? 'Reading the report…'}</Note>;

  const report = picked ?? view.latest;
  const queue = view.queue && (
    <div className="flex items-center gap-2 flex-wrap" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t300)' }}>
      <span className="h-1.5 w-1.5 rounded-full animate-pulse" style={{ background: 'var(--w-active)' }} />
      {view.queue === 'running' ? 'Writing the report' : 'Waiting its turn'}
      {view.queueNote && <span style={{ color: 'var(--w-t500)' }}>- {view.queueNote}</span>}
      <GhostButton disabled={busy} onClick={() => ask(() => cancelUnitReport(project, plan, unit))}>Stop</GhostButton>
    </div>
  );

  if (!report) {
    return (
      <div style={{ padding: '14px 16px 18px', maxWidth: 812 }}>
        <p style={{ font: `400 13px/1.6 ${BODY}`, color: 'var(--w-t300)', margin: 0 }}>
          No report on this unit yet. One is written over its tickets' own Classroom analyses - any a
          ticket is missing are written first - and rules on every outcome you agreed, with the
          screenshots that show it. It takes a few minutes, and runs in the Classroom's queue.
        </p>
        <div style={{ marginTop: 12 }}>{queue || <PrimaryButton disabled={busy} onClick={write}>Write the report</PrimaryButton>}</div>
        {(view.lastError || failed) && <Bad>{view.lastError ?? failed}</Bad>}
        <HowItWent tickets={view.tickets} />
      </div>
    );
  }

  return (
    <div style={{ padding: '14px 16px 18px', maxWidth: 1080 }}>
      <Written report={report} view={view} picked={picked} onPick={id =>
        id === view.latest?.meta.id ? setPicked(null) : getUnitReport(id).then(setPicked, e => setFailed(String(e)))} />

      {queue && <div style={{ marginTop: 10 }}>{queue}</div>}
      {(view.lastError || failed) && <Bad>{view.lastError ?? failed}</Bad>}

      {report.stale.length > 0 && !picked && (
        <Box tone={TIER.gate.c}>
          <div style={{ font: `600 12px ${BODY}`, color: 'var(--w-t100)' }}>Written before some of what is there now</div>
          <ul style={{ margin: '4px 0 0', paddingLeft: 18, font: `400 12px/1.6 ${BODY}`, color: 'var(--w-t300)' }}>
            {report.stale.map(s => <li key={s}>{s}</li>)}
          </ul>
          {!view.queue && <div style={{ marginTop: 8 }}><GhostButton disabled={busy} onClick={write}>Write it again</GhostButton></div>}
        </Box>
      )}

      {report.gaps.length > 0 && (
        <Box tone="var(--w-t500)">
          <div style={{ font: `600 12px ${BODY}`, color: 'var(--w-t200)' }}>Written without</div>
          <ul style={{ margin: '4px 0 0', paddingLeft: 18, font: `400 12px/1.6 ${BODY}`, color: 'var(--w-t400)' }}>
            {report.gaps.map(g => <li key={`${g.ticketId}-${g.kind}`}>{g.why}</li>)}
          </ul>
        </Box>
      )}

      <div key={report.meta.id} className="orbit-swap" style={{ marginTop: 18 }}>
        <WalkthroughCard subject={unitSubject(report, unit, title)} />
        <Document report={report} view={view} />
      </div>

      <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 22 }}>
        <span style={label()}>Before it goes out</span>
        <GhostButton onClick={() => onNotes(unit)}>Read its release notes</GhostButton>
      </div>

      <HowItWent tickets={view.tickets} />
    </div>
  );
}

/** A unit report, as the walkthrough player narrates it. */
function unitSubject(r: Report, unit: number, title: string): DeckSubject {
  const m = r.meta;
  return {
    key: `unit-report-${m.id}`,
    mark: `Unit ${unit}`,
    title,
    headline: m.headline,
    tags: [{ text: `${m.fromSha.slice(0, 7)}..${m.toSha.slice(0, 7)}`, mono: true }, { text: m.baseRef, mono: true }],
    doc: r.document,
    shots: r.shots,
    askFrom: 'From the spec',
    shotName: n => `Shot ${n}`,
    writing: 'writing the walkthrough - it follows the report by a few minutes',
    load: () => getUnitWalkthrough(m.id),
    write: () => writeUnitWalkthrough(m.id),
  };
}

/** The headline, when it was written and over what, and the earlier reports to compare it with. */
function Written({ report, view, picked, onPick }: {
  report: Report; view: UnitReportView; picked: Report | null; onPick: (id: number) => void;
}) {
  const m = report.meta;
  return (
    <div>
      <div style={{ font: `600 17px/1.4 ${BODY}`, color: 'var(--w-t100)' }}>{m.headline}</div>
      <div className="flex items-baseline gap-x-3 gap-y-1 flex-wrap" style={{ marginTop: 6, font: `400 11.5px ${BODY}`, color: 'var(--w-t500)' }}>
        <span>Written {ago(m.generatedAt)}</span>
        <span className="min-w-0 truncate" style={{ fontFamily: MONO }}>{m.baseRef} {m.fromSha.slice(0, 7)}..{m.toSha.slice(0, 7)}</span>
        <span>
          {m.rangeCommits} commit{m.rangeCommits === 1 ? '' : 's'} on the base
          {m.ownCommits > 0 && m.ownCommits < m.rangeCommits && `, ${m.ownCommits} of them this unit's merges`}
        </span>
        {m.model && <span>{m.model}</span>}
      </div>
      {view.history.length > 1 && (
        <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 10 }}>
          <span style={label()}>Reports</span>
          {view.history.map((h, i) => (
            <Chip key={h.id} on={(picked?.meta.id ?? view.latest?.meta.id) === h.id} onClick={() => onPick(h.id)}
              title={h.headline}>
              {i === 0 ? 'latest' : ago(h.generatedAt)}
            </Chip>
          ))}
        </div>
      )}
    </div>
  );
}

function Document({ report, view }: { report: Report; view: UnitReportView }) {
  const { open, lightbox } = useShotLightbox(report.shots);
  return (
    <>
      <AnalysisDoc
        doc={report.document}
        repoPath={view.repoPath}
        ideUrl={view.ideUrl}
        diffUrl={unitReportDiffUrl(report.meta.id)}
        shots={report.shots}
        onOpenShot={open}
      />
      {lightbox}
    </>
  );
}

/**
 * How the unit was built, ticket by ticket: what each carried, when it landed, how often it was sent
 * back and what it cost. Counted off the board, so it is never the model's to get wrong.
 */
function HowItWent({ tickets }: { tickets: UnitReportTicket[] }) {
  const [opened, setOpened] = useState<number | null>(null);
  if (tickets.length === 0) return null;
  const minutes = tickets.reduce((n, t) => n + t.cost.minutes, 0);
  const bounces = tickets.reduce((n, t) => n + t.bounces.length, 0);
  const study = (id: number) => window.dispatchEvent(new CustomEvent('open-classroom', { detail: { ticketId: id } }));

  return (
    <section style={{ marginTop: 26 }}>
      <div className="flex items-baseline gap-3 flex-wrap">
        <span style={label()}>How it went</span>
        <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)' }}>
          {tickets.length} ticket{tickets.length === 1 ? '' : 's'} · {bounces} sent back · {Math.round(minutes)} min of worker time
        </span>
      </div>
      <div style={{ marginTop: 8, border: '1px solid rgb(var(--w-line-rgb) / .1)', clipPath: chamfer(8) }}>
        {tickets.map(t => (
          <div key={t.id} style={{ borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}>
            <div className="flex items-baseline gap-x-3 gap-y-1 max-sm:flex-wrap" style={{ padding: '8px 12px' }}>
              <button
                className="orbit-pick min-w-0 flex-1 flex items-baseline gap-3 text-left"
                aria-current={opened === t.id ? 'true' : undefined}
                onClick={() => setOpened(opened === t.id ? null : t.id)}
                title={t.bounces.length > 0 ? 'Why it was sent back' : undefined}
              >
                <span style={{ font: `600 12px ${MONO}`, color: 'var(--w-t400)', flex: 'none' }}>#{t.id}</span>
                <span className="min-w-0 truncate" style={{ font: `400 12.5px ${BODY}`, color: 'var(--w-t100)' }}>{t.title}</span>
              </button>
              <span style={{ font: `400 11px ${MONO}`, color: 'var(--w-t500)', flex: 'none' }}>{t.outcomes.join(' ')}</span>
              <span style={{ font: `400 11px ${BODY}`, color: t.phase === 'Done' ? TIER.done.c : TIER.active.c, flex: 'none' }}>
                {t.phase === 'Done' && t.landedAt ? `landed ${ago(t.landedAt)}` : PHASE_LABEL[t.phase as Phase] ?? t.phase}
              </span>
              <span style={{ font: `400 11px ${BODY}`, color: t.bounces.length > 0 ? TIER.gate.c : 'var(--w-t600)', flex: 'none' }}>
                {t.bounces.length} back
              </span>
              <span style={{ font: `400 11px ${BODY}`, color: 'var(--w-t600)', flex: 'none' }}>
                {t.cost.runs} runs · {Math.round(t.cost.minutes)} min
              </span>
              <GhostButton onClick={() => study(t.id)} title="Open this ticket in the Classroom">Study</GhostButton>
            </div>
            {opened === t.id && t.bounces.length > 0 && (
              <div className="orbit-swap" style={{ padding: '0 12px 12px' }}><Bounces bounces={t.bounces} /></div>
            )}
          </div>
        ))}
      </div>
    </section>
  );
}

function Box({ tone, children }: { tone: string; children: ReactNode }) {
  return (
    <div style={{ marginTop: 12, padding: '10px 12px', clipPath: chamfer(8), background: fade(tone, 7), border: `1px solid ${fade(tone, 30)}` }}>
      {children}
    </div>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t500)', padding: '14px 16px' }}>{children}</div>;
}

function Bad({ children }: { children: ReactNode }) {
  return <div style={{ font: `400 12px ${BODY}`, color: TIER.blocked.c, marginTop: 8 }}>{children}</div>;
}
