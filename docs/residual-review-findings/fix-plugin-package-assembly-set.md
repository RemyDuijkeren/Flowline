# Residual review findings — plugin package assembly set

Accepted, not fixed, during the code review of `fix/plugin-package-assembly-set`
(plan: `docs/plans/2026-07-30-001-fix-plugin-package-assembly-set-plan.md`).

Both concern the self-registration fallback in
`src/Flowline.Core/Plugins/PluginService.cs`. Both were raised by the adversarial
reviewer, are advisory and human-owned, and were accepted rather than built
because each hedges against a condition this work has not observed — which is
the same standard the plan itself sets for compensating for platform behaviour.

## R1 — Concurrent pushes race on the same create, and the loser gets misleading advice

**Severity:** P2 · **Confidence:** 75 · advisory

The fallback fires only after the confirm budget is exhausted, which is exactly
when a second invocation is most likely to overlap — a CI retry, a user re-running
what looked like a hang, or two pipeline jobs against one environment. If both
reach the create for the same assembly, one is rejected on name uniqueness. The
failure path treats every rejection identically and tells the user to remove the
assembly or register it by hand, when the correct remedy is simply to re-run once
the other push finishes.

**If it proves real:** on a rejected create, re-query whether the assembly now
exists under the package before failing. A concurrent creator may have just won.

**Why not now:** no concurrent-push failure has been observed, and the guard would
add a query on a path that already only runs after a full budget expiry.

## R2 — The post-registration reload carries no retry margin

**Severity:** P3 · **Confidence:** 50 · advisory

The confirm loop above it exists as defence-in-depth for slower environments. The
fallback's own follow-up — create, re-write content, reload once — has no such
margin. If plugin-type population from the re-written content were not immediate
somewhere, the single reload could still show the assembly type-less, and the
caller's guard would report it as never registered, contradicting the success line
the same run just printed.

**If it proves real:** wrap the final reload in the same bounded retry.

**Why not now:** measurement pointed the other way. On a package create both
assemblies carried `createdon` timestamps earlier than the moment the create call
returned, so registration completes inside the call rather than after it. Adding a
retry would hedge against latency the evidence says is absent.

## Also considered and skipped during the simplify pass

Parallelising the registration loop with `Task.WhenAll`. It changes error ordering,
so it is not behaviour-preserving, and it optimises a path that only runs when
several assemblies fail at once — against a platform where this repo has already
hit concurrent-write collisions.
