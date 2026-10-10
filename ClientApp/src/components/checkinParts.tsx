/**
 * The small pieces the two TFVC dialogs share - the check-in and the sync - so the same banner means
 * the same thing in both.
 */

export type Tone = 'good' | 'warn' | 'bad' | 'wait';

const TONES: Record<Tone, string> = {
  good: 'border-emerald-900 bg-emerald-950/30 text-emerald-300',
  warn: 'border-amber-900 bg-amber-950/30 text-amber-300',
  bad: 'border-red-900 bg-red-950/30 text-red-300',
  // Not a verdict: an explanation, or an answer still on its way.
  wait: 'border-hair bg-gray-900/40 text-gray-400',
};

export function Banner({ tone, children }: { tone: Tone; children: React.ReactNode }) {
  return <div className={`text-xs leading-relaxed rounded border px-3 py-2 ${TONES[tone]}`}>{children}</div>;
}

/** A command the reader can select and run themselves. */
export function Cmd({ children }: { children: string }) {
  return (
    <div className="mt-1.5 font-mono text-[10px] text-gray-400 bg-black/50 rounded px-2 py-1 select-all break-all">
      {children}
    </div>
  );
}

/** A repo path with its folders dimmed, so the file name is what the eye lands on. */
export function PathText({ path, dim = false }: { path: string; dim?: boolean }) {
  const cut = path.lastIndexOf('/');
  return (
    <span className="font-mono truncate min-w-0 flex-1" title={path}>
      <span className="text-gray-600">{cut > 0 ? path.slice(0, cut + 1) : ''}</span>
      <span className={dim ? 'text-gray-500' : 'text-gray-200'}>{cut > 0 ? path.slice(cut + 1) : path}</span>
    </span>
  );
}
