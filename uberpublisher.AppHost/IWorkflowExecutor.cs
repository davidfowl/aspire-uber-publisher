public interface IWorkflowExecutor
{
    Task<IDictionary<string, string>> ExecuteAsync(WorkflowExecutionContext context);
}

public class WorkflowExecutionContext
{
    public required TextWriter OutputStream { get; init; }
    public required string NodeName { get; init; }
    public required IDictionary<string, string> RequiredInputs { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}
