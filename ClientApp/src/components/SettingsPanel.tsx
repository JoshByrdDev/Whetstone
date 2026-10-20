import { Fragment, useState, useEffect, useCallback } from 'react';
import { PageHead, SectionRule } from './ui';
import { chamfer } from './orbit';
import { skyEnabled, setSkyEnabled } from './Backdrop';
import { THEMES, WEEKDAYS, WEEKDAY_LABEL, currentTheme, nextTheme, resolveTheme, setTheme, setThemePref, useThemePref, weekOf } from '../theme';
import type { ThemePref, Weekday } from '../theme';
import type { Track } from '../types/tickets';

// ---------- Shared field primitives -----------------------------------------

function FieldGroup({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-gray-800 border border-hair p-5" style={{ clipPath: chamfer(13) }}>
      <SectionRule title={title} colour="var(--w-queue)" />
      <div className="space-y-4">{children}</div>
    </div>
  );
}

function Field({
  label, description, value, onChange, type = 'text', placeholder, isSecret, error, disabled,
}: {
  label: string;
  description?: string;
  value: string | number;
  onChange: (val: string) => void;
  type?: string;
  placeholder?: string;
  isSecret?: boolean;
  error?: string;
  disabled?: boolean;
}) {
  const [revealed, setRevealed] = useState(false);
  return (
    <div>
      <label className="block text-sm text-gray-400 mb-1">{label}</label>
      {description && <p className="text-xs text-gray-500 mb-1.5">{description}</p>}
      <div className="flex gap-2">
        <input
          type={isSecret && !revealed ? 'password' : type}
          value={value}
          disabled={disabled}
          onChange={e => onChange(e.target.value)}
          placeholder={placeholder}
          className={`flex-1 min-w-0 bg-gray-900 border rounded px-3 py-2 text-sm text-gray-200 placeholder-gray-600 focus:outline-none transition-colors font-mono disabled:opacity-50 ${
            error ? 'border-red-500 focus:border-red-400' : 'border-gray-600 focus:border-gray-400'
          }`}
        />
        {isSecret && (
          <button
            type="button"
            onClick={() => setRevealed(!revealed)}
            className="px-2 text-xs text-gray-400 hover:text-gray-200 bg-gray-700 rounded transition-colors"
            title={revealed ? 'Hide' : 'Reveal'}
          >
            {revealed ? 'Hide' : 'Show'}
          </button>
        )}
      </div>
      {error && <p className="text-xs text-red-400 mt-1">{error}</p>}
    </div>
  );
}

/** A boolean field laid out like Field: label, optional description, control on the left. */
function CheckField({ label, description, checked, onChange, disabled, title }: {
  label: string; description?: string; checked: boolean; onChange: (v: boolean) => void;
  /** Locked, with `title` saying what would have to change first. Still shown, still readable. */
  disabled?: boolean; title?: string;
}) {
  return (
    <label
      title={title}
      className={`flex items-start gap-2.5 group ${disabled ? 'cursor-not-allowed' : 'cursor-pointer'}`}
    >
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={e => onChange(e.target.checked)}
        className={`mt-[3px] w-3.5 h-3.5 flex-none rounded accent-blue-500 ${disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}
      />
      <span className="min-w-0">
        <span className="block text-sm text-gray-300 group-hover:text-gray-100 transition-colors">{label}</span>
        {description && <span className="block text-xs text-gray-500 mt-1">{description}</span>}
      </span>
    </label>
  );
}


/**
 * How each track is described where a person chooses it. One table rather than a conditional at
 * each use so the settings checkbox and the New Ticket toggle cannot end up calling the same thing
 * two different names.
 */
const TRACK_COPY: Record<Track, { label: string; noun: string; hint: string }> = {
  Delivery: {
    label: 'Delivery',
    noun: 'delivery',
    hint: 'Our own work, start to finish: plan, build, test and ship it on our own branch.',
  },
  Review: {
    label: 'Peer review',
    noun: 'peer review',
    hint: "Somebody else's branch: read the diff, drive-test it and stage PR feedback. The pipeline never edits their code and nothing leaves this machine until you send it.",
  },
};
// Pops the native Windows folder dialog (server-side) and returns the chosen path.
async function pickFolder(initialPath?: string): Promise<string | null> {
  try {
    const res = await fetch('/api/system/pick-folder', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ initialPath: initialPath ?? '' }),
    });
    if (!res.ok) return null;
    const data = await res.json();
    return data.cancelled ? null : (data.path ?? null);
  } catch {
    return null;
  }
}

// Last path segment, e.g. "C:\\Projects\\Acme.Storefront" -> "Acme.Storefront".
function leafName(path: string): string {
  const parts = path.split(/[\\/]+/).filter(Boolean);
  return parts[parts.length - 1] ?? path;
}

// Folder name -> a valid project key (lowercase, [a-z0-9_-]).
function slugify(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-+|-+$)/g, '') || 'project';
}

// Read-only path field whose value is set only by the native folder picker.
function FolderField({ label, description, value, onPick }: {
  label: string; description?: string; value: string; onPick: (path: string) => void;
}) {
  const browse = async () => {
    const picked = await pickFolder(value || undefined);
    if (picked) onPick(picked);
  };
  return (
    <div>
      <label className="block text-sm text-gray-400 mb-1">{label}</label>
      {description && <p className="text-xs text-gray-500 mb-1.5">{description}</p>}
      <div className="flex gap-2">
        <input
          readOnly
          value={value}
          placeholder="No folder selected"
          onClick={browse}
          className="flex-1 min-w-0 bg-gray-900 border border-gray-600 rounded px-3 py-2 text-sm text-gray-200 placeholder-gray-600 font-mono cursor-pointer focus:outline-none"
        />
        <button
          type="button"
          onClick={browse}
          className="btn-act px-3.5 text-gray-200 bg-gray-700 whitespace-nowrap"
        >
          Browse…
        </button>
      </div>
    </div>
  );
}

type Msg = { type: 'success' | 'error'; text: string } | null;

/** The result of actually trying the saved usage token, as opposed to merely having stored it. */
type TokenCheck = { state: 'checking' } | { state: 'ok' | 'bad'; text: string } | null;

function SaveBar({ msg, saving, disabled, onSave, label = 'Save' }: {
  msg: Msg; saving: boolean; disabled: boolean; onSave: () => void; label?: string;
}) {
  return (
    <div className="flex items-center gap-3">
      {msg && (
        <span className={`text-xs ${msg.type === 'success' ? 'text-emerald-400' : 'text-red-400'}`}>{msg.text}</span>
      )}
      <button
        onClick={onSave}
        disabled={saving || disabled}
        className="btn-act px-4 py-2 bg-blue-500 text-void disabled:bg-gray-700 disabled:text-gray-500"
      >
        {saving ? 'Saving…' : label}
      </button>
    </div>
  );
}

// ---------- Types -----------------------------------------------------------

interface GlobalConfig {
  dashboardUrl: string;
  /** Where notifications go — a key from notificationChannels. */
  notificationChannel: string;
  /** Which notifications are sent, by key from notificationEventCatalog. Empty = none. */
  notificationEvents: string[];
  slackWebhookUrl: string;
  /** Blank means the public server, https://ntfy.sh. */
  ntfyServerUrl: string;
  ntfyTopic: string;
  /** Only for a protected topic; blank sends anonymously. */
  ntfyToken: string;
  baseHeartbeatMinutes: number;
  heartbeatIdleMultiplier: number;
  heartbeatMaxMinutes: number;
  offHoursStart: string;
  offHoursEnd: string;
  heartbeatMaxAttempts: number;
  heartbeatWorkerTimeoutMinutes: number;
  /** End a run whose agent has been silent this long; 0 = off. */
  heartbeatWorkerStallMinutes: number;
  /** How many agents run at once. */
  heartbeatWorkers: number;
  /** The hard ceiling the server enforces — not editable, just what the UI offers. */
  maxHeartbeatWorkers: number;
  /** Which agent CLI runs the work. The model lists below depend on it. */
  agentProvider: string;
  workerModel: string;
  reflectionModel: string;
  /** Per-phase overrides of workerModel, keyed by skill. A phase absent from this map runs
   *  workerModel — absence is the "default" state, not an empty string. */
  phaseModels: Record<string, string>;
  /** Overrides workerModel for the Classroom analysis alone. Empty means "follow workerModel",
   *  stored as an absence for the same reason phaseModels are. */
  analysisModel: string;
  /** Passed to providers that expose a reasoning dial; ignored by those that do not. */
  workerReasoningEffort: string;
  /** Whether a token is already saved. The token itself is never sent back to the browser. */
  claudeOauthTokenSet: boolean;
  /** Write-only: absent unless the user typed one, so saving without touching it keeps the stored
   *  token, and an explicit empty string clears it. */
  claudeOauthToken?: string;
  /** Claude logins beyond the CLI's own, which the usage chip can switch the fleet between. */
  claudeAccounts: ClaudeAccountOption[];
  /** Served by the API rather than hardcoded here, so the two halves cannot disagree about
   *  which providers and models exist. */
  agentProviders: AgentProviderOption[];
  /** The phases phaseModels may name, in pipeline order — served for the same reason. */
  workerPhases: WorkerPhaseOption[];
  /** The notifications this server knows how to send, and the channels it can send them through —
   *  served alongside the config for the same reason as agentProviders. */
  notificationEventCatalog: NotificationEventOption[];
  notificationChannels: NotificationChannelOption[];
}

/** `configDir` is blank until the first save, when the server names a folder for the account;
 *  `signedIn` is the server's report and absent on a row that has not been saved yet. */
interface ClaudeAccountOption { name: string; configDir: string; signedIn?: boolean }

interface ModelOption { value: string; label: string }

interface NotificationEventOption { key: string; label: string; description: string }

interface NotificationChannelOption { key: string; label: string; description: string }

/** One pipeline phase, by the skill name phaseModels is keyed by. */
interface WorkerPhaseOption { key: string; label: string; description: string }

interface AgentProviderOption {
  key: string;
  label: string;
  executable: string;
  defaultWorkerModel: string;
  defaultReflectionModel: string;
  supportsReasoningEffort: boolean;
  reportsUsage: boolean;
  workerModels: ModelOption[];
  reflectionModels: ModelOption[];
  /** A caveat worth showing before this one is selected. Absent for a provider with none. */
  note?: string;
}

interface SkillStatus { name: string; present: boolean }

interface ProjectDetail {
  key: string;
  displayName: string;
  /**
   * Which pipelines this project runs — the checkboxes below. Presentation only: nothing in the
   * pipeline reads it, and a ticket carries its own track. `allTracks` is the full catalogue, served
   * rather than hard-coded so the form cannot offer one the server does not know.
   */
  tracks: Track[];
  allTracks: Track[];
  /** Unfinished tickets on each track here — what makes a ticked box un-tickable. */
  openTickets: Record<Track, number>;
  /**
   * What a new ticket's "skip classroom analysis" box starts as. Only a default: every ticket
   * carries its own setting and can be switched either way.
   */
  skipClassroomAnalysisByDefault?: boolean;
  /** Deliver by merging the ticket's branch into the base instead of raising a pull request. */
  mergeInsteadOfPr?: boolean;
  projectPath: string;
  baseBranch: string;
  branchPrefix: string;
  // Where per-ticket agent worktrees are created. Blank = derived beside the repo, which the
  // server reports back as resolvedWorktreeRoot so the placeholder can show what blank means.
  worktreeRoot: string;
  resolvedWorktreeRoot: string;
  worktreeSetup: string[];
  worktreeCopyFiles: string[];
  disabled: boolean;
  skills: SkillStatus[];
  skillsReady: boolean;
  tracker: TrackerSettings;
  /** Each provider's default vocabulary, served so the two halves of the app cannot disagree
   *  about it — see the reset in `setTrackerKind`. Keyed by the same token as `tracker.kind`. */
  trackerDefaults: Partial<Record<TrackerKindName, { blockingLinkTypes: string[]; doneStates: string[] }>>;
  qaApiPort: number;
  qaWebPort: number;
  /** `passwordSet` says whether one is saved; the password itself is never sent back. `password`
   *  is write-only, as `TrackerSettings.token` is. */
  qaLogin: { username: string; passwordSet: boolean; password?: string };
}

/**
 * One project's external issue tracker. One flat shape for both, because the two need almost the
 * same things and each field below is simply hidden for the provider that has no use for it —
 * `email` is Jira's (Azure DevOps authenticates a PAT with an empty username) and `project` is
 * Azure DevOps' (a Jira key names its own project).
 */
type TrackerKindName = 'none' | 'jira' | 'azureDevOps';
interface TrackerSettings {
  kind: TrackerKindName;
  baseUrl: string;
  email: string;
  /** Whether a token is already saved. The token itself is never sent back to the browser. */
  tokenSet: boolean;
  /** Write-only: absent unless the user typed one, so saving without touching it keeps the stored
   *  token, and an explicit empty string clears it. */
  token?: string;
  project: string;
  // Which link types gate a ticket, and which states mean finished. Left to the config file on
  // Jira, where both answers are built in; surfaced here for Azure DevOps, where they are not.
  blockingLinkTypes: string[];
  doneStates: string[];
}

/** What each provider calls its own fields, so the form can label itself honestly. */
const TRACKERS: Record<TrackerKindName, {
  label: string; baseUrlLabel: string; baseUrlPlaceholder: string; tokenLabel: string; note: string;
}> = {
  none: {
    label: 'None', baseUrlLabel: '', baseUrlPlaceholder: '', tokenLabel: '',
    note: 'Tickets on this project carry their own titles and descriptions.',
  },
  jira: {
    label: 'Jira',
    baseUrlLabel: 'Site URL',
    baseUrlPlaceholder: 'https://yoursite.atlassian.net',
    tokenLabel: 'API Token',
    note: 'Read-only. The dashboard never writes to Jira.',
  },
  azureDevOps: {
    label: 'Azure Boards',
    baseUrlLabel: 'Organization URL',
    baseUrlPlaceholder: 'https://dev.azure.com/your-org',
    tokenLabel: 'Personal Access Token',
    note: 'Read-only. The PAT needs Work Items (Read) only.',
  },
};

const TIME_24H = /^([01]\d|2[0-3]):[0-5]\d$/;
const URL_RE = /^https?:\/\/.+/;
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const KEY_RE = /^[a-z0-9][a-z0-9_-]*$/;
const NTFY_TOPIC_RE = /^[A-Za-z0-9_-]{1,64}$/;

/** A comma-separated field back to a list, dropping the blanks a trailing comma leaves. */
const splitList = (v: string) => v.split(',').map(s => s.trim()).filter(Boolean);

// ---------- Global settings -------------------------------------------------

function GlobalSettings() {
  const [cfg, setCfg] = useState<GlobalConfig | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<Msg>(null);
  const [tokenCheck, setTokenCheck] = useState<TokenCheck>(null);
  // The notification test, reported beside its button for the same reason as the token check: "sent"
  // is a claim about the channel, not about the save.
  const [notifyTest, setNotifyTest] = useState<TokenCheck>(null);
  // What opening a sign-in terminal for a second Claude account answered.
  const [accountMsg, setAccountMsg] = useState<TokenCheck>(null);
  // Per-machine, so it is not part of cfg and never rides along with "Save global" — it is written
  // the moment it is ticked, which is also the moment the background changes behind the panel.
  const [sky, setSky] = useState(skyEnabled);
  // Same story as the sky: per-machine, written the moment it is picked, and the moment it is
  // picked is also the moment the panel it is sitting in changes colour. Read through the hook
  // rather than held locally, because two of the three modes change the answer on their own.
  const pref = useThemePref();

  useEffect(() => {
    fetch('/api/config').then(r => r.json()).then(setCfg).catch(() => setMsg({ type: 'error', text: 'Failed to load' }));
  }, []);

  const update = (patch: Partial<GlobalConfig>) => {
    setCfg(prev => (prev ? { ...prev, ...patch } : prev));
    setDirty(true);
    setMsg(null);
    // Any edit makes the last test result a claim about settings that are no longer on screen.
    setNotifyTest(null);
  };

  const errors: Record<string, string> = {};
  if (cfg) {
    if (cfg.dashboardUrl && !URL_RE.test(cfg.dashboardUrl)) errors.dashboardUrl = 'Must be a valid URL';
    if (cfg.slackWebhookUrl && !URL_RE.test(cfg.slackWebhookUrl)) errors.slackWebhookUrl = 'Must be a valid URL';
    if (cfg.ntfyServerUrl && !URL_RE.test(cfg.ntfyServerUrl)) errors.ntfyServerUrl = 'Must be a valid URL';
    // ntfy topics are a path segment: letters, digits, dash and underscore, up to 64.
    if (cfg.ntfyTopic && !NTFY_TOPIC_RE.test(cfg.ntfyTopic)) errors.ntfyTopic = 'Letters, digits, - and _ only (max 64)';
    if (cfg.baseHeartbeatMinutes < 1) errors.baseHeartbeatMinutes = 'Must be ≥ 1';
    if (cfg.heartbeatIdleMultiplier < 1) errors.heartbeatIdleMultiplier = 'Must be ≥ 1';
    if (cfg.heartbeatMaxMinutes < 1) errors.heartbeatMaxMinutes = 'Must be ≥ 1';
    if (cfg.heartbeatMaxMinutes < cfg.baseHeartbeatMinutes) errors.heartbeatMaxMinutes = 'Must be ≥ base interval';
    if (cfg.offHoursStart && !TIME_24H.test(cfg.offHoursStart)) errors.offHoursStart = 'Use 24h format (e.g. 17:00)';
    if (cfg.offHoursEnd && !TIME_24H.test(cfg.offHoursEnd)) errors.offHoursEnd = 'Use 24h format (e.g. 06:30)';
    if (cfg.heartbeatMaxAttempts < 1) errors.heartbeatMaxAttempts = 'Must be ≥ 1';
    if (cfg.heartbeatWorkerTimeoutMinutes < 1) errors.heartbeatWorkerTimeoutMinutes = 'Must be ≥ 1';
    if (cfg.heartbeatWorkerStallMinutes < 0) errors.heartbeatWorkerStallMinutes = 'Must be ≥ 0';
  }
  const hasErrors = Object.keys(errors).length > 0;

  /**
   * Ask the server to poll with whatever token it now holds. Reported next to the field rather than
   * in the save bar: "saved" and "the token works" are different claims, and a wrong paste makes the
   * first one true and the second one false.
   */
  const checkToken = async () => {
    setTokenCheck({ state: 'checking' });
    try {
      const r = await fetch('/api/usage/check', { method: 'POST' });
      const d = await r.json();
      setTokenCheck(
        d.ok
          ? { state: 'ok', text: d.summary || 'Token accepted.' }
          : { state: 'bad', text: d.message ?? 'The token was not accepted.' },
      );
    } catch {
      setTokenCheck({ state: 'bad', text: 'Could not reach the dashboard to check.' });
    }
  };

  /**
   * Send one test notification through whatever the form currently says — saved or not, so a topic
   * can be tried before it is committed. The server runs the heartbeat's own sender to do it.
   */
  const testNotification = async () => {
    if (!cfg) return;
    setNotifyTest({ state: 'checking' });
    try {
      const r = await fetch('/api/notifications/test', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          notificationChannel: cfg.notificationChannel,
          slackWebhookUrl: cfg.slackWebhookUrl,
          ntfyServerUrl: cfg.ntfyServerUrl,
          ntfyTopic: cfg.ntfyTopic,
          ntfyToken: cfg.ntfyToken,
        }),
      });
      const d = await r.json();
      setNotifyTest({ state: d.ok ? 'ok' : 'bad', text: d.message ?? d.error ?? 'The test did not send.' });
    } catch {
      setNotifyTest({ state: 'bad', text: 'Could not reach the dashboard to send the test.' });
    }
  };

  /**
   * Open a sign-in terminal on the dashboard's machine, pointed at this account's folder. The server
   * can only do that for an account it has stored, hence the save-first rule on the button.
   */
  const signInAccount = async (name: string) => {
    setAccountMsg(null);
    try {
      const r = await fetch(`/api/usage/signin?account=${encodeURIComponent(name)}`, { method: 'POST' });
      const d = await r.json();
      setAccountMsg({ state: d.ok ? 'ok' : 'bad', text: d.message ?? 'Could not open a sign-in terminal.' });
    } catch {
      setAccountMsg({ state: 'bad', text: 'Could not reach the dashboard to open a sign-in terminal.' });
    }
  };

  const save = async () => {
    if (!cfg || hasErrors) return;
    setSaving(true); setMsg(null);
    try {
      const res = await fetch('/api/config', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(cfg),
      });
      if (!res.ok) throw new Error((await res.json()).error ?? 'Save failed');
      setMsg({ type: 'success', text: 'Saved. Restart the server to apply.' });
      setDirty(false);
      // The server tidies the account list as it stores it - a folder for each new account, blanks
      // and duplicates dropped - so what is on screen is re-read rather than assumed.
      const stored = await fetch('/api/config').then(r => (r.ok ? r.json() : null)).catch(() => null);
      if (stored?.claudeAccounts)
        setCfg(prev => (prev ? { ...prev, claudeAccounts: stored.claudeAccounts } : prev));
      // The usage token is the one setting whose value can be wrong in a way the server can check,
      // and the only feedback otherwise is a header chip that goes on saying nothing. So try it.
      if (cfg.claudeOauthToken) await checkToken();
    } catch (e) {
      setMsg({ type: 'error', text: e instanceof Error ? e.message : 'Save failed' });
    } finally { setSaving(false); }
  };

  if (!cfg) return <div className="text-gray-500 text-sm">Loading global settings…</div>;

  // The catalogue is served with the config. Falling back to an empty list rather than a hardcoded
  // one is deliberate: a stale bundle inventing a provider the server does not know about would
  // offer a setting that silently does nothing.
  const providers = cfg.agentProviders ?? [];
  const provider = providers.find(p => p.key === cfg.agentProvider);
  // Same reasoning for the notification list: what is on offer is whatever the server can actually
  // send, so a stale bundle cannot show a tickbox for a notification nothing posts.
  const events = cfg.notificationEventCatalog ?? [];
  const ticked = cfg.notificationEvents ?? [];
  const channels = cfg.notificationChannels ?? [];
  const channel = channels.find(c => c.key === cfg.notificationChannel);
  // Whether the chosen channel has what it needs to send anything at all.
  const channelReady = cfg.notificationChannel === 'ntfy' ? !!cfg.ntfyTopic : !!cfg.slackWebhookUrl;
  // And again for the phases: the pipeline decides which exist, not this bundle.
  const phases = cfg.workerPhases ?? [];
  const phaseModels = cfg.phaseModels ?? {};
  // Setting a phase back to "" removes it rather than storing a blank. The server would drop it
  // either way, but leaving it in would make the panel show a saved override that is about to
  // vanish on the next load.
  const setPhaseModel = (key: string, model: string) => {
    const next = { ...phaseModels };
    if (model) next[key] = model; else delete next[key];
    update({ phaseModels: next });
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div className="flex-1 min-w-0">
          <SectionRule title="Global" colour="var(--w-gate)" />
          <p className="text-xs text-gray-500">One set for the whole system, shared across every project.</p>
        </div>
        <SaveBar msg={msg} saving={saving} disabled={!dirty || hasErrors} onSave={save} label="Save global" />
      </div>

      <FieldGroup title="Dashboard">
        <Field
          label="Dashboard URL"
          description="URL the heartbeat scripts post events to and reach the ticketing MCP server."
          value={cfg.dashboardUrl}
          onChange={v => update({ dashboardUrl: v })}
          placeholder="http://localhost:5338"
          error={errors.dashboardUrl}
        />
      </FieldGroup>

      <FieldGroup title="Appearance">
        <ThemeField pref={pref} />
        <CheckField
          label="Animated 3D background"
          description={
            'The parallax nebula behind the app. Turn it off on a machine without the graphics ' +
            'headroom for it and the background becomes flat black — nothing is animated, and no ' +
            'canvas is drawn at all. Remembered on this machine only, and applies immediately.'
          }
          checked={sky}
          onChange={v => { setSky(v); setSkyEnabled(v); }}
        />
      </FieldGroup>

      <FieldGroup title="Notifications">
        <SelectField
          label="Send via"
          description={channel?.description}
          value={cfg.notificationChannel}
          onChange={v => update({ notificationChannel: v })}
          options={channels.map(c => ({ value: c.key, label: c.label }))}
        />
        {/* Each channel's own fields. The other channel's values are kept, not cleared, so
            switching back does not mean pasting a webhook in again. */}
        <div key={cfg.notificationChannel} className="orbit-swap space-y-4">
          {cfg.notificationChannel === 'ntfy' ? (
            <>
              <div className="grid grid-cols-2 gap-4">
                <Field
                  label="Topic"
                  description="Subscribe to the same topic in the ntfy app to receive these."
                  value={cfg.ntfyTopic}
                  onChange={v => update({ ntfyTopic: v.trim() })}
                  placeholder="whetstone-3kq9x"
                  error={errors.ntfyTopic}
                />
                <Field
                  label="Server"
                  description="Blank for the public server, ntfy.sh."
                  value={cfg.ntfyServerUrl}
                  onChange={v => update({ ntfyServerUrl: v.trim() })}
                  placeholder="https://ntfy.sh"
                  error={errors.ntfyServerUrl}
                />
              </div>
              <Field
                label="Access Token"
                description="Only for a topic that is protected on its server. Blank sends without one."
                value={cfg.ntfyToken}
                onChange={v => update({ ntfyToken: v.trim() })}
                placeholder="tk_…"
                isSecret
              />
            </>
          ) : (
            <Field
              label="Webhook URL"
              description="Slack incoming webhook for heartbeat notifications."
              value={cfg.slackWebhookUrl}
              onChange={v => update({ slackWebhookUrl: v })}
              isSecret
              error={errors.slackWebhookUrl}
            />
          )}
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={testNotification}
            disabled={!channelReady || notifyTest?.state === 'checking'}
            title={channelReady ? 'Sends using the values above, saved or not.' : undefined}
            className="orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800 disabled:opacity-50"
          >
            {notifyTest?.state === 'checking' ? 'Sending…' : 'Send test'}
          </button>
          {notifyTest && notifyTest.state !== 'checking' && (
            <span className={`text-xs ${notifyTest.state === 'ok' ? 'text-emerald-400' : 'text-red-400'}`}>
              {notifyTest.text}
            </span>
          )}
        </div>
        <div>
          <div className="text-sm text-gray-300">Notify me about</div>
          <p className="text-xs text-gray-500 mt-1 mb-3">
            {channelReady
              ? 'Only ticked events are sent. The heartbeat re-reads these settings each time it sends, so a change here takes effect without restarting the fleet.'
              : cfg.notificationChannel === 'ntfy'
                ? 'Nothing is sent until a topic is set above.'
                : 'Nothing is sent until a webhook URL is set above.'}
          </p>
          <div className="space-y-3">
            {events.map(ev => (
              <CheckField
                key={ev.key}
                label={ev.label}
                description={ev.description}
                checked={ticked.includes(ev.key)}
                onChange={on => update({
                  notificationEvents: on ? [...ticked, ev.key] : ticked.filter(k => k !== ev.key),
                })}
              />
            ))}
            {events.length === 0 && (
              <p className="text-xs text-gray-500">This build of the server offers no notifications.</p>
            )}
          </div>
        </div>
      </FieldGroup>

      <FieldGroup title="Heartbeat Timing">
        <div className="grid grid-cols-3 gap-4">
          <Field label="Base Interval (min)" description="Minutes between cycles" type="number"
            value={cfg.baseHeartbeatMinutes} onChange={v => update({ baseHeartbeatMinutes: Number(v) || 0 })} error={errors.baseHeartbeatMinutes} />
          <Field label="Idle Multiplier" description="Backoff when no work found" type="number"
            value={cfg.heartbeatIdleMultiplier} onChange={v => update({ heartbeatIdleMultiplier: Number(v) || 0 })} error={errors.heartbeatIdleMultiplier} />
          <Field label="Max Interval (min)" description="Cap between cycles" type="number"
            value={cfg.heartbeatMaxMinutes} onChange={v => update({ heartbeatMaxMinutes: Number(v) || 0 })} error={errors.heartbeatMaxMinutes} />
        </div>
      </FieldGroup>

      <FieldGroup title="Heartbeat Off-Hours">
        <p className="text-xs text-gray-500 -mt-2 mb-3">Heartbeat pauses during off-hours only once the max interval is reached.</p>
        <div className="grid grid-cols-2 gap-4">
          <Field label="Off-Hours Start" description="When to stop (24h)" value={cfg.offHoursStart}
            onChange={v => update({ offHoursStart: v })} placeholder="17:00" error={errors.offHoursStart} />
          <Field label="Off-Hours End" description="When to resume (24h)" value={cfg.offHoursEnd}
            onChange={v => update({ offHoursEnd: v })} placeholder="06:30" error={errors.offHoursEnd} />
        </div>
      </FieldGroup>

      <FieldGroup title="Agents">
        <p className="text-xs text-gray-500 -mt-2 mb-3">
          How many tickets the heartbeat works at once. This is a ceiling, not a target: it fills a slot
          only when there is a workable ticket for it, so a quiet board still runs one agent. Takes effect
          the next time the heartbeat is started.
        </p>
        <SelectField
          label="Concurrent Agents"
          description="Every agent is a full Claude session drawing on the same rate-limit window and the same machine."
          value={String(cfg.heartbeatWorkers)}
          onChange={v => update({ heartbeatWorkers: Number(v) || 1 })}
          options={Array.from({ length: cfg.maxHeartbeatWorkers || 3 }, (_, i) => ({
            value: String(i + 1),
            label: i === 0 ? '1 (one at a time)' : `${i + 1} agents`,
          }))}
        />
      </FieldGroup>

      <FieldGroup title="Heartbeat Reliability">
        <p className="text-xs text-gray-500 -mt-2 mb-3">Guards that keep one bad ticket from wedging or draining the heartbeat.</p>
        <div className="grid grid-cols-2 gap-4">
          <Field label="Max Attempts / Ticket" description="Block a ticket after this many no-progress resumes" type="number"
            value={cfg.heartbeatMaxAttempts} onChange={v => update({ heartbeatMaxAttempts: Number(v) || 0 })} error={errors.heartbeatMaxAttempts} />
          <Field label="Worker Timeout (min)" description="Kill a worker that has run this long, busy or not. Per run, not per ticket - a resumed ticket gets the full budget again." type="number"
            value={cfg.heartbeatWorkerTimeoutMinutes} onChange={v => update({ heartbeatWorkerTimeoutMinutes: Number(v) || 0 })} error={errors.heartbeatWorkerTimeoutMinutes} />
          <Field label="Stall Limit (min)" description="Kill a worker whose agent has said nothing for this long - usually one command that never returned. 0 turns it off." type="number"
            value={cfg.heartbeatWorkerStallMinutes} onChange={v => update({ heartbeatWorkerStallMinutes: Number(v) || 0 })} error={errors.heartbeatWorkerStallMinutes} />
        </div>
      </FieldGroup>

      <FieldGroup title="Agent Provider">
        <p className="text-xs text-gray-500 -mt-2 mb-3">
          Which agent CLI runs the work, and the models it runs. Applies on the next cycle. The CLI has to be
          installed and signed in{provider ? <> — this one is <code>{provider.executable}</code></> : null}.
        </p>
        <SelectField
          label="Provider"
          description="Every phase, the Classroom analysis and the improvement applier all run through this CLI."
          value={cfg.agentProvider}
          onChange={v => {
            // The models go with the provider. Sent in the same update so the panel never shows a
            // model the newly-selected CLI would reject — the server drops a mismatched one anyway,
            // but then the field would read as blank until the next load.
            const next = providers.find(p => p.key === v);
            update({
              agentProvider: v,
              workerModel: next?.defaultWorkerModel ?? '',
              reflectionModel: next?.defaultReflectionModel ?? '',
              // The per-phase overrides go with them. Every one of them names a model of the
              // outgoing provider, so the server would drop the lot on save regardless; clearing
              // here means the panel says so now rather than after the next load.
              phaseModels: {},
              // Same for the Classroom override, and back to blank rather than the new provider's
              // default: blank means "follow the worker model", which is where a fresh provider
              // should start.
              analysisModel: '',
            });
          }}
          options={providers.map(p => ({ value: p.key, label: p.label }))}
        />
        <div className="grid grid-cols-2 gap-4">
          <SelectField
            label="Worker Model"
            description="Runs the per-ticket work. Every phase, unless it is given its own below."
            value={cfg.workerModel}
            onChange={v => update({ workerModel: v })}
            options={provider?.workerModels ?? []}
          />
          <SelectField
            label="Reflection Model"
            description="Runs the post-cycle lessons-learned summary."
            value={cfg.reflectionModel}
            onChange={v => update({ reflectionModel: v })}
            options={provider?.reflectionModels ?? []}
          />
          {/* Its own field rather than a per-phase row: the analysis is not a pipeline phase, and
              it is the run where a different model is most defensible — a worker's output is
              checked by QA and by a human at review, while an analysis is read once and believed. */}
          <SelectField
            label="Classroom Analysis Model"
            description="Runs the on-demand teaching analysis. Left on Default it follows the Worker Model."
            value={cfg.analysisModel ?? ''}
            onChange={v => update({ analysisModel: v })}
            options={[
              { value: '', label: `Default (${modelLabel(provider, cfg.workerModel)})` },
              ...(provider?.workerModels ?? []),
            ]}
          />
          {/* One setting for every run rather than one per model: the runs above differ in which
              model they take, not in how carefully they should think. */}
          {provider?.supportsReasoningEffort && (
            <SelectField
              label="Effort"
              description="How hard every run thinks: each phase, reflection, the Classroom analysis and walkthrough, and the improvement applier. Higher is slower and spends more tokens."
              value={cfg.workerReasoningEffort || 'high'}
              onChange={v => update({ workerReasoningEffort: v })}
              options={[
                { value: 'low', label: 'Low' },
                { value: 'medium', label: 'Medium' },
                { value: 'high', label: 'High (default)' },
                { value: 'xhigh', label: 'Extra high' },
              ]}
            />
          )}
        </div>
        {phases.length > 0 && (
          <div className="border-t border-hair pt-4 space-y-3">
            <div>
              <p className="text-xs font-medium text-gray-400">Per-phase worker models</p>
              <p className="text-[11px] text-gray-600 mt-1">
                Run one phase on a different model without moving the others. Anything left on{' '}
                <span className="text-gray-500">Default</span> uses the Worker Model above, so it
                changes when that does.
              </p>
            </div>
            <div className="grid grid-cols-2 gap-4">
              {phases.map(ph => (
                <SelectField
                  key={ph.key}
                  label={ph.label}
                  description={ph.description}
                  value={phaseModels[ph.key] ?? ''}
                  onChange={v => setPhaseModel(ph.key, v)}
                  options={[
                    { value: '', label: `Default (${modelLabel(provider, cfg.workerModel)})` },
                    ...(provider?.workerModels ?? []),
                  ]}
                />
              ))}
            </div>
          </div>
        )}
        {provider?.note && (
          <p className="text-xs text-amber-500/80">{provider.note}</p>
        )}
        {provider && !provider.reportsUsage && (
          <p className="text-xs text-amber-500/80">
            The rate-limit meter in the header only reads Claude Code's subscription, so it stays hidden while
            this provider is selected.
          </p>
        )}
      </FieldGroup>

      <FieldGroup title="Claude Accounts">
        <p className="text-xs text-gray-500 -mt-2 mb-3">
          Other Claude logins the fleet can run on, besides the one Claude Code is already signed in to. Each
          gets a folder of its own to keep its login in. Add one, save, sign in to it once, and the usage chip
          in the header gains a picker for switching between them. A switch applies to the next run to start.
        </p>
        {(cfg.claudeAccounts ?? []).map((a, i) => (
          <div key={i} className="flex items-center gap-2">
            <input
              aria-label="Account name"
              value={a.name}
              placeholder="Name, e.g. Second"
              onChange={e => update({
                claudeAccounts: cfg.claudeAccounts.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)),
              })}
              className="w-40 bg-gray-900 border border-hair rounded px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-gray-500"
            />
            <span className="flex-1 min-w-0 truncate text-[11px] text-gray-600" title={a.configDir}>
              {a.configDir
                ? `${a.configDir} — ${a.signedIn ? 'signed in' : 'not signed in yet'}`
                : 'Save to create its folder'}
            </span>
            <button
              type="button"
              onClick={() => signInAccount(a.name)}
              disabled={!a.configDir || dirty}
              title={!a.configDir || dirty ? 'Save first' : 'Opens a terminal on this machine to sign this account in'}
              className="orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800 disabled:opacity-50"
            >
              Sign in
            </button>
            <button
              type="button"
              onClick={() => update({ claudeAccounts: cfg.claudeAccounts.filter((_, j) => j !== i) })}
              title="Forget this account. Its folder and login are left on disk."
              className="orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-400 hover:bg-gray-800"
            >
              Remove
            </button>
          </div>
        ))}
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => update({ claudeAccounts: [...(cfg.claudeAccounts ?? []), { name: '', configDir: '' }] })}
            className="orbit-tap text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800"
          >
            Add account
          </button>
          {accountMsg && accountMsg.state !== 'checking' && (
            <span className={`text-xs ${accountMsg.state === 'ok' ? 'text-emerald-400' : 'text-red-400'}`}>
              {accountMsg.text}
            </span>
          )}
        </div>
      </FieldGroup>

      <FieldGroup title="Rate-Limit Meter">
        <p className="text-xs text-gray-500 -mt-2 mb-3">
          The headroom chip reads Claude Code's own login. A token from{' '}
          <code className="text-gray-400">claude setup-token</code> does <strong>not</strong> work here — it
          authenticates and runs inference, but the usage endpoint refuses it (403). This field is an override
          for a token that can read usage; leave it empty unless you have one.
        </p>
        <Field
          label="Claude OAuth Token (override)"
          description={
            cfg.claudeOauthTokenSet
              ? 'A token is saved. Clear it to fall back to Claude Code\'s own login.'
              : 'Optional. Leave blank to use Claude Code\'s login.'
          }
          value={cfg.claudeOauthToken ?? ''}
          onChange={v => { update({ claudeOauthToken: v }); setTokenCheck(null); }}
          isSecret
          placeholder={cfg.claudeOauthTokenSet ? '••••••••  (saved)' : ''}
        />
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={checkToken}
            disabled={tokenCheck?.state === 'checking'}
            className="text-xs px-2 py-1 rounded border border-hair text-gray-300 hover:bg-gray-800 disabled:opacity-50"
          >
            {tokenCheck?.state === 'checking' ? 'Checking…' : 'Test meter'}
          </button>
          {tokenCheck && tokenCheck.state !== 'checking' && (
            <span className={`text-xs ${tokenCheck.state === 'ok' ? 'text-emerald-400' : 'text-red-400'}`}>
              {tokenCheck.state === 'ok' ? `Working — ${tokenCheck.text}` : tokenCheck.text}
            </span>
          )}
        </div>
      </FieldGroup>
    </div>
  );
}

/**
 * What to call a worker model on screen. Falls back to the raw slug rather than to a blank or to
 * "unknown": a model saved by hand, or one added to the config before this build knew about it, is
 * still the honest answer to "what does Default mean here".
 */
function modelLabel(provider: AgentProviderOption | undefined, value: string): string {
  return provider?.workerModels.find(m => m.value === value)?.label ?? value;
}

function SelectField({ label, description, value, onChange, options }: {
  label: string; description?: string; value: string;
  onChange: (v: string) => void; options: { value: string; label: string }[];
}) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-400 mb-1">{label}</label>
      {description && <p className="text-[11px] text-gray-600 mb-1.5">{description}</p>}
      <select
        value={value}
        onChange={e => onChange(e.target.value)}
        className="w-full bg-gray-900 border border-hair rounded px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-gray-500"
      >
        {/* Keep an unknown saved value visible instead of silently blank. */}
        {!options.some(o => o.value === value) && value && <option value={value}>{value}</option>}
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  );
}

// ---------- Theme ------------------------------------------------------------

/** A line of explanation under a control, in the same voice as a field's description. */
function Hint({ children }: { children: React.ReactNode }) {
  return <p className="text-[11px] text-gray-600">{children}</p>;
}

/**
 * The rota: one theme per day of the week.
 *
 * Laid out as a two-column grid rather than seven stacked fields because the thing you actually do
 * here is read down the list and spot the day you want to change — which needs the selects to line
 * up. Today's row wears the accent, so "what am I looking at and why" is answered without counting.
 */
function WeekField({ week, onChange }: {
  week: Record<Weekday, string>;
  onChange: (week: Record<Weekday, string>) => void;
}) {
  const today = WEEKDAYS[new Date().getDay()];
  return (
    <div>
      <label className="block text-xs font-medium text-gray-400 mb-1">Rota</label>
      <Hint>Each day gets a theme. It changes at midnight, with the app open or not.</Hint>
      <div className="grid items-center gap-x-3 gap-y-1.5 mt-1.5" style={{ gridTemplateColumns: 'auto 1fr' }}>
        {WEEKDAYS.map(d => (
          <Fragment key={d}>
            <label
              htmlFor={`rota-${d}`}
              className="text-xs whitespace-nowrap"
              style={{ color: d === today ? 'var(--w-accent-text)' : 'var(--w-t500)' }}
            >
              {WEEKDAY_LABEL[d]}
            </label>
            <select
              id={`rota-${d}`}
              value={week[d]}
              onChange={e => onChange({ ...week, [d]: e.target.value })}
              className="w-full bg-gray-900 border border-hair rounded px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-gray-500"
            >
              {/* Keep an unknown saved value visible instead of silently blank. */}
              {!THEMES.some(t => t.key === week[d]) && week[d] && <option value={week[d]}>{week[d]}</option>}
              {THEMES.map(t => <option key={t.key} value={t.key}>{t.name}</option>)}
            </select>
          </Fragment>
        ))}
      </div>
    </div>
  );
}

/**
 * How the theme gets chosen, and — depending on the answer — which one.
 *
 * Four modes, differing only in *when* the question is asked: manual asks once, random and cycle
 * ask every morning, the rota asks the calendar. See `theme/preference.ts` for what each one means;
 * this is only the surface for editing it.
 *
 * Nothing here has a Save button. The whole point of the setting is to change what you are looking
 * at, so it is written and applied on the same click — which is also why the panel this sits in
 * repaints under your cursor as you use it.
 */
function ThemeField({ pref }: { pref: ThemePref }) {
  const live = currentTheme();
  const options = THEMES.map(t => ({ value: t.key, label: t.name }));

  const setMode = (mode: string) => {
    if (mode === 'random') {
      // Clearing the day it last drew for is what makes entering the mode draw immediately, rather
      // than sitting on the theme you were already wearing until tomorrow.
      setThemePref({ ...pref, mode: 'random', drawnOn: undefined });
    } else if (mode === 'cycle') {
      // Same field cleared for the opposite reason: with no day recorded, cycle takes today as its
      // starting point and stays on what is showing until tomorrow. Carrying random's `drawnOn`
      // over would have it either sit for a day it never chose, or step the instant you switch.
      setThemePref({ ...pref, mode: 'cycle', theme: live.key, drawnOn: undefined });
    } else if (mode === 'schedule') {
      // A fresh rota starts as every day on the theme showing now: a rota you have not filled in
      // yet should change nothing, not drop you somewhere you did not choose.
      setThemePref({ ...pref, mode: 'schedule', week: pref.week ?? weekOf(live.key) });
    } else {
      // Back to manual on whatever is showing, which for random is the theme it last drew — leaving
      // the mode should keep the look, not undo it.
      setThemePref({ ...pref, mode: 'manual', theme: live.key });
    }
  };

  return (
    <div className="space-y-4">
      <SelectField
        label="Theme"
        description={
          'Changes the colours, the faces and the shapes only — nothing moves, and no page gains ' +
          'or loses a control. Remembered on this machine only, and applies immediately.'
        }
        value={pref.mode}
        onChange={setMode}
        options={[
          { value: 'manual', label: 'Manual — one theme until you change it' },
          { value: 'random', label: 'Random — a different one each day' },
          { value: 'cycle', label: 'Cycle — the next one each day, in order' },
          { value: 'schedule', label: 'By day of week — a fixed rota' },
        ]}
      />

      {pref.mode === 'manual' && (
        <SelectField
          label="Which"
          description={live.blurb}
          value={pref.theme}
          onChange={setTheme}
          options={options}
        />
      )}

      {pref.mode === 'random' && (
        <div>
          <div className="text-xs text-gray-400 mb-1">
            Today: <span style={{ color: 'var(--w-accent-text)' }}>{live.name}</span>
          </div>
          <Hint>
            Redrawn at midnight, with the app open or not — and never the same theme two days
            running, so the mode always has something to show for itself.
          </Hint>
        </div>
      )}

      {pref.mode === 'cycle' && (
        <div>
          <div className="text-xs text-gray-400 mb-1">
            Today: <span style={{ color: 'var(--w-accent-text)' }}>{live.name}</span>
            {THEMES.length > 1 && <> · tomorrow: {resolveTheme(nextTheme(live.key)).name}</>}
          </div>
          {/* The order is the registry's, which is also the order of the Manual dropdown — so the
              list here is not new information to learn, it is the one already on screen. */}
          <Hint>
            Steps one along at midnight, with the app open or not: {THEMES.map(t => t.name).join(' → ')}
            {THEMES.length > 1 && <> → {THEMES[0].name}</>}. Miss a few days and it still moves by one,
            so coming back always looks different.
          </Hint>
        </div>
      )}

      {pref.mode === 'schedule' && (
        <WeekField
          week={pref.week ?? weekOf(live.key)}
          onChange={week => setThemePref({ ...pref, mode: 'schedule', week })}
        />
      )}

      {THEMES.length < 2 && pref.mode !== 'manual' && (
        <Hint>
          Only one theme is installed, so this has nothing to choose between yet. It will start
          working the moment a second one is added.
        </Hint>
      )}
    </div>
  );
}

// ---------- One project card ------------------------------------------------

function ProjectCard({ project, onChanged }: { project: ProjectDetail; onChanged: () => void }) {
  const [p, setP] = useState<ProjectDetail>(project);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<Msg>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => { setP(project); setDirty(false); }, [project]);

  const update = (patch: Partial<ProjectDetail>) => { setP(prev => ({ ...prev, ...patch })); setDirty(true); setMsg(null); };

  const tracker = p.tracker;
  const spec = TRACKERS[tracker.kind] ?? TRACKERS.none;

  /**
   * Switching provider resets the two vocabulary settings to the new one's defaults.
   *
   * Carrying them over is not a cosmetic slip: Jira's `["Blocks"]` on an Azure Boards project
   * matches no relation that exists there, so nothing would ever block — silently, and looking in
   * the config file exactly like a decision someone made on purpose.
   */
  const setTrackerKind = (kind: TrackerKindName) => {
    const defaults = p.trackerDefaults?.[kind];
    update({
      tracker: {
        ...tracker,
        kind,
        blockingLinkTypes: defaults?.blockingLinkTypes ?? [],
        doneStates: defaults?.doneStates ?? [],
      },
    });
  };

  const errors: Record<string, string> = {};
  if (tracker.baseUrl && !URL_RE.test(tracker.baseUrl)) errors.baseUrl = 'Must be a valid URL';
  if (tracker.kind === 'jira' && tracker.email && !EMAIL_RE.test(tracker.email)) errors.email = 'Must be a valid email';
  // Named rather than left to fail at the first request: an Azure DevOps organisation URL with no
  // team project produces a 404 on every read, and nothing in the board says why.
  if (tracker.kind === 'azureDevOps' && tracker.baseUrl && !tracker.project.trim())
    errors.trackerProject = 'Required for Azure Boards';
  if (p.qaApiPort < 1 || p.qaApiPort > 65535) errors.qaApiPort = 'Must be 1-65535';
  if (p.qaWebPort < 1 || p.qaWebPort > 65535) errors.qaWebPort = 'Must be 1-65535';
  if (p.qaApiPort && p.qaWebPort && p.qaApiPort === p.qaWebPort) errors.qaWebPort = 'Must differ from API port';
  const hasErrors = Object.keys(errors).length > 0;

  const save = async () => {
    if (hasErrors) return;
    setSaving(true); setMsg(null);
    try {
      const res = await fetch(`/api/projects/${p.key}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          displayName: p.displayName, projectPath: p.projectPath, tracks: p.tracks,
          skipClassroomAnalysisByDefault: p.skipClassroomAnalysisByDefault ?? false,
          mergeInsteadOfPr: p.mergeInsteadOfPr ?? false,
          baseBranch: p.baseBranch, branchPrefix: p.branchPrefix,
          worktreeRoot: p.worktreeRoot ?? '', worktreeSetup: p.worktreeSetup ?? [], worktreeCopyFiles: p.worktreeCopyFiles ?? [],
          tracker: p.tracker, qaApiPort: p.qaApiPort, qaWebPort: p.qaWebPort, qaLogin: p.qaLogin,
        }),
      });
      if (!res.ok) throw new Error((await res.json()).error ?? 'Save failed');
      setMsg({ type: 'success', text: 'Saved. Restart to apply.' });
      setDirty(false);
      onChanged();
    } catch (e) {
      setMsg({ type: 'error', text: e instanceof Error ? e.message : 'Save failed' });
    } finally { setSaving(false); }
  };

  const action = async (path: string) => {
    setBusy(true); setMsg(null);
    try {
      const res = await fetch(path, { method: 'POST' });
      if (!res.ok) throw new Error((await res.json()).error ?? 'Action failed');
      onChanged();
    } catch (e) {
      setMsg({ type: 'error', text: e instanceof Error ? e.message : 'Action failed' });
    } finally { setBusy(false); }
  };

  const scaffoldSkills = async () => {
    setBusy(true); setMsg(null);
    try {
      const res = await fetch(`/api/projects/${p.key}/scaffold-skills`, { method: 'POST' });
      if (!res.ok) throw new Error((await res.json()).error ?? 'Setup failed');
      const data = await res.json();
      const n = Array.isArray(data.created) ? data.created.length : 0;
      setMsg({ type: 'success', text: n ? `Added ${n} file${n === 1 ? '' : 's'} from templates` : 'Already set up' });
      onChanged();
    } catch (e) {
      setMsg({ type: 'error', text: e instanceof Error ? e.message : 'Setup failed' });
    } finally { setBusy(false); }
  };

  const skillsPresent = p.skills.filter(s => s.present).length;

  return (
    <div className={`border rounded-lg ${p.disabled ? 'border-hair bg-gray-900/40' : 'border-hair bg-gray-800'}`}>
      {/* Card header */}
      <div className="flex items-center justify-between p-4">
        <button onClick={() => setOpen(o => !o)} className="flex items-center gap-3 text-left min-w-0">
          <span className={`text-gray-500 transition-transform ${open ? 'rotate-90' : ''}`}>▶</span>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className={`text-sm font-semibold ${p.disabled ? 'text-gray-500' : 'text-gray-100'}`}>{p.displayName || p.key}</span>
              <span className="text-[11px] font-mono text-gray-500">{p.key}</span>
              {p.disabled && <span className="px-1.5 py-0.5 rounded bg-gray-700 text-gray-400 text-[10px] font-semibold">UNREGISTERED</span>}
              {!p.disabled && (
                <span className={`px-1.5 py-0.5 rounded text-[10px] font-semibold ${p.skillsReady ? 'bg-emerald-500/10 text-emerald-300' : 'bg-amber-500/10 text-amber-300'}`}
                  title="Required heartbeat skills for this project">
                  {p.skillsReady ? 'Skills ready' : `Skills ${skillsPresent}/${p.skills.length}`}
                </span>
              )}
            </div>
            <div className="text-[11px] text-gray-600 font-mono truncate max-w-md">{p.projectPath || 'no path set'}</div>
          </div>
        </button>
        <div className="flex items-center gap-2 flex-shrink-0">
          {msg && <span className={`text-xs ${msg.type === 'success' ? 'text-emerald-400' : 'text-red-400'}`}>{msg.text}</span>}
          {p.disabled ? (
            <button onClick={() => action(`/api/projects/${p.key}/enable`)} disabled={busy}
              className="btn-act px-3 py-1.5 text-emerald-300 bg-emerald-500/10 disabled:opacity-50">
              Re-register
            </button>
          ) : (
            <button onClick={() => action(`/api/projects/${p.key}/disable`)} disabled={busy}
              className="btn-act px-3 py-1.5 text-red-300 bg-red-500/10 disabled:opacity-50">
              Unregister
            </button>
          )}
        </div>
      </div>

      {/* Card body */}
      {open && (
        <div className="border-t border-hair p-4 space-y-5">
          <div className="grid grid-cols-2 gap-4">
            <Field label="Display Name" value={p.displayName} onChange={v => update({ displayName: v })} placeholder="My Project" />
          </div>

          {/* Which pipelines this project runs. High up because it decides what the board draws for
              this project and what the New Ticket form offers — a bigger question than a port. */}
          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
              Enabled Tracks <span className="text-gray-600 normal-case font-normal">(which lanes the board draws for it)</span>
            </h4>
            <div className="space-y-2.5">
              {(p.allTracks ?? ['Delivery']).map(k => {
                const on = (p.tracks ?? []).includes(k);
                const open = p.openTickets?.[k] ?? 0;
                // Two reasons a ticked box will not untick, and both are about not stranding work.
                // Open tickets would keep being claimed and walked forward with no lane on the board
                // to show where they are; the last track would leave a project that can hold no
                // ticket at all. Neither is a rule the form invents - the server refuses the same
                // save - but finding out by having a save fail is a worse way to learn it.
                const locked = on && (open > 0 || (p.tracks ?? []).length === 1);
                const why = open > 0
                  ? `${open} unfinished ${TRACK_COPY[k].noun} ticket${open === 1 ? '' : 's'} — finish or delete them first`
                  : 'A project needs at least one track enabled';
                return (
                  <CheckField
                    key={k}
                    label={TRACK_COPY[k].label}
                    description={locked ? `${TRACK_COPY[k].hint} · ${why}.` : TRACK_COPY[k].hint}
                    checked={on}
                    disabled={locked}
                    title={locked ? why : undefined}
                    onChange={v => update({
                      tracks: (p.allTracks ?? []).filter(t => (t === k ? v : (p.tracks ?? []).includes(t))),
                    })}
                  />
                );
              })}
            </div>
          </div>
          {/* A default for the per-ticket box, not a switch for the project: the pipeline only ever
              reads the ticket's own setting, so changing this never touches a ticket that exists. */}
          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
              New Ticket Defaults <span className="text-gray-600 normal-case font-normal">(each ticket can still change its own)</span>
            </h4>
            <CheckField
              label="Skip classroom analysis by default"
              description="New tickets start with refinement not waiting for an independent Classroom analysis. Turn on for a project whose work is mostly small; leave off to have every ticket's findings ruled on before review."
              checked={p.skipClassroomAnalysisByDefault ?? false}
              onChange={v => update({ skipClassroomAnalysisByDefault: v })}
            />
          </div>

          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
              Delivery <span className="text-gray-600 normal-case font-normal">(what finishing a ticket does)</span>
            </h4>
            <CheckField
              label="Merge instead of pull request"
              description="For a team that merges to the base branch by hand rather than raising PRs. The PR dialog keeps its checks and diff, and its button merges the ticket's branch into the base in your repo with a merge commit. Nothing is pushed."
              checked={p.mergeInsteadOfPr ?? false}
              onChange={v => update({ mergeInsteadOfPr: v })}
            />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <Field label="Base Branch" description="Branch new feature branches fork from. Blank = main." value={p.baseBranch} onChange={v => update({ baseBranch: v })} placeholder="main" />
            <Field label="Branch Prefix" description="Prefix for auto-created branches, e.g. jb/ → jb/PROJ-123." value={p.branchPrefix} onChange={v => update({ branchPrefix: v })} placeholder="jb/" />
          </div>
          <FolderField label="Project Path" description="Absolute path to the repo this project works on. Yours to work in — the heartbeat never runs here." value={p.projectPath}
            onPick={path => update({ projectPath: path })} />

          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Agent Worktrees <span className="text-gray-600 normal-case font-normal">(one checkout per ticket)</span></h4>
            <div className="space-y-4">
              <Field label="Worktree Root" description="Where per-ticket checkouts are created. Keep it outside the repo. Blank = the default below."
                value={p.worktreeRoot} onChange={v => update({ worktreeRoot: v })} placeholder={p.resolvedWorktreeRoot || 'C:\\Projects\\.worktrees\\key'} />
              <Field label="Copy Into New Worktrees"
                description="Comma-separated repo-relative gitignored files (.env, appsettings.Development.json). Git cannot supply these."
                value={(p.worktreeCopyFiles ?? []).join(', ')}
                onChange={v => update({ worktreeCopyFiles: v.split(',').map(s => s.trim()).filter(Boolean) })}
                placeholder="docker/.env, src/appsettings.Development.json" />
              <Field label="Setup Commands"
                description="Comma-separated, run once in a new worktree before any work. A fresh worktree has no node_modules or restored packages."
                value={(p.worktreeSetup ?? []).join(', ')}
                onChange={v => update({ worktreeSetup: v.split(',').map(s => s.trim()).filter(Boolean) })}
                placeholder="npm ci, dotnet restore" />
            </div>
          </div>

          {/* One tracker per project, chosen here. The fields below are the chosen provider's own,
              because half of them mean nothing to the other one — and a form asking an Azure Boards
              user for an email address is a form telling them they have configured it wrong. */}
          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
              Issue Tracker <span className="text-gray-600 normal-case font-normal">(read-only context)</span>
            </h4>
            <div className="space-y-4">
              <div>
                <label className="block text-xs text-gray-400 mb-1.5">Tracker</label>
                <div className="flex gap-1.5">
                  {(Object.keys(TRACKERS) as TrackerKindName[]).map(kind => (
                    <button
                      key={kind}
                      type="button"
                      onClick={() => setTrackerKind(kind)}
                      className={`px-2.5 py-1 rounded text-xs border transition-colors ${
                        tracker.kind === kind
                          ? 'border-blue-500/60 bg-blue-500/10 text-blue-200'
                          : 'border-hair text-gray-400 hover:text-gray-200'}`}
                    >
                      {TRACKERS[kind].label}
                    </button>
                  ))}
                </div>
                <p className="mt-1.5 text-[11px] text-gray-500">{spec.note}</p>
              </div>

              {tracker.kind !== 'none' && (
                <>
                  <Field
                    label={spec.baseUrlLabel} value={tracker.baseUrl}
                    onChange={v => update({ tracker: { ...tracker, baseUrl: v } })}
                    placeholder={spec.baseUrlPlaceholder} error={errors.baseUrl} />
                  <div className="grid grid-cols-2 gap-4">
                    {tracker.kind === 'jira' ? (
                      <Field label="Email" value={tracker.email}
                        onChange={v => update({ tracker: { ...tracker, email: v } })}
                        placeholder="you@company.com" error={errors.email} />
                    ) : (
                      <Field label="Team Project" value={tracker.project}
                        onChange={v => update({ tracker: { ...tracker, project: v } })}
                        placeholder="YourTeamProject" error={errors.trackerProject} />
                    )}
                    <Field label={spec.tokenLabel} value={tracker.token ?? ''}
                      onChange={v => update({ tracker: { ...tracker, token: v } })} isSecret
                      placeholder={tracker.tokenSet ? '••••••••  (saved)' : ''} />
                  </div>
                </>
              )}

              {/* Azure DevOps only, because Jira answers both of these itself: every Jira status
                  carries a done/not-done category, and Jira has a "Blocks" link type that means
                  impossibility. Azure DevOps has neither — System.State is per-process free text,
                  and its nearest relation (Predecessor) is a scheduling hint many teams set for
                  ordering they never meant to gate anything on. So the team says what it meant. */}
              {tracker.kind === 'azureDevOps' && (
                <div className="grid grid-cols-2 gap-4">
                  <Field
                    label="Done states"
                    description="States that mean finished"
                    value={tracker.doneStates.join(', ')}
                    onChange={v => update({ tracker: { ...tracker, doneStates: splitList(v) } })}
                    placeholder="Closed, Done, Removed" />
                  <Field
                    label="Blocking link types"
                    description="Relations that gate a ticket; clear it so nothing does"
                    value={tracker.blockingLinkTypes.join(', ')}
                    onChange={v => update({ tracker: { ...tracker, blockingLinkTypes: splitList(v) } })}
                    placeholder="System.LinkTypes.Dependency-Reverse" />
                </div>
              )}
            </div>
          </div>

          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">QA Testing</h4>
            <div className="grid grid-cols-2 gap-4">
              <Field label="API Port" description="IIS Express port for API" type="number" value={p.qaApiPort} onChange={v => update({ qaApiPort: Number(v) || 0 })} placeholder="53536" error={errors.qaApiPort} />
              <Field label="Web Port" description="IIS Express port for Web" type="number" value={p.qaWebPort} onChange={v => update({ qaWebPort: Number(v) || 0 })} placeholder="53537" error={errors.qaWebPort} />
            </div>
            <div className="grid grid-cols-2 gap-4 mt-4">
              <Field label="Username" value={p.qaLogin.username} onChange={v => update({ qaLogin: { ...p.qaLogin, username: v } })} placeholder="qa-user" />
              <Field label="Password" value={p.qaLogin.password ?? ''} onChange={v => update({ qaLogin: { ...p.qaLogin, password: v } })} isSecret
                placeholder={p.qaLogin.passwordSet ? '••••••••  (saved)' : ''} />
            </div>
          </div>

          <div>
            <h4 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-2">Heartbeat Skills</h4>
            <p className="text-xs text-gray-500 mb-3">
              The skills the heartbeat runs for this project. A missing skill blocks that project's matching tickets — set them up from the shared templates, then customize under
              <span className="font-mono text-gray-400"> data/projects/{p.key}/skills/</span>.
            </p>
            <div className="flex flex-wrap items-center gap-2">
              {p.skills.map(s => (
                <span key={s.name}
                  className={`px-2 py-0.5 rounded text-xs font-mono ${s.present
                    ? 'bg-gray-700 text-gray-300'
                    : 'bg-amber-500/10 text-amber-300 border border-amber-500/30'}`}>
                  {s.present ? '✓' : '○'} {s.name}
                </span>
              ))}
              {!p.skillsReady && (
                <button onClick={scaffoldSkills} disabled={busy}
                  className="btn-act px-3 py-1.5 text-blue-300 bg-blue-500/10 disabled:opacity-50">
                  {busy ? 'Setting up…' : 'Set up missing skills'}
                </button>
              )}
            </div>
          </div>

          <div className="flex justify-end pt-1">
            <SaveBar msg={null} saving={saving} disabled={!dirty || hasErrors} onSave={save} label="Save project" />
          </div>
        </div>
      )}
    </div>
  );
}

// ---------- Add-project form ------------------------------------------------

interface AddForm { key: string; displayName: string; projectPath: string }

function AddProject({ onCreated, existingKeys }: { onCreated: () => void; existingKeys: string[] }) {
  const [form, setForm] = useState<AddForm | null>(null);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<Msg>(null);

  // Pick a folder and auto-derive everything from it.
  const browse = async () => {
    const picked = await pickFolder(form?.projectPath);
    if (!picked) return;
    const leaf = leafName(picked);
    setForm({ key: slugify(leaf), displayName: leaf, projectPath: picked });
    setMsg(null);
  };

  const keyError = !form ? '' : !KEY_RE.test(form.key) ? 'Lowercase letters, numbers, - or _' : existingKeys.includes(form.key) ? 'Already exists' : '';
  const canCreate = !!form && form.key.length > 0 && !keyError;

  const create = async () => {
    if (!form || !canCreate) return;
    setSaving(true); setMsg(null);
    try {
      const res = await fetch('/api/projects', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...form, displayName: form.displayName || form.key }),
      });
      if (!res.ok) throw new Error((await res.json()).error ?? 'Create failed');
      setForm(null);
      onCreated();
    } catch (e) {
      setMsg({ type: 'error', text: e instanceof Error ? e.message : 'Create failed' });
    } finally { setSaving(false); }
  };

  if (!form) {
    return (
      <button onClick={browse}
        className="w-full py-3 border border-dashed border-hair rounded-lg text-sm text-gray-400 hover:text-gray-200 hover:border-gray-600 transition-colors">
        + Add project — pick a folder…
      </button>
    );
  }

  return (
    <div className="border border-hair bg-gray-800 rounded-lg p-4 space-y-4">
      <div className="flex items-center justify-between">
        <h4 className="text-sm font-semibold text-gray-200">New project</h4>
        <span className="text-[11px] font-mono text-gray-500 truncate max-w-md">{form.projectPath}</span>
      </div>
      <p className="text-xs text-gray-500 -mt-1">
        Derived from the folder you picked — adjust if needed. Registers the project so it shows on the board beside
        the others and can hold tickets, and copies the generic skills into
        <span className="font-mono text-gray-400"> data/projects/{form.key || '<key>'}/</span>. Its local stack and
        anything it shares with another project are set up there by hand — see <span className="font-mono text-gray-400">data/README.md</span>.
      </p>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Key" description="Folder name — lowercase, no spaces" value={form.key}
          onChange={v => setForm({ ...form, key: v })} placeholder="myproject" error={keyError} />
        <Field label="Display Name" value={form.displayName} onChange={v => setForm({ ...form, displayName: v })} placeholder="My Project" />
      </div>
      <div className="flex items-center justify-end gap-3">
        {msg && <span className="text-xs text-red-400">{msg.text}</span>}
        <button onClick={browse} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200">Change folder</button>
        <button onClick={() => setForm(null)} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200">Cancel</button>
        <button onClick={create} disabled={!canCreate || saving}
          className="btn-act px-4 py-2 bg-blue-500 text-void disabled:bg-gray-700 disabled:text-gray-500">
          {saving ? 'Creating…' : 'Create'}
        </button>
      </div>
    </div>
  );
}

// ---------- Projects manager ------------------------------------------------

function ProjectsManager() {
  const [projects, setProjects] = useState<ProjectDetail[]>([]);
  const [loading, setLoading] = useState(true);

  const load = useCallback(() => {
    fetch('/api/projects/manage')
      .then(r => r.json())
      .then(data => setProjects(Array.isArray(data.projects) ? data.projects : []))
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <div className="space-y-4">
      <div>
        <SectionRule title="Projects" colour="var(--w-queue)" count={projects.length} />
        <p className="text-xs text-gray-500">Repos the dashboard works on. Issue tracker and QA settings are configured per project here.</p>
      </div>
      {loading ? (
        <div className="text-gray-500 text-sm">Loading projects…</div>
      ) : (
        <>
          {projects.map(p => <ProjectCard key={p.key} project={p} onChanged={load} />)}
          {projects.length === 0 && <div className="text-sm text-gray-600 py-2">No projects registered yet.</div>}
          <AddProject onCreated={load} existingKeys={projects.map(p => p.key)} />
        </>
      )}
    </div>
  );
}

// ---------- Page ------------------------------------------------------------

export function SettingsPanel() {
  return (
    <div className="max-w-3xl mx-auto px-3 sm:px-6 py-4">
      <PageHead eyebrow="Whetstone // config" title="Settings" />
      <div className="space-y-10">
        <ProjectsManager />
        <GlobalSettings />
      </div>
    </div>
  );
}
