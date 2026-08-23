## Summary
<!-- Describe your changes in detail -->

## Motivation
<!-- Why is this change required? What problem does it solve? -->

## Reviewer Checklist
<!-- Use this as a guide for self-review before asking for a merge -->

- [ ] **I have reviewed [REVIEW_POLICY.md](../REVIEW_POLICY.md)** and my changes comply.
- [ ] **Braces everywhere** (even single-line `if` blocks).
- [ ] **One statement per line** (no `if (x) return;`).
- [ ] **No `else` after `return/throw`**.
- [ ] **No semaphore permit leaks**: every acquired permit is released or deliberately withheld (reservation-debt accounting) on every code path, including exceptions.
- [ ] **Convergence still holds**: `WaitForAllAsync`/`DisposeAsync` cannot return before all in-scope work has reached a terminal state, even under concurrent producers or concurrent callers.
- [ ] **No silent failures**: event/observer-handler exceptions are surfaced (logged or traced), not just swallowed.
- [ ] **Record validation isn't bypassable via `with`** (if `AdaptiveConfig`/`RetryPolicy`-style types gained new invariants).
- [ ] **TDD First**: tests included to reproduce the fix or verify the feature, with a real concurrent-contention test if concurrency/cancellation/disposal behavior changed.

## Concurrency Impact Notes
<!-- Note any change to enforced concurrency, cancellation propagation, or convergence/disposal ordering -->
