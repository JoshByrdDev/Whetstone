/**
 * A unified diff, coloured.
 *
 * Extracted so the PR dialog and the Classroom render a patch identically. They arrive by different
 * routes — one from a branch preflight, one from a stop on a user-flow diagram — and a reader who
 * learned to read green-is-added in the first should not have to learn it again in the second.
 */
export function PatchView({ patch, className = '' }: { patch: string; className?: string }) {
  const lines = patch.split('\n');
  if (lines.length === 0 || (lines.length === 1 && lines[0] === ''))
    return <div className="px-3 py-2 text-[10px] text-gray-600">No text diff for this file.</div>;

  return (
    <pre className={`text-[10.5px] font-mono leading-[1.45] ${className}`}>
      {lines.map((l, i) => (
        <div key={i} className={`px-3 whitespace-pre-wrap break-all ${lineCls(l)}`}>{l || ' '}</div>
      ))}
    </pre>
  );
}

export function lineCls(line: string) {
  if (line.startsWith('@@')) return 'text-sky-400 bg-sky-950/30';
  if (line.startsWith('+')) return 'text-emerald-300 bg-emerald-950/25';
  if (line.startsWith('-')) return 'text-red-300 bg-red-950/25';
  return 'text-gray-500';
}
