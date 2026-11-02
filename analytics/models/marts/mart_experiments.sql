-- One row per experiment: its stage, each guardrail's one-sided Fisher test of the variant against
-- control, and the verdict the board would suggest - the board's rules (ExperimentService.Results,
-- docs/bugs-and-experiments-plan.md 2.6 and 2.7) worked out again from the marts, as of the refresh.
--
-- The thresholds are the board's constants: a guardrail stops an experiment at p < 0.01 with at
-- least 6 variant tickets with an outcome and 4 bad ones; it passes the verdict at p >= 0.10.
{%- set stop_min_tickets = 6 %}
{%- set stop_min_bad = 4 %}
{%- set stop_p = 0.01 %}
{%- set verdict_p = 0.10 %}
with e as (
    select * from {{ ref('stg_board__experiments') }}
),
arms as (
    select * from {{ ref('mart_experiment_arms') }}
),
t as (
    select * from {{ ref('fct_experiment_tickets') }}
),

progress as (
    select
        e.experiment_id,
        count(t.ticket_id) filter (where t.arm_key = 'control')        as control_tickets,
        count(t.ticket_id) filter (where t.arm_key = 'variant')        as variant_tickets,
        -- The last ticket to reach Done, once every enrolled ticket has.
        case when count(t.ticket_id) > 0 and count(t.ticket_id) = count(t.done_at)
             then max(t.done_at) end                                   as last_done_at
    from e left join t using (experiment_id)
    group by e.experiment_id
),

-- The variant against control on each guardrail, in the board's order.
pairs as (
    select c.experiment_id, g.guardrail, g.seq,
           case g.guardrail when 'qa-failure' then v.qa_failure_n when 'rework' then v.rework_n else v.escaped_n end   as n_v,
           case g.guardrail when 'qa-failure' then v.qa_failure_bad when 'rework' then v.rework_bad else v.escaped_bad end as bad_v,
           case g.guardrail when 'qa-failure' then c.qa_failure_n when 'rework' then c.rework_n else c.escaped_n end   as n_c,
           case g.guardrail when 'qa-failure' then c.qa_failure_bad when 'rework' then c.rework_bad else c.escaped_bad end as bad_c
    from arms c
    join arms v on v.experiment_id = c.experiment_id and v.arm_key = 'variant'
    cross join (values ('qa-failure', 1), ('rework', 2), ('escaped-bug', 3)) g(guardrail, seq)
    where c.arm_key = 'control'
),

-- One-sided Fisher's exact test: with the margins fixed, the chance of the variant having at least
-- bad_v bad tickets if the arms do not differ (ExperimentService.FisherOneSided). 1 when there is
-- nothing to compare.
fisher as (
    select
        p.*,
        case when p.n_v <= 0 or p.n_c <= 0 then 1.0 else least(1.0, (
            select coalesce(sum(exp(
                       {{ lchoose('(p.bad_v + p.bad_c)', 'x') }}
                     + {{ lchoose('(p.n_v + p.n_c - p.bad_v - p.bad_c)', '(p.n_v - x)') }}
                     - {{ lchoose('(p.n_v + p.n_c)', 'p.n_v') }})), 0)
            from (select unnest(range(p.bad_v, least(p.bad_v + p.bad_c, p.n_v) + 1)) as x)
            where p.n_v - x <= p.n_v + p.n_c - p.bad_v - p.bad_c
        )) end as p
    from pairs p
),

checks as (
    select
        experiment_id,
        {%- for g, col in [('qa-failure', 'qa_failure'), ('rework', 'rework'), ('escaped-bug', 'escaped')] %}
        any_value(p) filter (where guardrail = '{{ g }}')                       as {{ col }}_p,
        bool_or(n_v >= {{ stop_min_tickets }} and bad_v >= {{ stop_min_bad }} and p < {{ stop_p }})
            filter (where guardrail = '{{ g }}')                                as {{ col }}_breaks,
        {%- endfor %}
        bool_or(n_v >= {{ stop_min_tickets }} and bad_v >= {{ stop_min_bad }} and p < {{ stop_p }}) as guardrail_broken,
        arg_min(guardrail, seq) filter (where p < {{ verdict_p }})            as failed_guardrail,
        arg_min(p, seq) filter (where p < {{ verdict_p }})                    as failed_p
    from fisher
    group by experiment_id
),

head as (
    select
        e.*,
        pr.control_tickets,
        pr.variant_tickets,
        pr.last_done_at,
        pr.last_done_at + to_days(e.wait_days)                        as verdict_from,
        ck.qa_failure_p, ck.qa_failure_breaks, ck.rework_p, ck.rework_breaks, ck.escaped_p, ck.escaped_breaks,
        coalesce(ck.guardrail_broken, false)                          as guardrail_broken,
        ck.failed_guardrail,
        ck.failed_p,
        c.median_minutes as c_minutes, v.median_minutes as v_minutes,
        c.median_fresh_tokens as c_tokens, v.median_fresh_tokens as v_tokens,
        c.arm_key is not null and v.arm_key is not null               as has_both_arms
    from e
    join progress pr using (experiment_id)
    left join checks ck using (experiment_id)
    left join arms c on c.experiment_id = e.experiment_id and c.arm_key = 'control'
    left join arms v on v.experiment_id = e.experiment_id and v.arm_key = 'variant'
)

select
    experiment_id,
    project_key,
    skill,
    name,
    hypothesis,
    status,
    case
        when status = 'Concluded' then 'concluded'
        when status = 'Stopped' then 'stopped'
        when control_tickets < min_tickets_per_arm or variant_tickets < min_tickets_per_arm then 'enrolling'
        when last_done_at is null then 'landing'
        when verdict_from > {{ as_of() }} then 'waiting'
        else 'ready'
    end                                                                as stage,
    min_tickets_per_arm,
    wait_days,
    started_at,
    stopped_at,
    stop_kind,
    stop_reason,
    concluded_at,
    winner,
    control_tickets,
    variant_tickets,
    last_done_at,
    verdict_from,
    -- Each guardrail: the one-sided p, and whether it is enough to stop the experiment. A guardrail
    -- passes the verdict at p >= {{ verdict_p }}.
    qa_failure_p, coalesce(qa_failure_breaks, false) as qa_failure_breaks,
    rework_p, coalesce(rework_breaks, false) as rework_breaks,
    escaped_p, coalesce(escaped_breaks, false) as escaped_breaks,
    guardrail_broken,
    case
        when not has_both_arms then null
        when failed_guardrail is not null then 'control'
        when c_minutes is null or v_minutes is null or c_tokens is null or v_tokens is null then null
        when v_minutes < c_minutes and v_tokens < c_tokens then 'variant'
        when not (v_minutes < c_minutes) and not (v_tokens < c_tokens) then 'control'
    end                                                                as suggestion,
    case
        when not has_both_arms then 'Both arms are needed.'
        when failed_guardrail is not null then 'The variant is worse on ' || failed_guardrail || ' (p = ' || printf('%.3f', failed_p) || ').'
        when c_minutes is null or v_minutes is null or c_tokens is null or v_tokens is null then 'No ticket of one arm has reached Done yet.'
        when v_minutes < c_minutes and v_tokens < c_tokens then 'The variant passes every guardrail and is quicker and cheaper per ticket to Done.'
        when not (v_minutes < c_minutes) and not (v_tokens < c_tokens) then 'The variant passes every guardrail but is no quicker and no cheaper per ticket to Done.'
        when v_minutes < c_minutes then 'The variant is quicker per ticket but uses more tokens.'
        else 'The variant uses fewer tokens per ticket but takes longer.'
    end                                                                as suggestion_why
from head
