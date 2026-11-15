-- One visit, placed as Recon counts it (macros/recon.sql, recon_locate): its area, route pattern,
-- role and width bucket, and where the commit it ran on sits on the project's base - 'line',
-- 'merged', or 'unknown' (off the base, or not recorded), which is never read as fresh.
with v as (
    select v.*, d.project_key, d.created_at as discovery_created_at
    from {{ ref('stg_board__discovery_visits') }} v
    join {{ ref('stg_board__discovery_tickets') }} d using (discovery_id)
),
located as (
    {{ recon_locate('v') }}
),
-- A recorded commit is looked for in each repo; the repo whose line has it wins.
vc as (
    select * from {{ ref('stg_recon__visit_commits') }}
    qualify row_number() over (partition by project_key, commit_sha
                               order by case placement when 'line' then 0 when 'merged' then 1 else 2 end, repo) = 1
)
select
    l.visit_id,
    l.discovery_id,
    l.project_key,
    l.discovery_created_at,
    l.url,
    l.visited_at,
    coalesce(l.seconds, 0)                  as seconds,
    l.lease_run_id,
    l.area                                  as logged_area,
    l.signed_in_as                          as logged_role,
    l.width                                 as logged_width,
    l.recon_area                            as area,
    l.recon_area_source                     as area_source,
    l.recon_area_ordinal                    as area_ordinal,
    l.recon_pattern                         as route_pattern,
    l.recon_role                            as role,
    l.recon_role_ordinal                    as role_ordinal,
    l.recon_width                           as width,
    l.commit_sha,
    vc.repo                                 as commit_repo,
    vc.resolved_sha,
    case when vc.placement in ('line', 'merged') then vc.fp_index end as fp_index,
    coalesce(vc.placement, 'unknown')       as placement
from located l
left join vc on vc.project_key = l.project_key and vc.commit_sha = l.commit_sha
