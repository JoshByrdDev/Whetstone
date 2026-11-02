-- One transcript line that carries a time.
select
    file, session_id, ts, type, subtype, uuid, is_sidechain,
    {{ norm_path('cwd') }} as cwd,
    cli_version, model, message_id,
    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
    prompt_ticket_id, prompt_phase
from {{ source('transcripts', 'transcript_lines') }}
where session_id is not null and ts is not null
