import type { GateFinding, IndependentReview as Review } from '../types/tickets';
import { Markdown } from './LearningBrief';

const KIND_LABEL: Record<GateFinding['kind'], string> = {
  Risk: 'Risk',
  Gap: 'Requirement not addressed',
  Partial: 'Requirement partly met',
};

/**
 * What the independent Classroom analysis raised before refinement, and what refinement ruled on
 * each. Drawn open rather than behind a toggle: it is short, and a finding ruled "not a defect" is
 * exactly the call the person signing the ticket off is there to second-guess.
 */
export function IndependentReview({ review }: { review: Review }) {
  const { findings } = review;
  const defects = findings.filter(f => f.ruling === 'Defect').length;
  const open = findings.filter(f => !f.ruling).length;

  return (
    <div>
      <div className="flex items-center gap-2">
        <span className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-amber-300/80">
          Independent analysis
        </span>
        <span className="text-[11px] text-gray-600">
          {findings.length === 0
            ? 'no findings'
            : `${findings.length} finding${findings.length === 1 ? '' : 's'}`
              + (defects ? ` · ${defects} defect${defects === 1 ? '' : 's'}` : '')
              + (open ? ` · ${open} not yet ruled` : '')}
        </span>
      </div>

      {findings.length > 0 && (
        <ul className="mt-2 flex flex-col gap-2">
          {findings.map(f => (
            <li key={f.id} className="rounded-lg border border-hair bg-panel px-3 py-2.5">
              <div className="flex items-start gap-2">
                <span className="text-[10px] uppercase tracking-wide text-gray-500 flex-none mt-[2px]">
                  {KIND_LABEL[f.kind]}
                </span>
                <span className="ml-auto flex-none"><Ruling finding={f} /></span>
              </div>
              <div className="text-[13px] text-gray-200 mt-1">{f.title}</div>
              {f.text && <div className="mt-1 text-gray-400"><Markdown text={f.text} /></div>}
              {f.rulingNote && (
                <div className="mt-2 border-t border-hair pt-2 text-[12px] leading-snug text-gray-400">
                  <span className="text-gray-500">Refinement: </span>{f.rulingNote}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Ruling({ finding }: { finding: GateFinding }) {
  if (finding.ruling === 'Defect')
    return <span className="text-[11px] px-1.5 py-0.5 rounded bg-red-950/40 text-red-300">Defect</span>;
  if (finding.ruling === 'NotADefect')
    return <span className="text-[11px] px-1.5 py-0.5 rounded bg-emerald-950/40 text-emerald-300">Not a defect</span>;
  return <span className="text-[11px] px-1.5 py-0.5 rounded bg-gray-800 text-gray-400">Not yet ruled</span>;
}
