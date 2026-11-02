-- A session's own running totals, as Claude Code last wrote them. List-price dollars: on a
-- subscription nothing is billed per call, so this is a relative weight, not a bill.
select
    session_id,
    max(total_cost_usd)        as list_cost_usd,
    max(api_ms) / 1000.0       as api_s,
    max(tool_ms) / 1000.0      as tool_s,
    max(duration_ms) / 1000.0  as duration_s
from {{ source('transcripts', 'transcript_costs') }}
where session_id is not null
group by session_id
