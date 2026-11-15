-- A proposal that repeats an earlier one: in the same project, written before it (in this
-- discovery or another, shown or dropped), with titles that share most of their words. Sift
-- records no "same as" link, so this is a match on words - lower-cased, punctuation dropped, words
-- of three letters or more - and a pair counts when the shared words are at least half of both
-- titles' words together (Jaccard >= 0.5) and at least two. Crude on purpose: it is a signal that
-- Sift's lookup of past proposals is missing things, or that an area is worn out, not a verdict.
with p as (
    select
        p.proposal_id, p.discovery_id, d.project_key, p.created_at,
        list_distinct(list_filter(
            string_split(trim(regexp_replace(lower(coalesce(nullif(p.draft_title, ''), p.title, '')), '[^a-z0-9]+', ' ', 'g')), ' '),
            w -> length(w) >= 3)) as words
    from {{ ref('stg_board__discovery_proposals') }} p
    join {{ ref('stg_board__discovery_tickets') }} d using (discovery_id)
),
pairs as (
    select
        a.proposal_id, b.proposal_id as repeat_of_proposal_id, b.discovery_id as repeat_of_discovery_id,
        len(list_intersect(a.words, b.words)) as shared,
        len(list_intersect(a.words, b.words))
            / nullif(len(list_distinct(list_concat(a.words, b.words))), 0)::double as similarity
    from p a
    join p b
      on b.project_key = a.project_key and b.proposal_id <> a.proposal_id
     and (b.created_at < a.created_at or (b.created_at = a.created_at and b.proposal_id < a.proposal_id))
)

select proposal_id, repeat_of_proposal_id, repeat_of_discovery_id, similarity
from pairs
where shared >= 2 and similarity >= 0.5
qualify row_number() over (partition by proposal_id order by similarity desc, repeat_of_proposal_id) = 1
