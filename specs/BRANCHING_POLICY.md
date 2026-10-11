# Branching Policy — MUD (Planar Realms)

**Status:** mandatory, in effect since 2026-09-21.
**Applies to:** every person, agent, task, and automated pipeline that makes
changes to this repository — all current and all future work, without
exception.

## 1. The rule (unambiguous)

1. **All changes must be applied to branches other than `master`.**
2. **No commits to `master` are ever allowed.**
3. **No pushes to `master` are ever allowed** — direct pushes, force pushes,
   and pushes of any ref that updates `master` are all prohibited.

This rule applies to all future tasks on this repository, including work done
by automated coding agents and CI/CD jobs. A task may be completed only by
committing to a non-`master` branch; it must never need `master` access to
finish.

## 2. Workflow

1. Create a branch from the current tip of `master`. Use a descriptive name
   with a purpose prefix, e.g. `specs/initial-draft`, `feature/<topic>`,
   `fix/<topic>`.
2. Do all work, commits, and iteration on that branch.
3. The branch is then brought to the maintainer's attention (e.g. a pull
   request or a report with the branch name) for review.
4. `master` advances only by an explicit, deliberate human action (a reviewed
   merge performed by the maintainer outside of any automated task). Nothing
   in a task, agent run, or CI job may itself move `master`.

## 3. Enforcement

### CI/CD

- Enable GitHub branch protection on `master`:
  - *Restrict who can push* → no one may push directly;
  - *Require a pull request before merging* with at least one approving review.
- Add a CI gate that fails any push whose target ref is `refs/heads/master`,
  so the rule is rejected server-side even if protection settings are
  changed.
- Any new CI/CD job added to this repository must run on non-`master`
  branches and must not contain a step that commits or pushes to `master`.

### Contributor guidelines

- This policy must be referenced from the repository's contributor
  guidelines (CONTRIBUTING document / repository README link) so every
  contributor — human or automated — sees it before making changes.
- The first action when setting up any contribution workflow (fork, agent
  task, CI pipeline) for this repository is to read this file.

## 4. Verification

Compliance can be checked at any time with:

```
git log master..origin/master --oneline   # must be empty after a push attempt
git log --author / --since                # audit who committed where
```

Any commit that appears on `master` without a reviewed merge is a policy
violation and must be reported.

## 5. This document

This file lives in `specs/` on the `specs/initial-draft` branch and is
referenced from the spec index ([README.md](README.md)). It must never be
committed to `master` as part of automated work — like all spec content, it
reaches `master` only through a reviewed, deliberate merge by the maintainer.
