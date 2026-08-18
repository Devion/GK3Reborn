# ADR 0001: Record architecture decisions

- Status: accepted
- Date: 2026-08-18

## Context

GK3Reborn reimplements a 1999 game whose data formats are only partly documented,
using a reference implementation (G-Engine) that is a behavioral oracle rather than
a design to copy. Many decisions will look arbitrary later unless the reasoning and
the rejected alternatives are written down at the time.

## Decision

Record every architecturally significant decision as a numbered file in
`docs/adr/`. A decision is significant if reversing it would require changing more
than one subsystem, if it constrains file formats or schemas, or if it commits the
project to a dependency, a platform or a legal position.

Each record states context, the decision, and consequences — including the bad ones.
Records are immutable once accepted: a change gets a new record that supersedes the
old one. When a plan document in `../Plan` is invalidated by a decision here, update
the plan and reference the ADR.

## Consequences

Slightly more ceremony per decision, in exchange for not relitigating settled
questions and for being able to explain the engine to contributors who arrive after
the reasoning has left everyone's head.
