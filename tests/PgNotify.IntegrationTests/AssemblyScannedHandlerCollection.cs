namespace PgNotify.IntegrationTests;

/// <summary>
/// Groups every test class whose host calls <c>AddHandlersFromAssembly</c>. That scan registers
/// <i>all</i> notification handlers in this assembly — including the private nested handlers other
/// test classes declare — so two such hosts running
/// concurrently each invoke the other's handlers, against their own container. Handlers that
/// record into static state (the only way a nested handler can report back to its test) then see
/// counts and ids that did not come from their own database. Sharing one collection makes xUnit
/// run these classes sequentially; the handlers themselves stay inert until their own test arms
/// them.
/// </summary>
[CollectionDefinition(nameof(AssemblyScannedHandlerCollection))]
public sealed class AssemblyScannedHandlerCollection;
