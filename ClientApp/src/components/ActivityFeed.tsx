import { Fragment, useRef, useState, useEffect } from 'react';
import { isStewardEvent } from '../types/events';
import type { HeartbeatEvent } from '../types/events';
import { AgentTag, agentAccent } from './AgentMascot';
import { STEWARD_HUE, STEWARD_WASH, StewardTag } from './StewardMark';
import { FEED_HIDDEN, formatEventText, prettyTool } from './eventText';

interface Props {
  events: HeartbeatEvent[];
  /**
   * Agent slots the feed is narrowed to, set from the status banner's chips. Empty means the whole
   * fleet. Filtering drops the orchestrator's own lines too — sleep and idle belong to no agent, so
   * a feed asked for one strand should not keep interleaving them.
   */
  agentFilter?: ReadonlySet<number>;
  /**
   * The feed is one voice's - the Steward page's, handed only its own steward's lines. Nothing in
   * it needs marking as whose, and it is as tall as what it has to say rather than a fixed column.
   */
  solo?: boolean;
  /** What to say when there is nothing to show, where the fleet's own wording would be wrong. */
  empty?: string;
}

const typeIcons: Record<string, string> = {
  cycle_start:    '\u25B6',
  cycle_end:      '\u25A0',
  ticket_start:   '\uD83C\uDFAB',
  tool_call:      '\uD83D\uDD27',
  sleep:          '\uD83D\uDE34',
  error:          '\u274C',
  summary:        '\uD83D\uDCCB',
  phase_detected: '\uD83D\uDD04',
  state_change:   '\u26A1',
  skill_start:    '\u25B6',
  skill_end:      '\u2714',
  ticket_skip:    '\u23ED',
};

const typeColors: Record<string, string> = {
  cycle_start:    'border-emerald-500',
  cycle_end:      'border-gray-500',
  ticket_start:   'border-blue-500',
  tool_call:      'border-yellow-500',
  sleep:          'border-amber-500',
  error:          'border-red-500',
  summary:        'border-purple-500',
  phase_detected: 'border-cyan-500',
  state_change:   'border-indigo-500',
  skill_start:    'border-green-400',
  skill_end:      'border-green-700',
  ticket_skip:    'border-red-500',
};

export function ActivityFeed({ events, agentFilter, solo = false, empty }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [autoScroll, setAutoScroll] = useState(true);

  // Auto-scroll to bottom on new events (only within the container). Changing the agent filter
  // rewrites the column under you, so it counts as new events for this purpose — otherwise you
  // land mid-history in a strand you just asked to follow.
  useEffect(() => {
    if (autoScroll && containerRef.current) {
      containerRef.current.scrollTop = containerRef.current.scrollHeight;
    }
  }, [events, autoScroll, agentFilter]);

  const handleScroll = () => {
    if (!containerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = containerRef.current;
    const isAtBottom = scrollHeight - scrollTop - clientHeight < 40;
    setAutoScroll(isAtBottom);
  };

  // Keep the feed to the story: FEED_HIDDEN drops state_change (internal plumbing for the status
  // banner, duplicates cycle/sleep) and summary (just restates the final narration; still goes to
  // Slack). Shared with the board's berths, which quote the last line an agent put in this feed.
  // The agent filter is applied before the 200-line window, not after: narrowing to one agent
  // should hand you that agent's last two hundred lines, not whatever survives of the fleet's.
  const filtering = (agentFilter?.size ?? 0) > 0;
  const feedEvents = events
    .filter(e => !FEED_HIDDEN.has(e.type) && (!filtering || (e.agent != null && agentFilter!.has(e.agent))))
    .slice(-200);

  if (feedEvents.length === 0) {
    return (
      <div className={`${solo ? 'h-24' : 'h-[500px]'} flex items-center justify-center bg-gray-900 rounded-lg border border-hair text-gray-500 text-center px-6`}>
        {empty ?? (filtering
          ? 'Nothing from the selected agents yet. Press Esc, or click the chip again, for the whole fleet.'
          : 'Waiting for heartbeat events...')}
      </div>
    );
  }

  return (
    <div className="relative">
      {!autoScroll && (
        <button
          onClick={() => {
            setAutoScroll(true);
            if (containerRef.current) {
              containerRef.current.scrollTo({ top: containerRef.current.scrollHeight, behavior: 'smooth' });
            }
          }}
          className="absolute top-2 right-4 z-10 bg-gray-700 hover:bg-gray-600 text-xs px-3 py-1 rounded-full"
        >
          ↓ Latest
        </button>
      )}
      <div
        ref={containerRef}
        onScroll={handleScroll}
        className={`${solo ? 'max-h-[500px]' : 'h-[500px]'} overflow-y-auto bg-gray-900 rounded-lg border border-hair p-3 dark-scrollbar`}
      >
        {feedEvents.map((evt, i) => {
          // Where the steward takes up the telling, say so once, at the head of its stretch: its
          // narration and thinking rows carry no name of their own, and a tag on every line of
          // them would be louder than the lines.
          const heads = !solo && isStewardEvent(evt) && !(i > 0 && isStewardEvent(feedEvents[i - 1]));
          return (
            <Fragment key={evt.id}>
              {heads && <StewardHead evt={evt} />}
              {evt.type === 'text_output' ? <NarrationRow evt={evt} solo={solo} />
                : evt.type === 'thinking' ? <ThinkingRow evt={evt} solo={solo} />
                : evt.type === 'tool_call' ? <ToolRow evt={evt} solo={solo} />
                : <ActionRow evt={evt} solo={solo} />}
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}

/**
 * A coloured rail down the left of every line one agent wrote.
 *
 * Three agents narrating into one feed is the single place this upgrade is most likely to become
 * unreadable: the lines are already short and similar, and interleaved they stop being three
 * stories and become noise. The rail is what lets the eye follow one strand down the column
 * without reading a word. Orchestrator lines (sleep, idle) have no agent and no rail.
 */
function rail(evt: HeartbeatEvent, solo = false) {
  // The steward is not a fourth agent and does not borrow one's colour: its lines sit on its own
  // rail and a faint wash, so its stretch of the feed reads as a different kind of thing rather
  // than as one more strand. On a feed that is only the steward's there is nothing to tell apart.
  if (isStewardEvent(evt)) return solo ? undefined : { borderLeft: `2px solid ${STEWARD_HUE}`, background: STEWARD_WASH };
  if (evt.agent == null) return undefined;
  return { borderLeft: `2px solid ${agentAccent(evt.agent).hex}` };
}

/** The line that opens a stretch of the steward's lines: who is speaking, and for which project. */
function StewardHead({ evt }: { evt: HeartbeatEvent }) {
  return (
    <div style={rail(evt)} className="flex items-center gap-2 pl-3 pr-2 pt-2 pb-1 mt-1 rounded-r">
      <StewardTag />
      {evt.project && <span className="text-[10px] font-medium text-gray-400">{evt.project}</span>}
    </div>
  );
}

function ActionRow({ evt, solo }: { evt: HeartbeatEvent; solo: boolean }) {
  const steward = isStewardEvent(evt);
  const agentRail = rail(evt, solo);
  return (
    <div
      style={agentRail}
      className={`flex items-start gap-3 pl-3 py-1.5 rounded-r ${
        agentRail || steward ? '' : `border-l-2 ${typeColors[evt.type] ?? 'border-hair'}`
      }`}
    >
      <span className="text-sm flex-shrink-0 w-5" title={evt.type}>
        {typeIcons[evt.type] ?? '\u2022'}
      </span>
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <span className="text-xs text-gray-500">
            {new Date(evt.timestamp).toLocaleTimeString()}
          </span>
          {evt.agent != null && !steward && <AgentTag agent={evt.agent} />}
          {evt.project && !steward && (
            <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-gray-700 text-gray-300">
              {evt.project}
            </span>
          )}
          <span className="text-xs font-mono text-gray-600">
            {evt.type}
          </span>
        </div>
        <div className="text-sm text-gray-200 truncate">
          {formatEventText(evt)}
        </div>
      </div>
    </div>
  );
}

function time(evt: HeartbeatEvent) {
  return new Date(evt.timestamp).toLocaleTimeString();
}

// Claude's narration — the voice of the run. Rendered prominently, like a chat turn.
function NarrationRow({ evt, solo }: { evt: HeartbeatEvent; solo: boolean }) {
  const steward = isStewardEvent(evt);
  const accent = steward || evt.agent == null ? null : agentAccent(evt.agent);
  return (
    <div style={rail(evt, solo)} className="flex gap-3 py-1.5 px-2 rounded-r">
      {/* The speaker's mark takes the agent's colour: with three voices in one column, an
          identical bullet on every line is the thing that makes them one indistinct stream. */}
      <span
        className="flex-shrink-0 text-sm leading-relaxed select-none"
        style={{ color: steward ? STEWARD_HUE : accent ? accent.hex : 'rgb(var(--w-queue-rgb) / .8)' }}
        title={steward ? 'Steward' : evt.agent != null ? `Agent ${evt.agent}` : undefined}
      >
        {steward ? '◆' : '✦'}
      </span>
      <p className="flex-1 min-w-0 text-sm text-gray-100 leading-relaxed whitespace-pre-wrap break-words">
        {evt.text ?? ''}
      </p>
      <span className="flex-shrink-0 text-[10px] text-gray-600 pt-1">{time(evt)}</span>
    </div>
  );
}

// The model's reasoning — a quiet, dimmed aside beneath the narration.
function ThinkingRow({ evt, solo }: { evt: HeartbeatEvent; solo: boolean }) {
  return (
    <div style={rail(evt, solo)} className="flex gap-2 py-1 pl-3 pr-2 rounded-r">
      <span className="flex-shrink-0 text-gray-600 text-xs leading-relaxed select-none">💭</span>
      <p className="flex-1 min-w-0 text-xs italic text-gray-500 leading-relaxed whitespace-pre-wrap break-words">
        {evt.text ?? ''}
      </p>
    </div>
  );
}

// A single tool invocation — compact and subordinate: verb + what it acted on.
function ToolRow({ evt, solo }: { evt: HeartbeatEvent; solo: boolean }) {
  return (
    <div style={rail(evt, solo)} className="flex items-baseline gap-2 py-0.5 pl-8 pr-2 rounded-r">
      <span className="flex-shrink-0 text-[10px] text-gray-700">{typeIcons.tool_call}</span>
      <span className="flex-shrink-0 text-xs font-mono text-gray-400">{prettyTool(evt.toolName)}</span>
      {evt.text && (
        <span className="min-w-0 truncate text-xs text-gray-500 font-mono">{evt.text}</span>
      )}
    </div>
  );
}
