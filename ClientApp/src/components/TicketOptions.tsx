/**
 * The per-ticket pipeline settings: the two human review gates, how much QA the ticket gets, and
 * whether refinement waits for an independent Classroom analysis. The gates and QA are off by
 * default; the analysis setting starts from the project's default — Claude runs the pipeline end to end, and QA runs in full. Shared
 * by the create modal (local state until submit) and the ticket drawer (persists on change).
 *
 * The gates and the QA setting are drawn apart on purpose. A gate ADDS a stop for a human; skipping
 * QA testing REMOVES work from a phase. Reading them as one list is how you end up believing the
 * third one parks the ticket somewhere, which it never does.
 */
export type TicketOptionValues = {
  requiresSpeculationReview?: boolean;
  requiresImplementationReview?: boolean;
  skipQaTesting?: boolean;
  skipClassroomAnalysis?: boolean;
};

export function TicketOptions({
  speculation, implementation, skipQaTesting, skipClassroomAnalysis, disabled, onChange, track = 'Delivery',
}: {
  speculation: boolean;
  implementation: boolean;
  skipQaTesting: boolean;
  skipClassroomAnalysis: boolean;
  disabled?: boolean;
  onChange: (values: TicketOptionValues) => void;
  /**
   * The two gates below are Delivery-only: a review ticket never enters speculation or
   * implementation, so offering them would be offering settings nothing will ever read. The third
   * survives the switch unchanged — on either track "skip QA testing" means read the diff and do
   * not stand the app up, which is why the flag is reused rather than reinvented.
   */
  track?: 'Delivery' | 'Review';
}) {
  const reviewing = track === 'Review';
  return (
    <div className="flex flex-col gap-1.5">
      {!reviewing && (
        <>
          <OptionCheckbox
            checked={speculation}
            disabled={disabled}
            label="Requires speculation review"
            hint="Stop at Needs Approval so you can review the plan before implementation."
            onChange={v => onChange({ requiresSpeculationReview: v })}
          />
          <OptionCheckbox
            checked={implementation}
            disabled={disabled}
            label="Requires implementation review"
            hint="Stop after implementation so you can review the code before QA runs."
            onChange={v => onChange({ requiresImplementationReview: v })}
          />
        </>
      )}
      <div className={reviewing ? '' : 'border-t border-white/5 mt-1 pt-2'}>
        <OptionCheckbox
          checked={skipQaTesting}
          disabled={disabled}
          label={reviewing ? 'Read the diff only' : 'Skip QA testing (code review only)'}
          hint={reviewing
            ? 'Review the diff, but never run the app. No checklist is written and no screenshots are asked for.'
            : 'QA still reviews the diff, but never runs the app. No QA checklist is written, and no screenshots are asked for.'}
          accent="accent-amber-500"
          onChange={v => onChange({ skipQaTesting: v })}
        />
        {/* Delivery-only like the gates: a review ticket has no refinement phase to hand one to. */}
        {!reviewing && (
          <div className="mt-1.5">
            <OptionCheckbox
              checked={skipClassroomAnalysis}
              disabled={disabled}
              label="Skip classroom analysis"
              hint="Refinement starts without waiting for an independent analysis of the change, and has no findings to rule on. Saves a run on small work."
              accent="accent-amber-500"
              onChange={v => onChange({ skipClassroomAnalysis: v })}
            />
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * What a ticket's testing needs whatever its branch changes. A project can give a resource only to
 * the branches that touch its paths - an emulator only to a change of the phone app - and a ticket
 * whose proof is on that device but whose change is not (a layout check with screenshots inside the
 * app) has to say so, or its tester never gets the device and can only block. Drawn only for a
 * project that has such a resource.
 */
export function TicketNeeds({ askable, needs, disabled, onChange }: {
  askable: string[];
  needs: string[];
  disabled?: boolean;
  onChange: (needs: string[]) => void;
}) {
  if (askable.length === 0) return null;
  return (
    <div className="flex flex-col gap-1.5">
      {askable.map(name => (
        <OptionCheckbox
          key={name}
          checked={needs.includes(name)}
          disabled={disabled}
          label={`Testing needs ${name}`}
          hint="Given to this ticket's testing whatever its branch changes. Without it, only a branch that changes what it is for gets it."
          onChange={v => onChange(v ? [...needs.filter(n => n !== name), name] : needs.filter(n => n !== name))}
        />
      ))}
    </div>
  );
}

function OptionCheckbox({ checked, disabled, label, hint, accent = 'accent-violet-500', onChange }: {
  checked: boolean;
  disabled?: boolean;
  label: string;
  hint: string;
  accent?: string;
  onChange: (v: boolean) => void;
}) {
  return (
    <label className="flex items-start gap-2.5 cursor-pointer group">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={e => onChange(e.target.checked)}
        className={`mt-[3px] w-3.5 h-3.5 flex-none rounded ${accent} cursor-pointer disabled:cursor-default`}
      />
      <span className="min-w-0">
        <span className={`text-[13px] ${checked ? 'text-gray-200' : 'text-gray-400'} group-hover:text-gray-200 transition-colors`}>
          {label}
        </span>
        <span className="block text-[11px] leading-snug text-gray-600">{hint}</span>
      </span>
    </label>
  );
}
