// Rename this file to match your Dataverse table (e.g. account.ts).
// Export functions and register them as event handlers in Dataverse
// using the IIFE name derived from the filename (e.g. Example.onLoad).

export function onLoad(executionContext: Xrm.Events.EventContext): void {
    const formContext = executionContext.getFormContext();
}
