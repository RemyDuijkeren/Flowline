// Rename this file to match your Dataverse table (e.g. account.js).
// Export functions and register them as event handlers in Dataverse
// using the IIFE name derived from the filename (e.g. ExampleJs.onLoad).

// flowline:onload account "Account"

/**
 * OnLoad event handler.
 * @param {Xrm.Events.EventContext} executionContext The execution context
 */
export function onLoad(executionContext) {
    const formContext = executionContext.getFormContext();

    const nameAttr = formContext.getAttribute("name");
    nameAttr?.addOnChange(onChangeName);

    // Create (1), Quick Create (5)
    if (formContext.ui.getFormType() === 1 || formContext.ui.getFormType() === 5) {
        nameAttr?.fireOnChange();
    }
}

/**
 * OnChange handler for Name.
 * @param {Xrm.Events.EventContext} executionContext The execution context
 */
async function onChangeName(executionContext) {
    const formContext = executionContext.getFormContext();

    // Dummy example — show the Fax field only once a name has been entered.
    const hasName = Boolean(formContext.getAttribute("name")?.getValue());
    formContext.getControl("fax")?.setVisible(hasName);
}