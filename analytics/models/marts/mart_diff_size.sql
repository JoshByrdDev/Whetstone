-- Whether a bigger change takes longer, by bucket of lines changed on the branch once the run was
-- over. Only runs that recorded their diff; fct_runs carries each run for a scatter.
with s as (
    {{ scoped(ref('fct_runs')) }}
)

select
    window_days, project_scope, track_scope, phase, size_bucket,
    case size_bucket when '0' then 0 when '1-50' then 1 when '51-200' then 2
                     when '201-800' then 3 when '800+' then 4 end  as size_order,
    count(*)                                                       as runs,
    median(lines_changed)                                          as median_lines,
    median(files_changed)                                          as median_files,
    median(minutes)                                                as median_minutes,
    quantile_cont(minutes, 0.9)                                    as p90_minutes,
    median(tokens)                                                 as median_tokens,
    sum(minutes) / nullif(sum(lines_changed), 0) * 100             as minutes_per_100_lines,
    count(*) filter (where went_backward)                          as went_backward
from s
where size_bucket <> 'not recorded'
group by all
