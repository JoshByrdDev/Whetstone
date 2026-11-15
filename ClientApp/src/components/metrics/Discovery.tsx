import { useState } from 'react';
import { Chip } from '../ui';
import { MONO } from '../orbit';
import type { DiscoveryFunnelRow, DiscoveryRejectionRow, DiscoveryVersionRow, DiscoveryView } from './types';
import { Empty, Meter, Section, Sheet, Stat, compact, mins, pct, useJson } from './parts';

/**
 * The Discovery track, measured (docs/discovery-track-plan.md, "What is measured"): whether it saves
 * the owner more than it costs them, and whether the last change to it - a prompt, a model, a fold of
 * the taste file - helped. Read from the Discovery marts at /api/analytics/discovery, always on the
 * Discovery track whatever the rest of the page is on.
 *
 * There is little to measure at first, and the section says so in words: below the marts' floor of
 * closed verdicts a rate is "not enough verdicts yet", never 0% or 100% from two proposals, and a
 * count of nothing is said as nothing rather than drawn as an empty chart.
 */

const n = (v: number | null | undefined) => v ?? 0;

/** A rate the marts held back below the floor reads as words, not as a dash that looks like a zero. */
const FEW = 'not enough verdicts yet';

const CUTS: { key: string; label: string; mono?: boolean }[] = [
  { key: 'week', label: 'Week filed', mono: true },
  { key: 'model', label: 'Model' },
  { key: 'effort', label: 'Effort' },
  { key: 'provider', label: 'Provider' },
  { key: 'skill_version', label: 'Sift / recheck skill', mono: true },
  { key: 'scout_model', label: 'Scout model' },
  { key: 'scout_skill_version', label: 'Scout skill', mono: true },
  { key: 'taste_version', label: 'Taste file' },
  { key: 'experiment_arm', label: 'A/B arm' },
  { key: 'direction', label: 'Direction' },
  { key: 'area', label: 'Area' },
];

/** Each reason, and the fix it points at - the plan's table, said where it is acted on. */
const REASONS: { key: string; label: string; swatch: string; fix: string }[] = [
  { key: 'Generic', label: 'Generic', swatch: 'bg-amber-500', fix: 'look at the prompts and the taste file' },
  { key: 'AlreadyDecided', label: 'Already decided', swatch: 'bg-sky-500', fix: 'look at what Sift is given to check against' },
  { key: 'Wrong', label: 'Wrong', swatch: 'bg-red-500', fix: "look at Sift's checking" },
  { key: 'NotNow', label: 'Not now', swatch: 'bg-gray-500', fix: 'timing, not quality - nothing to fix' },
];

const VERSION_KINDS: { key: DiscoveryVersionRow['kind']; label: string }[] = [
  { key: 'taste', label: 'Taste file' },
  { key: 'sift_skill', label: 'Sift skill' },
  { key: 'scout_skill', label: 'Scout skill' },
  { key: 'recheck_skill', label: 'Recheck skill' },
  { key: 'chart_skill', label: 'Chart skill' },
];

const hours = (h: number | null | undefined) => (h === null || h === undefined ? '—' : mins(h * 60));
const money = (usd: number | null | undefined) =>
  usd === null || usd === undefined ? '—' : usd < 10 ? `$${usd.toFixed(2)}` : `$${Math.round(usd)}`;

export function Discovery({ days, project, bump }: { days: number; project: string | null; bump: number }) {
  const [by, setBy] = useState('week');
  const q = new URLSearchParams({ days: String(days), by });
  if (project) q.set('project', project);
  const { data, error } = useJson<DiscoveryView>(`/api/analytics/discovery?${q}`, bump);
  const s = data?.summary ?? null;

  return (
    <Section title="Discovery"
      meta={s ? `${n(s.discoveries)} discoveries · ${n(s.closed)} closed verdicts · ${n(s.approved)} approved` : undefined}>
      {error && <Empty>Could not read the Discovery metrics: {error}</Empty>}
      {!error && !data && <Empty>Loading…</Empty>}
      {data && (!s || !n(s.discoveries)) && (
        <Empty>
          No discovery was filed in this window, so there is nothing to measure yet. Once a discovery's
          proposals have verdicts, this says whether Discovery is worth running and whether the last
          change to its prompts, models or taste file helped.
        </Empty>
      )}
      {data && s && n(s.discoveries) > 0 && (
        <div className="space-y-4">
          {!s.enoughVerdicts && <FloorNote row={s} />}
          <Headline s={s} />
          <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-2">
            <Funnel s={s} />
            <Rejections rows={data.rejections} s={s} />
          </div>
          <Quality s={s} />
          <Split by={by} setBy={setBy} rows={data.split} />
          <Versions rows={data.versions} />
        </div>
      )}
    </Section>
  );
}

/** Said once, at the top, in words: how far from a rate worth reading the scope is. */
function FloorNote({ row }: { row: DiscoveryFunnelRow }) {
  const have = n(row.closed);
  return (
    <div className="border border-hair bg-sunk px-4 py-3 text-xs leading-relaxed text-gray-400">
      <span className="text-amber-300">Not enough verdicts yet.</span>{' '}
      {have === 0
        ? `No proposal here has a closed verdict. Rates appear once ${row.minVerdicts} have one; until then, the counts below are all there is.`
        : `${have} of the ${row.minVerdicts} closed verdicts a rate needs. The counts below are real; the rates wait, so two proposals do not read as 0% or 100%.`}
    </div>
  );
}

function Headline({ s }: { s: DiscoveryFunnelRow }) {
  const priced = n(s.discoveriesPriced);
  return (
    <div className="flex flex-wrap gap-3">
      <Stat label="Approval rate" value={s.enoughVerdicts ? pct(s.approvalRate) : '—'} muted={!s.enoughVerdicts}
        context={s.enoughVerdicts ? `${n(s.approved)} approved of ${n(s.closed)} shown and decided` : `${FEW} (${n(s.approved)} of ${n(s.closed)} so far)`} />
      <Stat label="Approved per dollar" value={s.approvedPerDollar == null ? '—' : s.approvedPerDollar.toFixed(2)}
        muted={s.approvedPerDollar == null}
        context={!s.enoughVerdicts ? FEW
          : priced === 0 ? 'no discovery here has a list price on every run'
          : `${n(s.approvedPriced)} approved for ${money(s.costUsd)}, over ${priced} priced ${priced === 1 ? 'discovery' : 'discoveries'}`} />
      <Stat label="Approved per owner-minute" value={s.approvedPerOwnerMinute == null ? '—' : s.approvedPerOwnerMinute.toFixed(2)}
        muted={s.approvedPerOwnerMinute == null}
        context={!s.enoughVerdicts ? FEW
          : !n(s.ownerMinutesTimed) ? 'no verdict time recorded yet'
          : `${n(s.approvedTimed)} approved in about ${mins(s.ownerMinutesTimed)} of the owner's time`} />
      <Stat label="New to me" value={s.enoughVerdicts ? pct(s.newToMeShare) : '—'} muted={!s.enoughVerdicts}
        context={s.enoughVerdicts
          ? `${n(s.newToMe)} of ${n(s.approved)} approved were not already on the owner's mind`
          : `${FEW} (${n(s.newToMe)} so far)`} />
      <Stat label="Time to verdict" value={hours(s.medianHoursToVerdict)} muted={s.medianHoursToVerdict == null}
        context={n(s.verdictRoundsCompleted)
          ? `median wait for a verdict, over ${n(s.verdictRoundsCompleted)} completed${s.medianOwnerMinutesPerRound != null ? ` · about ${mins(s.medianOwnerMinutesPerRound)} active each` : ''}`
          : 'no verdict has been completed yet'} />
    </div>
  );
}

/** Observations to landed, each step a count against the one before - said, not only drawn. */
function Funnel({ s }: { s: DiscoveryFunnelRow }) {
  const steps: { label: string; value: number; note: string }[] = [
    { label: 'Observations', value: n(s.observations), note: `${n(s.scoutObservations)} by scouts, the rest by rechecks` },
    { label: 'Shown', value: n(s.shown), note: `${n(s.dropped)} dropped by Sift, ${n(s.restored)} of them restored by the owner` },
    { label: 'Decided', value: n(s.closed), note: `${n(s.shown) - n(s.closed)} still waiting for a verdict or its round to close` },
    { label: 'Approved', value: n(s.approved), note: `${n(s.rejected)} rejected, ${n(s.snoozed)} snoozed` },
    { label: 'Filed', value: n(s.filed), note: `${n(s.filedTickets)} as tickets, ${n(s.filedTicketsDone)} of them done` },
    { label: 'Landed first time', value: n(s.landedFirstTime), note: n(s.goalsChecked) ? `of ${n(s.goalsChecked)} goals a recheck has judged` : 'no recheck has judged a goal yet' },
  ];
  const most = Math.max(1, ...steps.map(x => x.value));
  return (
    <Sheet>
      <div className="mb-2 text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">The funnel</div>
      <ul className="space-y-2 text-xs">
        {steps.map(x => (
          <li key={x.label} className="min-w-0">
            <div className="grid grid-cols-[minmax(0,8.5rem)_minmax(0,1fr)_3rem] items-center gap-x-3">
              <span className="min-w-0 truncate text-gray-300">{x.label}</span>
              <Meter value={x.value} max={most} swatch="bg-sky-500" />
              <span className={`text-right ${x.value ? 'text-gray-100' : 'text-gray-600'}`} style={{ fontFamily: MONO }}>{compact(x.value)}</span>
            </div>
            <div className="mt-0.5 min-w-0 text-[10.5px] leading-snug text-gray-600">{x.note}</div>
          </li>
        ))}
      </ul>
    </Sheet>
  );
}

function Rejections({ rows, s }: { rows: DiscoveryRejectionRow[]; s: DiscoveryFunnelRow }) {
  const total = n(s.rejected);
  return (
    <Sheet>
      <div className="mb-2 text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
        Rejections by reason
      </div>
      {total === 0 ? (
        <Empty>{n(s.closed) ? 'Nothing decided here was rejected.' : 'No verdicts yet, so no rejections.'}</Empty>
      ) : (
        <ul className="space-y-2 text-xs">
          {REASONS.map(r => {
            const row = rows.find(x => x.reason === r.key);
            const k = row?.rejections ?? 0;
            return (
              <li key={r.key} className="min-w-0">
                <div className="grid grid-cols-[minmax(0,8.5rem)_minmax(0,1fr)_3rem] items-center gap-x-3">
                  <span className="min-w-0 truncate text-gray-300">{r.label}</span>
                  <Meter value={k} max={Math.max(1, total)} swatch={r.swatch} />
                  <span className={`text-right ${k ? 'text-gray-100' : 'text-gray-600'}`} style={{ fontFamily: MONO }}>{k}</span>
                </div>
                <div className="mt-0.5 min-w-0 text-[10.5px] leading-snug text-gray-600">
                  {k ? `${s.enoughVerdicts ? `${pct(row?.shareOfClosed)} of decided · ` : ''}` : ''}{r.fix}
                </div>
              </li>
            );
          })}
          {rows.some(x => x.reason === 'Unstated') && (
            <li className="text-[10.5px] text-gray-600">
              {rows.find(x => x.reason === 'Unstated')?.rejections} rejected with no reason given.
            </li>
          )}
        </ul>
      )}
    </Sheet>
  );
}

/** The rest of the table of measures: each a count always, a rate only above the floor. */
function Quality({ s }: { s: DiscoveryFunnelRow }) {
  const gated = (rate: number | null | undefined) => (s.enoughVerdicts ? pct(rate) : '—');
  const visits = n(s.discoveriesWithVisits);
  return (
    <div className="flex flex-wrap gap-3">
      <Stat label="Edited before approval" value={gated(s.editedShare)} muted={!s.enoughVerdicts}
        context={`${n(s.edited)} of ${n(s.approved)} approved were rewritten${s.medianEditShare != null ? ` · those changed a median ${pct(s.medianEditShare)} of the draft` : ''}`} />
      <Stat label="Restored from dropped" value={String(n(s.restored))} muted={!n(s.restored)}
        context={n(s.dropped) ? `of ${n(s.dropped)} lines Sift dropped · ${n(s.restoredApproved)} then approved` : 'Sift has dropped nothing yet'} />
      <Stat label="Sift yield" value={s.siftYield == null ? '—' : s.siftYield.toFixed(2)} muted={s.siftYield == null}
        context={n(s.scoutObservations) ? `${n(s.siftOut)} proposals from ${n(s.scoutObservations)} scout observations · ${pct(s.siftCutShare)} of its lines cut` : 'no scout observation yet'} />
      <Stat label="Repeats" value={gated(s.repeatShare)} muted={!s.enoughVerdicts}
        context={`${n(s.repeats)} shown matched an earlier proposal, ${n(s.repeatsAcross)} of them from another discovery`} />
      <Stat label="Landed" value={s.landedRate == null ? '—' : pct(s.landedRate)} muted={s.landedRate == null}
        context={n(s.goalsChecked) ? `${n(s.landedFirstTime)} of ${n(s.goalsChecked)} approved goals met at their first recheck` : `${n(s.goals)} goals filed, none rechecked yet`} />
      <Stat label="Recheck yield" value={s.recheckYield == null ? '—' : pct(s.recheckYield)} muted={s.recheckYield == null}
        context={n(s.recheckClosed)
          ? `${n(s.recheckApproved)} of ${n(s.recheckClosed)} decided · fix-ups ${n(s.fixupsApproved)}/${n(s.fixupsClosed)} · new ${n(s.recheckNewApproved)}/${n(s.recheckNewClosed)}`
          : 'no recheck proposal decided yet'} />
      <Stat label="Found by discovery" value={String(n(s.bugsFiled))} unit="bugs" muted={!n(s.bugsFiled)}
        context={n(s.bugsFiled)
          ? `${n(s.bugsConfirmed)} traced to their cause, ${n(s.bugsFixed)} fixed · bugs it walked past need areas' code paths, which come with Recon`
          : 'no bug filed from a discovery yet'} />
      <Stat label="Coverage per discovery" value={visits ? `${compact(s.medianPages)} pages` : '—'} muted={!visits}
        context={visits
          ? `median ${compact(s.medianAreas)} areas, ${compact(s.medianRoles)} roles, ${compact(s.medianWidths)} widths${s.offRouteShare != null ? ` · ${pct(s.offRouteShare)} of visits off the route` : ''}`
          : 'no scout logged a visit yet'} />
    </div>
  );
}

/** One cut at a time, most verdicts first (the week in order); a rate only where the row has enough. */
function Split({ by, setBy, rows }: { by: string; setBy: (b: string) => void; rows: DiscoveryFunnelRow[] }) {
  const cut = CUTS.find(c => c.key === by) ?? CUTS[0];
  const most = Math.max(1, ...rows.map(r => n(r.closed)));
  return (
    <div className="min-w-0">
      <div className="mb-2 flex flex-wrap gap-1.5">
        {CUTS.map(c => <Chip key={c.key} on={by === c.key} onClick={() => setBy(c.key)}>{c.label}</Chip>)}
      </div>
      {rows.length === 0 ? <Empty>Nothing to split by {cut.label.toLowerCase()} yet.</Empty> : (
        <Sheet className="orbit-swap" key={by}>
          <ul className="space-y-2 text-xs">
            {rows.map(r => (
              <li key={r.dimValue} className="min-w-0">
                <div className="grid grid-cols-[minmax(0,1fr)_4.5rem_3rem] items-center gap-x-3 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)_6rem_3rem]">
                  <span className="min-w-0 truncate text-gray-300" style={cut.mono ? { fontFamily: MONO } : undefined}>
                    {r.dimLabel ? `${r.dimLabel} (#${r.dimValue})` : r.dimValue}
                  </span>
                  <span className="max-sm:hidden"><Meter value={n(r.approved)} max={most} swatch="bg-emerald-500" /></span>
                  <span className={`text-right ${r.enoughVerdicts ? 'text-gray-100' : 'text-gray-600'}`} style={{ fontFamily: MONO }}>
                    {r.enoughVerdicts ? pct(r.approvalRate) : 'too few'}
                  </span>
                  <span className="text-right text-gray-500" style={{ fontFamily: MONO }}>{n(r.approved)}/{n(r.closed)}</span>
                </div>
              </li>
            ))}
            <li className="pt-1 text-[10.5px] leading-snug text-gray-600">
              Columns: approval rate (once a row has {rows[0]?.minVerdicts ?? 10} decided) · approved of decided.
              {by === 'area' && ' By the area Sift gave each proposal; discovery-wide numbers (cost, time) are not split by area.'}
              {by === 'model' || by === 'effort' || by === 'provider' || by === 'skill_version'
                ? ' Proposals by the run that wrote them; cost and time by the discovery\'s sift.' : ''}
            </li>
          </ul>
        </Sheet>
      )}
    </div>
  );
}

/** Each version in the order it came, with the change from the one before when both have enough verdicts. */
function Versions({ rows }: { rows: DiscoveryVersionRow[] }) {
  const [kind, setKind] = useState<DiscoveryVersionRow['kind']>('taste');
  const shown = rows.filter(r => r.kind === kind).sort((a, b) => a.versionSeq - b.versionSeq);
  return (
    <div className="min-w-0">
      <div className="mb-2 text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
        Before and after each version
      </div>
      <div className="mb-2 flex flex-wrap gap-1.5">
        {VERSION_KINDS.map(k => <Chip key={k.key} on={kind === k.key} onClick={() => setKind(k.key)}>{k.label}</Chip>)}
      </div>
      {shown.length === 0 ? (
        <Empty>
          {kind === 'taste'
            ? 'No sift or recheck here has read a taste file yet, or none of their proposals has been shown.'
            : 'No proposal here is credited to a version of this skill yet.'}
        </Empty>
      ) : (
        <Sheet className="orbit-swap" key={kind}>
          <ul className="space-y-2 text-xs">
            {shown.map((r, i) => {
              const prev = i > 0 ? shown[i - 1] : null;
              const delta = prev && prev.enoughVerdicts && r.enoughVerdicts && prev.approvalRate != null && r.approvalRate != null
                ? r.approvalRate - prev.approvalRate : null;
              return (
                <li key={r.version} className="min-w-0">
                  <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5">
                    <span className="shrink-0 text-gray-200" style={{ fontFamily: MONO }}>{r.version}</span>
                    {r.label && <span className="min-w-0 flex-1 truncate text-gray-500">{r.label}</span>}
                    <span className={`ml-auto ${r.enoughVerdicts ? 'text-gray-100' : 'text-gray-600'}`} style={{ fontFamily: MONO }}>
                      {r.enoughVerdicts ? pct(r.approvalRate) : FEW}
                    </span>
                  </div>
                  <div className="mt-0.5 min-w-0 text-[10.5px] leading-snug text-gray-600">
                    {r.approved} approved of {r.closed} decided, over {r.discoveries} {r.discoveries === 1 ? 'discovery' : 'discoveries'}
                    {r.closed ? ` · ${r.rejectedTaste} rejected as generic or wrong` : ''}
                    {delta != null && (
                      <span className={delta >= 0 ? 'text-emerald-300' : 'text-red-300'}>
                        {` · ${delta >= 0 ? '+' : '−'}${Math.abs(Math.round(delta * 100))} points on ${prev!.version}`}
                      </span>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        </Sheet>
      )}
    </div>
  );
}
