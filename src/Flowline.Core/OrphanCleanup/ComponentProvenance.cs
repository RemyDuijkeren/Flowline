namespace Flowline.Core.OrphanCleanup;

// R1: exactly three verdicts, and KTD3 makes them an explicit case rather than a nullable field —
// the neighbouring Dependents field overloads null to mean both "not applicable" and "lookup faulted",
// and R8 needs those distinguishable. Undetermined is deliberately the zero value so an entry that
// never reaches a lookup can never read as NeverInSource.
public enum ProvenanceVerdict
{
    // The lookup did not run, could not run, faulted, or could not answer with the affirmative
    // evidence NeverInSource requires. Never render or treat this as NeverInSource (KD6).
    Undetermined = 0,

    // The component was declared in the solution source and a commit removed it from there. Carries
    // that commit's identity (R2). Says nothing about whether the component still exists in any
    // environment — only that source no longer declares it.
    Declared,

    // The component's local-source identity has no removal anywhere in the reachable history, in a
    // checkout complete enough for that absence to be evidence rather than ignorance (R8).
    NeverInSource,
}

// R2: the identity of the commit that removed the component from source. Subject is arbitrary user
// text and must be escaped before it reaches the console.
public sealed record RemovalCommit(string Sha, string Author, DateTimeOffset Date, string Subject);

// Removal is non-null exactly when Verdict is Declared — the factories below are the only way to
// build one, so the two can't drift apart.
public sealed record ComponentProvenance
{
    ComponentProvenance(ProvenanceVerdict verdict, RemovalCommit? removal = null)
    {
        Verdict = verdict;
        Removal = removal;
    }

    public ProvenanceVerdict Verdict { get; }
    public RemovalCommit? Removal { get; }

    public static readonly ComponentProvenance Undetermined  = new(ProvenanceVerdict.Undetermined);
    public static readonly ComponentProvenance NeverInSource = new(ProvenanceVerdict.NeverInSource);

    public static ComponentProvenance Declared(RemovalCommit removal) =>
        new(ProvenanceVerdict.Declared, removal);

    public static ComponentProvenance Declared(string sha, string author, DateTimeOffset date, string subject) =>
        Declared(new RemovalCommit(sha, author, date, subject));
}
