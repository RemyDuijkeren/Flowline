# `status`'s profile-mismatch note shows `''` instead of falling back to the user email

- **Status**: fixed — 2026-07-23.
- **Severity**: low — cosmetic, `status` command only, no functional impact (the underlying mismatch
  detection is correct; only the printed identity name was blank).
- **Found**: 2026-07-23, live, running `flowline status` after removing a duplicate PAC auth profile,
  leaving one genuinely unnamed profile active for Dev.

## Repro (pre-fix)

1. Have an unnamed PAC auth profile active for an environment `status` checks.
2. Run `flowline status`.
3. Expected: `PAC auth profile mismatch — active identity may not be 'someone@example.com'`.
4. Actual: `PAC auth profile mismatch — active identity may not be ''` — empty quotes, no identity
   named at all.

## Root cause

`StatusCommand.FormatProfileNote` (`StatusCommand.cs:41`, pre-fix) built the message with
`found.Profile.Name ?? found.Profile.User ?? "(unnamed)"`. PAC's `authprofiles_v2.json` gives an
unnamed profile `Name: ""` (empty string), not a missing/null field — confirmed live via `pac auth
list`'s blank Name column for that profile. `??` only substitutes on `null`, so `""` short-circuits the
chain before it ever reaches `User`, producing the empty-quote message.

The existing unit test for this fallback path
(`StatusCommandTests.FormatProfileNote_ProfileFoundNotActive_UnnamedProfile_FallsBackToUser`)
constructed its `PacProfile` with `Name` simply left unset (C#-default `null`), which never exercises
the empty-string shape real PAC data actually produces — so the test suite couldn't have caught this.

## Fix applied

Added a small `FirstNonBlank(string?, string?)` helper using `string.IsNullOrWhiteSpace` instead of a
bare `??` chain, so both a `null` and an empty/whitespace `Name` correctly fall through to `User`
(and then to `"(unnamed)"` only when both are blank).

Regression tests: `StatusCommandTests.cs` —
`FormatProfileNote_ProfileFoundNotActive_EmptyStringName_FallsBackToUser` (the exact live shape) and
`FormatProfileNote_ProfileFoundNotActive_EmptyStringNameAndUser_FallsBackToUnnamed`. Full suite green
after the fix (1934 passing).

## Live re-verification (post-fix)

Re-ran `flowline status` against the rebuilt/reinstalled CLI: the line now reads `PAC auth profile
mismatch — active identity may not be 'remy@automatevalue.com'`.
