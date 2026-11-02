-- Each version of each phase's skill and of the worker prompt, in the order they were in force,
-- with what runs under it cost and how often their work came back. The question it answers: did
-- that change make the phase better, worse, or nothing at all?
with s as (
    {{ scoped(ref('fct_runs')) }}
),
cut as (
    select *, 'skill' as kind, skill_version as version, skill_version_label as label,
           skill_version_at as version_at, skill_version_source as source
    from s where skill_version is not null
    union all
    select *, 'prompt', prompt_version, prompt_version_label, prompt_version_at, prompt_version_source
    from s where prompt_version is not null
),

agg as (
select
    window_days, project_scope, track_scope, phase, kind, version,
    any_value(label)                                                  as label,
    min(version_at)                                                   as version_at,
    -- 'recorded' once any run carried this hash itself; 'git' when it is known only from history.
    max(source)                                                       as source,
    min(started_at)                                                   as first_at,
    max(started_at)                                                   as last_at,
    count(*)                                                          as runs,
    count(distinct ticket_id)                                         as tickets,
    median(minutes)                                                   as median_minutes,
    quantile_cont(minutes, 0.9)                                       as p90_minutes,
    median(tokens)                                                    as median_tokens,
    median(num_turns)                                                 as median_turns,
    count(*) filter (where rework_class = 'rework') / count(*)        as rework_run_share,
    count(*) filter (where went_backward) / count(*)                  as sent_back_share,
    count(*) filter (where outcome <> 'Completed') / count(*)         as unfinished_share
from cut
group by all
)

select
    *,
    row_number() over (partition by window_days, project_scope, track_scope, phase, kind
                       order by coalesce(version_at, first_at), first_at, version) as version_seq
from agg
