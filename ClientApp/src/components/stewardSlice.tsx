import { useMemo, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { BODY, MONO, TIER, fade } from './orbit';
import { label, remember, remembered } from './stewardParts';
import { PHASE_LABEL } from '../types/tickets';
import type { Phase } from '../types/tickets';
import type {
  StewardDecision, StewardMode, StewardPlanOutcome, StewardPlanUnitView, StewardQuestion, StewardRegister,
} from '../api/steward';

/**
 * A slice read as what it promises: its story, step by step, each step citing the outcomes that
 * make it true; where each outcome stands, in one of five looks rather than one blended percent;
 * what stands between the slice and built, by who has the next move; and the decisions it is held to.
 *
 * Everything here is read off what the plan view, the page and the register already return. The
 * story is found and split by the same rules the board checks a draft with (`Services/SliceStory.cs`),
 * so a step here is a sentence there.
 */

// ---- the story ----------------------------------------------------------------------------

export interface Step { text: string; cites: string[] }

const HEADING = /^##\s+(?:the\s+walk-?through\b.*|(?:the\s+)?story\b.*)$/i;
const SECTION_END = /^#{1,2}\s/;
const CITATION = /\(([^()]*)\)/g;
const CITES_ONLY = /^\s*[A-Z]{1,2}\d+(?:\s*(?:,|and|&|;)\s*[A-Z]{1,2}\d+)*\s*$/;

/** The ids one parenthesis cites, or none when it holds anything else. */
function citedIn(inside: string): string[] {
  return CITES_ONLY.test(inside) ? [...inside.matchAll(/\b([A-Z]{1,2}\d+)\b/g)].map(m => m[1]) : [];
}

/** A slice's story as its steps, or null when its spec has no story section. */
export function storyOf(spec: string): Step[] | null {
  const lines = spec.replace(/\r\n/g, '\n').split('\n');
  const h = lines.findIndex(l => HEADING.test(l.trim()));
  if (h < 0) return null;
  let end = h + 1;
  while (end < lines.length && !SECTION_END.test(lines[end])) end++;
  // The story is one paragraph. Specs written before the check often follow it with a line about
  // it ("everything in the slice is in service of that paragraph"), which is not a step.
  const section = lines.slice(h + 1, end).map(l => l.trim()).filter(l => !l.startsWith('>'));
  const first = section.findIndex(l => l.length > 0);
  const stop = first < 0 ? 0 : section.findIndex((l, i) => i > first && l.length === 0);
  const body = section.slice(Math.max(first, 0), stop < 0 ? section.length : stop).join(' ').replace(/\s+/g, ' ').trim();
  if (!body) return null;
  return body.split(/(?<=[.!?])\s+(?=[A-Z"(])/).map(s => s.trim()).filter(Boolean).map(text => ({
    text,
    cites: [...new Set([...text.matchAll(CITATION)].flatMap(m => citedIn(m[1])))],
  }));
}

/** Which steps cite each outcome. */
export function servedBy(steps: Step[] | null): Map<string, number[]> {
  const out = new Map<string, number[]>();
  (steps ?? []).forEach((s, i) => s.cites.forEach(c => out.set(c, [...(out.get(c) ?? []), i + 1])));
  return out;
}

// ---- where each outcome stands --------------------------------------------------------------

export type Look = 'done' | 'moving' | 'blocked' | 'you' | 'todo';
export type Who = 'you' | 'blocked' | 'steward' | 'fleet';

export const LOOK: Record<Look, { word: string; c: string }> = {
  done: { word: 'done', c: TIER.done.c },
  moving: { word: 'moving', c: TIER.active.c },
  blocked: { word: 'blocked', c: TIER.blocked.c },
  you: { word: 'waits on you', c: TIER.gate.c },
  todo: { word: 'not started', c: 'var(--w-t500)' },
};
const LOOKS: Look[] = ['done', 'moving', 'blocked', 'you', 'todo'];

export interface Standing {
  look: Look;
  /** Whose move it is next, for an outcome that is not done. */
  who?: Who;
  /** Why, in a line. */
  why?: string;
  /** The question on Now that holds it, when one does. */
  question?: StewardQuestion;
}

/** A ticket's phase in a few words: "in QA", "being specced". */
export function phaseWord(phase: string): string {
  const words: Partial<Record<Phase, string>> = {
    Backlog: 'waiting to start', NeedsSpeculation: 'to be specced', Speculating: 'being specced',
    NeedsApproval: 'spec waits for approval', NeedsImplementation: 'to be built', Implementing: 'being built',
    NeedsImplementationReview: 'built, to be checked', NeedsQa: 'to be tested', QaTesting: 'being tested',
    NeedsRefinement: 'to be reworked', Refining: 'being reworked', NeedsPeerReview: 'to be peer reviewed',
    PeerReviewing: 'being peer reviewed', NeedsReview: 'in review', Blocked: 'blocked', Done: 'merged',
  };
  return words[phase as Phase] ?? (PHASE_LABEL[phase as Phase] ?? phase).toLowerCase();
}

/** "slice 5 D1", "slice 5 C2 and C3": the outcomes a question's text names for a slice. */
function namedFor(text: string, unit: number): Set<string> {
  const out = new Set<string>();
  const re = new RegExp(`\\b(?:slice|unit)\\s+0*${unit}\\b((?:[\\s,]+(?:and\\s+)?[A-Z]{1,2}\\d+\\b)+)`, 'gi');
  for (const m of text.matchAll(re)) for (const id of m[1].matchAll(/\b([A-Z]{1,2}\d+)\b/g)) out.add(id[1]);
  return out;
}

/**
 * Where each outcome of a slice stands. Waiting on you: a question on Now holds one of its tickets
 * or names it, or a ticket of it waits for a step only you take (approving a spec; a merge, when the
 * steward does not merge). Blocked: a ticket of it is. Otherwise the fleet has it while a ticket is
 * open, and the steward has it when there is more to file, nothing filed, or only merged work left
 * for it to call done.
 */
export function standings(unit: StewardPlanUnitView, waiting: StewardQuestion[], mode: StewardMode | undefined): Map<string, Standing> {
  const out = new Map<string, Standing>();
  const outcomes = unit.groups.flatMap(g => g.outcomes);
  for (const o of outcomes) {
    if (o.state === 'Done') { out.set(o.outcome, { look: 'done' }); continue; }
    const open = o.tickets.filter(t => t.phase !== 'Done');
    const ids = new Set(o.tickets.map(t => t.id));
    // What a question holds is its own ticket, or the first one its "held up" line names (the rest
    // of that line is what waits behind it), or an outcome it names by slice.
    const holds = (w: StewardQuestion) => w.ticketId ?? (Number(/#(\d+)\b/.exec(w.heldUp ?? '')?.[1]) || null);
    const q = unit.agreed ? waiting.find(w => {
      const t = holds(w);
      return (t != null && ids.has(t)) || namedFor(`${w.heldUp} ${w.question}`, unit.unit).has(o.outcome);
    }) : undefined;
    const blocked = open.find(t => t.phase === 'Blocked');
    const yours = open.find(t => t.phase === 'NeedsApproval' || (t.phase === 'NeedsReview' && mode !== 'full'));
    const list = (ts: typeof open) => ts.map(t => `#${t.id} ${phaseWord(t.phase)}`).join(', ');
    if (q) out.set(o.outcome, { look: 'you', who: 'you', why: q.question, question: q });
    else if (blocked) out.set(o.outcome, { look: 'blocked', who: 'blocked', why: o.note || list(open) });
    else if (yours) out.set(o.outcome, { look: 'you', who: 'you', why: `#${yours.id} ${phaseWord(yours.phase)}${yours.phase === 'NeedsReview' ? ': the merge is yours' : ''}` });
    else if (open.length > 0) out.set(o.outcome, { look: 'moving', who: 'fleet', why: o.note || list(open) });
    else if (isWalkthrough(o)) out.set(o.outcome, { look: o.state === 'InProgress' ? 'moving' : 'todo', who: 'fleet', why: o.note || 'The board files it once every other outcome is done.' });
    else out.set(o.outcome, {
      look: o.state === 'InProgress' || o.tickets.length > 0 ? 'moving' : 'todo', who: 'steward',
      why: o.note || (o.moreToFile ? 'More to file.' : o.tickets.length > 0 ? 'Everything filed is merged: to be called done.' : 'Not filed yet.'),
    });
  }
  return out;
}

/** The outcome the board files a walk-through for: a Z outcome whose sentence says so. */
export const isWalkthrough = (o: StewardPlanOutcome) => /^Z\d+$/.test(o.outcome) && /walk-?through/i.test(o.text);

// ---- the strip ---------------------------------------------------------------------------

/** One cell per outcome, grouped by letter. `mini` is the 4px line a card in the plan carries. */
export function Strip({ unit, standing, mini, focus, onPick }: {
  unit: StewardPlanUnitView; standing?: Map<string, Standing>; mini?: boolean;
  focus?: string | null; onPick?: (outcome: string) => void;
}) {
  const groups = unit.groups.filter(g => g.outcomes.length > 0);
  const lookOf = (o: StewardPlanOutcome): Look => standing?.get(o.outcome)?.look ?? (o.state === 'Done' ? 'done' : o.state === 'InProgress' ? 'moving' : 'todo');
  if (mini) {
    return (
      <div className="flex" style={{ gap: 1.5, marginTop: 7 }} aria-hidden="true">
        {groups.map((g, gi) => (
          <span key={g.heading} className="flex" style={{ gap: 1.5, flex: g.outcomes.length, marginLeft: gi ? 3 : 0 }}>
            {g.outcomes.map(o => <i key={o.outcome} style={{ height: 3, flex: 1, background: cellFill(lookOf(o), true) }} />)}
          </span>
        ))}
      </div>
    );
  }
  return (
    <div className="flex flex-wrap" style={{ gap: '8px 10px' }}>
      {groups.map(g => (
        <div key={g.heading} className="flex flex-col" style={{ gap: 4 }}>
          <span style={label()}>{letter(g.heading)}</span>
          <div className="flex" style={{ gap: 2 }}>
            {g.outcomes.map(o => {
              const l = lookOf(o);
              return (
                <button
                  key={o.outcome} type="button" onClick={() => onPick?.(o.outcome)}
                  className="orbit-tap"
                  aria-pressed={focus === o.outcome}
                  title={`${o.outcome} ${LOOK[l].word}${standing?.get(o.outcome)?.why ? ` - ${standing.get(o.outcome)!.why}` : ''}`}
                  style={{
                    width: 18, height: 18, background: cellFill(l, false),
                    border: `1px solid ${focus === o.outcome ? 'var(--w-t100)' : l === 'todo' ? 'rgb(var(--w-line-rgb) / .22)' : fade(LOOK[l].c, 70)}`,
                  }}
                />
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

function cellFill(l: Look, mini: boolean): string {
  if (l === 'todo') return mini ? 'rgb(var(--w-line-rgb) / .18)' : 'rgb(var(--w-line-rgb) / .05)';
  // A moving cell is striped, so "started" never reads as "done" at a glance.
  if (l === 'moving' && !mini) return `repeating-linear-gradient(135deg, ${fade(LOOK.moving.c, 60)} 0 4px, ${fade(LOOK.moving.c, 20)} 4px 8px)`;
  return fade(LOOK[l].c, mini ? 90 : 60);
}

/** A heading's letter: "A. Roles" is A. */
const letter = (heading: string) => /^([A-Z]{1,2})\b/.exec(heading.trim())?.[1] ?? (heading.trim()[0] ?? '-');

/** "2 done - 5 moving - 1 blocked", each count in its own colour. */
export function Counts({ unit, standing }: { unit: StewardPlanUnitView; standing: Map<string, Standing> }) {
  const n: Partial<Record<Look, number>> = {};
  for (const s of standing.values()) n[s.look] = (n[s.look] ?? 0) + 1;
  const total = unit.groups.reduce((a, g) => a + g.outcomes.length, 0);
  return (
    <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
      {LOOKS.filter(l => n[l]).map((l, i) => (
        <span key={l}>{i > 0 && ' - '}<b style={{ fontWeight: 600, color: LOOK[l].c }}>{n[l]}</b> {LOOK[l].word}</span>
      ))}
      <span style={{ color: 'var(--w-t600)' }}> of {total}</span>
    </span>
  );
}

// ---- the story, shown ------------------------------------------------------------------------

/** One citation, in its outcome's look. Pressing it narrows the slice to that outcome. */
export function Cite({ id, standing, focus, onPick }: {
  id: string; standing: Map<string, Standing>; focus: string | null; onPick: (id: string) => void;
}) {
  const l = standing.get(id)?.look ?? 'todo';
  const c = LOOK[l].c;
  return (
    <button
      type="button" onClick={() => onPick(id)} aria-pressed={focus === id}
      className="orbit-tap"
      title={`${id}: ${LOOK[l].word}`}
      style={{
        font: `700 10px ${MONO}`, padding: '0 5px 1px', margin: '0 1px', verticalAlign: 1, whiteSpace: 'nowrap',
        color: l === 'todo' ? 'var(--w-t400)' : c,
        border: `1px solid ${focus === id ? 'var(--w-t100)' : l === 'todo' ? 'rgb(var(--w-line-rgb) / .22)' : fade(c, 45)}`,
        background: l === 'todo' ? 'rgb(var(--w-line-rgb) / .04)' : fade(c, 9),
      }}
    >
      {id}
    </button>
  );
}

/** A step's look: done when all it cites is, otherwise the most pressing look among them. */
function stepLook(s: Step, standing: Map<string, Standing>): Look {
  const ls = s.cites.map(c => standing.get(c)?.look ?? 'todo');
  if (ls.length > 0 && ls.every(l => l === 'done')) return 'done';
  for (const l of ['blocked', 'you', 'moving'] as Look[]) if (ls.includes(l)) return l;
  return ls.includes('done') ? 'moving' : 'todo';
}

type StoryShow = 'steps' | 'paragraph';

export function StoryView({ steps, standing, focus, onPick, agreed, render }: {
  steps: Step[]; standing: Map<string, Standing>; focus: string | null; onPick: (id: string) => void;
  /** A draft's steps have no standing yet: no look is shown for them. */
  agreed: boolean;
  /** The sentence's own emphasis and code. */
  render: (text: string) => ReactNode;
}) {
  const [show, setShow] = useState<StoryShow>(() => (remembered('plan.story') === 'paragraph' ? 'paragraph' : 'steps'));
  const pick = (s: StoryShow) => { setShow(s); remember('plan.story', s); };
  const spot = (s: Step) => (focus ? (s.cites.includes(focus) ? 'lit' : 'dim') : undefined);
  const bare = (t: string) => t.replace(CITATION, (m, inside: string) => (citedIn(inside).length > 0 ? '' : m)).replace(/\s+([.,;:!?])/g, '$1').replace(/\s{2,}/g, ' ').trim();

  return (
    <div>
      <div className="flex items-center gap-3 flex-wrap" style={{ marginBottom: 8 }}>
        <span style={label()}>The walk-through this slice makes possible</span>
        <span className="flex" style={{ marginLeft: 'auto', gap: 0, border: '1px solid rgb(var(--w-line-rgb) / .18)' }} role="group" aria-label="How to show the story">
          {(['steps', 'paragraph'] as StoryShow[]).map(s => (
            <button key={s} type="button" onClick={() => pick(s)} aria-pressed={show === s} className="orbit-tap"
              style={{
                ...label(show === s ? 'var(--w-accent-text-lit)' : 'var(--w-t400)'), padding: '5px 10px',
                background: show === s ? 'rgb(var(--w-accent-rgb) / .14)' : 'transparent',
              }}>
              {s === 'steps' ? 'Step by step' : 'Paragraph'}
            </button>
          ))}
        </span>
      </div>

      {show === 'paragraph'
        ? (
          <div key="paragraph" className="orbit-swap" style={{ font: `400 14px/1.75 ${BODY}`, color: 'var(--w-t200)', maxWidth: '74ch' }}>
            {steps.map((s, i) => (
              <span key={i} className="orbit-spot spot-flat" data-spot={spot(s)}>
                {splitCites(s.text).map((p, j) => typeof p === 'string'
                  ? <span key={j}>{render(p)}</span>
                  : <span key={j}>({p.map((id, k) => <Cite key={k} id={id} standing={standing} focus={focus} onPick={onPick} />)})</span>)}
                {' '}
              </span>
            ))}
          </div>
        )
        : (
          <ol key="steps" className="orbit-swap" style={{ margin: 0, padding: 0, listStyle: 'none' }}>
            {steps.map((s, i) => {
              const l = stepLook(s, standing);
              return (
                <li key={i} className="orbit-spot spot-flat grid gap-x-3 gap-y-1 max-sm:grid-cols-[22px_minmax(0,1fr)] grid-cols-[22px_minmax(0,1fr)_auto]"
                  data-spot={spot(s)}
                  style={{ padding: '8px 0', borderTop: i ? '1px solid rgb(var(--w-line-rgb) / .07)' : undefined, alignItems: 'start' }}>
                  <span style={{ font: `700 11px ${MONO}`, color: agreed && l === 'done' ? TIER.done.c : 'var(--w-t500)', paddingTop: 2 }}>{i + 1}</span>
                  <span className="min-w-0" style={{ font: `400 13.5px/1.55 ${BODY}`, color: agreed && l === 'done' ? 'var(--w-t400)' : 'var(--w-t200)' }}>{render(bare(s.text))}</span>
                  <span className="flex items-center flex-wrap gap-1 max-sm:col-start-2" style={{ paddingTop: 2, justifyContent: 'flex-end' }}>
                    {agreed && <span style={{ ...label(LOOK[l].c), marginRight: 4 }}>{LOOK[l].word}</span>}
                    {s.cites.map(id => <Cite key={id} id={id} standing={standing} focus={focus} onPick={onPick} />)}
                  </span>
                </li>
              );
            })}
          </ol>
        )}
    </div>
  );
}

/** A sentence cut at its citations: text, then the ids a parenthesis cites, then text. */
function splitCites(text: string): (string | string[])[] {
  const out: (string | string[])[] = [];
  let last = 0;
  for (const m of text.matchAll(CITATION)) {
    const ids = citedIn(m[1]);
    if (ids.length === 0) continue;
    out.push(text.slice(last, m.index));
    out.push(ids);
    last = m.index! + m[0].length;
  }
  out.push(text.slice(last));
  return out.filter(p => p !== '');
}

// ---- between the slice and built ---------------------------------------------------------------

const WHO: { who: Who; title: string; c: string }[] = [
  { who: 'you', title: 'You', c: TIER.gate.c },
  { who: 'blocked', title: 'Blocked', c: TIER.blocked.c },
  { who: 'steward', title: 'The steward', c: TIER.queue.c },
  { who: 'fleet', title: 'The fleet and the board', c: TIER.active.c },
];

export function Between({ unit, standing, onPick, onNow }: {
  unit: StewardPlanUnitView; standing: Map<string, Standing>; onPick: (id: string) => void;
  /** Go to Now, where a question is answered. */
  onNow?: () => void;
}) {
  // A column longer than a few lines pushes the story off the screen: the rest is a press away.
  const [all, setAll] = useState(false);
  const FEW = 4;
  const left = unit.groups.flatMap(g => g.outcomes).filter(o => standing.get(o.outcome)?.look !== 'done');
  if (left.length === 0) return null;
  const by = (w: Who) => left.filter(o => standing.get(o.outcome)?.who === w);
  const yours = by('you').length, blocked = by('blocked').length;
  const cols = WHO.filter(w => by(w.who).length > 0);
  return (
    <div style={{ marginTop: 14, border: '1px solid rgb(var(--w-line-rgb) / .1)', background: 'rgb(var(--w-line-rgb) / .025)' }}>
      <div style={{ padding: '9px 12px', font: `400 13px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>
        <b style={{ fontWeight: 600, color: 'var(--w-t100)' }}>{left.length} outcome{left.length === 1 ? '' : 's'}</b> between this slice and built
        {yours + blocked > 0 && <>: {[
          yours > 0 && <b key="y" style={{ fontWeight: 600, color: TIER.gate.c }}>{yours} wait{yours === 1 ? 's' : ''} on you</b>,
          blocked > 0 && <b key="b" style={{ fontWeight: 600, color: TIER.blocked.c }}>{blocked} {blocked === 1 ? 'is' : 'are'} blocked</b>,
        ].filter(Boolean).reduce<ReactNode[]>((a, x, i) => (i ? [...a, ', ', x] : [x]), [])}</>}.
      </div>
      <div className="grid" style={{ gridTemplateColumns: `repeat(auto-fit, minmax(220px, 1fr))`, borderTop: '1px solid rgb(var(--w-line-rgb) / .08)' }}>
        {cols.map(w => (
          <div key={w.who} className="min-w-0 flex flex-col" style={{ padding: '10px 12px', gap: 8, borderLeft: '1px solid rgb(var(--w-line-rgb) / .06)' }}>
            <div className="flex items-baseline gap-2">
              <span style={{ font: `700 16px ${MONO}`, color: w.c }}>{by(w.who).length}</span>
              <span style={label(w.c)}>{w.title}</span>
            </div>
            {by(w.who).filter((_, i) => all || i < FEW).map(o => {
              const s = standing.get(o.outcome)!;
              return (
                <div key={o.outcome} className="flex gap-2 min-w-0" style={{ font: `400 12px/1.45 ${BODY}`, color: 'var(--w-t300)' }}>
                  <button type="button" onClick={() => onPick(o.outcome)} className="orbit-tap flex-none"
                    style={{ font: `700 10.5px/1.6 ${MONO}`, color: LOOK[s.look].c, alignSelf: 'flex-start' }} title="Show this outcome">{o.outcome}</button>
                  <div className="min-w-0">
                    <Why text={s.why ?? ''} />
                    {s.question && onNow && (
                      <button type="button" onClick={onNow} className="orbit-tap" style={{ ...label('var(--w-accent-text)'), marginTop: 3 }}>Answer on Now</button>
                    )}
                  </div>
                </div>
              );
            })}
            {by(w.who).length > FEW && (
              <button type="button" onClick={() => setAll(a => !a)} className="orbit-tap text-left" style={label('var(--w-t300)')}>
                {all ? 'Fewer' : `${by(w.who).length - FEW} more`}
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

/** A steward's note can be a paragraph: three lines of it, and the rest on a press. */
function Why({ text }: { text: string }) {
  const [whole, setWhole] = useState(false);
  const long = text.length > 160;
  if (!long) return <div>{text}</div>;
  return (
    <button type="button" onClick={() => setWhole(w => !w)} aria-expanded={whole} className="orbit-tap text-left block"
      title={whole ? 'Show less' : 'Show all of it'} style={{ font: 'inherit', color: 'inherit' }}>
      <span className={whole ? '' : 'line-clamp-3'}>{text}</span>
    </button>
  );
}

// ---- the decisions a slice is held to ----------------------------------------------------------

export interface SliceDecisionSet {
  own: StewardDecision[];
  /** Decisions of the project or of other slices that this slice's text cites by number. */
  cited: StewardDecision[];
  /** Every decision number known to the register, so text can link only real ones. */
  known: Set<number>;
  /** Touched by a question waiting on Now. */
  touched: Set<number>;
}

export function sliceDecisions(unit: StewardPlanUnitView, register: StewardRegister | null, waiting: StewardQuestion[]): SliceDecisionSet | null {
  if (!register) return null;
  const live = register.decisions.filter(d => d.status === 'inForce' || d.status === 'provisional');
  const own = live.filter(d => d.unit === unit.unit && (d.plan ?? '') === (register.plan ?? ''));
  const ownSet = new Set(own.map(d => d.number));
  const text = `${storyOf(unit.text)?.map(s => s.text).join(' ') ?? ''} ${unit.groups.flatMap(g => g.outcomes.map(o => o.text)).join(' ')}`;
  const nums = new Set([...text.matchAll(/#(\d+)\b/g)].map(m => Number(m[1])));
  const cited = live.filter(d => nums.has(d.number) && !ownSet.has(d.number));
  const touched = new Set(waiting.flatMap(q => (q.touches ?? []).map(t => t.number)));
  return { own, cited, known: new Set(register.decisions.map(d => d.number)), touched };
}

export function DecisionsBeside({ set, lit, onLight, onAll }: {
  set: SliceDecisionSet; lit: number | null; onLight: (n: number | null) => void; onAll?: () => void;
}) {
  const [shown, setShown] = useState(8);
  const own = useMemo(() => [...set.own].sort((a, b) => a.number - b.number), [set.own]);
  if (own.length === 0 && set.cited.length === 0) {
    return <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)' }}>No decision in the register applies to this slice.</div>;
  }
  const row = (d: StewardDecision) => (
    <DecisionCard key={d.number} d={d} lit={lit === d.number} touched={set.touched.has(d.number)} onClick={() => onLight(lit === d.number ? null : d.number)} />
  );
  // A lit decision is always shown, however far down the list it sits.
  const visible = own.filter((d, i) => i < shown || d.number === lit);
  return (
    <div className="flex flex-col" style={{ gap: 8 }}>
      <div style={label()}>This slice's decisions - {own.length}</div>
      {visible.map(row)}
      {own.length > visible.length && (
        <button type="button" onClick={() => setShown(own.length)} className="orbit-tap text-left" style={label('var(--w-t300)')}>
          {own.length - visible.length} more
        </button>
      )}
      {set.cited.length > 0 && (
        <>
          <div style={{ ...label(), marginTop: 6 }}>Cited from elsewhere - {set.cited.length}</div>
          {set.cited.map(row)}
        </>
      )}
      {onAll && (
        <button type="button" onClick={onAll} className="orbit-tap text-left" style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)', marginTop: 2 }}>
          Every decision, on Decisions
        </button>
      )}
    </div>
  );
}

function DecisionCard({ d, lit, touched, onClick }: { d: StewardDecision; lit: boolean; touched: boolean; onClick: () => void }) {
  const tone = touched ? TIER.gate.c : 'var(--w-accent)';
  const style: CSSProperties = {
    padding: '7px 9px', textAlign: 'left', width: '100%',
    border: `1px solid ${lit ? fade(tone, 60) : touched ? fade(TIER.gate.c, 40) : 'rgb(var(--w-line-rgb) / .12)'}`,
    background: lit ? fade(tone, 8) : 'rgb(var(--w-line-rgb) / .03)',
  };
  return (
    <button type="button" id={`decision-${d.number}`} onClick={onClick} aria-current={lit ? 'true' : undefined} className="orbit-pick" style={style}>
      <div className={lit ? '' : 'line-clamp-3'} style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t300)' }}>
        <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-accent-text)', marginRight: 5 }}>#{d.number}</span>
        {d.title && <span style={{ fontWeight: 600, color: 'var(--w-t200)' }}>{d.title}. </span>}
        {d.rule}
      </div>
      <div style={{ font: `400 10.5px ${MONO}`, color: 'var(--w-t500)', marginTop: 4 }}>
        {[d.outcomes, d.status === 'provisional' ? `provisional until ${d.provisionalUntil ?? 'you say'}` : null, d.on].filter(Boolean).join(' - ')}
      </div>
      {touched && <div style={{ ...label(TIER.gate.c), marginTop: 4 }}>A question on Now touches it</div>}
    </button>
  );
}
