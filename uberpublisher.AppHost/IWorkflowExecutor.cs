public interface IWorkflowExecutor
{
    Task<IDictionary<string, string>> ExecuteAsync(
        IDictionary<string, string> requiredInputs,
        CancellationToken cancellationToken = default);
}
