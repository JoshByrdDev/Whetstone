-- Each time a discovery waited for the owner's verdict: from entering NeedsVerdict to leaving it
-- (Complete, or sent back), and the owner's active minutes on it, estimated from their own events.
--
-- The page records no time-on-page, so active minutes are read off the verdict log: one minute for
-- the first event of a sitting, and the gap to each next event of the same sitting, where a gap
-- longer than five minutes starts a new sitting rather than counting as work. It undercounts
-- reading done before the first tap, and says so on the page.
with t as (
    select * from {{ ref('stg_board__phase_transitions') }} where track = 'Discovery'
),
entered as (
    select ticket_id as discovery_id, transition_id, moved_at as entered_at,
           row_number() over (partition by ticket_id order by moved_at, transition_id) as verdict_round
    from t where to_phase = 'NeedsVerdict'
),
rounds as (
    select
        e.discovery_id, e.verdict_round, e.entered_at,
        (select min(x.moved_at) from t x
          where x.ticket_id = e.discovery_id and x.from_phase = 'NeedsVerdict'
            and x.moved_at >= e.entered_at and x.transition_id > e.transition_id) as left_at,
        (select any_value(x.to_phase order by x.moved_at) from t x
          where x.ticket_id = e.discovery_id and x.from_phase = 'NeedsVerdict'
            and x.moved_at >= e.entered_at and x.transition_id > e.transition_id) as left_to
    from entered e
),
ev as (
    select r.discovery_id, r.verdict_round, v.acted_at,
           v.acted_at - lag(v.acted_at) over (partition by r.discovery_id, r.verdict_round order by v.acted_at, v.event_id) as gap
    from rounds r
    join {{ ref('stg_board__discovery_verdict_events') }} v
      on v.discovery_id = r.discovery_id and v.acted_at >= r.entered_at
     and (r.left_at is null or v.acted_at <= r.left_at)
),
active as (
    select discovery_id, verdict_round,
           sum(case when gap is null or gap > interval 5 minute then 1.0
                    else epoch(gap) / 60.0 end) as owner_minutes,
           count(*) as owner_events
    from ev group by all
)

select
    r.discovery_id,
    r.verdict_round,
    r.entered_at,
    r.left_at,
    r.left_to,
    r.left_at is not null                                     as completed,
    case when r.left_at is not null
         then epoch(r.left_at - r.entered_at) / 3600.0 end    as hours_to_verdict,
    coalesce(a.owner_minutes, 0)                              as owner_minutes,
    coalesce(a.owner_events, 0)                               as owner_events
from rounds r
left join active a using (discovery_id, verdict_round)
