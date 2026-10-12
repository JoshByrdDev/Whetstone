import { useState, useMemo, useEffect, useCallback } from 'react';
import type { CSSProperties } from 'react';
import { useHeartbeat } from './hooks/useHeartbeat';
import { StatusBanner } from './components/StatusBanner';
import { AuthBanner } from './components/AuthBanner';
import { TicketCards } from './components/TicketCard';
import { ActivityFeed } from './components/ActivityFeed';
import { SkillsPanel } from './components/SkillsPanel';
import { Board, TicketDrawer } from './components/Board';
import { HeartbeatControl } from './components/HeartbeatControl';
import { HeartbeatProjectFilter } from './components/HeartbeatProjectFilter';
import { HeartbeatPriorityFilter } from './components/HeartbeatPriorityFilter';
import { SettingsPanel } from './components/SettingsPanel';
import { ImprovementsPanel } from './components/ImprovementsPanel';
import { HeaderHeartbeat } from './components/HeaderHeartbeat';
import { HeaderHeartbeatToggle } from './components/HeaderHeartbeatToggle';
import { HeaderUsage } from './components/ClaudeUsage';
import { HistoryPanel } from './components/HistoryPanel';
import { MetricsPanel } from './components/MetricsPanel';
import { Classroom } from './components/Classroom';
import { StewardPanel } from './components/StewardPanel';
import { Backdrop } from './components/Backdrop';
import { PageHead, SectionRule } from './components/ui';
import { chamfer, CAPS, DISPLAY } from './components/orbit';
import { useTheme, track } from './theme';
import { useTabStatus } from './hooks/useTabStatus';
import { useNarrow } from './hooks/useNarrow';

type Tab = 'board' | 'steward' | 'classroom' | 'heartbeat' | 'metrics' | 'history' | 'skills' | 'improvements' | 'settings';

/**
 * The tabs, in the order they appear in the nav — which is also the order they sit in space.
 *
 * The transition reads this: moving to a tab further right sends the old page out to the left and
 * brings the new one in from the right, so the nav stops being eight independent buttons and
 * becomes one strip you are moving along.
 */
const TABS: { id: Tab; label: string }[] = [
  { id: 'board', label: 'Board' },
  { id: 'steward', label: 'Steward' },
  { id: 'classroom', label: 'Classroom' },
  { id: 'heartbeat', label: 'Heartbeat' },
  { id: 'metrics', label: 'Metrics' },
  { id: 'history', label: 'History' },
  { id: 'skills', label: 'Skills' },
  { id: 'improvements', label: 'System' },
  { id: 'settings', label: 'Settings' },
];

/** Out is quicker than in: the old page should already be leaving before you look for the new one. */
const OUT_MS = 165;
const IN_MS = 280;

export default function App() {
  const { events, state, isConnected } = useHeartbeat();
  // Subscribed here and nowhere else. Most of the app reads the theme through CSS variables, which
  // the browser repaints on its own; this is for the handful of values that are computed in JS —
  // the chamfers, the backdrop spec — which need a render to be recomputed. Nothing below is
  // memoised, so one state change at the root is enough to reach all of them.
  useTheme();
  // On a phone the bar is two short rows - the fleet's pulse, then the tabs as one strip you swipe
  // along. Left to wrap, nine tabs and three agents' tickets stood a third of the screen tall, and
  // stuck there. The name gives up its place: the row is exactly as wide as a full fleet's pulse,
  // and nobody holding the app needs telling which app it is.
  const narrow = useNarrow();
  const [tab, setTab] = useState<Tab>('board');
  const [classroomTicket, setClassroomTicket] = useState<number | null>(null);
  // The ticket open in the slide-over on the heartbeat page — the board's own drawer, not a lookalike.
  const [drawerTicket, setDrawerTicket] = useState<number | null>(null);
  const closeDrawer = useCallback(() => setDrawerTicket(null), []);

  // Which agents the heartbeat page is narrowed to, set by clicking the chips in the status
  // banner. Empty means "the whole fleet" rather than "nobody": a filter you have to switch on
  // before the page says anything would be a worse default than the full story.
  const [agentFilter, setAgentFilter] = useState<ReadonlySet<number>>(() => new Set());
  const toggleAgent = useCallback((slot: number) => {
    setAgentFilter(prev => {
      const next = new Set(prev);
      if (!next.delete(slot)) next.add(slot);
      return next;
    });
  }, []);
  // A filter set from a banner at the top of the page is one you can scroll away from, so it needs
  // an exit that is not "find the banner again". Same key the board's stage filter answers to.
  // With a ticket open, Esc belongs to the drawer — one press should not also drop the filter.
  useEffect(() => {
    if (agentFilter.size === 0 || drawerTicket != null) return;
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setAgentFilter(new Set()); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [agentFilter, drawerTicket]);
  // An agent that has finished and gone home cannot be un-picked — its chip is no longer on the
  // banner — and a filter naming only agents that are no longer out would empty both panels with
  // nothing left on screen to explain why. Dropping them restores the full story instead.
  useEffect(() => {
    setAgentFilter(prev => {
      if (prev.size === 0) return prev;
      const out = new Set(state.agents.map(a => a.slot));
      if (state.steward) out.add(state.steward.slot);
      const kept = [...prev].filter(s => out.has(s));
      return kept.length === prev.size ? prev : new Set(kept);
    });
  }, [state.agents, state.steward]);

  // Where the page is in its two-step move. `leaving` holds the tab we are on our way to while the
  // current one animates out; `entering` is the direction the new one flies in from once it lands.
  // Only ever one page mounted, which is what it was before the animation existed — a crossfade
  // would mean mounting two, and two of these pages means two of their fetches.
  const [leaving, setLeaving] = useState<{ to: Tab; dir: 1 | -1 } | null>(null);
  const [entering, setEntering] = useState<1 | -1 | 0>(0);

  const go = useCallback((next: Tab) => {
    if (next === tab || leaving) return;
    // Reduced motion still switches tabs — it just arrives already there.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) { setTab(next); return; }
    const dir: 1 | -1 = TABS.findIndex(t => t.id === next) > TABS.findIndex(t => t.id === tab) ? 1 : -1;
    setLeaving({ to: next, dir });
  }, [tab, leaving]);

  useEffect(() => {
    if (!leaving) return;
    const swap = setTimeout(() => {
      setTab(leaving.to);
      setEntering(leaving.dir);
      setLeaving(null);
      window.scrollTo({ top: 0 });
    }, OUT_MS);
    return () => clearTimeout(swap);
  }, [leaving]);

  // Clearing the entrance class matters: while it is on, the stage carries a transform, and a
  // transform makes the stage the containing block for any `position: fixed` inside it.
  useEffect(() => {
    if (!entering) return;
    const done = setTimeout(() => setEntering(0), IN_MS);
    return () => clearTimeout(done);
  }, [entering]);

  // "Study this ticket" in the drawer, which sits several components deep inside the board. A window
  // event rather than a callback threaded through Board -> TicketCard -> TicketDetail: one listener
  // here against one dispatch there, instead of three components carrying a prop none of them use.
  useEffect(() => {
    const open = (e: Event) => {
      const id = (e as CustomEvent<{ ticketId: number }>).detail?.ticketId;
      if (typeof id !== 'number') return;
      setClassroomTicket(id);
      go('classroom');
    };
    window.addEventListener('open-classroom', open);
    return () => window.removeEventListener('open-classroom', open);
  }, [go]);

  // When sleeping, derive the wake time from the latest sleep event (timestamp + duration).
  const wakeAt = useMemo(() => {
    if (state.status !== 'sleeping') return null;
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      if (e.type === 'sleep' && e.durationSeconds) {
        return new Date(new Date(e.timestamp).getTime() + e.durationSeconds * 1000);
      }
    }
    return null;
  }, [state.status, events]);

  useTabStatus(state, wakeAt);

  const moving = leaving !== null || entering !== 0;
  const stageClass = leaving
    ? (leaving.dir === 1 ? 'tab-out-left' : 'tab-out-right')
    : entering === 1 ? 'tab-in-right'
    : entering === -1 ? 'tab-in-left'
    : '';

  return (
    <div className="min-h-screen text-gray-100 font-sans">
      {/* One backdrop, mounted once, behind everything. Tabs come and go over the top of it. */}
      <Backdrop />

      <div className="relative" style={{ zIndex: 1 }}>
        <header
          className={`sticky top-0 flex items-center justify-between flex-wrap ${narrow ? 'gap-x-3 gap-y-1.5' : 'gap-6'}`}
          style={{
            zIndex: 30,
            padding: narrow ? '8px 12px 6px' : '10px 24px',
            borderBottom: '1px solid rgb(var(--w-line-rgb) / .13)',
            background: 'linear-gradient(180deg,rgb(var(--w-s950-rgb) / .88),rgb(var(--w-s950-rgb) / .62))',
            backdropFilter: 'blur(10px)',
          }}
        >
          {/* `contents` on a phone: the tabs leave this group and take the whole second row. */}
          <div className={narrow ? 'contents' : 'flex items-center gap-7 flex-wrap'}>
            {!narrow && <div>
              <div
                style={{
                  font: `700 15px ${DISPLAY}`, letterSpacing: track(.3),
                  color: 'var(--w-t100)', textTransform: CAPS, lineHeight: 1.1,
                }}
              >
                Whetstone
              </div>
              <div style={{ font: `500 8px ${DISPLAY}`, letterSpacing: track(.3), color: 'var(--w-t600)', textTransform: CAPS }}>
                Local ticketing &amp; heartbeat
              </div>
            </div>}
            <nav className={narrow ? 'tab-strip flex gap-1 order-last w-full' : 'flex gap-1 flex-wrap'}>
              {TABS.map(t => (
                <TabButton key={t.id} on={tab === t.id} onClick={() => go(t.id)}>
                  {t.label}
                </TabButton>
              ))}
            </nav>
          </div>
          <div className={`flex items-center min-w-0 ${narrow ? 'w-full justify-between flex-wrap gap-x-2.5 gap-y-1' : 'gap-4'}`}>
            <HeaderHeartbeat state={state} wakeAt={wakeAt} compact={narrow} />
            <HeaderHeartbeatToggle />
            <HeaderUsage />
          </div>
        </header>

        <AuthBanner />

        {/* The stage, inside a clip that only exists while it is moving.
            A page sliding in from +56px is 56px wider than the document, which raises a horizontal
            scrollbar; the scrollbar then eats ten pixels of viewport height, which raises a
            vertical one, which eats ten pixels of width — and everything reflows twice inside a
            280ms animation. Clipping for the duration means the translation never reaches the
            document's scroll width. The class is dropped the moment the move ends, so nothing on
            a resting page is ever clipped. */}
        <div className={moving ? 'tab-clip' : undefined}>
          {/* Keyed on the tab so React tears the old page down and the entrance animation restarts
              from its first frame rather than being skipped as an unchanged class. */}
          <div key={tab} className={stageClass} style={{ pointerEvents: leaving ? 'none' : undefined }}>
          {tab === 'board' && <Board state={state} wakeAt={wakeAt} events={events} />}

          {tab === 'steward' && <StewardPanel events={events} state={state} />}

          {tab === 'classroom' && <Classroom initialTicketId={classroomTicket} />}

          {tab === 'heartbeat' && (
            <div className={`max-w-7xl mx-auto py-4 ${narrow ? 'px-3' : 'px-6'}`}>
              <PageHead
                eyebrow="Whetstone // fleet"
                title="Heartbeat"
              />
              <div className="space-y-6">
                <StatusBanner
                  state={state}
                  isConnected={isConnected}
                  events={events}
                  agentFilter={agentFilter}
                  onToggleAgent={toggleAgent}
                  onClearAgents={() => setAgentFilter(new Set())}
                />
                <HeartbeatControl />
                <HeartbeatProjectFilter />
                <HeartbeatPriorityFilter />

                <div className="grid grid-cols-1 lg:grid-cols-4 gap-6">
                  <div className="lg:col-span-3">
                    <SectionRule title="Activity feed" colour="var(--w-queue)" />
                    <ActivityFeed events={events} agentFilter={agentFilter} />
                  </div>

                  <div>
                    <SectionRule title="Tickets" colour="var(--w-gate)" />
                    {/* Padded on both sides, not just the right: the cards lean a couple of pixels
                        left under the pointer, and a scroller with no left padding would clip the
                        lean (and offer a horizontal scrollbar for it on the other edge). */}
                    <div className="h-[500px] overflow-y-auto px-1 dark-scrollbar">
                      <TicketCards
                        events={events}
                        agentFilter={agentFilter}
                        onTicketClick={(id) => {
                          const n = parseInt(id.replace(/\D/g, ''), 10);
                          if (n) setDrawerTicket(n);
                        }}
                      />
                    </div>
                  </div>
                </div>
              </div>
              {drawerTicket != null && <TicketDrawer id={drawerTicket} state={state} onClose={closeDrawer} />}
            </div>
          )}

          {tab === 'metrics' && <MetricsPanel />}
          {tab === 'history' && <HistoryPanel />}
          {tab === 'skills' && <SkillsPanel />}
          {tab === 'improvements' && <ImprovementsPanel />}
          {tab === 'settings' && <SettingsPanel />}
          </div>
        </div>
      </div>
    </div>
  );
}

/**
 * One tab in the strip.
 *
 * The live one is amber and sits on a lit rail; the rest are hairline glass. The rail is the part
 * that does the work — eight chips of the same size need something that reads as position, not
 * just as emphasis, or the nav looks like eight buttons of which one happens to be brighter.
 */
function TabButton({ on, onClick, children }: {
  on: boolean; onClick: () => void; children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className="orbit-btn relative flex-none"
      // The strip scrolls on a phone, and the tab you are on has to come with it: the one you
      // picked from the far end would otherwise be the one tab not on screen.
      ref={el => { if (on && el) el.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }}
      style={{
        clipPath: chamfer(8),
        ['--rbg' as string]: on ? 'rgb(var(--w-accent-rgb) / .14)' : 'transparent',
        ['--rbw' as string]: `1px solid ${on ? 'rgb(var(--w-accent-rgb) / .4)' : 'transparent'}`,
        padding: '6px 13px 7px',
        font: `600 10px ${DISPLAY}`, letterSpacing: track(.19), textTransform: CAPS,
        color: on ? 'var(--w-accent-text)' : 'var(--w-t500)',
        ['--hbg' as string]: on ? 'rgb(var(--w-accent-rgb) / .2)' : 'rgb(var(--w-line-rgb) / .09)',
        ['--htxt' as string]: on ? 'var(--w-accent-text-lit)' : 'var(--w-t100)',
        ['--hbc' as string]: on ? 'rgb(var(--w-accent-rgb) / .55)' : 'rgb(var(--w-line-rgb) / .2)',
        ['--hsh' as string]: 'none',
      } as CSSProperties}
    >
      {children}
      {on && (
        <span
          style={{
            position: 'absolute', left: 8, right: 8, bottom: 0, height: 2,
            background: 'var(--w-accent)', boxShadow: '0 0 10px var(--w-accent)',
          }}
        />
      )}
    </button>
  );
}
