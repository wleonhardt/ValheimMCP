# plans/

Planning and decision records for this repository. Agents read this directory first when they pick up work.

| Path | Contents |
|---|---|
| `next-up.md` | Queue of work that is agreed but not done, and what was changed but not yet verified live |
| `open-questions.md` | Questions that need an answer from the maintainer |
| `decisions/` | One file per settled decision, numbered `NNNN-title.md` |

## Status vocabulary

- `queued`: agreed, not started.
- `in progress`: someone is working on it.
- `blocked`: waiting on something named in the entry.
- `needs live test`: implemented and unit-tested, not yet run against a real game.
- `done`: finished and verified; moves to the Done section.

## Conventions

- Plain sentences. These files are read months later by someone without the conversation.
- A decision file states the context, the choice, the reasons and what would change it.
- Update these files in the same commit as the work they describe.
