All Copilot code reviews for this repository MUST adhere to the standards defined in the canonical review policy.

The full policy lives in [`REVIEW_POLICY.md`](../REVIEW_POLICY.md) at the repo root - read it in full for reviews. This file is a condensed pointer, kept in sync with it.

This repository is **pengdows.threading** (`ConvergeWait`): bounded concurrency, adaptive
throttling, retry, and convergence waiting. It is not a database/ORM library - do not apply
SQL, connection-pooling, or dialect-specific rules from other `pengdows` projects here.

---

## 1) Scope rules

* Review **only the PR diff** plus any code directly referenced by the diff.
* Do not propose unrelated refactoring, "cleanup," or cosmetic changes outside the diff.
* Be concise. Focus on P0 (Blockers) and P1 (Majors).

## 2) Severity Levels

| Level | Definition |
|-------|------------|
| **P0: Blocker** | Hard-ban violations, a task convergence guarantee (`WaitForAllAsync`/`DisposeAsync`) that can be broken, a semaphore permit leak, or a security/silent-failure issue. Must be fixed before merge. |
| **P1: Major** | Adaptive-throttling logic that doesn't actually change enforced concurrency, non-idiomatic public API changes, or poor test coverage of concurrent scenarios. |
| **P2: Minor** | Naming improvements, documentation typos, or small optimization opportunities. |

## 3) The Hard Bans (P0)

If you see these, it is a hard "DO NOT MERGE":

* **`WaitForAllAsync`/`DisposeAsync` can return before all in-scope work has converged** — including under concurrent producers or concurrent callers.
* **Semaphore permit leaks** — every acquired permit must be released or deliberately, provably withheld (reservation-debt accounting), on every code path including exceptions.
* **Missing Braces** — `if`, `else`, `while`, `for` must ALWAYS use braces `{}`.
* **One Statement per Line** — do not use `if (cond) return;` on a single line.
* **Else after Return/Throw** — if the `if` block returns or throws, do not use `else`. Just continue the method.
* **Silent failure** — a swallowed exception (including in an event/observer handler) with no log, trace, or other observable signal.
* **Record validation bypassable via `with`** — if `AdaptiveConfig`/`RetryPolicy`-style types have invariants, they must not be settable outside the validated constructor.
* **Secrets in Code** — no hardcoded keys, passwords, or connection strings.

## 4) Engineering Standards

* **TDD First** — Every bug fix or feature must have a corresponding test that reproduces the issue or verifies the feature; concurrency-affecting changes need a test with real concurrent contention, not just a single-task happy path.
* **Reported state must match enforced reality** — `_currentConcurrency`/`OnConcurrencyChanged` must reflect what the semaphore actually enforces, not an aspirational target.
* **Interface-First** — Public surface should be usable through `IConvergeWait` where an interface exists.

## 5) The Review Format

Always use this exact template for the summary:

1. **MERGE:** YES/NO
2. **Blockers (P0):** List violations or "None"
3. **Majors (P1):** List issues or "None"
4. **Minors (P2):** List suggestions or "None"
5. **Missing evidence:** List missing tests/benchmarks
6. **Concurrency impact notes:** Brief summary of any change to enforced concurrency, cancellation, or convergence ordering
7. **Minimal patch guidance:** Clear, concise instructions for the developer to fix blockers
8. **Confidence:** LOW/MEDIUM/HIGH

## 6) Philosophical Alignment

Always align with these experts:
Lampson (explicit ownership, every resource has a single clear owner), Abrash (know what the machine is actually doing - a "cheap" non-blocking primitive can be structurally unwinnable under real contention), Goetz (concurrency safety, immutability, thread-safety), Martin (small cohesive functions, single responsibility, clean contracts, dependency direction), Schneier (hostile/careless inputs, explicit boundaries, fail loudly, secrets stay secret).
