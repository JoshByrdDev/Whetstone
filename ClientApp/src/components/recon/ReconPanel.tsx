import { useCallback, useEffect, useRef, useState } from 'react';
import { PageHead, GhostButton } from '../ui';
import { ProjectPicker } from '../ProjectPicker';
import { CreateTicketModal, type TicketPrefill } from '../CreateTicketModal';
import { TicketDrawer } from '../Board';
import { getProjects } from '../../api/tickets';
import type { ProjectInfo } from '../../types/tickets';
import type { DashboardState } from '../../types/events';
import { ago, useJson } from '../metrics/parts';
import { ScoutNext } from './ScoutNext';
import { Coverage } from './Coverage';
import { Yield } from './Yield';
import { Notice, TextButton, mono } from './parts';
import type { CoverageCell, ReconProject, ReconProjects, ScoutAsk, SuggestionsRead } from './types';

/**
 * Recon: where to point the next scout (docs/recon-tab-plan.md). One project at a time - coverage
 * does not add up across projects - in three parts: Scout next, Coverage with its area drill-down,
 * and Yield. Everything is read from the Recon marts through /api/recon; the page computes nothing
 * but layout. Opening it refreshes marts older than the Metrics page's limit, as Metrics does, and
 * says when they were built.
 *
 * Every state that is not "here is the answer" is said in words: no areas yet, no roles yet, the
 * analytics not set up here, Recon not built yet, a repo whose history did not load.
 */

const PAGE = 'max-w-7xl mx-auto px-3 sm:px-6 py-4';
const STORE = 'whetstone.project.recon';

function remembered(): string | null {
  try { return localStorage.getItem(STORE); } catch { return null; }
}
function remember(key: string) {
  try { localStorage.setItem(STORE, key); } catch { /* the choice stands for this visit */ }
}

async function post(url: string, body: unknown): Promise<void> {
  const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!r.ok) {
    const j = await r.json().catch(() => null) as { error?: string } | null;
    throw new Error(j?.error ?? `${r.status} ${r.statusText}`);
  }
}

export function ReconPanel({ state }: { state: DashboardState }) {
  const [read, setRead] = useState<ReconProjects | null>(null);
  const [readError, setReadError] = useState<string | null>(null);
  const [project, setProject] = useState<string | null>(null);
  const [bump, setBump] = useState(0);
  const [days, setDays] = useState(0);
  const [area, setArea] = useState<string | null>(null);
  const [ask, setAsk] = useState<TicketPrefill | null>(null);
  const [ticketProjects, setTicketProjects] = useState<ProjectInfo[] | null>(null);
  const [drawer, setDrawer] = useState<number | null>(null);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const asked = useRef(false);

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/recon/projects');
      if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
      const j = await r.json() as ReconProjects;
      setRead(prev => {
        // A refresh just finished: everything on the page is read again.
        if (prev?.status.running && !j.status.running) setBump(b => b + 1);
        return j;
      });
      setReadError(null);
      setProject(prev => {
        if (prev && j.projects.some(p => p.key === prev)) return prev;
        const saved = remembered();
        return j.projects.find(p => p.key === saved)?.key ?? j.projects[0]?.key ?? null;
      });
      return j;
    } catch (e) {
      setReadError(String(e));
      return null;
    }
  }, []);

  const refresh = useCallback(async () => {
    try { await fetch('/api/analytics/refresh', { method: 'POST' }); } catch { /* the status says what happened */ }
    void load();
  }, [load]);

  // On arrival, refresh once if the marts are old or missing - the Metrics page's rule.
  useEffect(() => {
    load().then(j => {
      if (!j || asked.current) return;
      asked.current = true;
      const s = j.status;
      if (s.available && !s.running && (s.stale || !s.hasMarts)) void refresh();
    });
  }, [load, refresh]);

  // While a refresh runs, watch it. No timer otherwise.
  const running = read?.status.running ?? false;
  useEffect(() => {
    if (!running) return;
    const t = window.setInterval(() => { void load(); }, 3000);
    return () => window.clearInterval(t);
  }, [running, load]);

  const p = read?.projects.find(x => x.key === project) ?? null;
  const pick = (key: string) => { setProject(key); setArea(null); remember(key); };

  const showable = !!p && p.built && p.areas.length > 0 && !!read?.status.hasMarts;
  const base = p ? `/api/recon/${encodeURIComponent(p.key)}` : null;
  const sugg = useJson<SuggestionsRead>(showable ? `${base}/suggestions` : null, bump);
  const cov = useJson<{ cells: CoverageCell[] }>(showable ? `${base}/coverage` : null, bump);

  const scout = async (a: ScoutAsk) => {
    if (!p) return;
    if (!ticketProjects) {
      try { setTicketProjects((await getProjects()).projects); }
      catch (e) { setActionError(`Could not open the form: ${String(e)}`); return; }
    }
    setAsk({
      projectKey: p.key, track: 'Discovery', title: a.title, description: a.direction, note: a.note, reconFrom: a.key,
      placeholder: a.placeholder, draftKey: a.draftKey, area: a.area,
    });
  };

  const act = async (key: string, url: string, body: unknown) => {
    setBusyKey(key); setActionError(null);
    try { await post(url, body); setBump(b => b + 1); }
    catch (e) { setActionError(e instanceof Error ? e.message : String(e)); }
    finally { setBusyKey(null); }
  };

  const openArea = (a: string) => {
    setArea(a);
    document.getElementById('recon-coverage')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  const status = read?.status;
  const sub = status?.running ? 'refreshing…'
    : p?.builtAt ? `built ${ago(p.builtAt)}`
    : status?.asOf ? `as of ${ago(status.asOf)}` : 'loading…';

  return (
    <div className={PAGE}>
      <PageHead
        eyebrow="Whetstone // discovery"
        title="Recon"
        sub={sub}
        left={read && read.projects.length > 1
          ? <div style={{ paddingBottom: 5 }}><ProjectPicker projects={read.projects} project={project} onChange={pick} /></div>
          : undefined}
        right={
          <GhostButton onClick={() => void refresh()} disabled={!status?.available || status.running}
            title={status?.lastSeconds ? `The last refresh took ${Math.round(status.lastSeconds)}s` : undefined}>
            {status?.running ? 'Refreshing…' : 'Refresh'}
          </GhostButton>
        }
      />

      <div key={project ?? 'none'} className="orbit-swap min-w-0 space-y-6">
        {readError && <Notice warn>Could not read Recon: {readError}</Notice>}
        {!readError && !read && <Notice>Loading…</Notice>}
        {read && read.projects.length === 0 && (
          <Notice>No project has a <span style={mono}>discovery</span> block, so there is nowhere to point a scout.</Notice>
        )}
        {status?.lastOk === false && status.lastError && (
          <Notice warn>The last refresh failed: {status.lastError}. The page shows the build before it.</Notice>
        )}
        {actionError && <Notice warn>{actionError}</Notice>}
        {read && p && <States p={p} read={read} onRefresh={() => void refresh()} />}

        {showable && p && (
          <>
            <ScoutNext read={sugg.data} error={sugg.error} onScout={a => void scout(a)}
              onDismiss={(key, until) => void act(key, `${base}/dismiss`, { key, until: until?.toISOString() ?? null })}
              onUndismiss={key => void act(key, `${base}/undismiss`, { key })}
              onOpenArea={openArea} onOpenTicket={setDrawer} busyKey={busyKey} cannotFile={p.cannotFile} />
            <div id="recon-coverage" className="min-w-0 scroll-mt-24">
              <Coverage project={p} cells={cov.data?.cells ?? null} error={cov.error} area={area} onArea={setArea}
                onOpenTicket={setDrawer} bump={bump} />
            </div>
            <Yield project={p.key} days={days} setDays={setDays} onScout={p.cannotFile ? null : a => void scout(a)} bump={bump} />
          </>
        )}
      </div>

      {ask && ticketProjects && (
        <CreateTicketModal projects={ticketProjects} prefill={ask}
          onClose={() => setAsk(null)}
          onCreated={id => { setAsk(null); setBump(b => b + 1); setDrawer(id); }} />
      )}
      {drawer != null && <TicketDrawer id={drawer} state={state} onClose={() => setDrawer(null)} />}
    </div>
  );
}

/** The states that stand in for the page, or sit above it, each in words with what to do next. */
function States({ p, read, onRefresh }: { p: ReconProject; read: ReconProjects; onRefresh: () => void }) {
  const s = read.status;
  if (p.areas.length === 0) {
    return (
      <Notice>
        <div className="text-gray-200">Name this product&apos;s areas first.</div>
        <p className="mt-1">
          Recon maps where scouts have been by area, and {p.displayName} has none. Add them to its
          <span style={mono}> discovery</span> block in <span style={mono}>project.defaults.json</span> - each a name,
          the page patterns it covers, the code paths that make it, and optionally a sentence on who it is for
          and the roles that use it, which is what a drafted direction is written from:
        </p>
        <pre className="mt-2 overflow-x-auto border border-hair bg-sunk p-2 text-[11px] text-gray-300" style={mono}>{`"discovery": {
  "areas": [
    { "name": "Billing", "pages": ["/billing/**"], "code": ["src/billing/**"],
      "about": "Where a member pays and an admin chases what is owed.",
      "roles": ["member", "admin"] }
  ],
  "roles": ["signed out", "member", "admin"]
}`}</pre>
      </Notice>
    );
  }
  if (!s.available) {
    return (
      <Notice>
        Recon is built by the analytics refresh, which cannot run here: {s.unavailable} The Setup check on the
        System page lists what this machine is missing.
      </Notice>
    );
  }
  if (!p.built || !s.hasMarts) {
    return s.running
      ? <Notice>Building Recon for {p.displayName}. The page fills in when the refresh finishes.</Notice>
      : <Notice>Recon has not been built yet for {p.displayName}. <TextButton onClick={onRefresh}>Refresh</TextButton> to build it.</Notice>;
  }
  const notes: React.ReactNode[] = [];
  if (p.builtAreas != null && p.builtAreas !== p.areas.length) {
    notes.push(<>The areas changed since Recon was built ({p.builtAreas} then, {p.areas.length} now). <TextButton onClick={onRefresh}>Refresh</TextButton> to map them.</>);
  }
  if (p.builtRoles != null && p.builtRoles !== p.roles.length) {
    notes.push(<>The roles changed since Recon was built. <TextButton onClick={onRefresh}>Refresh</TextButton> to use them.</>);
  }
  for (const r of p.repos ?? []) {
    if (r.error) notes.push(<>The history of {r.repo || 'its repo'} did not load ({r.error}), so what changed in an area there reads as unknown.</>);
    else if (!r.base_ref) notes.push(<>{r.repo || 'Its repo'} has no base ref Recon could resolve, so staleness there is unknown.</>);
  }
  if (notes.length === 0) return null;
  return <div className="space-y-2">{notes.map((n, i) => <Notice key={i}>{n}</Notice>)}</div>;
}
