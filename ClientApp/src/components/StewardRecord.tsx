import { useMemo, useState } from 'react';
import { Chip, GhostButton, Panel } from './ui';
import { BODY, MONO, TIER, chamfer } from './orbit';
import { Markdown } from './Markdown';
import { Fold, Quiet, ago, label, remember, remembered } from './stewardParts';
import { unseenRuling } from '../api/steward';
import type { ReleaseKind, StewardEntry, StewardQuestion } from '../api/steward';

/**
 * What a steward did in its owner's place, kept to be read back.
 *
 * It grows by a few lines with every ticket, for as long as the project runs, so nothing here is
 * shown as one list in the order it happened. A release is read by what it will do - the
 * migrations together, then what has to be set up, then what is the owner's to do - and each line
 * is ticked off as it is dealt with, which is what turns hundreds of notes into the handful still
 * outstanding. A line shows its opening and opens in full when asked.
 */

const KINDS: { id: ReleaseKind; title: string; says: string; tier: keyof typeof TIER }[] = [
  { id: 'Migration', title: 'Migrations', says: 'A change to the schema or the data that runs when this is released.', tier: 'blocked' },
  { id: 'Config', title: 'To set up', says: 'Something outside the code: a variable, a secret, infrastructure, an account.', tier: 'gate' },
  { id: 'Action', title: 'Yours to do', says: 'Something to do, check or approve: wording to read, a line for your own documents, a step by hand.', tier: 'active' },
  { id: 'Note', title: 'To know', says: 'Nothing to do: how a thing behaves, a limit, a reading somebody took.', tier: 'queue' },
];

type Mark = (id: number, mark: { handled?: boolean; release?: ReleaseKind }) => Promise<void>;

export function StewardRecord({ entries, answered, onMark, onKeep, onReverse, unit, onUnit }: {
  entries: StewardEntry[]; answered: StewardQuestion[]; onMark: Mark;
  onKeep: (id: number) => Promise<void>; onReverse: (id: number, because: string) => Promise<void>;
  /** Show only the notes that go out with this unit of the plan; null is all of them. */
  unit: number | null; onUnit: (unit: number | null) => void;
}) {
  // A note with no kind is one to know: the same reading the board gives it.
  const notes = useMemo(() => entries.filter(e => e.kind === 'BeforeRelease').map(e => ({ ...e, release: e.release ?? 'Note' as const })), [entries]);
  const calls = useMemo(() => entries.filter(e => e.kind === 'Call'), [entries]);
  const said = useMemo(() => entries.filter(e => e.kind === 'OwnerSaid'), [entries]);
  const rulings = useMemo(() => entries.filter(e => e.kind === 'Ruling'), [entries]);

  return (
    <>
      <Rulings rows={rulings} onKeep={onKeep} onReverse={onReverse} />
      <ReleaseNotes notes={notes} onMark={onMark} unit={unit} onUnit={onUnit} />

      <Fold id="Call" title="Calls it made for you" colour={TIER.gate.c} count={calls.filter(e => !e.handledAt).length}
        meta={calls.length > 0 ? `${calls.length} in all` : undefined}>
        {calls.length === 0
          ? <Quiet>No calls recorded yet.</Quiet>
          : <EntryList id="calls" rows={calls} onMark={onMark} tickTitle="Read" empty="Every call has been read." />}
      </Fold>

      <Fold id="OwnerSaid" title="What you told it" colour="var(--w-accent)" count={said.length}>
        {said.length === 0
          ? <Quiet>No standing instructions.</Quiet>
          : (
            <Panel pad={false} className="mb-8">
              {said.map(e => (
                <div key={e.id} style={{ padding: '9px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}>
                  <div style={label()}>{ago(e.at)}</div>
                  <div className="whitespace-pre-wrap" style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t200)', marginTop: 3 }}>{e.text}</div>
                </div>
              ))}
            </Panel>
          )}
      </Fold>

      <Fold id="answered" title="Answered" colour={TIER.done.c} count={answered.length}>
        {answered.length === 0
          ? <Quiet>No questions answered yet.</Quiet>
          : (
            <div className="flex flex-col gap-2 mb-8">
              {answered.map(q => (
                <Panel key={q.key}>
                  <div style={{ font: `600 12.5px ${BODY}`, color: 'var(--w-t200)' }}>{q.question}</div>
                  <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>{q.answer}</div>
                  <div style={{ ...label(), marginTop: 6 }}>{q.key} - answered {ago(q.answeredAt)}</div>
                </Panel>
              ))}
            </div>
          )}
      </Fold>
    </>
  );
}

/** What to read before a release, by what kind of thing each note is. */
function ReleaseNotes({ notes: every, onMark, unit, onUnit }: {
  notes: StewardEntry[]; onMark: Mark; unit: number | null; onUnit: (unit: number | null) => void;
}) {
  // A note goes out with one unit of the plan, and a release is one unit's: so the list can be
  // narrowed to exactly what that release is held for.
  const units = [...new Set(every.map(e => e.unit).filter((u): u is number => u != null))].sort((a, b) => a - b);
  const notes = unit == null ? every : every.filter(e => e.unit === unit);
  const left = (k: ReleaseKind) => notes.filter(e => e.release === k && !e.handledAt).length;
  const [kind, setKind] = useState<ReleaseKind>(() => {
    const was = remembered('record.kind') as ReleaseKind | null;
    if (was && KINDS.some(k => k.id === was)) return was;
    // Start on the first kind with anything outstanding: that is the order a release is read in.
    return KINDS.find(k => left(k.id) > 0)?.id ?? 'Migration';
  });
  const choose = (k: ReleaseKind) => { setKind(k); remember('record.kind', k); };

  const outstanding = notes.filter(e => !e.handledAt).length;
  const now = KINDS.find(k => k.id === kind)!;
  const rows = notes.filter(e => e.release === kind);

  return (
    <Fold id="BeforeRelease" open title="To read before a release" colour={TIER.active.c} count={outstanding}
      meta={notes.length > outstanding ? `${notes.length - outstanding} ticked off` : undefined}>
      {every.length === 0
        ? <Quiet>Nothing noted for release yet.</Quiet>
        : (
          <>
            {(units.length > 1 || unit != null) && (
              <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 10 }}>
                <span style={label()}>Going out with</span>
                <Chip on={unit == null} onClick={() => onUnit(null)}>Every unit</Chip>
                {units.map(u => (
                  <Chip key={u} on={unit === u} onClick={() => onUnit(u)}>Unit {String(u).padStart(2, '0')}</Chip>
                ))}
              </div>
            )}
            <div className="flex flex-wrap gap-2" style={{ marginBottom: 8 }}>
              {KINDS.map(k => (
                <Chip key={k.id} on={kind === k.id} onClick={() => choose(k.id)} title={k.says}>
                  {k.title} {left(k.id)}
                </Chip>
              ))}
            </div>
            <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)', margin: '0 0 10px 2px' }}>{now.says}</div>
            <EntryList key={kind} id={`release.${kind}`} rows={rows} onMark={onMark} tickTitle="Dealt with" retag
              empty={rows.length === 0 ? `Nothing of this kind has been noted.` : 'Everything of this kind is ticked off.'} />
          </>
        )}
    </Fold>
  );
}

/**
 * A long run of record lines made short: each shows who it is about and how it opens, and the
 * whole of it only when pressed. What has been ticked off is out of the way until asked for.
 */
function EntryList({ id, rows, onMark, tickTitle, empty, retag = false }: {
  id: string; rows: StewardEntry[]; onMark: Mark; tickTitle: string; empty: string; retag?: boolean;
}) {
  const [find, setFind] = useState('');
  const [ticked, setTicked] = useState(false);
  const [open, setOpen] = useState<number | null>(null);

  const needle = find.trim().toLowerCase();
  const shown = rows.filter(e => (ticked || !e.handledAt)
    && (!needle || `#${e.ticketId ?? ''} ${e.ticketTitle ?? ''} ${e.text}`.toLowerCase().includes(needle)));
  const tickedCount = rows.filter(e => e.handledAt).length;

  return (
    <div className="mb-8">
      <div className="flex items-center gap-2 flex-wrap" style={{ marginBottom: 8 }}>
        <input
          value={find} onChange={e => setFind(e.target.value)}
          placeholder="Find a ticket number or a word"
          aria-label="Find in these lines"
          className="flex-1 min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
          style={{ maxWidth: 320 }}
        />
        {tickedCount > 0 && (
          <Chip on={ticked} onClick={() => setTicked(!ticked)} title="Also show what has been ticked off">
            Ticked off {tickedCount}
          </Chip>
        )}
        <span style={{ ...label(), marginLeft: 'auto' }}>{shown.length} shown</span>
      </div>

      {shown.length === 0
        ? <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', padding: '4px 0 0 2px' }}>{needle ? 'Nothing matches.' : empty}</div>
        : (
          <Panel pad={false}>
            {shown.map(e => (
              <EntryRow
                key={`${id}.${e.id}`} e={e} open={open === e.id} tickTitle={tickTitle} retag={retag}
                onToggle={() => setOpen(open === e.id ? null : e.id)} onMark={onMark}
              />
            ))}
          </Panel>
        )}
    </div>
  );
}

/** How a line opens: its first sentence, or its first line when that is shorter. */
function opening(text: string): string {
  const first = text.split('\n').map(l => l.replace(/^\s*[-*]\s+/, '').trim()).find(l => l.length > 0) ?? '';
  return first.replace(/\*\*|`/g, '');
}

function EntryRow({ e, open, onToggle, onMark, tickTitle, retag }: {
  e: StewardEntry; open: boolean; onToggle: () => void; onMark: Mark; tickTitle: string; retag: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const mark = async (m: { handled?: boolean; release?: ReleaseKind }) => {
    setBusy(true);
    try { await onMark(e.id, m); } finally { setBusy(false); }
  };
  const done = !!e.handledAt;

  return (
    <div className="flex gap-3" style={{ padding: '8px 12px 8px 14px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)', opacity: done ? 0.55 : 1 }}>
      <button
        onClick={() => mark({ handled: !done })}
        disabled={busy}
        role="checkbox"
        aria-checked={done}
        title={done ? 'Put it back among what is outstanding' : `${tickTitle}: tick it off`}
        className="orbit-tap flex-none"
        style={{
          width: 18, height: 18, marginTop: 2, clipPath: chamfer(4),
          background: done ? TIER.done.c : 'rgb(var(--w-line-rgb) / .05)',
          border: `1px solid ${done ? TIER.done.c : 'rgb(var(--w-line-rgb) / .3)'}`,
          font: `700 11px ${MONO}`, color: 'var(--w-void)', lineHeight: '16px', textAlign: 'center',
        }}
      >
        {done ? 'x' : ''}
      </button>

      <div className="min-w-0 flex-1">
        <button
          onClick={onToggle}
          aria-expanded={open}
          aria-current={open ? 'true' : undefined}
          className="orbit-pick block w-full text-left min-w-0"
        >
          <div className="truncate" style={label()}>
            {e.ticketId != null ? <>#{e.ticketId} {e.ticketTitle} - </> : null}{ago(e.at)}
          </div>
          {!open && (
            <div className="line-clamp-2" style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)', marginTop: 2 }}>
              {opening(e.text)}
            </div>
          )}
        </button>
        {open && (
          <div key="open" className="orbit-swap" style={{ marginTop: 4 }}>
            <Markdown text={e.text} />
            {retag && (
              <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 10 }}>
                <span style={label()}>Filed under</span>
                {KINDS.map(k => (
                  <Chip key={k.id} on={e.release === k.id} disabled={busy} title={k.says}
                    onClick={() => { if (e.release !== k.id) mark({ release: k.id }); }}>
                    {k.title}
                  </Chip>
                ))}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * The steward's standing calls: each applies from the moment it is made and is handed to every run,
 * so this is where the owner sees the precedents being set in their name. New ones first, each with
 * what it rests on; keep it and it stops counting as new, reverse it and say what holds instead,
 * and the next run is told.
 */
function Rulings({ rows, onKeep, onReverse }: {
  rows: StewardEntry[]; onKeep: (id: number) => Promise<void>; onReverse: (id: number, because: string) => Promise<void>;
}) {
  const [showReversed, setShowReversed] = useState(false);
  const live = rows.filter(e => !e.reversedAt);
  const unseen = live.filter(unseenRuling);
  const kept = live.filter(e => !unseenRuling(e));
  const reversed = rows.filter(e => e.reversedAt);

  return (
    <Fold id="Ruling" open title="Its rulings" colour="var(--w-accent)" count={unseen.length}
      meta={live.length > 0 ? `${live.length} in force` : undefined}>
      {rows.length === 0
        ? <Quiet>No rulings yet. When the steward takes something as settled and means to apply it again, it shows here.</Quiet>
        : (
          <div className="flex flex-col gap-2 mb-8">
            {[...unseen, ...kept].map(e => <RulingCard key={e.id} e={e} onKeep={onKeep} onReverse={onReverse} />)}
            {live.length === 0 && <Quiet>No ruling is in force.</Quiet>}
            {reversed.length > 0 && (
              <div>
                <Chip on={showReversed} onClick={() => setShowReversed(!showReversed)} title="The rulings you reversed, and what you said instead">
                  Reversed {reversed.length}
                </Chip>
                {showReversed && (
                  <div key="rev" className="orbit-swap flex flex-col gap-2" style={{ marginTop: 8 }}>
                    {reversed.map(e => <RulingCard key={e.id} e={e} onKeep={onKeep} onReverse={onReverse} />)}
                  </div>
                )}
              </div>
            )}
          </div>
        )}
    </Fold>
  );
}

function RulingCard({ e, onKeep, onReverse }: {
  e: StewardEntry; onKeep: (id: number) => Promise<void>; onReverse: (id: number, because: string) => Promise<void>;
}) {
  const [reversing, setReversing] = useState(false);
  const [because, setBecause] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const isNew = unseenRuling(e);
  const gone = !!e.reversedAt;

  const act = async (fn: () => Promise<void>) => {
    setBusy(true); setFailed(null);
    try { await fn(); setReversing(false); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  return (
    <Panel tone={isNew ? 'var(--w-accent)' : undefined} style={{ opacity: gone ? 0.6 : 1 }}>
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div className="min-w-0 flex-1">
          <div style={label(isNew ? 'var(--w-accent-text)' : undefined)}>
            {isNew ? 'New' : gone ? 'Reversed' : 'Kept'}
            {e.ticketId != null ? <> - from #{e.ticketId}{e.ticketTitle ? ` ${e.ticketTitle}` : ''}</> : null}
            {' - '}{ago(e.at)}
          </div>
          <div style={{ font: `500 13px/1.5 ${BODY}`, color: 'var(--w-t100)', marginTop: 4, textDecoration: gone ? 'line-through' : undefined }}>
            {e.text}
          </div>
          {e.basis && (
            <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>
              <span style={{ color: 'var(--w-t500)' }}>Rests on: </span>{e.basis}
            </div>
          )}
          {gone && e.reversedBecause && (
            <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t300)', marginTop: 6 }}>
              <span style={{ color: 'var(--w-t500)' }}>You said instead ({ago(e.reversedAt)}): </span>{e.reversedBecause}
            </div>
          )}
        </div>
        {!gone && !reversing && (
          <div className="flex flex-none items-center gap-2">
            {isNew && <GhostButton tone="go" disabled={busy} onClick={() => act(() => onKeep(e.id))} title="Let it stand. It stops counting as new.">Keep</GhostButton>}
            <GhostButton disabled={busy} onClick={() => { setReversing(true); setFailed(null); }} title="Stop it applying, and say what holds instead">Reverse</GhostButton>
          </div>
        )}
      </div>

      {reversing && (
        <div key="reverse" className="orbit-swap" style={{ marginTop: 10 }}>
          <textarea
            autoFocus value={because} onChange={ev => setBecause(ev.target.value)} rows={3}
            placeholder="What should hold instead? The steward reads this as a decision you made."
            className="w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
          />
          <div className="flex items-center justify-end gap-2" style={{ marginTop: 6 }}>
            <GhostButton disabled={busy} onClick={() => setReversing(false)}>Cancel</GhostButton>
            <GhostButton tone="go" disabled={busy || !because.trim()} onClick={() => act(() => onReverse(e.id, because.trim()))}>
              {busy ? 'Reversing' : 'Reverse it'}
            </GhostButton>
          </div>
        </div>
      )}
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 6 }}>{failed}</div>}
    </Panel>
  );
}
