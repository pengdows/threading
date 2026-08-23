# REVIEW_POLICY.md (canonical)

Use this as the single source of truth for Claude/Codex/Gemini/Copilot reviews of **pengdows.threading**
(the `ConvergeWait` task-coordination library — bounded concurrency, adaptive throttling, retry,
and convergence waiting). This policy is scoped to this repository's actual domain
(concurrency/async correctness), not a generic template — do not reintroduce
database/ORM-specific rules from other pengdows projects (e.g. `pengdows.crud`) here.

---

## 1) Scope rules

* Review **only the PR diff** plus any code directly referenced by the diff.
* Do not propose large refactors unless a **P0 blocker** requires it.
* Do not propose new architectural patterns, wrappers, service layers, or abstractions unless required to resolve a P0 blocker.
* Ignore unrelated files, formatting-only changes, and generated code unless the diff changes runtime behavior or public contracts.
* Prefer smallest changes that preserve existing public contracts.

---

## 2) Review output contract

Every review must output, in this order:

1. **MERGE:** YES/NO
2. **Blockers (P0)** — must fix
3. **Majors (P1)** — should fix before merge; waivers only for confirmed false positives
4. **Minors (P2)** — optional/nits
5. **Missing evidence** — tests/profiling/threat note required
6. **Concurrency impact notes** — only if semaphore permit accounting, cancellation semantics, or disposal/convergence ordering changed
7. **Minimal patch guidance** — concrete steps, no handwaving
8. **Confidence:** HIGH / MEDIUM / LOW
   * **HIGH** — directly evidenced by diff or tests
   * **MEDIUM** — strongly indicated but needs confirmation
   * **LOW** — plausible concern, limited evidence

---

## 3) Foundational principles (inform all review decisions)

These are not checklist items — they are the lens through which every finding is evaluated.

**The Boundary Rule** — Systems fail at boundaries. Every boundary must be explicit and controlled: thread-pool admission, cancellation propagation, semaphore permit ownership, task lifetime. A change that makes a boundary implicit or uncontrolled is a defect regardless of whether it manifests immediately.

**The Lampson Rule** — Ownership must be explicit. Every resource must have a single clear owner: `SemaphoreSlim` permits, `CancellationTokenSource` instances, tracked `Task`s. Shared resources without explicit ownership eventually leak, corrupt state, or deadlock. Ask: who owns this resource? Who releases it? Can ownership escape the scope where it is controlled? Can two components believe they own it? If the answer is unclear, the design is wrong. *(Lampson, Hints for Computer System Design, 1983.)*

**The Abrash Rule** — Know what the machine is actually doing. If it looks simple and cheap, verify that it actually is. A synchronous `SemaphoreSlim.Wait(0)` "opportunistic acquire" looks cheap but can be structurally unwinnable under real backlog — `Release()` always favors an already-registered async waiter over a fresh synchronous poller. Optimization or throttling logic built on an assumption like that must be verified against real contention, not just read as plausible. Hidden costs are design defects.

**The Schneier Rule** — Assume hostile inputs at every trust boundary (caller-supplied delegates, tokens, retry/backoff configuration). Fail loudly — silent failure is a defect. An observer/event-handler exception must never be discarded with zero observability just because no logger was configured.

**The Holub/Martin Rule** — If it is hard to test, the design is wrong. Complexity is the enemy. Favor small, cohesive units whose behavior is easy to reason about and verify.

**The Simplicity Rule** — Prefer the simplest design that solves the problem correctly. Complexity must justify its existence. Solve the problem you have, not the one you imagine.

---

## 4) P0 blockers (no merge)

### A. Tests (TDD enforcement)

* Any new behavior or bug fix **must include unit tests**.
* Any change affecting `WaitForAllAsync`/`DisposeAsync` convergence guarantees, semaphore permit accounting, or cancellation propagation **must include a test that exercises real concurrent contention**, not just a single-threaded happy path.
* No skipped tests.
* **Block if** behavior changed but tests did not change.

### B. Security invariants

Block if any of these occur:

* Unvalidated input crosses a trust boundary (e.g. a caller-supplied `RetryPolicy`/`AdaptiveConfig` value that can drive an unbounded delay, a negative delay, or an invalid concurrency bound).
* A failure path is silent — swallowed exceptions, unchecked return codes, or missing error logging are defects, not merely quality issues. This includes event/observer handler exceptions: they must be surfaced (logged, traced, or otherwise observable) even when no `ILogger` was supplied.
* A boundary (semaphore permit, cancellation token, task lifetime) becomes implicit or uncontrolled.

### C. Performance evidence (Abrash rule)

* If a PR claims a throttling/scheduling improvement or touches the adaptive-concurrency hot path: require evidence that it actually changes enforced concurrency under real contention, not just that it changes reported state (`_currentConcurrency`, `OnConcurrencyChanged`). A mechanism that "reports" a change without a semaphore permit ever actually being withheld is not evidence of anything.
* Block "optimization by belief" and "it looks simple and cheap therefore it is" reasoning without verification.

### D. Public API contract safety

* `IConvergeWait`'s public surface must remain stable unless the change is explicitly intended as a breaking change.
* Concrete types (`ConvergeWait`, `AdaptiveConfig`, `RetryPolicy`) should be usable entirely through `IConvergeWait` where an interface exists; avoid leaking implementation details into the public contract.
* A `record`'s constructor validation must not be bypassable via a `with` expression — if a type has invariants, either validate in every `init` accessor or make the properties construction-only (`get;` with no `init`/`set`).

### E. Resource lifetime violations

Block if:

* A `CancellationTokenSource` is disposed while something may still be observing its token, or is never disposed on any code path.
* A `SemaphoreSlim` permit acquired by one code path is not guaranteed to be released or explicitly, deliberately withheld (reservation accounting) on every subsequent path, including exception paths.
* `WaitForAllAsync`/`DisposeAsync` can return before every unit of work it is responsible for has reached a terminal state, under concurrent producers or concurrent callers.
* `DisposeAsync` called concurrently from multiple callers behaves inconsistently (e.g. a non-owning caller returning before the actual disposal work completes).

Leaks and premature-return-before-convergence are architecture violations here, not just bugs — the entire reason this library exists is to make that guarantee actually hold.

### F. Project hard bans

* **One statement per line, always.** `if (something) break;` on one line is forbidden — it is invisible in a diff and makes debugger breakpoints ambiguous.
* **Braces are required for all control flow blocks without exception.**
* **After any scope-exiting statement — `return`, `break`, `continue`, or `throw` — do not use `else`.**
* **Parentheses must make expression intent explicit.** `(a && b) || c` is required; `a && b || c` is not acceptable.
* **No fire-and-forget `Task.Run` whose exceptions are never observed** by an owning tracked-task list, an event, or an explicit log — an unobserved faulted task is a silent failure.

### G. Metrics/observability integrity

* If behavior changes, verify that `TotalQueued`/`TotalCompleted`/`TotalFailed`/`ProgressPercent`/the `On*` events still tell the truth about what actually happened, not what was merely attempted or intended.
* A change that silently breaks one of these is equivalent to a change that breaks a query.

### H. Correctness regressions

Block if a change introduces any of the following:

* Error paths that leave `_tasks`, semaphore permit counts, or reservation-debt accounting inconsistent.
* Retries that can duplicate side effects beyond what the caller's own `RetryPolicy` explicitly requested.
* Exception handling that changes observable behavior (what ends up in `Exceptions`, what triggers `OnError`, what propagates from `WaitForAllAsync`/`DisposeAsync`) without tests.
* Logic changes that can produce incorrect results for valid, boundary, or adversarial inputs (e.g. a negative `RetryPolicy.BackoffMultiplier`, `AdaptiveConfig.MaxConcurrency` less than the constructor's `maxConcurrency`).

---

## 5) P1 majors (fix or justify with false-positive evidence)

### A. Function and method design (Holub/Martin)

* A function must do one conceptual thing. If a meaningful sub-operation obscures the main flow, extract it into a named function.
* A function must be small enough to understand without losing the control flow in mental paging.
* Parameter count should normally be fewer than four. When more are required, consider whether a parameter object, richer domain type, or decomposition is the correct resolution.
* Constructor parameter count is a class responsibility signal.
* Inheritance is a form of coupling. Flag new inheritance hierarchies where composition would serve.

### B. Behavior locality (Holub)

* Behavior must live with the structure that owns the data or invariant it manipulates (e.g. permit-reservation accounting belongs inside `ConvergeWait`, not scattered across callers).
* Flag "ask then act" patterns — code that retrieves state only to make decisions on it elsewhere.
* Objects should expose capabilities, not data. Flag getters whose primary purpose is to allow outside code to enforce invariants that should live with the owning type. Do not flag simple data carriers, value objects, DTOs, or read models solely for exposing state.

### C. Hidden cost violations (Abrash rule — extended)

Flag methods that introduce hidden work not obvious from the call site: hidden allocations, hidden blocking in async paths (a synchronous `.Wait()`/`.Result` in an async path), or a "cheap-looking" non-blocking primitive (`SemaphoreSlim.Wait(0)`) used where it is actually unlikely to succeed under real load.

Hidden cost violations are P1 unless they affect a known hot path or introduce blocking behavior in async code — in which case report them under P0 instead.

### D. Deterministic resource ownership

* Every error path must be explicitly handled and tested — assume things will go wrong.
* Never silently ignore errors.
* No long-lived `CancellationTokenSource` or `SemaphoreSlim` stored without a clear disposal owner.

### E. Move errors left

* Move errors from runtime to compile time wherever possible.
* Prefer type safety, compiler checks, and static guarantees over runtime validation when feasible.
* If a design choice converts a compile-time guarantee into a runtime check, flag it.

### F. Comments

* Code must be understandable without comments. If a comment is needed, consider renaming or decomposing first.
* A comment is only justified to explain something the code is structurally incapable of expressing — a known bug workaround, a non-obvious invariant, a race the reader would otherwise "fix" incorrectly.
* Bad comments are worse than no comments.
* Ceremonial header comments are forbidden — authorship and change history belong in source control.

---

## 6) P2 minors (nice to fix)

* Naming, local clarity, consistent style with the repo.
* Minor allocations or micro-optimizations in non-hot paths.
* Small simplifications that do not alter behavior.

---

## 7) Evidence requirements ("prove it")

### Tests

* Unit tests for behavior changes.
* Regression tests for bug fixes.
* Tests should primarily verify observable behavior (did every queued item actually converge? was a permit actually withheld or released?), not internal implementation details.
* **Concurrent/adversarial scenarios are required** for anything touching `WaitForAllAsync`, `DisposeAsync`, `QueueAsync` admission, or adaptive scaling: concurrent producers while waiting, concurrent `DisposeAsync` callers, cancellation mid-wait, real contention (not just a single task) when asserting throttling behavior.
* Test names must express intent. A well-named test is documentation that cannot go stale. `Test1` is not a test name.

### Performance

Required when a PR claims a throttling/concurrency-limiting improvement:

* Evidence that enforced concurrency actually changed (e.g. an assertion on `SemaphoreSlim.CurrentCount` or observed concurrent execution count), not just that a reporting event fired.
* Under real contention, not just an idle semaphore.

### Threat note (when applicable)

Required when touching: cancellation-token handling that crosses a trust boundary, logging of caller-supplied exception data, or any place a caller-supplied delegate's exception could otherwise be swallowed.

Threat note format (5 bullets):

* Entry points
* Trust boundaries
* Assets at risk (e.g. silent loss of queued work, unbounded delay/backoff, deadlock)
* Attacker/misuse goals (what a malicious or merely careless caller could trigger)
* Mitigations and tests

---

## 8) Waivers

* **P1 majors** may only be waived when the PR author demonstrates the finding is a **false positive** — the rule does not apply given the actual context. Deferral is not a waiver; deferred concerns require a follow-up issue.
* **P0 blockers cannot be waived.**

---

## 9) Concurrency impact notes

If a change touches semaphore permit accounting, cancellation propagation, or disposal/convergence ordering, reviewers must state:

* What the enforced concurrency ceiling is before and after the change.
* Whether `WaitForAllAsync`/`DisposeAsync` can still return before all in-scope work has reached a terminal state, under concurrent producers/callers.
* Which tests cover it, or what new test is required.

---

## 10) Reviewer personas (how to think)

* **Boundary:** Where does control transfer? Where does ownership transfer? Is every boundary explicit and controlled?
* **Ownership:** Who owns this resource (permit, token, task)? Who releases it? Can ownership escape the scope where it is controlled? Can two components believe they own it?
* **Security:** Assume hostile/careless inputs (bad `RetryPolicy`/`AdaptiveConfig` values). Insist on invariants and tests. Silence is failure.
* **Runtime reality:** What does this actually do at runtime? Are there hidden allocations, blocking calls, or a "cheap" primitive that's actually racy under load?
* **Behavior locality:** Does behavior live with the structure that owns the invariant? Are objects exposing capabilities or leaking data?
* **Design/Testability:** If it is hard to test, the design is wrong.
* **Clarity:** Code should read like an executable spec. One statement per line. Braces everywhere. Explicit parentheses. If you cannot say it in English, you cannot say it in code.

---

## Notes (repo-specific)

This policy aligns with this repo's actual mandates (see `CLAUDE.md`): TDD required for every
behavior/bug-fix/contract change, no skipped tests, and the central non-negotiable invariant
that `WaitForAllAsync`/`DisposeAsync` must guarantee genuine convergence — everything queued
actually completes before the caller continues — even under concurrent producers, concurrent
waiters/disposers, and adaptive throttling.

Influences: Abrash (measure everything, know the machine, expose hidden costs), Holub (behavior lives with the owning structure, design for testability, interfaces over concrete types), Lampson (explicit ownership, every resource has a single clear owner), Martin (small cohesive functions, single responsibility, clean contracts, dependency direction), Schneier (hostile inputs, explicit boundaries, fail loudly, secrets stay secret).

---

## 11) Enforcement Mechanisms

Compliance with this policy is enforced via:

1. **GitHub PR Template (`.github/PULL_REQUEST_TEMPLATE.md`)**: Reminds contributors of the P0 hard-bans and required standards.
2. **Copilot Instructions (`.github/copilot-instructions.md`)**: Configures GitHub Copilot Code Review to use this policy as its primary system prompt for PR analysis.
3. **Manual Peer/AI Review**: Every PR requires review against this policy, including the concurrency-specific evidence in Section 7.
