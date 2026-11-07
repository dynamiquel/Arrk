# Documentation retrieval evaluation

Run from the repository; no AI key, model download, Python packages or network calls are
required after the .NET dependencies are restored:

```bash
dotnet run --project scripts/DocumentationEvaluation -- /tmp/retrieval-results.json
```

The optional output contains every query, accepted section, rank, returned ids and local search
duration. Invalid/duplicate case ids and missing gold sections fail the run. The runner calls the
real `DocumentationCatalog.Search`, not a reimplementation of its algorithm.

## Dataset and limits

`cases.tsv` fixes the questions, reference keywords and relevant sections before ranking changes.
There are **88 curated cases**: 30 English paraphrases, 30 questions in Spanish, French, German,
Portuguese, Japanese and Chinese, 20 plugin workflows, and eight boundary questions. Two of the
boundary questions have documented refusals/limitations; six have no supported answer.
The language group translates five intents into six languages; it is not 30 independent intents.

The corpus contains all nine built-in module documents plus five realistic but **fictional**
plugin fixtures: backup schedules/recovery, playlist reconciliation, books/audiobooks, local
subtitle editing and offline copies. Overlapping vocabulary deliberately tests whether backup
retention, playlist favourites, book retention and library deletion get confused. These fixture
files live only here: they are not installed, published as product guides or offered to users.
The corpus is **14 documents, 91 sections, 54,133 characters** at this measurement.

These are diagnostic, author-labelled cases, not real user traffic, a held-out benchmark or an
independent native-speaker review. All documents are English. This tests cross-language queries
against English instructions, not localized documentation or arbitrary-language tokenization.
Accepted sections include alternate correct answers where either section explains the task;
those labels were not changed after observing the candidate results.

Two modes are measured separately:

- **Raw:** pass the complete question directly to search, like a person using Help.
- **Reference keywords:** manually authored terms in the documentation's language, preserving
  named plugins. This measures whether useful source text is retrievable with suitable terms.
  It is **not** a test of a model translating/rephrasing correctly, answering faithfully or
  completing a tool loop. No provider was called and no billed-token claim is made.

Recall@k below means cases with at least one accepted section in the first k hits, not recall
over every acceptable section. MRR@8 averages the reciprocal first relevant rank, with misses
scoring zero. The eight-hit limit is the actual production tool contract.

## Measured results — 2026-11-07

Baseline: PR commit `71d33c7` (equal term weights, no query inflection reduction).
Candidate: inverse-section-frequency weighting plus conservative `-ed`/`-ing`/plural query
prefix reduction in `DocumentationCatalog`. The source corpus, case labels and hit limit were
identical. Only lexical ranking changed; there is no embedding model or synonym table.

| Group / mode | Cases | Baseline @1 / @3 / @8 | Candidate @1 / @3 / @8 | Baseline → candidate MRR@8 |
|---|---:|---|---|---|
| English paraphrases / raw | 30 | 10 / 14 / 23 | 10 / 16 / 24 | 0.452 → 0.479 |
| English paraphrases / reference keywords | 30 | 26 / 29 / 30 | 28 / 29 / 30 | 0.923 → 0.951 |
| Six languages / raw | 30 | 0 / 1 / 8 | 0 / 1 / 8 | 0.047 → 0.047 |
| Six languages / reference keywords | 30 | 24 / 30 / 30 | 24 / 30 / 30 | 0.900 → 0.867 |
| Plugin workflows / raw | 20 | 11 / 16 / 20 | 13 / 18 / 20 | 0.703 → 0.782 |
| Plugin workflows / reference keywords | 20 | 20 / 20 / 20 | 20 / 20 / 20 | 1.000 → 1.000 |
| Documented boundary answers / raw | 2 | 2 / 2 / 2 | 2 / 2 / 2 | 1.000 → 1.000 |

Raw cross-language @8 is Spanish 1/5, French 2/5, German 2/5, Portuguese 1/5, Japanese 1/5 and
Chinese 1/5, unchanged by the lexical fix. Several hits depend on a shared plugin name; these
numbers do not demonstrate cross-language understanding.

The two original failures are now within the first three hits in this mixed corpus:

- “Where are my settings backed up?”: missing → **#1 Storage and backups**.
- “How do I stop favourites being deleted?”: #8 → **#2 Review before enabling deletion**.
  The similar playlist-favourites operation still ranks first, so the model must read the section
  and distinguish the operations rather than accept the first title blindly.

Not every query improved. “The album has gaps where some songs should be” lost its accepted
section from the top eight; “Is saving arrk.yaml enough to recover everything?” gained its answer.
Other misses include “beloved movie forever”, “starring a film”, “replacing the server”, “hard
drive” imports and “uploading after finishing”. These require meaning/context, not more copies
of the documentation or unlimited result counts. Reference-keyword controls still retrieve every
answerable case (82/82) within eight results; the language MRR decrease is reported, not hidden.

**Unsupported questions:** both versions returned some result for 4/6 raw questions with no
supported answer (and 4/6 reference controls). This is a candidate-retrieval false positive,
not a measured hallucination: lexical overlap is not proof that the documented feature exists.
The assistant must read the source, preserve negative statements and decline unsupported claims.
Do not turn a raw ranking score into a confidence/probability or an automatic write permission.

## Decision

**Do not add an embedding service or vector database in this PR.** The measurements show that
raw lexical search is *not* a multilingual semantic search engine, and that simple ranking
improvements do not close the paraphrase gap. They do not show that maintaining embeddings is
necessary for the AI workflow: suitable English keywords found every supported answer, using
the current small local catalog. The existing conversational model can be asked to translate
and extract keywords in its normal tool-planning response, without a separate translation call,
another provider, or a second maintained copy/index of the documentation.

The assistant's instruction now explicitly asks for that translation/rephrasing, preservation
of plugin names and reading the source before claiming support. **Actual model adherence is
unverified**; manual controls are an upper-bound diagnostic, not evidence this prompt solves
all languages. Help's direct raw-query limitations remain and are not presented as solved.

If multilingual natural-language Help becomes a required provider-independent feature, or a
held-out evaluation of actual assistant-generated queries still misses relevant sections, these
measured gaps justify a **local multilingual hybrid experiment**. Retain lexical matching for
exact config keys, acronyms and plugin names; compare a multilingual encoder plus lexical
ranking against the same corpus, with unseen queries and explicit unsupported-answer checks.
Measure recall, provider calls, p95 latency, memory/model download, index rebuilding after plugin
updates and data exposure before adopting it. A vector database is not required merely to rank
91 sections: local vectors could be rebuilt from the same module sources. No semantic/hybrid
accuracy or cost superiority has been measured here, so neither is claimed.
