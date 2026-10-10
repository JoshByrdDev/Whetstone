import type { HeartbeatEvent } from '../types/events';

interface Props {
  events: HeartbeatEvent[];
}

const typeIcons: Record<string, string> = {
  cycle_start:    '▶',
  cycle_end:      '■',
  ticket_start:   '🎫',
  tool_call:      '🔧',
  text_output:    '💬',
  sleep:          '😴',
  error:          '❌',
  summary:        '📋',
  phase_detected: '🔄',
  state_change:   '⚡',
};

const typeColors: Record<string, string> = {
  cycle_start:    'border-emerald-500',
  cycle_end:      'border-gray-500',
  ticket_start:   'border-blue-500',
  tool_call:      'border-yellow-500',
  text_output:    'border-gray-600',
  sleep:          'border-amber-500',
  error:          'border-red-500',
  summary:        'border-purple-500',
  phase_detected: 'border-cyan-500',
  state_change:   'border-indigo-500',
};

export function Timeline({ events }: Props) {
  // Filter out text_output for timeline — too noisy
  const timelineEvents = events.filter(e => e.type !== 'text_output').slice(-50).reverse();

  if (timelineEvents.length === 0) {
    return (
      <div className="text-gray-500 text-center py-8">
        Waiting for heartbeat events...
      </div>
    );
  }

  return (
    <div className="space-y-1 h-[420px] overflow-y-auto pr-2">
      {timelineEvents.map(evt => (
        <div
          key={evt.id}
          className={`flex items-start gap-3 pl-3 border-l-2 ${typeColors[evt.type] ?? 'border-hair'} py-1.5`}
        >
          <span className="text-sm flex-shrink-0 w-5" title={evt.type}>
            {typeIcons[evt.type] ?? '•'}
          </span>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2">
              <span className="text-xs text-gray-400">
                {new Date(evt.timestamp).toLocaleTimeString()}
              </span>
              <span className="text-xs font-mono text-gray-500">
                {evt.type}
              </span>
            </div>
            <div className="text-sm text-gray-200 truncate">
              {formatEventText(evt)}
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

function formatEventText(evt: HeartbeatEvent): string {
  switch (evt.type) {
    case 'cycle_start':
      return 'Heartbeat cycle started';
    case 'cycle_end':
      return 'Heartbeat cycle complete';
    case 'ticket_start':
      return `Working on ${evt.ticketId ?? 'ticket'}`;
    case 'tool_call':
      return evt.toolName ?? 'Unknown tool';
    case 'sleep':
      return evt.durationSeconds
        ? `Sleeping for ${Math.ceil(evt.durationSeconds / 60)}m`
        : 'Going to sleep';
    case 'error':
      return evt.error ?? evt.text ?? 'Error occurred';
    case 'summary':
      return evt.text ?? 'Cycle summary';
    case 'phase_detected':
      return `${evt.phase ?? 'Phase'} on ${evt.ticketId ?? 'ticket'}`;
    case 'state_change':
      return evt.text ?? 'State changed';
    default:
      return evt.text ?? evt.type;
  }
}
