namespace Flowline.Commands;

// The four named places a Flowline project addresses (CONCEPTS.md "Environment role"). Its own file
// so it's reachable from Config and Services without pulling in FlowlineCommand's command-pipeline
// machinery — the enum has no dependency on any of it.
public enum EnvironmentRole { Prod, Uat, Test, Dev }
