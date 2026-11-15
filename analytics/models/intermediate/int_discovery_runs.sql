-- Every worker run on a discovery ticket, with the lease id its rows carry (RunId on every route,
-- visit, observation and proposal is the run's ticket lease, CycleRuns.LeaseRunId), so a row leads
-- to its run's model, effort, provider, skill version and arm by one join.
select
    r.run_id, r.lease_run_id, r.ticket_id as discovery_id, r.project_key, r.phase, r.skill,
    r.started_at, r.ended_at, r.model, r.effort, r.provider,
    r.skill_version, r.skill_version_label, r.skill_version_at,
    r.experiment_arm, r.minutes, r.tokens, r.list_cost_usd, r.outcome
from {{ ref('int_runs') }} r
join {{ ref('stg_board__discovery_tickets') }} d on d.discovery_id = r.ticket_id
