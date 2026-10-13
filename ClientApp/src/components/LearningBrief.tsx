import { useState } from 'react';
import { ago } from './JourneyTrack';
import { Markdown } from './Markdown';

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

// The renderer lives in its own file now that a plan and a spec are read with it too. Still
// exported from here, where everything that renders a brief already looks for it.
export { Markdown, inline } from './Markdown';
