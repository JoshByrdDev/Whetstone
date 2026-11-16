import { useCallback, useEffect, useState } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { GhostButton, PageHead, Panel, SectionRule } from './ui';
import { MONO } from './orbit';
import { Empty, Stat, TD, TH, NUM, ago, pct } from './metrics/parts';
import { TicketDrawer } from './Board';
import type { DashboardState } from '../types/events';
import { CHANNEL_LABEL, getIntake, pollIntake, setIntakeMuted, testIntakeSource } from '../api/intake';
import type { IntakePage, IntakeSourceView } from '../api/intake';

/**
 * Intake, per project (docs/intake-plan.md, section 7): the sources and whether each is answering,
 * every problem production has reported and the ticket standing for it, and the messages no adapter
 * recognised - kept 14 days with their raw body, so a new kind of report is seen before anyone
 * writes its adapter. Read from the board; the page itself never reaches a queue except through
 * Poll now and Test connection, which a QA copy refuses.
 */
export function IntakePanel({ state }: { state: DashboardState }) {
  const [projects, project, setProject] = useProjectList('intake');
  const [page, setPage] = useState<IntakePage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [drawer, setDrawer] = useState<number | null>(null);
  const [said, setSaid] = useState<Record<string, string>>({});

  const load = useCallback(async () => {
    if (!project) return;
    try { setPage(await getIntake(project)); setError(null); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  }, [project]);

  useEffect(() => { setPage(null); void load(); }, [load]);
  useEffect(() => {
    const t = window.setInterval(() => void load(), 15000);
    return () => window.clearInterval(t);
  }, [load]);

  const pollNow = async () => {
    if (!project) return;
    setBusy(true);
    try { await pollIntake(project); await load(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  const test = async (s: IntakeSourceView) => {
    if (!project) return;
    setSaid(p => ({ ...p, [s.name]: 'Testing…' }));
    const r = await testIntakeSource(project, s.name).catch(e => ({ ok: false, error: String(e) }) as { ok: boolean; error?: string; waiting?: number | null });
    setSaid(p => ({ ...p, [s.name]: r.ok ? `Answers: ${r.waiting ?? 0} waiting.` : `Does not answer: ${r.error}` }));
  };

  const mute = async (id: number, muted: boolean) => {
    await setIntakeMuted(id, muted).catch(e => setError(String(e)));
    await load();
  };

  const c = page?.counts;
  return (
    <div className="mx-auto max-w-7xl px-3 py-4 sm:px-6">
      <PageHead
        eyebrow="Whetstone // production reports"
        title="Intake"
        sub={!page ? (error ?? 'Loading') : !page.on ? 'Intake is not switched on for this project' : undefined}
        left={<div style={{ paddingBottom: 5 }}><ProjectPicker projects={projects} project={project} onChange={setProject} /></div>}
        right={page?.on && <GhostButton onClick={pollNow} disabled={busy} title="Read every source of this project now, rather than at its interval">{busy ? 'Polling…' : 'Poll now'}</GhostButton>}
      />
      {error && page && <p className="mb-3 text-xs text-red-300">{error}</p>}
      {page && (
        <div className="flex min-w-0 flex-col gap-6">
          {page.problems.length > 0 && (
            <Panel tone="var(--w-blocked)">
              <div className="text-xs text-gray-300">The intake config has problems, so those sources are not read:</div>
              <ul className="mt-1 list-disc pl-5 text-xs text-gray-400">{page.problems.map(p => <li key={p}>{p}</li>)}</ul>
            </Panel>
          )}

          <section className="min-w-0">
            <SectionRule title="Sources" count={page.sources.length}
              meta={`at most ${page.limits.newPerHour} new an hour · ${page.limits.newPerDay} a day`} />
            {page.sources.length === 0 ? <Empty>No source. Add one in Settings, under the project's Intake.</Empty> : (
              <div className="grid min-w-0 grid-cols-1 gap-3 lg:grid-cols-2">
                {page.sources.map(s => <SourceCard key={s.name} s={s} note={said[s.name]} onTest={() => test(s)} />)}
              </div>
            )}
          </section>

          {c && (
            <section className="min-w-0">
              <SectionRule title="The last fortnight" meta="the message log is kept 14 days, as the queue keeps them" />
              <div className="flex flex-wrap gap-3">
                <Stat label="Reports received" value={String(c.received)} muted={!c.received}
                  context={c.unrecognised ? `${c.unrecognised} unrecognised` : 'every message taken off a queue'} />
                <Stat label="Tickets filed" value={String(c.filed)} muted={!c.filed}
                  context={`${c.ticketsAllTime} from intake in all`} />
                <Stat label="Repeats" value={c.repeatRatio == null ? '—' : pct(c.repeatRatio)} muted={c.repeatRatio == null}
                  context={`${c.noted} noted on an open ticket`} />
                <Stat label="Held by the limits" value={String(c.held)} muted={!c.held}
                  context={`${c.storms} storm ticket${c.storms === 1 ? '' : 's'}`} />
                <Stat label="Muted" value={String(c.mutedIssues)} muted={!c.mutedIssues}
                  context={`${c.muted} report${c.muted === 1 ? '' : 's'} counted and not filed`} />
              </div>
            </section>
          )}

          <section className="min-w-0">
            <SectionRule title="Issues" count={page.issues.length} meta="one problem, one ticket" />
            {page.issues.length === 0 ? <Empty>Nothing reported yet.</Empty> : (
              <Panel pad={false} className="min-w-0 overflow-x-auto">
                <table className="w-full min-w-[640px] table-fixed text-xs">
                  <thead>
                    <tr className="border-b border-hair text-left text-gray-500">
                      <th className={`${TH} w-[46%]`}>Problem</th>
                      <th className={`${TH} ${NUM} w-16`}>Seen</th>
                      <th className={`${TH} w-24`}>Last</th>
                      <th className={TH}>Ticket</th>
                      <th className={`${TH} w-24`} />
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-hairsoft">
                    {page.issues.map(i => (
                      <tr key={i.id} className={i.muted ? 'opacity-60' : undefined}>
                        <td className={TD}>
                          <div className="truncate text-gray-200" title={i.title}>{i.title}</div>
                          <div className="truncate text-[10.5px] text-gray-500">
                            {CHANNEL_LABEL[i.channel] ?? i.channel} · <span style={{ fontFamily: MONO }}>{i.fingerprint}</span> · {i.lastState}
                            {i.heldIn && ' · held back'}
                          </div>
                        </td>
                        <td className={`${TD} ${NUM} text-gray-300`} style={{ fontFamily: MONO }}>{i.occurrences}</td>
                        <td className={`${TD} text-gray-500`}>{ago(i.lastSeen)}</td>
                        <td className={TD}>
                          {i.ticket
                            ? <button className="orbit-tap max-w-full truncate text-left text-gray-300 underline decoration-gray-600 underline-offset-2"
                                onClick={() => setDrawer(i.ticket!.id)} title={i.ticket.title ?? ''}>
                                #{i.ticket.id} · {i.ticket.phase}
                              </button>
                            : <span className="text-gray-600">{i.muted ? 'muted' : i.heldIn ? 'on a storm ticket' : '—'}</span>}
                          {i.ticketHistory && i.ticketHistory.includes(',') && (
                            <div className="text-[10.5px] text-gray-600">came back: {i.ticketHistory.split(',').map(x => `#${x}`).join(' → ')}</div>
                          )}
                        </td>
                        <td className={`${TD} text-right`}>
                          <GhostButton tone={i.muted ? 'go' : 'plain'} onClick={() => mute(i.id, !i.muted)}
                            title={i.muted ? 'File this again when it is reported' : 'Count its reports and file nothing'}>
                            {i.muted ? 'Unmute' : 'Mute'}
                          </GhostButton>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </Panel>
            )}
          </section>

          {page.storms.length > 0 && (
            <section className="min-w-0">
              <SectionRule title="Storms" count={page.storms.length} meta="past the limits, one ticket per window" />
              <ul className="space-y-1 text-xs">
                {page.storms.map(s => (
                  <li key={s.window} className="flex min-w-0 gap-2">
                    <button className="orbit-tap shrink-0 text-gray-300 underline decoration-gray-600 underline-offset-2" onClick={() => setDrawer(s.ticketId)}>#{s.ticketId}</button>
                    <span className="min-w-0 truncate text-gray-500">{s.name}</span>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section className="min-w-0">
            <SectionRule title="Unrecognised" count={page.unrecognised.length} meta="kept 14 days, raw, for the next adapter" />
            {page.unrecognised.length === 0 ? <Empty>Every message was one intake knows how to read.</Empty> : (
              <div className="flex min-w-0 flex-col gap-2">
                {page.unrecognised.map(u => (
                  <Panel key={u.id} className="min-w-0">
                    <div className="flex min-w-0 flex-wrap gap-x-3 text-[11px] text-gray-500">
                      <span>{ago(u.handledAt)}</span><span>{u.source}</span>
                      <span className="min-w-0 truncate" style={{ fontFamily: MONO }}>{u.messageId}</span>
                    </div>
                    {u.note && <div className="mt-1 text-xs text-gray-400">{u.note}</div>}
                    {u.rawAttributes && u.rawAttributes !== '{}' && (
                      <div className="mt-1 break-all text-[11px] text-gray-500" style={{ fontFamily: MONO }}>{u.rawAttributes}</div>
                    )}
                    <pre className="mt-2 max-h-48 overflow-auto whitespace-pre-wrap break-all text-[11px] text-gray-300" style={{ fontFamily: MONO }}>{u.rawBody}</pre>
                  </Panel>
                ))}
              </div>
            )}
          </section>

          <section className="min-w-0">
            <SectionRule title="Recent messages" count={page.recent.length} />
            {page.recent.length === 0 ? <Empty>No message has been taken off a queue in the last fortnight.</Empty> : (
              <ul className="space-y-1 text-xs">
                {page.recent.map(m => (
                  <li key={m.id} className="flex min-w-0 items-baseline gap-2">
                    <span className="w-16 shrink-0 text-gray-500">{ago(m.handledAt)}</span>
                    <span className="w-20 shrink-0 text-gray-400">{m.outcome}</span>
                    <span className="min-w-0 flex-1 truncate text-gray-300" title={m.note ?? ''}>{m.note}</span>
                    {m.ticketId != null && (
                      <button className="orbit-tap shrink-0 text-gray-400 underline decoration-gray-600 underline-offset-2" onClick={() => setDrawer(m.ticketId!)}>#{m.ticketId}</button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      )}
      {drawer != null && <TicketDrawer id={drawer} state={state} onClose={() => setDrawer(null)} />}
    </div>
  );
}

function SourceCard({ s, note, onTest }: { s: IntakeSourceView; note?: string; onTest: () => void }) {
  const tone = s.lastError ? 'var(--w-blocked)' : s.backingUp ? 'var(--w-active)' : undefined;
  const status = s.lastError ? `Last poll failed: ${s.lastError}`
    : !s.lastPollAt ? 'Not polled yet'
    : s.backingUp ? `Backing up: ${s.waiting} waiting`
    : `Answering${s.waiting != null ? ` · ${s.waiting} waiting` : ''}`;
  return (
    <Panel tone={tone} className="min-w-0">
      <div className="flex min-w-0 items-baseline justify-between gap-3">
        <div className="min-w-0">
          <div className="text-[13px] text-gray-100">{s.name} <span className="text-gray-500">· {s.kind === 'standin' ? 'stand-in' : 'SQS'}</span></div>
          {s.queueUrl && <div className="truncate text-[11px] text-gray-500" style={{ fontFamily: MONO }} title={s.queueUrl}>{s.queueUrl}</div>}
        </div>
        <GhostButton onClick={onTest} title="Read the queue's attributes, and nothing else">Test connection</GhostButton>
      </div>
      <div className={`mt-2 text-xs ${s.lastError ? 'text-red-300' : 'text-gray-300'}`}>{status}</div>
      <div className="mt-1 text-[11px] text-gray-500">
        every {s.pollSeconds}s · last polled {s.lastPollAt ? ago(s.lastPollAt) : 'never'} · last message {s.lastReceivedAt ? ago(s.lastReceivedAt) : 'none in 14 days'}
      </div>
      {note && <div className="mt-1 text-[11px] text-gray-400">{note}</div>}
    </Panel>
  );
}
