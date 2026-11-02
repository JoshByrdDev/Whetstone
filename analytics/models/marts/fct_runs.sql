-- One worker run, for drill-down and for any question the aggregate marts do not answer yet.
select
    r.*,
    t.title as ticket_title
from {{ ref('int_runs') }} r
left join {{ ref('stg_board__tickets') }} t using (ticket_id)
