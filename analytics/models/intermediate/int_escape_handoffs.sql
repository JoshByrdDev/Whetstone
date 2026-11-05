-- The two things mart_escapes counts, one row each, on the delivery track: every hand-off of a
-- ticket to review (a move to NeedsReview, whoever made it), and every escape - finished work a
-- person sent back (EscapeEvent). An escape belongs to the latest hand-off on its ticket before it,
-- and both are dated and cut by that hand-off, so a rate is escapes over the hand-offs they came from.
--
-- The run settings are the implementing run that built what was handed off: the latest
-- Implementing run on the ticket that started before the hand-off.
with moves as (
    select * from {{ ref('stg_board__phase_transitions') }}
),
escapes as (
    select * from {{ ref('stg_board__escape_events') }}
    where track = 'Delivery'
),
runs as (
    select * from {{ ref('int_runs') }}
    where phase = 'Implementing'
),

handoffs as (
    select transition_id as handoff_id, ticket_id, project_key, track, moved_at as handed_off_at
    from moves
    where to_phase = 'NeedsReview' and track = 'Delivery'
),

-- Each escape with the hand-off it came back from. One from before any recorded hand-off (history
-- older than the transition log) is dated by itself.
escape_rows as (
    select
        e.escape_id, e.ticket_id, e.project_key, e.track, e.kind, e.source, e.backfilled, e.overridden,
        h.handoff_id,
        coalesce(h.handed_off_at, e.escaped_at) as handed_off_at
    from escapes e
    left join handoffs h on h.ticket_id = e.ticket_id and h.handed_off_at <= e.escaped_at
    qualify row_number() over (partition by e.escape_id order by h.handed_off_at desc nulls last) = 1
),

rows_ as (
    select 'handoff' as row_kind, handoff_id, null::integer as escape_id, ticket_id, project_key, track,
           handed_off_at, null::varchar as kind, null::varchar as source, false as overridden
    from handoffs
    union all
    select 'escape', handoff_id, escape_id, ticket_id, project_key, track, handed_off_at, kind, source, overridden
    from escape_rows
)

select
    x.row_kind, x.handoff_id, x.escape_id, x.ticket_id, x.project_key, x.track,
    x.handed_off_at,
    x.kind, x.source,
    x.row_kind = 'handoff'                                      as is_handoff,
    x.row_kind = 'escape'                                       as is_escape,
    -- A requirement change is a ticket coming back, not something the pipeline missed. An escape
    -- nobody classified (one from before runs classified their own) is neither: it is counted
    -- apart, as unclassified, rather than guessed into a kind.
    x.row_kind = 'escape' and coalesce(x.kind, '') = 'RequirementChange' as is_requirement_change,
    x.row_kind = 'escape' and x.kind is not null and x.kind <> 'RequirementChange' as is_miss,
    x.row_kind = 'escape' and x.kind is null                    as is_unclassified,
    x.row_kind = 'escape' and coalesce(x.overridden, false)     as is_overridden,
    r.run_id as built_by_run_id, r.model, r.effort, r.provider, r.skill_version
from rows_ x
left join runs r on r.ticket_id = x.ticket_id and r.started_at <= x.handed_off_at
qualify row_number() over (
    partition by x.row_kind, coalesce(x.escape_id, x.handoff_id)
    order by r.started_at desc nulls last) = 1
