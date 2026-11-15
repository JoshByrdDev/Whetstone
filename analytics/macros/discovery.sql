{#- The Discovery marts' cuts (docs/discovery-track-plan.md, "What is measured": every measure is cut
    by model, effort, provider, each phase's skill version, the taste file's version, the direction,
    the area and the A/B arm).

    `discovery_cut(relation, grain)` stacks one copy of a scoped relation per dimension, with
    `dimension`, `dim_value` and `dim_label`. `grain` says which columns the relation has:
    'proposal' rows carry their own writing run's model, effort, provider, skill version and taste
    version, and an area; 'discovery' rows carry their sift run's and its round-0 taste, and no area -
    so a discovery-wide number (cost, owner time, coverage) is simply absent from the area cut rather
    than shared out by a guess. -#}
{% macro discovery_dims(grain) -%}
    {%- set own = grain == 'proposal' -%}
    {{ return({
        'all':                 ("'all'", "null"),
        'week':                ("cast(cohort_week as varchar)", "null"),
        'model':               ("coalesce(" ~ ("model" if own else "sift_model") ~ ", 'unknown')", "null"),
        'effort':              ("coalesce(" ~ ("effort" if own else "sift_effort") ~ ", 'unknown')", "null"),
        'provider':            ("coalesce(" ~ ("provider" if own else "sift_provider") ~ ", 'unknown')", "null"),
        'skill_version':       ("coalesce(" ~ ("skill_version" if own else "sift_skill_version") ~ ", 'unknown')", "null"),
        'scout_model':         ("coalesce(scout_model, 'unknown')", "null"),
        'scout_skill_version': ("coalesce(scout_skill_version, 'unknown')", "null"),
        'chart_skill_version': ("coalesce(chart_skill_version, 'unknown')", "null"),
        'taste_version':       (("taste_version" if own else "sift_taste_version"), "null"),
        'experiment_arm':      ("coalesce(experiment_arm, 'none')", "null"),
        'direction':           ("cast(direction_lineage_id as varchar)", "direction_lineage_title"),
        'area':                (("coalesce(area, 'none')" if own else "null"), "null"),
    }) }}
{%- endmacro %}

{#- Which cuts are built: every one by default; a unit test narrows it to 'all' to keep its rows few. -#}
{% macro discovery_dim_on(name) -%}
    {{ return(var('discovery_dims', none) is none or name in var('discovery_dims')) }}
{%- endmacro %}

{% macro discovery_cut(relation, grain) -%}
    {%- set dims = {} -%}
    {%- for name, pair in discovery_dims(grain).items() if discovery_dim_on(name) -%}
        {%- do dims.update({name: pair}) -%}
    {%- endfor -%}
    {%- for name, pair in dims.items() %}
    select *, '{{ name }}' as dimension, {{ pair[0] }} as dim_value, {{ pair[1] }} as dim_label from {{ relation }}
    where {{ pair[0] }} is not null
    {% if not loop.last %}union all{% endif %}
    {%- endfor %}
{%- endmacro %}
