{#- Tests this project needs that dbt does not ship, written here rather than pulled in as a package
    so a refresh needs nothing from the internet after the first install. -#}

{#- No two rows share these columns: the grain of a mart. -#}
{% test unique_combination(model, columns) %}
    select {{ columns | join(', ') }}, count(*) as n
    from {{ model }}
    group by all
    having count(*) > 1
{% endtest %}

{#- The parts add up to the whole, within a tolerance (seconds, minutes - whatever the columns are). -#}
{% test sums_to(model, column_name, parts, tolerance=1) %}
    select *
    from {{ model }}
    where abs(coalesce({{ column_name }}, 0) - ({% for p in parts %}coalesce({{ p }}, 0){% if not loop.last %} + {% endif %}{% endfor %})) > {{ tolerance }}
{% endtest %}

{#- A value is not below a floor. -#}
{% test at_least(model, column_name, floor=0) %}
    select * from {{ model }} where {{ column_name }} < {{ floor }}
{% endtest %}

{#- A rate is a fraction. -#}
{% test is_fraction(model, column_name) %}
    select * from {{ model }} where {{ column_name }} < 0 or {{ column_name }} > 1
{% endtest %}
