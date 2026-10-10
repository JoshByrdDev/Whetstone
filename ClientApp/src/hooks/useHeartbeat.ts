import { useState, useEffect, useRef, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import type { HeartbeatEvent, DashboardState } from '../types/events';

const HUB_URL = '/hub/heartbeat';

export function useHeartbeat() {
  const [events, setEvents] = useState<HeartbeatEvent[]>([]);
  const [state, setState] = useState<DashboardState>({
    status: 'idle',
    totalEvents: 0,
    workers: 1,
    agents: [],
  });
  const [isConnected, setIsConnected] = useState(false);
  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const addEvent = useCallback((evt: HeartbeatEvent) => {
    setEvents(prev => {
      const next = [...prev, evt];
      // Keep last 500 in the client
      return next.length > 500 ? next.slice(-500) : next;
    });
  }, []);

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    connection.on('ReceiveEvent', (evt: HeartbeatEvent) => {
      addEvent(evt);
    });

    connection.on('ReceiveHistory', (history: HeartbeatEvent[]) => {
      setEvents(history);
    });

    connection.on('ReceiveState', (s: DashboardState) => {
      setState(s);
    });

    connection.onreconnecting(() => setIsConnected(false));
    connection.onreconnected(() => setIsConnected(true));
    connection.onclose(() => setIsConnected(false));

    connection
      .start()
      .then(() => setIsConnected(true))
      .catch(err => console.error('SignalR connection failed:', err));

    return () => {
      connection.stop();
    };
  }, [addEvent]);

  // Poll /api/state as a fallback. SignalR only pushes state when an event arrives or on start/stop;
  // a graceful self-stop or a crashed heartbeat produces no event, so without this the status could
  // stay frozen. GetState() reconciles with process liveness, so this surfaces "stopped" within ~5s.
  useEffect(() => {
    let active = true;
    const poll = async () => {
      try {
        const r = await fetch('/api/state');
        if (active && r.ok) setState(await r.json());
      } catch { /* transient — keep last known state */ }
    };
    const id = setInterval(poll, 5000);
    return () => { active = false; clearInterval(id); };
  }, []);

  return { events, state, isConnected };
}
