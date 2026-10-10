import { useEffect, useRef, useState } from 'react';
import type { HeartbeatEvent } from '../types/events';

interface Props {
  events: HeartbeatEvent[];
}

export function LogStream({ events }: Props) {
  const bottomRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [autoScroll, setAutoScroll] = useState(true);

  // Auto-scroll to bottom on new events
  useEffect(() => {
    if (autoScroll && bottomRef.current) {
      bottomRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [events, autoScroll]);

  // Detect manual scroll — pause auto-scroll when user scrolls up
  const handleScroll = () => {
    if (!containerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = containerRef.current;
    const isAtBottom = scrollHeight - scrollTop - clientHeight < 40;
    setAutoScroll(isAtBottom);
  };

  const logEvents = events.filter(
    e => e.type === 'text_output' || e.type === 'error' || e.type === 'summary'
  );

  return (
    <div className="relative">
      {!autoScroll && (
        <button
          onClick={() => {
            setAutoScroll(true);
            bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
          }}
          className="absolute top-2 right-4 z-10 bg-gray-700 hover:bg-gray-600 text-xs px-3 py-1 rounded-full"
        >
          ↓ Scroll to bottom
        </button>
      )}
      <div
        ref={containerRef}
        onScroll={handleScroll}
        className="bg-gray-900 rounded-lg p-4 font-mono text-xs max-h-72 overflow-y-auto border border-hair"
      >
        {logEvents.length === 0 ? (
          <div className="text-gray-600">Waiting for output...</div>
        ) : (
          logEvents.map(evt => (
            <div
              key={evt.id}
              className={`py-0.5 ${evt.type === 'error' ? 'text-red-400' : 'text-gray-300'}`}
            >
              <span className="text-gray-600 mr-2">
                {new Date(evt.timestamp).toLocaleTimeString()}
              </span>
              {evt.text ?? evt.error ?? ''}
            </div>
          ))
        )}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}
