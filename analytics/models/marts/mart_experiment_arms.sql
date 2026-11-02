-- Each arm of each experiment: what it costs per ticket to Done, and its three guardrails as
-- tickets with an outcome (n) and how many were bad. The same numbers as the board's own
-- (ExperimentService.ArmOf), so the page and this mart can be checked against each other.
--
-- Beside them, the arm's cost over only the Done tickets that never left it (`applied_*`): a ticket
-- whose later runs ran as configured - the experiment was stopped, or an override could not be
-- honoured - is not evidence of what the arm costs.
with arms as (
    select ar.*, e.project_key
    from {{ ref('stg_board__experiment_arms') }} ar
    join {{ ref('stg_board__experiments') }} e using (experiment_id)
),
t as (
    select * from {{ ref('fct_experiment_tickets') }}
)

select
    arms.experiment_id,
    arms.project_key,
    arms.arm_key,
    arms.arm_name,
    arms.skill_variant,
    arms.model,
    arms.effort,
    arms.provider,
    count(t.ticket_id)                                                      as tickets,
    count(t.done_at)                                                        as done,
    median(t.worker_minutes) filter (where t.done_at is not null)           as median_minutes,
    median(t.fresh_tokens) filter (where t.done_at is not null)             as median_fresh_tokens,
    median(t.cache_read_tokens) filter (where t.done_at is not null)        as median_cache_read_tokens,
    count(t.done_at)                                                        as qa_failure_n,
    count(*) filter (where t.done_at is not null and t.had_qa_failure)      as qa_failure_bad,
    count(t.done_at)                                                        as rework_n,
    count(*) filter (where t.done_at is not null and t.had_rework)          as rework_bad,
    count(*) filter (where t.escape_known)                                  as escaped_n,
    count(*) filter (where t.escape_known and t.escaped_bug)                as escaped_bad,
    coalesce(sum(t.rework_runs) / nullif(sum(t.runs), 0), 0)                as rework_share,
    count(*) filter (where t.left_arm)                                      as tickets_left_arm,
    count(*) filter (where t.done_at is not null and not t.left_arm)        as applied_done,
    median(t.worker_minutes) filter (where t.done_at is not null and not t.left_arm)    as applied_median_minutes,
    median(t.fresh_tokens) filter (where t.done_at is not null and not t.left_arm)      as applied_median_fresh_tokens
from arms
left join t on t.experiment_id = arms.experiment_id and t.arm_key = arms.arm_key
group by all
