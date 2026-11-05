/**
 * The dashboard's own runs on a plan's monthly API credits (ClaudeApiCredits on the server).
 *
 * Off by default, and off is the whole of the old behaviour: every run on plan usage. Switched on,
 * the roles ticked here run through the Agent SDK on a Console key instead, and a run whose key
 * refuses it - credits spent, or a key that does not work - goes again on plan usage.
 *
 * The keys never come back from the server. A saved one shows its name and last four characters;
 * typing into its box replaces it, and leaving the box empty keeps it.
 */

export interface ApiCreditKeyRow {
  name: string;
  /** Write-only: present only while a key is being typed. */
  key?: string;
  keySet?: boolean;
  hint?: string | null;
  refused?: { at: string; why: string } | null;
}

export interface ApiCreditsSetting {
  enabled: boolean;
  roles: string[];
  keys: ApiCreditKeyRow[];
  eligibleRoles?: string[];
  runnerUnavailable?: string | null;
}

export const API_CREDITS_OFF: ApiCreditsSetting = { enabled: false, roles: ['analysis'], keys: [] };

const field =
  'bg-gray-900 border border-hair rounded px-2 py-1 text-sm text-gray-200 focus:outline-none focus:border-gray-500 min-w-0';
const small = 'orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800 disabled:opacity-40';

function when(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' });
}

export function ApiCreditsField({ value, roleLabels, onChange }: {
  value: ApiCreditsSetting;
  /** What each role is called elsewhere on this page, by key. */
  roleLabels: Record<string, string>;
  onChange: (v: ApiCreditsSetting) => void;
}) {
  const eligible = value.eligibleRoles ?? ['analysis', 'applier'];
  const keys = value.keys ?? [];
  const setKeys = (keys: ApiCreditKeyRow[]) => onChange({ ...value, keys });
  const patch = (i: number, p: Partial<ApiCreditKeyRow>) => setKeys(keys.map((k, j) => (j === i ? { ...k, ...p } : k)));
  const toggleRole = (role: string, on: boolean) =>
    onChange({ ...value, roles: on ? [...value.roles.filter(r => r !== role), role] : value.roles.filter(r => r !== role) });

  return (
    <div className="space-y-3">
      <label className="flex items-start gap-2.5 group cursor-pointer">
        <input
          type="checkbox"
          checked={value.enabled}
          onChange={e => onChange({ ...value, enabled: e.target.checked })}
          className="mt-[3px] w-3.5 h-3.5 flex-none rounded accent-blue-500 cursor-pointer"
        />
        <span className="min-w-0">
          <span className="block text-sm text-gray-300 group-hover:text-gray-100 transition-colors">
            Run the dashboard's own work on API credits
          </span>
          <span className="block text-xs text-gray-500 mt-1">
            Max and Ultra include $100 / $200 a month of API credit in a linked Console organization. It pays for the
            Agent SDK and not for Claude Code, so the roles ticked below run through the SDK on a key from that
            organization, sparing the plan's usage for the fleet. When a key's credit is spent, or the key is
            refused, the run goes again on plan usage. Leave this off if your plan has no linked organization:
            everything then runs on plan usage, as it always has.
          </span>
        </span>
      </label>

      <div className={`space-y-3 ${value.enabled ? '' : 'opacity-50'}`}>
        <div className="flex flex-wrap gap-x-5 gap-y-2">
          {eligible.map(role => (
            <label key={role} className="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
              <input
                type="checkbox"
                checked={value.roles.includes(role)}
                onChange={e => toggleRole(role, e.target.checked)}
                className="w-3.5 h-3.5 rounded accent-blue-500 cursor-pointer"
              />
              {roleLabels[role] ?? role}
            </label>
          ))}
        </div>

        {keys.map((k, i) => (
          <div key={i} className="rounded border border-hair px-3 py-2 space-y-1.5">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
              <span className="text-xs text-gray-500 w-4">{i + 1}.</span>
              <input
                aria-label="Key name"
                placeholder="Name, e.g. Default org"
                value={k.name}
                onChange={e => patch(i, { name: e.target.value })}
                className={`${field} w-40`}
              />
              <input
                aria-label="Console API key"
                type="password"
                autoComplete="off"
                placeholder={k.keySet ? `Saved key ${k.hint ?? ''} - type to replace` : 'sk-ant-api…'}
                value={k.key ?? ''}
                onChange={e => patch(i, { key: e.target.value })}
                className={`${field} flex-1 basis-48`}
              />
              <button type="button" className={small} onClick={() => setKeys(keys.filter((_, j) => j !== i))}>
                Remove
              </button>
            </div>
            <p className="text-[11px] text-gray-500">
              {k.refused
                ? <span className="text-amber-400">Refused {when(k.refused.at)}: {k.refused.why}. Runs use the next key or plan usage, and it is tried again a few hours later.</span>
                : k.keySet ? 'Saved. Used first while it has credit.' : 'Save to start using it.'}
            </p>
          </div>
        ))}

        <div className="flex flex-wrap items-center gap-3">
          <button type="button" className={small} onClick={() => setKeys([...keys, { name: '', key: '' }])}>
            Add key
          </button>
          <span className="text-xs text-gray-600">Keys are tried in this order.</span>
        </div>

        {value.enabled && value.runnerUnavailable && (
          <p className="text-xs text-amber-400">Not installed yet, so runs stay on plan usage: {value.runnerUnavailable}</p>
        )}

        <p className="text-xs text-gray-500">
          To make sure nothing is ever charged: in each organization's Console billing, keep auto-reload off and
          the purchased balance at zero. Then a spent key is simply refused until next month's credit arrives.
        </p>
      </div>
    </div>
  );
}
