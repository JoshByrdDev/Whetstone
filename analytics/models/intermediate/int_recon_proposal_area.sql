-- A proposal's area, the plan's rule (docs/recon-tab-plan.md, fct_discovery_proposals): the area
-- most of its observations' URLs fall in (ties to the area listed first), else Sift's own Area when
-- it names one of the project's areas, else '(unmapped)'. Only for projects that name their areas;
-- on any other, fct_discovery_proposals keeps Sift's own word.
with p as (
    select p.proposal_id, p.discovery_id, p.area as own_area, p.observation_ids, d.project_key
    from {{ ref('stg_board__discovery_proposals') }} p
    join {{ ref('stg_board__discovery_tickets') }} d using (discovery_id)
    where d.project_key in (select project_key from {{ ref('stg_recon__projects') }} where area_count > 0)
),
by_url as (
    select p.proposal_id, o.area, o.area_ordinal, count(*) as n
    from p
    join {{ ref('int_recon_observation_area') }} o
      on o.discovery_id = p.discovery_id and list_contains(p.observation_ids, o.observation_id)
    where o.area_source = 'url'
    group by p.proposal_id, o.area, o.area_ordinal
    qualify row_number() over (partition by p.proposal_id order by n desc, o.area_ordinal, o.area) = 1
)
select
    p.proposal_id,
    coalesce(u.area, a.area, '(unmapped)')                                  as area,
    case when u.area is not null then 'url' when a.area is not null then 'sift' else 'none' end as area_source
from p
left join by_url u using (proposal_id)
left join {{ ref('stg_recon__areas') }} a
  on a.project_key = p.project_key and lower(trim(a.area)) = lower(trim(p.own_area))
