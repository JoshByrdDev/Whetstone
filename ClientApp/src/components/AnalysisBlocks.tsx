import { createContext, Fragment, useCallback, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type {
  Block, CoverageSegment, Doc, FlowChange, FlowEdge, FlowNode, NodeDiff, Section, Verdict,
} from '../api/classroom';
import { getNodeDiff, isCoverage, nodeRefs, UNSETTLED } from '../api/classroom';
import type { LightboxShot } from './ShotLightbox';
import { inline } from './LearningBrief';
import { PatchView } from './PatchView';

/**
 * Where the analysis's file citations point.
 *
 * A context rather than a prop threaded through every block: only two of the eight kinds care, and
 * the two components between them would otherwise carry a prop they never read. Empty by default so
 * a document rendered without a project (a test, say) degrades to plain text rather than to a link
 * that opens nothing.
 */
const FileLinks = createContext<{ repoPath: string; ideUrl: string; analysisId: number | null }>(
  { repoPath: '', ideUrl: '', analysisId: null },
);

/**
 * The ticket's QA screenshots, and how to open one full size.
 *
 * A context rather than a prop because the two block kinds that want them — the coverage map and
 * the stops of a user flow — are each several components down. Empty by default: a document
 * rendered without a ticket (a test) shows the captions it was given and no pictures, rather than
 * reaching for images that are not there.
 */
const QaShots = createContext<{ shots: LightboxShot[]; open: (i: number) => void }>(
  { shots: [], open: () => {} },
);

/** Split "path/to/File.cs:88" — or ":88-104" — into its path and its first line. */
function splitLocation(location: string): { path: string; line: number | null } {
  const m = /^(.*?):(\d+)(?:-\d+)?$/.exec(location.trim());
  return m ? { path: m[1], line: Number(m[2]) } : { path: location.trim(), line: null };
}

/**
 * Where a model-written `file:line` actually points: the absolute path, and the editor URL that
 * opens it. Shared by the prose citations and by the nodes of a user-flow diagram, which need the
 * same answer inside an `<svg>` where `FileRef`'s markup cannot go.
 */
function resolveLocation(location: string, repoPath: string, ideUrl: string) {
  const { path, line } = splitLocation(location);

  // The model writes repo-relative paths; anything already absolute (or a bare filename it could not
  // place) is left alone rather than glued onto the repo root to make a path that does not exist.
  const rooted = /^([a-zA-Z]:|\/|~)/.test(path);
  const abs = rooted || !repoPath ? path : `${repoPath}/${path.replace(/^\.\//, '')}`;
  const href = ideUrl && (rooted || repoPath)
    ? ideUrl.replace('{path}', abs).replace('{line}', String(line ?? 1))
    : null;

  return { path, line, abs, href };
}

/**
 * One file citation, as a link into the reader's editor.
 *
 * The whole reason this exists: a `file:line` in prose is a copy-paste chore, and a chore attached
 * to every citation is how a reading path stops being read. Clicking opens the file in place.
 */
export function FileRef({ location, className = '' }: { location: string; className?: string }) {
  const { repoPath, ideUrl } = useContext(FileLinks);
  const [copied, setCopied] = useState(false);
  const { line, abs, href } = resolveLocation(location, repoPath, ideUrl);

  const copy = () => {
    navigator.clipboard?.writeText(line ? `${abs}:${line}` : abs).then(
      () => { setCopied(true); window.setTimeout(() => setCopied(false), 1200); },
      () => {},
    );
  };

  return (
    <span className="inline-flex items-baseline gap-1.5 min-w-0">
      {href ? (
        <a
          href={href}
          title={`Open ${abs}${line ? `:${line}` : ''} in your editor`}
          className={`font-mono text-sky-300 hover:text-sky-200 hover:underline decoration-dotted underline-offset-2 break-all ${className}`}
        >
          {location}
        </a>
      ) : (
        <span className={`font-mono text-sky-300 break-all ${className}`}>{location}</span>
      )}
      <button
        onClick={copy}
        title="Copy the absolute path"
        className="shrink-0 text-[10px] text-gray-600 hover:text-gray-300 transition-colors"
      >
        {copied ? 'copied' : 'copy'}
      </button>
    </span>
  );
}

/**
 * The Classroom's renderer.
 *
 * This file is the reason the analysis is asked for as typed blocks instead of markdown. The model
 * decides WHAT to say and which shape says it; everything about how it looks is decided here, once.
 * A run that has nothing real for a section returns no block and the section disappears — which is
 * the failure mode we want, rather than three paragraphs of filler that read like content.
 */
/**
 * What an analysis's blocks need from the page around them: where citations open, and the ticket's
 * screenshots. Exported for anything that renders a block outside the document - the walkthrough
 * shows the same flow diagram and callouts, and must resolve them the same way.
 */
export function AnalysisContext({ repoPath = '', ideUrl = '', analysisId = null, shots = [], onOpenShot, children }: {
  repoPath?: string; ideUrl?: string; analysisId?: number | null;
  shots?: LightboxShot[]; onOpenShot?: (i: number) => void; children: ReactNode;
}) {
  return (
    <FileLinks.Provider value={{ repoPath, ideUrl, analysisId }}>
      <QaShots.Provider value={{ shots, open: onOpenShot ?? (() => {}) }}>{children}</QaShots.Provider>
    </FileLinks.Provider>
  );
}

export function AnalysisDoc({ doc, repoPath = '', ideUrl = '', analysisId = null, shots = [], onOpenShot }: {
  doc: Doc; repoPath?: string; ideUrl?: string; analysisId?: number | null;
  shots?: LightboxShot[]; onOpenShot?: (i: number) => void;
}) {
  // The coverage map is lifted out of the document order and drawn first, unnumbered.
  //
  // The run is told to write it LAST, because ruling on requirements is only honest once it has
  // worked out what the change did — but "did we build what was asked for" is the question the
  // reader arrives with, and burying the answer at section 10 would leave them scrolling past nine
  // sections of teaching to find it. Where it is written and where it is read are different
  // decisions, and this file owns the second one.
  const map = doc.sections.filter(isCoverage);
  const spine = doc.sections.filter(s => !isCoverage(s));

  return (
    <AnalysisContext repoPath={repoPath} ideUrl={ideUrl} analysisId={analysisId} shots={shots} onOpenShot={onOpenShot}>
      <div className="flex flex-col gap-8">
        {map.map((s, i) => (
          <section key={`map-${i}`}>
            {s.blocks.map((b, bi) => <BlockView key={bi} block={b} />)}
          </section>
        ))}
        {spine.map((s, i) => (
          <SectionView key={i} index={i + 1} section={s} />
        ))}
      </div>
    </AnalysisContext>
  );
}

function SectionView({ index, section }: { index: number; section: Section }) {
  return (
    <section>
      <h3 className="flex items-baseline gap-2.5 mb-3">
        <span className="text-[11px] font-mono text-indigo-500/70 tabular-nums">
          {String(index).padStart(2, '0')}
        </span>
        <span className="text-[13px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-indigo-200">
          {section.title}
        </span>
        <span className="flex-1 h-px bg-gradient-to-r from-indigo-900/60 to-transparent" />
      </h3>

      <div className="flex flex-col gap-3.5 pl-[1.9rem]">
        {section.blocks.map((b, i) => <BlockView key={i} block={b} />)}
      </div>
    </section>
  );
}

function BlockView({ block }: { block: Block }) {
  switch (block.kind) {
    case 'prose': return <Prose block={block} />;
    case 'keyValue': return <KeyValue block={block} />;
    case 'comparison': return <Comparison block={block} />;
    case 'readingPath': return <ReadingPath block={block} />;
    case 'glossary': return <Glossary block={block} />;
    case 'callout': return <Callout block={block} />;
    case 'question': return <Question block={block} />;
    case 'code': return <CodeSnippet block={block} />;
    case 'userFlow': return <UserFlow block={block} />;
    case 'coverage': return <Coverage block={block} />;
    default: return null;
  }
}

/** A block's own heading, when it carries one. Optional on every kind but `prose`. */
function BlockTitle({ text }: { text?: string }) {
  if (!text) return null;
  return (
    <div className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 mb-1.5">
      {text}
    </div>
  );
}

function Prose({ block }: { block: Block }) {
  return (
    <p className="text-[13.5px] leading-[1.7] text-gray-300 max-w-[68ch]">
      {inline(block.text ?? '')}
    </p>
  );
}

/** Orientation facts at a glance — the things you would otherwise go and look up first. */
function KeyValue({ block }: { block: Block }) {
  return (
    <div>
      <BlockTitle text={block.title} />
      <dl className="grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-1.5 rounded-lg border border-hair bg-gray-900/40 px-4 py-3">
        {(block.items ?? []).map((it, i) => (
          <div key={i} className="contents">
            <dt className="text-[11px] uppercase tracking-[calc(var(--w-track)*0.06em)] text-gray-500 pt-0.5">{it.key}</dt>
            <dd className="text-[13px] text-gray-200 leading-relaxed">{inline(it.value)}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

/**
 * Any real trade-off or coverage matrix. Scrolls inside itself rather than widening the page — a
 * four-column table of rejected approaches is exactly the thing that would otherwise force the
 * whole analysis into a horizontal scroll.
 */
function Comparison({ block }: { block: Block }) {
  const columns = block.columns ?? [];
  const rows = block.rows ?? [];

  return (
    <div>
      <BlockTitle text={block.title} />
      <div className="overflow-x-auto rounded-lg border border-hair">
        <table className="w-full min-w-[34rem] border-collapse text-left">
          <thead>
            <tr className="bg-gray-900/70">
              {columns.map((c, i) => (
                <th key={i} className="px-3.5 py-2 text-[10.5px] font-semibold uppercase tracking-[calc(var(--w-track)*0.08em)] text-indigo-300/80 border-b border-hair">
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((r, ri) => (
              <tr key={ri} className={ri % 2 ? 'bg-gray-900/25' : undefined}>
                {r.map((cell, ci) => (
                  <td
                    key={ci}
                    className={`px-3.5 py-2.5 text-[12.5px] leading-snug align-top border-b border-hair ${
                      ci === 0 ? 'text-gray-200 font-medium' : 'text-gray-400'
                    }`}
                  >
                    {inline(cell)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {block.note && (
        <p className="mt-1.5 text-[11.5px] text-gray-500 italic max-w-[68ch]">{inline(block.note)}</p>
      )}
    </div>
  );
}

/**
 * The route through the code — ordered, because the order is the teaching. Each stop says what it is
 * and why it is the next one, so the path can be followed without holding the whole change in mind.
 */
function ReadingPath({ block }: { block: Block }) {
  const steps = block.steps ?? [];
  return (
    <div>
      <BlockTitle text={block.title ?? 'Reading path'} />
      <ol className="relative flex flex-col gap-3.5 rounded-lg border border-hair bg-gray-900/30 px-4 py-3.5">
        {steps.map((s, i) => (
          <li key={i} className="relative flex gap-3">
            <div className="flex flex-col items-center">
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-sky-800 bg-sky-950/60 text-[10px] font-mono text-sky-300 tabular-nums">
                {i + 1}
              </span>
              {i < steps.length - 1 && <span className="mt-1 w-px flex-1 bg-gray-800" />}
            </div>
            <div className="min-w-0 pb-0.5">
              <FileRef location={s.location} className="text-[12px]" />
              <div className="text-[13px] text-gray-300 leading-relaxed mt-0.5">{inline(s.what)}</div>
              {s.why && <div className="text-[12px] text-gray-500 leading-relaxed mt-0.5">{inline(s.why)}</div>}
            </div>
          </li>
        ))}
      </ol>
    </div>
  );
}

/** Domain vocabulary. The cheapest thing on the page and often the most useful. */
function Glossary({ block }: { block: Block }) {
  return (
    <div>
      <BlockTitle text={block.title ?? 'Vocabulary'} />
      <dl className="flex flex-col gap-2 rounded-lg border border-hair bg-gray-900/30 px-4 py-3">
        {(block.terms ?? []).map((t, i) => (
          <div key={i}>
            <dt className="font-mono text-[12px] text-indigo-300">{t.term}</dt>
            <dd className="text-[12.5px] text-gray-400 leading-relaxed">{inline(t.meaning)}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

const TONES: Record<string, { bar: string; bg: string; label: string; mark: string; text: string }> = {
  gotcha: { bar: 'border-amber-600/70', bg: 'bg-amber-950/20', label: 'text-amber-300', mark: '!', text: 'Gotcha' },
  insight: { bar: 'border-sky-600/70', bg: 'bg-sky-950/20', label: 'text-sky-300', mark: '*', text: 'Insight' },
  risk: { bar: 'border-rose-600/70', bg: 'bg-rose-950/20', label: 'text-rose-300', mark: '!', text: 'Risk' },
};

/** The thing that surprises a competent reader — deliberately loud, and deliberately rationed. */
export function Callout({ block }: { block: Block }) {
  const tone = TONES[block.tone ?? 'insight'] ?? TONES.insight;
  return (
    <div className={`rounded-r-lg border-l-2 ${tone.bar} ${tone.bg} px-4 py-2.5`}>
      <div className={`flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] ${tone.label} mb-1`}>
        <span className="font-mono">{tone.mark}</span>
        <span>{block.title ?? tone.text}</span>
      </div>
      <p className="text-[13px] leading-[1.65] text-gray-300 max-w-[66ch]">{inline(block.text ?? '')}</p>
    </div>
  );
}

/** The open question. Not a quiz — the thing the change raises and does not answer. */
export function Question({ block }: { block: Block }) {
  return (
    <div className="rounded-lg border border-indigo-900/60 bg-indigo-950/25 px-4 py-3.5">
      <div className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-indigo-300/80 mb-1.5">
        Worth sitting with
      </div>
      <p className="text-[14px] leading-[1.7] text-indigo-100/90 max-w-[64ch]">{inline(block.text ?? '')}</p>
    </div>
  );
}

/**
 * How each verdict reads on the rail.
 *
 * One glyph and one hue each, on the phase palette the rest of the board already speaks: done for
 * met, active for partial, blocked for gap. The glyph is what makes the rail work — a column of
 * marks down the left of the ticket can be taken in without reading a single line, which is the
 * whole reason the map is a rail rather than a table.
 */
const VERDICTS: Record<Verdict, { mark: string; label: string; color: string }> = {
  gap: { mark: '✕', label: 'gap', color: 'var(--w-blocked-300)' },
  partial: { mark: '◐', label: 'partial', color: 'var(--w-active-400)' },
  unknown: { mark: '?', label: 'unknown', color: 'var(--w-gate-300)' },
  unaddressed: { mark: '○', label: 'no ruling', color: 'var(--w-backlog-600)' },
  deferred: { mark: '→', label: 'deferred', color: 'var(--w-queue-300)' },
  met: { mark: '✓', label: 'met', color: 'var(--w-done-400)' },
  context: { mark: '·', label: 'context', color: 'var(--w-t700)' },
};

/** Worst first. The reader opened this to find what is missing, not to be congratulated. */
const SUMMARY_ORDER: Verdict[] = ['gap', 'partial', 'unknown', 'unaddressed', 'deferred', 'met'];

/** Whether a line has anything under it. A row with nothing to show does not open, and must not
 *  count towards "all expanded" either — one unruled line would otherwise leave the button stuck
 *  reading "expand all" with nothing left to expand. */
const hasDetail = (s: CoverageSegment) =>
  Boolean(s.evidence || s.refs?.length || s.qa || s.deferredTo);

/**
 * `S4` is an internal handle. The reader has never seen one and cannot look one up.
 *
 * The run needs the ids to key its rulings to lines, and it reaches for them again when it wants to
 * cross-reference — "the same gate as S5", "covered by the same implementation as S3". That is a
 * fair thing to want to say and an unreadable way to say it, so the id is swapped for a few of the
 * words it stands for. The prompt asks for those words directly; this is what makes the analyses
 * written before it asked still legible.
 *
 * Deliberately narrow. It substitutes only when the id names a line in THIS map, so an `S3` that
 * means a storage bucket is left alone, and it steps over ranges (`S2-S17`) because no single quote
 * stands for one.
 */
function resolveSegmentIds(text: string, segments: CoverageSegment[]): string {
  if (!text || !/\bS\d/.test(text)) return text;

  const byId = new Map(segments.map(s => [s.id.toUpperCase(), s.text]));

  return text.replace(/\bS\d{1,3}\b(\s*[-–—]\s*S\d{1,3}\b)?/g, (whole, range) => {
    if (range) return whole;
    const words = byId.get(whole.toUpperCase());
    if (!words) return whole;

    // Markers come off before the cut, not after: six words into a requirement can land between a
    // pair of `**`, and a dangling one renders as literal asterisks in the middle of the quote.
    // The excerpt is a label for a line, not a rendering of it — it has no use for emphasis.
    const plain = words.replace(/[*_`]/g, '').replace(/\s+/g, ' ').trim();
    const short = plain.split(' ').slice(0, 6).join(' ');
    return `“${short}${short.length < plain.length ? '…' : ''}”`;
  });
}

/**
 * The ticket down one side, what the change did about it down the other.
 *
 * This is the section that exists to close a browser tab. The reconciliation it does — read a
 * requirement, find where the change satisfied it, notice the ones nothing satisfied — is the
 * reading everybody does by hand with Jira open in one window and this in another, and doing it by
 * hand is how a missed requirement gets missed.
 *
 * The two columns are the whole design. An annotation folded into the text interrupts the one thing
 * the reader came here to do, which is READ THEIR TICKET: every note opened pushes the next
 * requirement further down the page, so following the description end to end means stepping over
 * commentary the whole way. Beside it instead, the description keeps its own shape and its own
 * order, and the commentary is something you glance across at.
 *
 * Nothing is behind a click, and that falls out of the layout rather than being a separate decision:
 * once the notes are not in the way of the text there is no longer a reason to hide them.
 *
 * Two rules make it worth trusting rather than just worth reading, and both are enforced before the
 * data reaches this file (see `MapCoverage` on the server). Every line of the description is here,
 * in the ticket's own words and its own order — so a requirement cannot go missing by not being
 * mentioned. And a `met` verdict cannot exist without a citation to open, so the cheapest possible
 * lie is the one thing the map cannot say.
 */
function Coverage({ block }: { block: Block }) {
  const all = block.segments ?? [];
  const [open, setOpen] = useState(true);

  /**
   * Land on the lines that are not settled.
   *
   * The reader arrives with one question — is anything missing — and the `met` rows are precisely
   * the ones that do not answer it. Opening on all twelve means reading eight annotations to find
   * the four that matter. The whole map is one click away, for the other use of this section, which
   * is proving nothing was missed rather than finding what was.
   *
   * Only when there IS something unresolved: on a ticket where everything landed, a filtered view
   * would open on an empty list.
   */
  const [unresolvedOnly, setUnresolvedOnly] = useState(
    () => all.some(s => UNSETTLED.has(s.verdict)),
  );

  // Which pair the pointer is over. The columns are far enough apart that "which note goes with
  // which line" stops being obvious on a long row, and lighting both ends of the pair answers it
  // without drawing a leader line across the gap for every row on the page.
  const [hovered, setHovered] = useState<string | null>(null);

  const inherited = useMemo(() => all.filter(s => s.origin), [all]);

  // Inherited criteria start collapsed — they are usually the epic restating what the ticket
  // already says — but NOT when one of them is unresolved. A requirement this ticket is being held
  // to, that nothing has answered, is the last thing that should be one click away.
  const [showInherited, setShowInherited] = useState(
    () => inherited.some(s => UNSETTLED.has(s.verdict)),
  );

  const shown = useMemo(
    () => (showInherited ? all : all.filter(s => !s.origin)),
    [all, showInherited],
  );

  const counts = useMemo(() => {
    const c: Partial<Record<Verdict, number>> = {};
    for (const s of shown) if (s.verdict !== 'context') c[s.verdict] = (c[s.verdict] ?? 0) + 1;
    return c;
  }, [shown]);

  const visible = useMemo(() => {
    let list = shown;

    if (unresolvedOnly) {
      // Keep the heading each survivor sits under. Filtered to the unresolved lines alone, a rail
      // becomes a handful of orphan sentences with nothing saying which part of the ticket they are
      // from — and "which part" is half of what makes a gap actionable.
      const out: CoverageSegment[] = [];
      let heading: CoverageSegment | null = null;
      for (const s of shown) {
        if (s.kind === 'heading') { heading = s; continue; }
        if (!UNSETTLED.has(s.verdict)) continue;
        if (heading) { out.push(heading); heading = null; }
        out.push(s);
      }
      list = out;
    }

    // Resolved against the WHOLE map, not the filtered view: a cross-reference points wherever it
    // points, and hiding the settled lines must not turn a readable note back into an id.
    return list.map(s => (s.evidence && /\bS\d/.test(s.evidence)
      ? { ...s, evidence: resolveSegmentIds(s.evidence, all) }
      : s));
  }, [all, shown, unresolvedOnly]);

  if (all.length === 0) return null;

  const unresolved = shown.filter(s => UNSETTLED.has(s.verdict)).length;
  const origins = [...new Set(inherited.map(s => s.origin))];

  return (
    <div className="rounded-lg border border-hair bg-gray-900/40">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3">
        <span className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-indigo-200">
          {block.title ?? 'Requirement coverage'}
        </span>

        {/* Doubles as the legend: every mark in the gutter below appears here beside its word. */}
        <span className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          {SUMMARY_ORDER.map(v => (counts[v] ? (
            <span key={v} className="inline-flex items-baseline gap-1 text-[11px]">
              <span className="font-mono" style={{ color: VERDICTS[v].color }}>{VERDICTS[v].mark}</span>
              <span className="tabular-nums text-gray-200">{counts[v]}</span>
              <span className="text-gray-500">{VERDICTS[v].label}</span>
            </span>
          ) : null))}
        </span>

        <button
          onClick={() => setOpen(o => !o)}
          className="ml-auto text-[10px] uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-600 hover:text-gray-300 transition-colors"
        >
          {open ? 'hide' : 'show'}
        </button>
      </div>

      {open && (
        <>
          {/* The answer, before a single row is read. Everything below is the working. */}
          {block.text && (
            <p className="px-4 pb-3 text-[13.5px] leading-[1.6] text-gray-200 max-w-[80ch]">
              {inline(resolveSegmentIds(block.text, all))}
            </p>
          )}

          {(unresolved > 0 || inherited.length > 0) && (
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1.5 px-4 pb-2.5 text-[11px]">
              {/* Two buttons rather than one switch. With the filtered view as the default, a lone
                  toggle would have to be read to work out which way round it is — a pair says what
                  you are looking at and what the other option is, in both states. */}
              {unresolved > 0 && (
                <span className="inline-flex rounded-full border border-hair overflow-hidden">
                  <Choice on={unresolvedOnly} onClick={() => setUnresolvedOnly(true)}>
                    {unresolved} unresolved
                  </Choice>
                  <Choice on={!unresolvedOnly} onClick={() => setUnresolvedOnly(false)}>
                    all {shown.filter(s => s.kind !== 'heading').length}
                  </Choice>
                </span>
              )}
              {inherited.length > 0 && (
                <Toggle on={showInherited} onClick={() => setShowInherited(v => !v)}>
                  {inherited.length} inherited from {origins.join(', ')}
                </Toggle>
              )}
            </div>
          )}

          {/*
            One grid for both columns rather than two scrolling panes, so a line and its note sit on
            the same row by construction. Measuring offsets in JS would put every annotation one
            resize, one font swap and one reflow away from pointing at the wrong requirement — here
            the browser keeps them level for free, and a narrow screen collapses to one column with
            each note directly under the line it belongs to.
          */}
          {/* Even split rather than favouring the description. Requirements are short lines that
              do not use extra width, while an annotation carries a sentence and its citations — and
              every line the annotation wraps to is blank space opposite it. */}
          <div className="border-t border-hair px-4 py-3 grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-x-6">
            {visible.map((s, i) => {
              const first = s.origin && s.origin !== visible[i - 1]?.origin;
              return (
                <Fragment key={s.id}>
                  {/* Where the ticket's own text stops and an ancestor's begins. Without it an
                      inherited criterion falls under whatever heading the ticket happened to end on
                      and reads as part of it. */}
                  {first && (
                    <SpanningLabel text={`inherited from ${s.origin}${s.originRole ? `, the ${s.originRole}` : ''}`} />
                  )}
                  {s.kind === 'heading' ? (
                    <SpanningLabel text={s.text} />
                  ) : (
                    <CoveragePair
                      segment={s}
                      lit={hovered === s.id}
                      onEnter={() => setHovered(s.id)}
                      onLeave={() => setHovered(h => (h === s.id ? null : h))}
                    />
                  )}
                </Fragment>
              );
            })}
          </div>

          {(counts.unaddressed ?? 0) > 0 && (
            <p className="px-4 pb-3 text-[11px] leading-relaxed text-gray-600 max-w-[68ch]">
              <span className="font-mono" style={{ color: VERDICTS.unaddressed.color }}>○</span>{' '}
              marks a line the analysis returned no ruling on. It is not a judgement about the line —
              it is the map saying it is incomplete, which is the one thing it must never do quietly.
            </p>
          )}

          {block.note && (
            <p className="px-4 pb-3 text-[11.5px] text-gray-500 italic max-w-[68ch]">{inline(block.note)}</p>
          )}
        </>
      )}
    </div>
  );
}

/** One half of a segmented pair: which view you are in, and the one you are not. */
function Choice({ on, onClick, children }: { on: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`px-2.5 py-0.5 transition-colors ${
        on ? 'bg-indigo-950/60 text-indigo-200' : 'text-gray-500 hover:text-gray-300'
      }`}
    >
      {children}
    </button>
  );
}

/** A filter switch, styled as on/off rather than as a button that might do something. */
function Toggle({ on, onClick, children }: { on: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`rounded-full border px-2.5 py-0.5 transition-colors ${
        on
          ? 'border-indigo-700/70 bg-indigo-950/40 text-indigo-200'
          : 'border-hair text-gray-500 hover:text-gray-300'
      }`}
    >
      {children}
    </button>
  );
}

/**
 * A heading from the description, or the line where an ancestor's criteria start.
 *
 * Runs the full width under both columns: it labels the requirements AND their annotations, and
 * confined to the left column it would read as a heading for the text alone.
 */
function SpanningLabel({ text }: { text: string }) {
  return (
    <div className="lg:col-span-2 flex items-baseline gap-2.5 pt-4 pb-1.5 first:pt-0">
      <span className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
        {text}
      </span>
      <span className="flex-1 h-px bg-gray-800/70" />
    </div>
  );
}

/**
 * One line of the ticket and its annotation — two grid cells, emitted together so they cannot drift
 * apart.
 */
function CoveragePair({ segment, lit, onEnter, onLeave }: {
  segment: CoverageSegment; lit: boolean; onEnter: () => void; onLeave: () => void;
}) {
  const v = VERDICTS[segment.verdict] ?? VERDICTS.unknown;
  const context = segment.verdict === 'context';

  // A snippet's header is already a link to its own file, and a better one than the citation below
  // it — it carries the line RANGE rather than just the first line. Printing both puts the same
  // path on screen twice, two lines apart, pointing at the same place.
  const snippetPath = segment.snippet?.path;
  const refs = (segment.refs ?? []).filter(r => !snippetPath || splitLocation(r).path !== snippetPath);

  const hover = { onMouseEnter: onEnter, onMouseLeave: onLeave };
  const halo = lit ? 'bg-gray-900/70' : '';

  return (
    <>
      {/* The ticket's own words. A code block from the description keeps its shape; everything else
          is a line of prose with its verdict in the gutter. */}
      <div {...hover} className={`grid grid-cols-[1.1rem_1fr] gap-x-2 rounded px-1.5 py-1 -mx-1.5 transition-colors ${halo}`}>
        {segment.kind === 'code' ? (
          <>
            <span />
            <pre className="overflow-x-auto rounded border border-hair bg-gray-950/60 px-2.5 py-1.5 font-mono text-[11px] leading-[1.55] text-gray-500">
              {segment.text}
            </pre>
          </>
        ) : (
          <>
            <span
              title={v.label}
              className="select-none text-center font-mono text-[12px] leading-[1.65]"
              style={{ color: v.color }}
            >
              {v.mark}
            </span>
            <span
              className={`text-[13px] leading-[1.65] ${context ? 'text-gray-600' : 'text-gray-300'}`}
              style={{ paddingLeft: `${segment.depth * 0.9}rem` }}
            >
              {inline(segment.text)}
            </span>
          </>
        )}
      </div>

      {/* The annotation. The bracket spans the row rather than hugging the note, because what it is
          marking is the extent of the TEXT this note accounts for — a bracket that stopped at the
          end of a one-line note would claim less of the requirement than the note actually covers. */}
      <div {...hover} className={`rounded px-1.5 py-1 -mx-1.5 mb-0.5 lg:mb-0 transition-colors ${halo}`}>
        {hasDetail(segment) ? (
          <div className="flex h-full gap-2.5">
            <span
              aria-hidden
              className="shrink-0 w-1.5 self-stretch border-l-2 border-t border-b rounded-l-[2px]"
              style={{ borderColor: v.color, opacity: lit ? 1 : 0.75 }}
            />
            <div className="min-w-0 flex flex-col gap-1 py-0.5">
              {segment.evidence && (
                <p className="text-[12.5px] leading-[1.55] text-gray-400">{inline(segment.evidence)}</p>
              )}
              {/* Reuses the document's own snippet renderer rather than a smaller copy of it: the
                  line numbers, the elision rule and the "no startLine means no numbers" honesty are
                  all decisions already made once, and a second implementation would be a second
                  place for them to drift. */}
              {segment.snippet && (
                <div className="pt-0.5">
                  <CodeSnippet block={{ kind: 'code', ...segment.snippet } as Block} />
                </div>
              )}
              {segment.deferredTo && <DetailLine label="handled by" value={segment.deferredTo} />}
              <QaProof qa={segment.qa} shot={segment.shot} />
              {/* Wrapped rather than stacked. Every line an annotation grows is a line of blank
                  space opposite it in the description, so three citations on one line instead of
                  three keeps the requirements reading like a list rather than a paragraph. */}
              {refs.length > 0 && (
                <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-[11px]">
                  {refs.map((r, i) => <FileRef key={i} location={r} />)}
                </div>
              )}
            </div>
          </div>
        ) : segment.verdict === 'unaddressed' ? (
          /* The one absence worth printing. A blank cell here would read as "nothing to say about
             this line", which is the opposite of what it means. */
          <div className="flex h-full gap-2.5">
            <span
              aria-hidden
              className="shrink-0 w-1.5 self-stretch border-l-2 border-t border-b rounded-l-[2px] border-dashed"
              style={{ borderColor: v.color, opacity: 0.5 }}
            />
            <p className="py-0.5 text-[11.5px] italic leading-[1.55] text-gray-600">
              no ruling returned for this line
            </p>
          </div>
        ) : null}
      </div>
    </>
  );
}

/**
 * What QA did about this line — the shot where there is one, the sentence where there is not.
 *
 * The picture is the point. Everything else on the row asks the reader to take a claim on trust or
 * go and open a file; a screenshot of the requirement working is the only evidence here that is
 * finished being read the moment it is seen.
 */
function QaProof({ qa, shot }: { qa?: string | null; shot?: number | null }) {
  const { shots, open } = useContext(QaShots);
  if (!qa && shot == null) return null;

  // Resolved by ORDER against the ticket's live shots. The document is stored and the shots are
  // not, so a number naming a shot that has since been replaced finds nothing and falls back to the
  // caption — never a broken image, and never someone else's screenshot.
  const i = shot == null ? -1 : shots.findIndex(s => s.order === shot);
  const found = i >= 0 ? shots[i] : null;

  if (!found) return qa ? <DetailLine label="verified by" value={qa} /> : null;

  return (
    <div className="flex items-start gap-2.5 pt-0.5">
      <button
        onClick={() => open(i)}
        title="Open the QA screenshot"
        className="shrink-0 rounded border border-hair overflow-hidden hover:border-indigo-700/80 transition-colors"
      >
        <img
          src={found.imageUrl}
          alt={found.caption}
          loading="lazy"
          className="block w-[7.5rem] h-[4.5rem] object-cover object-top"
        />
      </button>
      <div className="min-w-0 flex flex-col gap-0.5">
        <span className="text-[10px] uppercase tracking-[calc(var(--w-track)*0.08em)] text-gray-600">
          verified by
        </span>
        <span className="text-[11.5px] leading-[1.5] text-gray-300">{inline(qa ?? found.caption)}</span>
      </div>
    </div>
  );
}

function DetailLine({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline gap-2 text-[11.5px]">
      <span className="shrink-0 text-[10px] uppercase tracking-[calc(var(--w-track)*0.08em)] text-gray-600">
        {label}
      </span>
      <span className="text-gray-300">{inline(value)}</span>
    </div>
  );
}

/**
 * A real snippet, quoted from the file, with the file's own line numbers down the side.
 *
 * The numbers are the point as much as the code is: they are what let you match what is on screen
 * against what is in the editor when you follow the link, and they turn "somewhere in this method"
 * into a place. Deliberately no syntax highlighting — that would mean shipping a highlighter and
 * running model-written text through it, for decoration rather than understanding.
 */
function CodeSnippet({ block }: { block: Block }) {
  const lines = (block.text ?? '').split('\n');
  // No startLine means the code is not in the file at these lines any more — a quote from history.
  // Numbering it from 1 would be a confident lie, so it gets no numbers and no jump-to-line.
  const start = block.startLine ?? null;

  // An elision skips an unknown number of lines, so everything after it would be numbered wrongly.
  // The start line is still true and still worth linking, but the running count is not — so the
  // numbers come off entirely rather than being right for the first half and quietly wrong after.
  const elided = lines.some(l => l.trim() === '...' || l.trim() === '…');
  const numbered = start !== null && !elided;
  const end = numbered ? start! + lines.length - 1 : null;
  const width = String(end ?? 1).length;

  return (
    <figure className="rounded-lg border border-hair bg-panel overflow-hidden">
      {(block.path || block.title) && (
        <figcaption className="flex items-baseline gap-2 border-b border-hair bg-gray-900/50 px-3 py-1.5 text-[11px]">
          {block.title && (
            <span className="shrink-0 text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.08em)] text-amber-300/80">
              {block.title}
            </span>
          )}
          {block.path && (
            <FileRef
              location={
                end !== null ? `${block.path}:${start}-${end}`
                  : start !== null ? `${block.path}:${start}`
                  : block.path
              }
              className="text-[11px]"
            />
          )}
          {block.language && <span className="ml-auto shrink-0 text-[10px] text-gray-600">{block.language}</span>}
        </figcaption>
      )}

      {/* Capped rather than clipped: the prompt asks for 20 lines, and a run that ignores that should
          cost a scrollbar, not the rest of the page. */}
      <div className="overflow-auto max-h-[26rem]">
        <pre className="px-3 py-2.5 text-[12px] leading-[1.55] font-mono">
          {lines.map((l, i) => (
            <div key={i} className="flex">
              {numbered && (
                <span
                  className="select-none shrink-0 pr-3 text-right text-gray-700 tabular-nums"
                  style={{ width: `${width + 1}ch` }}
                >
                  {start! + i}
                </span>
              )}
              <span className="text-gray-300 whitespace-pre">{l || ' '}</span>
            </div>
          ))}
        </pre>
      </div>

      {block.note && (
        <div className="border-t border-hair px-3 py-2 text-[12px] leading-relaxed text-gray-400">
          {inline(block.note)}
        </div>
      )}
    </figure>
  );
}

// ---- user flow ----------------------------------------------------------------------------

/**
 * The route a person takes through the product, and what this ticket did to it.
 *
 * The model sends a graph — stops and the lines between them — and nothing else. Every coordinate
 * on screen is computed here, which is the same split every other block makes and matters more for
 * this one: a model asked to place boxes produces overlapping boxes, and a model asked to write
 * Mermaid produces a diagram whose one syntax slip is a blank rectangle where the teaching was.
 *
 * What makes it a diagram of a CHANGE rather than documentation is `change` on every stop. The grey
 * stops are the ones that were already there; they exist to tell the reader where they are before
 * the coloured ones tell them what is different.
 *
 * Each stop renders as its own `<g data-flow-node>` positioned from a pure layout pass, so anything
 * a stop grows — the QA screenshot below, the detail card beside it — is sized in `measure` and
 * placed from the box, with none of the geometry around it needing to know.
 */

const NODE_W = 224;
const COL_GAP = 76;
const ROW_GAP = 46;
const PAD_Y = 22;          // headroom for the actor chips, which sit on the top edge
const GUTTER = 34;         // left margin a return line loops back through
const INNER = NODE_W - 24;

/**
 * The QA screenshot that sits in the bottom of a stop.
 *
 * Inside the box rather than floating beside it, because the alternative is a lane down the side of
 * the diagram that every edge crossing a column gap has to be routed around — and a picture that is
 * not touching its stop is a picture the reader has to work out the owner of.
 *
 * Always drawn, never behind the click that opens the stop. A diagram whose subject is screens, and
 * which makes you open each box to see the screen, has hidden the one thing on it that needs no
 * reading at all.
 */
const SHOT_H = 108;
const SHOT_PAD = 10;       // the gap above the shot, and the same margin below it
const SHOT_INSET = 8;      // how far in from a rectangle's sides the picture sits

// Character budgets. The SVG cannot measure text, so labels are wrapped against a per-character
// width read off the rendered font — deliberately a touch wide, so a label errs towards wrapping
// early rather than towards running out of its box.
const LABEL_MAX = Math.floor(INNER / 6.9);
const SMALL_MAX = Math.floor(INNER / 5.9);

/**
 * How far the angled ends of a decision cut in from its sides.
 *
 * Decisions were diamonds, and a diamond is a bad container for words: its usable width closes to
 * nothing at all four points, so the text column was narrow, the shape had to grow to twice the
 * height of its own content to hold three lines, and the result was both ugly and harder to read
 * than the boxes around it. A flattened hexagon keeps the "this is a fork" silhouette while giving
 * the text nearly the full width of a normal stop at a normal height.
 */
const HEX_INSET = 16;

const DECISION_MAX = Math.floor((INNER - HEX_INSET * 2) / 6.9);

/**
 * A closed polygon with its corners rounded off.
 *
 * The vertex becomes the control point of a quadratic, so the curve leans into the corner without
 * reaching it — which is exactly the softening wanted, and why the radius is kept small enough that
 * the edges an arrow lands on (the top and bottom edges, and the left and right points) are still
 * where the layout believes they are.
 */
function roundedPolygon(points: [number, number][], r: number): string {
  const n = points.length;
  const at = (i: number) => points[(i + n) % n];
  const lerp = (a: [number, number], b: [number, number], t: number): [number, number] =>
    [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];

  const parts: string[] = [];
  for (let i = 0; i < n; i++) {
    const prev = at(i - 1);
    const cur = at(i);
    const next = at(i + 1);
    const p1 = lerp(cur, prev, Math.min(0.5, r / Math.hypot(cur[0] - prev[0], cur[1] - prev[1])));
    const p2 = lerp(cur, next, Math.min(0.5, r / Math.hypot(next[0] - cur[0], next[1] - cur[1])));
    parts.push(`${i === 0 ? 'M' : 'L'}${p1[0].toFixed(1)},${p1[1].toFixed(1)}`);
    parts.push(`Q${cur[0].toFixed(1)},${cur[1].toFixed(1)} ${p2[0].toFixed(1)},${p2[1].toFixed(1)}`);
  }
  return `${parts.join(' ')} Z`;
}

/** The six corners of a decision, in order. Top and bottom edges stay flat so arrows still land. */
const hexPoints = (x: number, y: number, w: number, h: number): [number, number][] => [
  [x + HEX_INSET, y], [x + w - HEX_INSET, y], [x + w, y + h / 2],
  [x + w - HEX_INSET, y + h], [x + HEX_INSET, y + h], [x, y + h / 2],
];

// A ceiling, not a target. Labels are asked for as 2-5 word headers, so this exists only so that a
// run which ignores that produces a tall box rather than an unbounded one. Nothing is cut before it.
const LABEL_LINES = 4;

const FLOW_TONES: Record<FlowChange, { stroke: string; fill: string; chip: string; text: string; line: string }> = {
  unchanged: { stroke: 'var(--w-backlog-700)', fill: 'var(--w-void)', chip: 'var(--w-backlog-600)', text: 'var(--w-t400)', line: 'var(--w-backlog-600)' },
  new: { stroke: 'var(--w-done)', fill: 'var(--w-done-950)', chip: 'var(--w-done-600)', text: 'var(--w-done-100)', line: 'var(--w-done-600)' },
  changed: { stroke: 'var(--w-active)', fill: 'var(--w-active-950)', chip: 'var(--w-active-600)', text: 'var(--w-active-200)', line: 'var(--w-active-600)' },
  removed: { stroke: 'var(--w-blocked-300)', fill: 'var(--w-blocked-950)', chip: 'var(--w-blocked-600)', text: 'var(--w-blocked-100)', line: 'var(--w-blocked-600)' },
};

const CHANGE_LABEL: Record<FlowChange, string> = {
  unchanged: 'already there', new: 'new here', changed: 'changed', removed: 'removed',
};

const KIND_LABEL: Record<string, string> = {
  start: 'START', screen: 'SCREEN', step: 'STEP', system: 'SYSTEM', end: 'END',
};

/** Enough hues to tell roles apart at a glance, assigned in order of first appearance. */
const ACTOR_HUES = ['var(--w-gate-400)', 'var(--w-queue-300)', 'var(--w-gate-300)', 'var(--w-done-300)', 'var(--w-active-400)'];

const clip = (s: string, max: number) => (s.length <= max ? s : `${s.slice(0, max - 1).trimEnd()}…`);

/** Greedy wrap into at most `maxLines`, with the overflow marked rather than silently lost. */
function wrap(text: string, max: number, maxLines: number): string[] {
  const words = text.trim().split(/\s+/).filter(Boolean);
  const lines: string[] = [];
  let line = '';

  for (const w of words) {
    const next = line ? `${line} ${w}` : w;
    if (next.length <= max) { line = next; continue; }
    if (lines.length + 1 === maxLines) return [...lines, clip(next, max)];
    if (line) lines.push(line);
    line = clip(w, max);
  }

  if (line) lines.push(line);
  return lines.length > 0 ? lines : [''];
}

/** A stop's screenshot, already resolved against the ticket's live shots. `index` is what the
 *  lightbox is opened at, so the arrows still walk the whole set from there. */
interface FlowShot { index: number; url: string; caption: string }

interface FlowBox {
  node: FlowNode;
  col: number;
  x: number; y: number; h: number;
  /** How much of `h` is the words. The shot, where there is one, fills the rest. */
  text: number;
  label: string[];
  refs: string[];
  /** Null when the stop named no shot, or named one the ticket no longer has. */
  shot: FlowShot | null;
  /** The one line under the label: where this stop lives, or that there is more to read. */
  hint: string | null;
  /** Decisions carry no kind chip — the diamond already says what they are. */
  chip: boolean;
  /** Whether opening the stop would show anything. */
  opens: boolean;
}

interface FlowLine {
  edge: FlowEdge;
  d: string;
  back: boolean;
  /** Wrapped, never cut — an edge label says what sends the user this way, and half of that is worse
   *  than none. The column gap widens to hold the longest one instead. */
  label: { x: number; y: number; anchor: 'start' | 'end' | 'middle'; lines: string[] } | null;
}

/** Per line of an edge label. Two lines fit inside a row gap; a third would not. */
const EDGE_LABEL_MAX = 16;

const edgeLabel = (e: FlowEdge) => (e.label ? wrap(e.label, EDGE_LABEL_MAX, 2) : null);

/**
 * How big a stop has to be to hold what it says.
 *
 * Nothing here truncates. An earlier version cut the label to one or two lines and left the rest on
 * a tooltip, which is exactly the wrong trade: a diagram you have to hover to read is a diagram you
 * stop reading. The box grows, and the long-form detail lives in the panel the stop opens instead.
 */
function measure(node: FlowNode, shot: FlowShot | null) {
  const decision = node.kind === 'decision';
  const label = wrap(node.label, decision ? DECISION_MAX : LABEL_MAX, LABEL_LINES);
  const refs = nodeRefs(node);

  // One line, and only a signpost: the file this stop lives in, or — with nothing to cite but
  // something to say — that opening it is worth doing.
  const hint = refs.length > 0
    ? clip(`${refs[0].split(/[\\/]/).pop() ?? refs[0]}${refs.length > 1 ? `  +${refs.length - 1}` : ''}`, SMALL_MAX)
    : node.explain ? 'details' : null;

  // A decision's actor cannot hang off its corner the way a rectangle's can, so it reads inside.
  const actorInside = decision && node.actor ? clip(node.actor, SMALL_MAX) : null;

  const content = label.length * 16 + (hint ? 13 : 0) + (actorInside ? 13 : 0);

  // A hexagon is full width everywhere its text sits, so a decision is now sized like any other
  // stop. As a diamond this had to be twice its own content to keep three lines off the sloped
  // sides, which is what made the questions the tallest and ugliest things on the canvas.
  //
  // The picture is added to the box rather than fitted inside it: the words keep exactly the room
  // they had, and the stop grows downwards by the height of the screenshot it now carries.
  const text = Math.max(decision ? 56 : 52, content + 42);
  const h = text + (shot ? SHOT_H + SHOT_PAD : 0);

  return { label, refs, hint, shot, text, chip: !decision, opens: refs.length > 0 || !!node.explain, h };
}

/** Where a stop's screenshot sits inside it. Read by both the measure pass and the renderer, so the
 *  chevron that has to stay clear of the picture is never working from its own guess. */
function shotRect(box: FlowBox) {
  const inset = box.node.kind === 'decision' ? HEX_INSET + 4 : SHOT_INSET;
  return { x: box.x + inset, y: box.y + box.text, w: NODE_W - inset * 2, h: SHOT_H };
}

/**
 * Rank the graph, then place it.
 *
 * Ranking is longest-path from the entry stops, so a node always sits below everything that can
 * reach it and the page reads top to bottom. Edges that point back at a stop still open on the walk
 * are pulled out first as RETURNS — a failed validation going back to the form is a real part of
 * the route and drawing it as forward progress would rank the whole flow wrongly.
 *
 * Column 0 is the spine: at every fork, the line with the most product left in front of it. Picking
 * the first line declared instead reads badly the moment a decision lists its refusal first — the
 * dead end takes the main column and the route the user actually completes gets pushed into the
 * margin. "Longest way still to go" is the main story by construction.
 */
function layout(nodes: FlowNode[], edges: FlowEdge[], shotOf: (n: FlowNode) => FlowShot | null) {
  const byId = new Map(nodes.map(n => [n.id, n]));
  const out = new Map<string, FlowEdge[]>(nodes.map(n => [n.id, [] as FlowEdge[]]));
  for (const e of edges) out.get(e.from)?.push(e);

  const hasIncoming = new Set(edges.map(e => e.to));
  const roots = nodes.filter(n => !hasIncoming.has(n.id)).map(n => n.id);
  // A flow drawn as one closed loop has no entry stop. The first node declared is what the model
  // wrote as the beginning, which is a better guess than refusing to draw it.
  if (roots.length === 0) roots.push(nodes[0].id);

  const back = new Set<FlowEdge>();
  const state = new Map<string, 1 | 2>();
  const order: string[] = [];

  const walk = (id: string) => {
    state.set(id, 1);
    order.push(id);
    for (const e of out.get(id) ?? []) {
      const s = state.get(e.to);
      if (s === 1) back.add(e);          // still open above us: a return, not progress
      else if (s === undefined) walk(e.to);
    }
    state.set(id, 2);
  };

  for (const r of roots) if (!state.has(r)) walk(r);
  for (const n of nodes) if (!state.has(n.id)) walk(n.id);

  const forward = edges.filter(e => !back.has(e));
  const rank = new Map(nodes.map(n => [n.id, 0]));
  // Longest path by relaxation. The node count is capped in the parser, so the naive O(V*E) pass is
  // a few hundred operations and needs no topological sort to be correct.
  for (let i = 0; i < nodes.length; i++)
    for (const e of forward)
      if ((rank.get(e.to) ?? 0) < (rank.get(e.from) ?? 0) + 1) rank.set(e.to, (rank.get(e.from) ?? 0) + 1);

  // How much route is left beyond each stop, by the same relaxation run the other way.
  const depth = new Map(nodes.map(n => [n.id, 0]));
  for (let i = 0; i < nodes.length; i++)
    for (const e of forward)
      if ((depth.get(e.from) ?? 0) < (depth.get(e.to) ?? 0) + 1) depth.set(e.from, (depth.get(e.to) ?? 0) + 1);

  const spine = new Set<string>();
  let cur: string | null = roots[0];
  while (cur !== null && !spine.has(cur)) {
    const at: string = cur;
    spine.add(at);
    let best: FlowEdge | null = null;
    for (const e of out.get(at) ?? [])
      if (!back.has(e) && (best === null || (depth.get(e.to) ?? 0) > (depth.get(best.to) ?? 0))) best = e;
    cur = best ? best.to : null;
  }

  const rows = new Map<number, string[]>();
  for (const id of order) {
    const r = rank.get(id) ?? 0;
    rows.set(r, [...(rows.get(r) ?? []), id]);
  }
  // Stable sort, so everything off the spine keeps the order the model declared it in.
  for (const ids of rows.values()) ids.sort((a, b) => Number(spine.has(b)) - Number(spine.has(a)));

  // The return lane has to hold its own labels — "pick another" on a loop back to the picker is the
  // most useful thing on that line, and at a fixed gutter it hangs off the left edge of the page.
  const backLabel = Math.max(0, ...[...back].map(e => {
    const lines = edgeLabel(e);
    return lines ? Math.max(...lines.map(l => l.length)) : 0;
  }));
  const padX = back.size > 0 ? Math.max(GUTTER, backLabel * 6 + 24) : 16;

  // The gap between columns has to hold the label on a line crossing it. Sized off the widest one
  // rather than fixed, because the alternative is cutting the label to fit a gap chosen in advance.
  const widestLabel = Math.max(0, ...edges.map(e => {
    const lines = edgeLabel(e);
    return lines ? Math.max(...lines.map(l => l.length)) : 0;
  }));
  const colGap = Math.max(COL_GAP, widestLabel * 6 + 28);
  const boxes: FlowBox[] = [];
  const at = new Map<string, FlowBox>();
  let y = PAD_Y;
  let maxCol = 0;

  for (const r of [...rows.keys()].sort((a, b) => a - b)) {
    const ids = rows.get(r)!;
    const placed = ids.map((id, col) => {
      const node = byId.get(id)!;
      const box: FlowBox = { node, col, x: padX + col * (NODE_W + colGap), y, ...measure(node, shotOf(node)) };
      maxCol = Math.max(maxCol, col);
      return box;
    });

    // Centre the shorter boxes in their row, so arrows leave and arrive where the eye expects.
    const rowH = Math.max(...placed.map(b => b.h));
    for (const b of placed) {
      b.y = y + (rowH - b.h) / 2;
      boxes.push(b);
      at.set(b.node.id, b);
    }
    y += rowH + ROW_GAP;
  }

  let rightmost = padX + (maxCol + 1) * NODE_W + maxCol * colGap;

  const lines: FlowLine[] = edges.map(e => {
    const a = at.get(e.from)!;
    const b = at.get(e.to)!;
    const ax = a.x + NODE_W / 2;
    const bx = b.x + NODE_W / 2;
    const lines = edgeLabel(e);

    /** The clear band under a row — every routing case that has to get past something uses it. */
    const below = (box: FlowBox) =>
      Math.max(...boxes.filter(o => o.y < box.y + box.h && o.y + o.h > box.y).map(o => o.y + o.h)) + 16;

    if (back.has(e)) {
      const g = padX - 14;
      // Out of the bottom and under the row, not out of the side: a return leaving a branch column
      // sideways crosses whatever is on the spine beside it. The bands above and below a row are
      // the only horizontal space guaranteed to be clear.
      const under = below(a);
      const over = Math.min(...boxes.filter(o => o.y < b.y + b.h && o.y + o.h > b.y).map(o => o.y)) - 16;

      // Into the target's left edge when it is on the spine; over its head when it is not.
      const turn = b.col === 0 ? b.y + b.h / 2 : over;
      const entry = b.col === 0
        ? `L${g},${turn} L${b.x},${turn}`
        : `L${g},${turn} L${bx},${turn} L${bx},${b.y}`;

      return {
        edge: e, back: true,
        d: `M${ax},${a.y + a.h} L${ax},${under} L${g},${under} ${entry}`,
        // Beside the vertical run rather than at either end of it, where the gutter is only as wide
        // as the line itself.
        label: lines && { x: g - 6, y: (under + turn) / 2, anchor: 'end', lines },
      };
    }

    if (a.col === b.col) {
      // An edge that skips a rank — a decision whose "no" rejoins further down — would otherwise be
      // drawn as a straight line down THROUGH the stop in between. Step out into the gap beside the
      // column and come back in.
      const between = boxes.some(o => o.col === a.col && o !== a && o !== b
        && o.y < b.y && o.y + o.h > a.y + a.h);

      if (!between)
        return {
          edge: e, back: false,
          d: `M${ax},${a.y + a.h} L${ax},${b.y}`,
          label: lines && { x: ax + 7, y: a.y + a.h + 15, anchor: 'start', lines },
        };

      const lane = a.x + NODE_W + 22;
      rightmost = Math.max(rightmost, lane + 10);
      return {
        edge: e, back: false,
        d: `M${ax},${a.y + a.h} L${ax},${a.y + a.h + 14} L${lane},${a.y + a.h + 14}`
          + ` L${lane},${b.y - 14} L${bx},${b.y - 14} L${bx},${b.y}`,
        // At the far end of the exit band. Both branches out of a decision leave from the same
        // point, so a label at the near end would sit on top of the other branch's label.
        label: lines && { x: lane - 6, y: a.y + a.h + 10, anchor: 'end', lines },
      };
    }

    if (b.col > a.col) {
      // Leaving sideways at mid-height crosses anything sitting in the columns between, and with
      // two decisions in a row branching right that is the normal case, not the rare one.
      const mid = a.y + a.h / 2;
      const blocked = boxes.some(o => o !== a && o !== b
        && o.x + NODE_W > a.x + NODE_W && o.x < bx && o.y < mid && o.y + o.h > mid);

      if (!blocked)
        return {
          edge: e, back: false,
          d: `M${a.x + NODE_W},${mid} L${bx},${mid} L${bx},${b.y}`,
          label: lines && { x: a.x + NODE_W + 8, y: mid - 7 - (lines.length - 1) * 11, anchor: 'start', lines },
        };

      const under = below(a);
      return {
        edge: e, back: false,
        d: `M${ax},${a.y + a.h} L${ax},${under} L${bx},${under} L${bx},${b.y}`,
        // Over the arrowhead going in, not beside the source: the sibling branch already owns the
        // space under the source, and this line is only identifiable by where it lands.
        label: lines && { x: bx, y: b.y - 8 - (lines.length - 1) * 11, anchor: 'middle', lines },
      };
    }

    // Merging back towards the spine: drop clear of the source, run across, then down into the target.
    const rejoin = b.y - ROW_GAP / 2;
    return {
      edge: e, back: false,
      d: `M${ax},${a.y + a.h} L${ax},${rejoin} L${bx},${rejoin} L${bx},${b.y}`,
      label: lines && { x: ax + 7, y: a.y + a.h + 15, anchor: 'start', lines },
    };
  });

  return { boxes, lines, width: rightmost + 16, height: y - ROW_GAP + PAD_Y };
}

/**
 * Which stops a narration is pointing at, for a diagram being talked through rather than read:
 * `lit` now, `seen` already. Everything else dims. Absent on the Classroom page itself.
 */
export interface FlowSpot { lit: string[]; seen: string[] }

export function UserFlow({ block, spot }: { block: Block; spot?: FlowSpot }) {
  // Markers are looked up by id across the whole document, so two diagrams on one page would
  // otherwise share the first one's arrowheads.
  const uid = useId().replace(/:/g, '');
  const nodes = block.nodes ?? [];
  const edges = block.edges ?? [];
  const [openId, setOpenId] = useState<string | null>(null);

  // Resolved by ORDER against the ticket's live shots, the same way a coverage row resolves its
  // own: the document is stored and the shots are not, so a stop naming a screenshot that has since
  // been replaced draws no picture — never a broken image, and never somebody else's screen.
  const { shots } = useContext(QaShots);
  const shotOf = useCallback((n: FlowNode): FlowShot | null => {
    if (n.shot == null) return null;
    const i = shots.findIndex(s => s.order === n.shot);
    return i < 0 ? null : { index: i, url: shots[i].imageUrl, caption: shots[i].caption };
  }, [shots]);

  const g = useMemo(
    () => (nodes.length >= 2 && edges.length > 0 ? layout(nodes, edges, shotOf) : null),
    [nodes, edges, shotOf],
  );

  const canvasRef = useRef<HTMLDivElement>(null);

  // Escape closes the opened stop, which is the one thing a reader will try without being told.
  // A click anywhere that is neither the card nor another stop closes it too — clicks ON a stop are
  // left alone so that moving straight from one to the next stays a single click.
  useEffect(() => {
    if (openId === null) return;

    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpenId(null); };
    const onDown = (e: MouseEvent) => {
      const el = e.target as Element | null;
      if (el?.closest?.('[data-flow-card]') || el?.closest?.('[data-flow-node]')) return;
      setOpenId(null);
    };

    window.addEventListener('keydown', onKey);
    window.addEventListener('mousedown', onDown);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('mousedown', onDown);
    };
  }, [openId]);

  // The card has to outlive `openId` or there is nothing left on screen to fade: unmounting on the
  // click that closes it is the instant disappearance. So what is mounted lags what is selected —
  // it follows immediately on open, and on close it stays behind, transparent and inert, until the
  // transition has run. Opening another stop mid-fade cancels the timer and the card simply comes
  // back, which is why this is a lag rather than a queue of enter/exit animations.
  const [mountedId, setMountedId] = useState<string | null>(null);
  useEffect(() => {
    if (openId !== null) { setMountedId(openId); return; }
    const t = setTimeout(() => setMountedId(null), FADE_MS);
    return () => clearTimeout(t);
  }, [openId]);

  if (!g) return null;

  const card = g.boxes.find(b => b.node.id === mountedId) ?? null;

  // Where each stop and line stands in the telling. Undefined without a spotlight, so the page's own
  // diagram is untouched by any of this.
  const lit = new Set(spot?.lit ?? []);
  const seen = new Set(spot?.seen ?? []);
  const stopSpot = (id: string) => !spot ? undefined : lit.has(id) ? 'lit' : seen.has(id) ? 'seen' : 'dim';
  const lineSpot = (e: FlowEdge) => {
    if (!spot) return undefined;
    const from = lit.has(e.from) || seen.has(e.from);
    if (lit.has(e.to) && from) return 'lit';
    return from && (seen.has(e.to) || lit.has(e.to)) ? 'seen' : 'dim';
  };

  const actors = [...new Set(nodes.map(n => n.actor).filter((a): a is string => !!a))];
  const hue = (actor: string) => ACTOR_HUES[actors.indexOf(actor) % ACTOR_HUES.length];
  const changes = [...new Set(nodes.map(n => n.change))];

  return (
    <div>
      <BlockTitle text={block.title ?? "The user's path"} />
      <figure className="m-0 rounded-lg border border-hair bg-panel overflow-hidden">
        {/* Scrolls inside itself rather than widening the page, like the comparison tables do.
            Relative because the opened stop's card is positioned in the canvas's own coordinates,
            so it travels with the diagram when this scrolls. */}
        <div ref={canvasRef} className="relative overflow-x-auto">
          <svg
            width={g.width}
            height={g.height}
            viewBox={`0 0 ${g.width} ${g.height}`}
            className="block"
            fontFamily="ui-sans-serif, system-ui, sans-serif"
          >
            <defs>
              {(Object.keys(FLOW_TONES) as FlowChange[]).map(c => (
                <marker
                  key={c} id={`${uid}-${c}`} viewBox="0 0 10 10" refX="9" refY="5"
                  markerWidth="6" markerHeight="6" orient="auto-start-reverse"
                >
                  <path d="M0,0 L10,5 L0,10 z" fill={FLOW_TONES[c].line} />
                </marker>
              ))}
            </defs>

            {g.lines.map((l, i) => {
              const tone = FLOW_TONES[l.edge.change] ?? FLOW_TONES.unchanged;
              const ls = lineSpot(l.edge);
              return (
                <g key={i} className={ls ? 'orbit-spot' : undefined} data-spot={ls}>
                  <path
                    d={l.d}
                    fill="none"
                    stroke={tone.line}
                    strokeWidth={1.4}
                    strokeDasharray={l.back ? '4 3' : undefined}
                    markerEnd={`url(#${uid}-${l.edge.change})`}
                  />
                  {l.label && (
                    <text
                      x={l.label.x} y={l.label.y} textAnchor={l.label.anchor}
                      fontFamily="ui-monospace, Consolas, monospace" fontSize={10} fill="var(--w-t500)"
                    >
                      {l.label.lines.map((line, k) => (
                        <tspan key={k} x={l.label!.x} dy={k === 0 ? 0 : 11}>{line}</tspan>
                      ))}
                    </text>
                  )}
                </g>
              );
            })}

            {g.boxes.map(box => {
              const ss = stopSpot(box.node.id);
              return (
                <g key={box.node.id} className={ss ? 'orbit-spot' : undefined} data-spot={ss} data-flow-spot={ss ? box.node.id : undefined}>
                  <FlowStop
                    box={box}
                    hue={hue}
                    open={box.node.id === openId}
                    onOpen={() => setOpenId(id => (id === box.node.id ? null : box.node.id))}
                  />
                </g>
              );
            })}
          </svg>

          {card && (
            <FlowStopCard
              box={card}
              hue={hue}
              shown={openId !== null}
              canvas={{ width: g.width, height: g.height }}
              onClose={() => setOpenId(null)}
            />
          )}
        </div>

        <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 border-t border-hair px-3 py-2 text-[10px] uppercase tracking-[calc(var(--w-track)*0.08em)] text-gray-600">
          {changes.map(c => (
            <span key={c} className="flex items-center gap-1.5">
              <span
                className="h-2 w-2 rounded-sm"
                style={{ background: FLOW_TONES[c].stroke }}
              />
              {CHANGE_LABEL[c]}
            </span>
          ))}
          {actors.map(a => (
            <span key={a} className="flex items-center gap-1.5 normal-case tracking-normal">
              <span className="h-2 w-2 rounded-full" style={{ background: hue(a) }} />
              {a}
            </span>
          ))}
          <span className="ml-auto normal-case tracking-normal text-gray-700">
            click a stop for the detail and its diff · click a screenshot to enlarge it · diamond =
            a branch · dashed box = the product acting, not the user · dashed line = back a step
          </span>
        </div>

        {block.note && (
          <div className="border-t border-hair px-3 py-2 text-[12px] leading-relaxed text-gray-400">
            {inline(block.note)}
          </div>
        )}
      </figure>
    </div>
  );
}

/** One stop. Its own group, positioned entirely from the layout pass — see the note on UserFlow. */
function FlowStop({ box, hue, open, onOpen }: {
  box: FlowBox; hue: (actor: string) => string; open: boolean; onOpen: () => void;
}) {
  const [hover, setHover] = useState(false);
  const [shotHover, setShotHover] = useState(false);
  const { open: openShot } = useContext(QaShots);
  // The picture is clipped to the stop's own corner radius, and a clip path is looked up by id
  // across the whole document — so it has to be this stop's, not this diagram's.
  const clipId = `shot-${useId().replace(/:/g, '')}`;
  const { node, x, y, h } = box;
  const tone = FLOW_TONES[node.change] ?? FLOW_TONES.unchanged;
  const decision = node.kind === 'decision';
  const cx = x + NODE_W / 2;

  const actorInside = decision && node.actor ? clip(node.actor, SMALL_MAX) : null;
  const content = box.label.length * 16 + (box.hint ? 13 : 0) + (actorInside ? 13 : 0);

  // Rectangles hang their text under the kind chip; a diamond has no chip and centres instead —
  // in the words' own part of the box, which is `text` rather than the full height once a
  // screenshot is sitting under them.
  let cursor = decision ? y + (box.text - content) / 2 + 12 : y + 32;
  const textX = decision ? cx : x + 12;
  const anchor = decision ? 'middle' : 'start';
  const rows: ReactNode[] = [];

  box.label.forEach((line, i) => {
    rows.push(
      <text key={`l${i}`} x={textX} y={cursor} textAnchor={anchor} fontSize={13} fill={tone.text}>
        {line}
      </text>,
    );
    cursor += 16;
  });

  if (actorInside) {
    rows.push(
      <text key="a" x={textX} y={cursor} textAnchor={anchor} fontSize={10} fill={hue(node.actor!)}>
        {actorInside}
      </text>,
    );
    cursor += 13;
  }

  if (box.hint) {
    rows.push(
      <text
        key="h" x={textX} y={cursor} textAnchor={anchor}
        fontFamily="ui-monospace, Consolas, monospace" fontSize={10}
        fill={box.opens ? 'var(--w-queue-300)' : 'var(--w-backlog-600)'}
      >
        {box.hint}
      </text>,
    );
  }

  const lit = open || hover;
  const shape = decision
    ? <path
        d={roundedPolygon(hexPoints(x, y, NODE_W, h), 7)}
        fill={tone.fill} stroke={lit ? 'var(--w-gate-300)' : tone.stroke} strokeWidth={open ? 2 : 1.3}
      />
    : <rect
        x={x} y={y} width={NODE_W} height={h}
        rx={node.kind === 'start' || node.kind === 'end' ? Math.min(h / 2, 24) : 9}
        fill={tone.fill} stroke={lit ? 'var(--w-gate-300)' : tone.stroke} strokeWidth={open ? 2 : 1.3}
        // A dashed edge is the product doing something on its own — the user is not here.
        strokeDasharray={node.kind === 'system' ? '4 3' : undefined}
      />;

  // The affordance: a chevron in the corner of anything worth opening. Bottom-right because the kind
  // chip owns the top-left and the actor tag owns the top-right, and inset on a decision so it stays
  // clear of the angled end. Bottom of the WORDS, not of the box: on a stop carrying a screenshot
  // the corner of the box belongs to the picture.
  const ix = x + NODE_W - (decision ? HEX_INSET + 13 : 15);
  const iy = y + box.text - 15;

  const pic = box.shot ? shotRect(box) : null;

  return (
    <g
      data-flow-node={node.id}
      onClick={box.opens ? onOpen : undefined}
      onMouseEnter={box.opens ? () => setHover(true) : undefined}
      onMouseLeave={box.opens ? () => setHover(false) : undefined}
      style={{
        cursor: box.opens ? 'pointer' : undefined,
        // Explicit translate/scale/translate rather than transform-origin: a <g> has no box of its
        // own to be the origin of, and growing about the SVG's origin would fling the stop across
        // the canvas instead of swelling it in place.
        transform: hover && box.opens
          ? `translate(${cx}px, ${y + h / 2}px) scale(1.03) translate(${-cx}px, ${-(y + h / 2)}px)`
          : undefined,
        transition: 'transform 140ms ease-out',
      }}
    >
      {/* The ring rather than a thicker border: growing the stroke would move the shape's edge, and
          the arrows are drawn to meet the edge where the layout put it. */}
      {open && (decision
        ? <path
            d={roundedPolygon(hexPoints(x - 5, y - 5, NODE_W + 10, h + 10), 9)}
            fill="none" stroke="var(--w-gate-600)" strokeWidth={1} opacity={0.75}
          />
        : <rect
            x={x - 4} y={y - 4} width={NODE_W + 8} height={h + 8} rx={12}
            fill="none" stroke="var(--w-gate-600)" strokeWidth={1} opacity={0.75}
          />)}

      {shape}

      {/* The screenshot, sitting in the bottom of the stop it is a picture of.
          `slice` from the top is `object-cover object-top` by another name: a UI screenshot is
          recognised by its header and its first rows, so a thumbnail that letterboxed the whole
          frame into 108px would be a grey smear where a recognisable screen could have been. */}
      {box.shot && pic && (
        <g
          onClick={e => { e.stopPropagation(); openShot(box.shot!.index); }}
          onMouseEnter={() => setShotHover(true)}
          onMouseLeave={() => setShotHover(false)}
          style={{ cursor: 'zoom-in' }}
        >
          <title>{box.shot.caption || 'QA screenshot — click to enlarge'}</title>
          <clipPath id={clipId}>
            <rect x={pic.x} y={pic.y} width={pic.w} height={pic.h} rx={6} />
          </clipPath>
          <rect x={pic.x} y={pic.y} width={pic.w} height={pic.h} rx={6} fill="var(--w-void)" />
          <image
            href={box.shot.url}
            x={pic.x} y={pic.y} width={pic.w} height={pic.h}
            preserveAspectRatio="xMidYMin slice"
            clipPath={`url(#${clipId})`}
          />
          <rect
            x={pic.x} y={pic.y} width={pic.w} height={pic.h} rx={6} fill="none"
            stroke={shotHover ? 'var(--w-gate-300)' : tone.stroke}
            strokeWidth={1} opacity={shotHover ? 1 : 0.7}
          />
          {/* Says the picture is a door, not a decoration. Dim until the pointer is on it, because
              on a stop that also opens there are now two things to click and only one of them
              should be shouting at rest. */}
          <g pointerEvents="none" opacity={shotHover ? 1 : 0.5}>
            <rect
              x={pic.x + pic.w - 24} y={pic.y + pic.h - 24} width={18} height={18} rx={5}
              fill="var(--w-void)" opacity={0.8}
            />
            <path
              d={`M${pic.x + pic.w - 19.5},${pic.y + pic.h - 9.5} L${pic.x + pic.w - 10.5},${pic.y + pic.h - 18.5}
                  M${pic.x + pic.w - 14},${pic.y + pic.h - 18.5} L${pic.x + pic.w - 10.5},${pic.y + pic.h - 18.5} L${pic.x + pic.w - 10.5},${pic.y + pic.h - 15}
                  M${pic.x + pic.w - 16},${pic.y + pic.h - 9.5} L${pic.x + pic.w - 19.5},${pic.y + pic.h - 9.5} L${pic.x + pic.w - 19.5},${pic.y + pic.h - 13}`}
              fill="none" stroke="var(--w-gate-100)" strokeWidth={1.3}
              strokeLinecap="round" strokeLinejoin="round"
            />
          </g>
        </g>
      )}

      {box.opens && (
        <g pointerEvents="none">
          {/* Sky, not the stop's own tone. This says "there is more here", which is true of an
              unchanged stop as much as a new one — tinting it by change would make the affordance
              nearly invisible on exactly the grey boxes whose detail explains the rest. */}
          <circle
            cx={ix} cy={iy} r={7.5}
            fill={lit ? 'var(--w-gate-900)' : 'none'}
            stroke={lit ? 'var(--w-gate-300)' : 'var(--w-queue-300)'}
            strokeWidth={1}
            opacity={lit ? 1 : 0.6}
          />
          <path
            d={`M${ix - 1.8},${iy - 3.2} L${ix + 1.6},${iy} L${ix - 1.8},${iy + 3.2}`}
            fill="none" stroke={lit ? 'var(--w-gate-100)' : 'var(--w-queue-300)'} strokeWidth={1.4}
            strokeLinecap="round" strokeLinejoin="round" opacity={lit ? 1 : 0.75}
          />
        </g>
      )}

      {box.chip && (
        <text x={x + 12} y={y + 15} fontSize={8.5} letterSpacing={1} fill={tone.chip}>
          {`${KIND_LABEL[node.kind] ?? 'STEP'}${node.change === 'unchanged' ? '' : ` · ${node.change.toUpperCase()}`}`}
        </text>
      )}

      {rows}

      {/* The role tag, sitting on the top edge where it reads as a label on the stop rather than
          as another line of its text. Diamonds carry theirs inside instead. */}
      {!decision && node.actor && (
        <g>
          <rect
            x={x + NODE_W - (node.actor.length * 5.6 + 14) - 10} y={y - 9}
            width={node.actor.length * 5.6 + 14} height={17} rx={4}
            fill="var(--w-void)" stroke={hue(node.actor)} strokeWidth={1}
          />
          <text
            x={x + NODE_W - (node.actor.length * 5.6 + 14) / 2 - 10} y={y + 3}
            textAnchor="middle" fontSize={9.5} fill={hue(node.actor)}
          >
            {node.actor}
          </text>
        </g>
      )}
    </g>
  );
}

/**
 * The opened stop, floating beside the stop it belongs to.
 *
 * It began as a strip under the canvas, which was easy and wrong: clicking a box near the top of a
 * long diagram dropped the answer somewhere below the fold, so you lost the node you were asking
 * about in order to read about it. Anchoring costs a placement pass — the card takes whichever side
 * of the stop has room, and falls back to sitting underneath it when neither does — and buys the
 * only thing that matters here, which is seeing the stop and its detail at the same time.
 *
 * The diff is what makes that awkward, being both wide and long, so the card is as wide as the room
 * allows up to 460 and scrolls internally rather than growing without limit.
 */
/** The card's ceiling. Capped so its height is knowable without measuring it — see the note on `top`. */
const CARD_MAX = 460;

/** How long the card takes to arrive or leave. Long enough to read as a movement, short enough that
 *  walking a flow stop by stop never waits on it, and the same 140ms the stops use to swell under
 *  the pointer, so the diagram moves at one speed. Kept here because `UserFlow` has to hold the
 *  card mounted for exactly this long. */
const FADE_MS = 140;

function FlowStopCard({ box, hue, shown, canvas, onClose }: {
  box: FlowBox; hue: (actor: string) => string;
  /** False once the stop has been closed: the card is still mounted, but on its way out. */
  shown: boolean;
  canvas: { width: number; height: number };
  onClose: () => void;
}) {
  const { node } = box;
  const tone = FLOW_TONES[node.change] ?? FLOW_TONES.unchanged;

  const ref = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(0);

  // What "room" actually means: the width of the column the reader can see. A two-stop flow draws a
  // canvas narrower than the column it sits in, and sizing the card to the canvas there would put it
  // behind a scrollbar with half the page empty beside it. Stable once mounted, so measured once.
  useLayoutEffect(() => {
    setVisible(ref.current?.parentElement?.clientWidth ?? 0);
  }, [node.id]);

  // Bring it into view when the canvas is scrolled sideways and the card lands off-screen.
  useEffect(() => {
    ref.current?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
  }, [node.id]);

  // Mount transparent, then turn it on. The measurement between the two is load-bearing rather than
  // wasted work: a transition interpolates from a previously computed style, and an element that is
  // inserted and styled inside one recalculation has none, so it snaps in whatever the transition
  // says. Reading the box forces the transparent state to be computed, giving the flip that follows
  // something to leave. A rAF would also do it, but only in a tab that is painting — this does not
  // care. Empty deps on purpose: moving from one stop to the next keeps this component mounted, and
  // that should slide the card across, not blink it off and on.
  const [entered, setEntered] = useState(false);
  useLayoutEffect(() => {
    ref.current?.getBoundingClientRect();
    setEntered(true);
  }, []);
  const on = shown && entered;

  const GAP = 14;
  const MIN_W = 300;
  const avail = Math.max(canvas.width, visible);
  const roomRight = avail - (box.x + NODE_W + GAP) - 8;
  const roomLeft = box.x - GAP;

  const side = roomRight >= MIN_W ? 'right' : roomLeft >= MIN_W ? 'left' : 'below';
  const width = side === 'right' ? Math.min(460, roomRight)
    : side === 'left' ? Math.min(460, roomLeft)
    : Math.min(460, Math.max(avail - 16, MIN_W));

  const left = side === 'right' ? box.x + NODE_W + GAP
    : side === 'left' ? box.x - GAP - width
    : Math.max(8, Math.min(box.x, avail - width - 8));

  // Positioned as if the card were always at its full height, and it is capped at one, so it never
  // needs measuring. Measuring is what the obvious version does and it does not work: the diff
  // arrives into a CHILD of this card, so only the child re-renders and the card's own measurement
  // stays frozen at the height it had before the content it is being positioned around existed.
  // Clamping against the cap only moves a card at all when it is near the bottom edge, which is
  // exactly the case where a short one would have grown into trouble anyway.
  const wanted = side === 'below' ? box.y + box.h + GAP : box.y - 4;
  const top = Math.max(4, Math.min(wanted, canvas.height - Math.min(CARD_MAX, canvas.height - 8) - 4));
  const pointer = box.y + box.h / 2 - top;

  // It arrives from the stop it belongs to rather than from nowhere: 4px on whichever axis separates
  // the two, so the movement points back at the box you clicked. `pointer-events-none` on the way
  // out because a card that is 20% opaque is one you have finished with, and it sits over the stops.
  const from = side === 'right' ? 'translateX(-4px)'
    : side === 'left' ? 'translateX(4px)'
    : 'translateY(-4px)';

  return (
    <div
      ref={ref}
      style={{ left, top, width, maxHeight: CARD_MAX, transform: on ? undefined : from }}
      data-flow-card=""
      className={`absolute z-20 flex flex-col rounded-lg border border-indigo-900/70 bg-panel shadow-2xl shadow-black/60 transition-[opacity,transform] duration-[140ms] ease-out motion-reduce:transition-none ${
        on ? 'opacity-100' : 'opacity-0 pointer-events-none'
      }`}
    >
      {side !== 'below' && (
        <span
          aria-hidden
          className="absolute h-2.5 w-2.5 rotate-45 border-indigo-900/70 bg-panel"
          style={{
            top: Math.max(10, Math.min(pointer - 5, CARD_MAX - 20)),
            [side === 'right' ? 'left' : 'right']: -6,
            borderLeftWidth: side === 'right' ? 1 : 0,
            borderBottomWidth: side === 'right' ? 1 : 0,
            borderRightWidth: side === 'left' ? 1 : 0,
            borderTopWidth: side === 'left' ? 1 : 0,
          }}
        />
      )}

      <div className="flex flex-none items-start gap-3 px-4 pt-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2 mb-1">
            <span className="text-[9px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)]" style={{ color: tone.chip }}>
              {KIND_LABEL[node.kind] ?? 'STEP'}
            </span>
            <span
              className="text-[9px] uppercase tracking-[calc(var(--w-track)*0.08em)] px-1.5 py-0.5 rounded border"
              style={{ color: tone.chip, borderColor: tone.stroke }}
            >
              {CHANGE_LABEL[node.change]}
            </span>
            {node.actor && (
              <span
                className="text-[10px] px-1.5 py-0.5 rounded border"
                style={{ color: hue(node.actor), borderColor: hue(node.actor) }}
              >
                {node.actor}
              </span>
            )}
          </div>
          <h4 className="text-[14px] leading-snug text-gray-100">{node.label}</h4>
        </div>
        <button onClick={onClose} className="shrink-0 text-[11px] text-gray-600 hover:text-gray-300">
          close
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {node.explain && (
          <p className="px-4 pt-2 text-[13px] leading-[1.65] text-gray-300">
            {inline(node.explain)}
          </p>
        )}

        <div className="flex flex-col gap-2 px-4 pb-3 pt-3">
          {box.refs.length === 0 ? (
            <div className="text-[11px] text-gray-600">No file was cited for this stop.</div>
          ) : (
            box.refs.map((r, i) => <RefDiff key={r} location={r} defaultOpen={i === 0} />)
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * One cited file: a link into the editor, and what this branch did to it.
 *
 * The first one opens with the stop, because a panel that makes you click twice to see the thing
 * you opened it for is a panel you stop opening. The rest wait — three files' diffs unrolled at
 * once is the wall of text the diagram exists to avoid.
 */
function RefDiff({ location, defaultOpen }: { location: string; defaultOpen: boolean }) {
  const { analysisId } = useContext(FileLinks);
  const [open, setOpen] = useState(defaultOpen);
  const [diff, setDiff] = useState<NodeDiff | null>(null);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const asked = useRef(false);

  const { path } = splitLocation(location);

  useEffect(() => {
    if (!open || asked.current || analysisId === null) return;
    asked.current = true;
    setLoading(true);
    getNodeDiff(analysisId, path)
      .then(setDiff)
      .catch(e => setFailed((e as Error).message))
      .finally(() => setLoading(false));
  }, [open, analysisId, path]);

  const empty = diff !== null && !diff.error && diff.patch.trim().length === 0;

  return (
    <div className="overflow-hidden rounded border border-hair">
      <div className="flex items-center gap-2 bg-gray-900/50 px-2.5 py-1.5">
        <button
          onClick={() => setOpen(o => !o)}
          className={`shrink-0 text-[9px] text-gray-600 transition-transform ${open ? 'rotate-90' : ''}`}
        >
          ▶
        </button>
        <FileRef location={location} className="text-[11px]" />
        {diff && !diff.error && !empty && (
          <span className="ml-auto shrink-0 font-mono text-[10px]">
            <span className="text-emerald-500">+{diff.added}</span>{' '}
            <span className="text-red-500">−{diff.removed}</span>
          </span>
        )}
      </div>

      {open && (
        <div className="max-h-80 overflow-auto border-t border-hair bg-sunk">
          {loading && <div className="px-3 py-2 text-[10px] text-gray-600">Reading the diff...</div>}
          {failed && <div className="px-3 py-2 text-[10px] text-rose-400/80">{failed}</div>}
          {diff?.error && <div className="px-3 py-2 text-[10px] text-amber-500/80">{diff.error}</div>}
          {empty && (
            <div className="px-3 py-2 text-[10.5px] leading-relaxed text-gray-600">
              This file did not change on the branch. The stop cites it for where the behaviour
              lives, not for an edit made here.
            </div>
          )}
          {diff && !diff.error && !empty && <PatchView patch={diff.patch} />}
          {diff?.truncated && (
            <div className="border-t border-hair px-3 py-1.5 text-[10px] text-amber-500/80">
              Cut at the size limit — read the rest in the repo.
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** Shared by the header strip and the picker rows. */
export function Stat({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <div className="text-[9.5px] uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-600">{label}</div>
      <div className="text-[12px] text-gray-300 tabular-nums">{children}</div>
    </div>
  );
}
