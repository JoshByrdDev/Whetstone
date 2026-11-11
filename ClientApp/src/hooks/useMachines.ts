import { useEffect, useState } from 'react';

/**
 * Every machine's hardware, latest load and suggested slot counts, from /api/machines
 * (Services/MachineLimits.cs, docs/machine-limits-plan.md). Read once a minute: the samples it is
 * built from arrive once a minute.
 */

export interface MachineInventory {
  cpuModel?: string | null; physicalCores?: number | null; logicalCores?: number | null;
  perfCores?: number | null; effCores?: number | null; ramMb?: number | null;
  os?: string | null; osVersion?: string | null; arch?: string | null;
  diskKind?: string | null; diskTotalGb?: number | null;
  dockerCpus?: number | null; dockerMemMb?: number | null;
  tools?: Record<string, string> | null;
}

export interface MachineLoad {
  at: string; cpuPct?: number | null; memAvailMb?: number | null; memTotalMb?: number | null;
  swapUsedMb?: number | null; pressure?: number | null; diskFreeGb?: number | null;
  netInKbps?: number | null; netOutKbps?: number | null; busy: number; phasesJson?: string | null;
  dockerMemUsedMb?: number | null; dockerMemLimitMb?: number | null;
}

export interface SlotSuggestion {
  set: number;
  suggested: number | null;
  basis: 'hardware' | 'learned' | 'ceiling' | 'none';
  confidence: 'low' | 'medium' | 'high';
  reasons: string[];
}

export interface RunSpeed {
  points: { others: number; runs: number; secPerTurn: number; modelSecPerTurn: number; machineSecPerTurn: number; throughput: number }[];
  mostWork: number | null; fastest: number | null; bound: 'machine' | 'model' | null; summary: string;
}

export interface Machine {
  place: string;
  inventory: MachineInventory | null;
  inventorySince: string | null;
  inventorySeen: string | null;
  history: { firstSeen: string; lastSeen: string; report: MachineInventory | null }[];
  load: MachineLoad | null;
  samples: number;
  workers: SlotSuggestion;
  qa: SlotSuggestion;
  speed: RunSpeed | null;
}

export interface Machines { auto: boolean; machines: Machine[]; fleet: string | null }

export const BOARD_PLACE = '@board';

export function useMachines(): Machines | null {
  const [m, setM] = useState<Machines | null>(null);
  useEffect(() => {
    let live = true;
    const load = async () => {
      try {
        const r = await fetch('/api/machines');
        if (r.ok && live) setM(await r.json());
      } catch { /* keep what we have */ }
    };
    load();
    const id = window.setInterval(load, 60000);
    return () => { live = false; window.clearInterval(id); };
  }, []);
  return m;
}

export const machineName = (place: string) => (place === BOARD_PLACE ? 'This computer' : place);

/** One line of hardware: "Apple M5 · 4P+6E · 48GB · macOS 26.1 arm64 · SSD". */
export function hardwareLine(i: MachineInventory | null | undefined): string {
  if (!i) return 'No hardware report yet';
  const cores = i.perfCores != null && i.effCores != null ? `${i.perfCores}P+${i.effCores}E cores`
    : i.physicalCores != null ? `${i.physicalCores} cores${i.logicalCores ? ` / ${i.logicalCores} threads` : ''}`
    : i.logicalCores != null ? `${i.logicalCores} threads` : null;
  return [
    i.cpuModel, cores, i.ramMb != null ? `${Math.round(i.ramMb / 1024)}GB` : null,
    [i.os, i.osVersion, i.arch].filter(Boolean).join(' ') || null,
    i.diskKind ? `${i.diskKind}${i.diskTotalGb ? ` ${Math.round(i.diskTotalGb)}GB` : ''}` : null,
    i.dockerMemMb != null ? `Docker ${i.dockerCpus ?? '?'} CPU / ${(i.dockerMemMb / 1024).toFixed(1)}GB` : null,
  ].filter(Boolean).join(' · ');
}
