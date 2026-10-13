import { useMemo, useState } from 'react';
import { Chip, Panel } from './ui';
import { BODY, MONO, TIER, chamfer } from './orbit';
import { Markdown } from './Markdown';
import { Fold, Quiet, ago, label, remember, remembered } from './stewardParts';
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

export function StewardRecord({ entries, answered, onMark, unit, onUnit }: {
  entries: StewardEntry[]; answered: StewardQuestion[]; onMark: Mark;
  /** Show only the notes that go out with this unit of the plan; null is all of them. */
  unit: number | null; onUnit: (unit: number | null) => void;
}) {
  // A note with no kind is one to know: the same reading the board gives it.
  const notes = useMemo(() => entries.filter(e => e.kind === 'BeforeRelease').map(e => ({ ...e, release: e.release ?? 'Note' as const })), [entries]);
  const calls = useMemo(() => entries.filter(e => e.kind === 'Call'), [entries]);
  const said = useMemo(() => entries.filter(e => e.kind === 'OwnerSaid'), [entries]);

  return (
    <>
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
