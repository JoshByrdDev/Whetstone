import { useState } from 'react';
import type { ReactNode } from 'react';
import { ago } from './JourneyTrack';

/**
 * The learning brief — reading material the refine phase leaves on a ticket so the human reviewing
 * it keeps learning the target repo's domain instead of only trusting the pipeline's output.
 *
 * Collapsed by default and gated on nothing: this is optional reading, and a panel that shouted
 * several hundred words at every ticket would be scrolled past rather than read. The heading is
 * whatever the project calls it ("Learn <project>"), passed in rather than known here.
 */
export function LearningBrief({ label, brief, writtenAt }: { label: string; brief: string; writtenAt?: string | null }) {
  const [open, setOpen] = useState(false);
  const words = brief.trim().split(/\s+/).length;

  return (
    <div>
      <button
        onClick={() => setOpen(!open)}
        className="w-full flex items-center gap-2 text-left group"
      >
        <span className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-indigo-300/80 group-hover:text-indigo-200">
          {label}
        </span>
        <span className="text-[11px] text-gray-600">
          {words} words{writtenAt ? ` · ${ago(writtenAt)} ago` : ''}
        </span>
        <span className={`ml-auto text-[10px] text-gray-500 transition-transform ${open ? 'rotate-90' : ''}`}>&#9654;</span>
      </button>

      {open && (
        <div className="mt-2.5 rounded-lg border border-indigo-900/50 bg-indigo-950/20 px-3.5 py-3">
          <Markdown text={brief} />
        </div>
      )}
    </div>
  );
}

/**
 * Just enough markdown for a brief: `##` headings, `-` bullets, paragraphs, `**bold**`, `` `code` ``
 * and the `[[note]]` links that point into the repo's own notes store. Deliberately a renderer that
 * builds elements rather than HTML — the text is model-written, so it never reaches innerHTML — and
 * deliberately not a dependency, since this is the only place the app renders prose.
 */
export function Markdown({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  let bullets: string[] = [];

  const flushBullets = () => {
    if (bullets.length === 0) return;
    blocks.push(
      <ul key={`u${blocks.length}`} className="list-disc pl-4 my-1.5 flex flex-col gap-1">
        {bullets.map((b, i) => <li key={i} className="text-[13px] leading-relaxed text-gray-300">{inline(b)}</li>)}
      </ul>,
    );
    bullets = [];
  };

  for (const raw of text.split('\n')) {
    const line = raw.trimEnd();
    const heading = /^#{1,4}\s+(.*)$/.exec(line);
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);

    if (heading) {
      flushBullets();
      // Bright and ruled off, not tinted. The ORBIT theme rebuilt `indigo` out of the board's blue
      // tier, which lands within a few points of `gray-300` — so a heading that separated itself
      // from the body by being indigo separated itself from nothing, and the sections read as one
      // wall of prose broken by capitals. The steps that still carry here are the ones the palette
      // did not flatten: a brighter rung of the same ramp, more weight, and a rule above.
      blocks.push(
        <h4 key={`h${blocks.length}`} className="text-[12px] font-bold uppercase tracking-[calc(var(--w-track)*0.14em)] text-gray-100 mt-4 pt-3 border-t border-hair first:mt-0 first:pt-0 first:border-0 mb-1.5">
          {inline(heading[1])}
        </h4>,
      );
    } else if (bullet) {
      bullets.push(bullet[1]);
    } else if (line.trim() === '') {
      flushBullets();
    } else {
      flushBullets();
      blocks.push(
        <p key={`p${blocks.length}`} className="text-[13px] leading-relaxed text-gray-300 mt-1.5 first:mt-0">
          {inline(line)}
        </p>,
      );
    }
  }
  flushBullets();

  return <>{blocks}</>;
}

/**
 * Inline spans: **bold**, `code`, and [[note]] links into the repo's notes store.
 * Exported because the Classroom's blocks render the same model-written prose and must agree with
 * the drawer on what those three markers mean.
 */
export function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /\*\*(.+?)\*\*|`([^`]+)`|\[\[([^\]]+)\]\]/g;
  let last = 0;
  let m: RegExpExecArray | null;

  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    if (m[1] !== undefined) {
      out.push(<strong key={out.length} className="font-semibold text-gray-100">{m[1]}</strong>);
    } else if (m[2] !== undefined) {
      out.push(<code key={out.length} className="font-mono text-[12px] text-sky-300">{m[2]}</code>);
    } else {
      // A note in the target repo's store. There is no URL to open it with — the value is knowing
      // it exists and what it is called, so it reads as a citation rather than a dead link.
      out.push(
        <span key={out.length} className="font-mono text-[12px] text-indigo-300" title="A note in this repo's notes store">
          {m[3]}
        </span>,
      );
    }
    last = re.lastIndex;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
