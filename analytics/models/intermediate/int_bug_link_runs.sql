-- The run that most likely wrote a bug's blamed lines (docs/bugs-and-experiments-plan.md, 1.8).
--
-- The board's "likely" tier, the same way: among the source ticket's runs whose own commits changed
-- a file the blamed commits changed (RunFilesJson, which survives a squash because it is paths, not
-- shas), the latest that started before the source was merged or Done. The board's "exact" tier
-- asks git whether a commit lies inside a run's recorded range while both still exist; that is a
-- question for the repository at read time, not for a snapshot, so this layer does not answer it and
-- says which tier it used.
with links as (
    select * from {{ ref('stg_board__bug_links') }} where source_ticket_id is not null
),
runs as (
    select * from {{ ref('stg_board__cycle_runs') }} where run_files_json is not null
),
tickets as (
    select * from {{ ref('stg_board__tickets') }}
),
moves as (
    select * from {{ ref('stg_board__phase_transitions') }}
),

-- When the source was delivered: merged by the board, else its first move to Done. The same
-- definition the board's escaped flag uses (BugTraceService.IsEscaped).
delivered as (
    select t.ticket_id, coalesce(t.merged_at, min(m.moved_at) filter (where m.to_phase = 'Done')) as delivered_at
    from tickets t left join moves m using (ticket_id)
    group by t.ticket_id, t.merged_at
),

blamed as (
    select
        link_id,
        source_ticket_id,
        list_transform(
            flatten(list_transform(from_json(commits_json, '[{"files": ["VARCHAR"]}]'), c -> coalesce(c.files, []))),
            f -> {{ norm_path('f') }}) as files
    from links
),

candidates as (
    select
        b.link_id,
        r.run_id,
        r.started_at
    from blamed b
    join runs r on r.ticket_id = b.source_ticket_id
    left join delivered d on d.ticket_id = b.source_ticket_id
    where (d.delivered_at is null or r.started_at <= d.delivered_at)
      and list_has_any(
            list_transform(from_json(r.run_files_json, '["VARCHAR"]'), f -> {{ norm_path('f') }}),
            b.files)
)

select
    l.link_id,
    d.delivered_at                                                             as source_delivered_at,
    (select c.run_id from candidates c where c.link_id = l.link_id
     order by c.started_at desc, c.run_id desc limit 1)                        as written_by_run_id,
    (select count(*) from candidates c where c.link_id = l.link_id)            as candidate_runs,
    case when exists (select 1 from candidates c where c.link_id = l.link_id)
         then 'likely' else 'none' end                                         as run_tier
from links l
left join delivered d on d.ticket_id = l.source_ticket_id
