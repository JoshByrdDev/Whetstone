-- One Claude session: when it ran, where, and what its prompt says it was for.
with lines as (
    select * from {{ ref('stg_transcripts__lines') }}
),

-- An assistant message is written as one line per content block, each repeating the message's
-- usage, so tokens are counted once per message id.
usage as (
    select session_id,
           sum(input_tokens) as input_tokens, sum(output_tokens) as output_tokens,
           sum(cache_read_tokens) as cache_read_tokens, sum(cache_creation_tokens) as cache_creation_tokens
    from (
        select session_id, message_id,
               any_value(input_tokens) as input_tokens, any_value(output_tokens) as output_tokens,
               any_value(cache_read_tokens) as cache_read_tokens,
               any_value(cache_creation_tokens) as cache_creation_tokens
        from lines
        where type = 'assistant' and message_id is not null and not is_sidechain
        group by all
    )
    group by session_id
)

select
    l.session_id,
    min(l.ts)                                        as first_at,
    max(l.ts)                                        as last_at,
    datediff('millisecond', min(l.ts), max(l.ts)) / 1000.0 as span_s,
    count(*)                                         as lines,
    arg_min(l.cwd, l.ts) filter (where l.cwd is not null)               as cwd,
    max(l.cli_version)                               as cli_version,
    mode(l.model) filter (where l.model is not null and l.model not like '<%') as model,
    min(l.prompt_ticket_id)                          as prompt_ticket_id,
    min(l.prompt_phase)                              as prompt_phase,
    any_value(u.input_tokens)                        as input_tokens,
    any_value(u.output_tokens)                       as output_tokens,
    any_value(u.cache_read_tokens)                   as cache_read_tokens,
    any_value(u.cache_creation_tokens)               as cache_creation_tokens,
    any_value(c.list_cost_usd)                       as list_cost_usd
from lines l
left join usage u using (session_id)
left join {{ ref('stg_transcripts__costs') }} c using (session_id)
group by l.session_id
