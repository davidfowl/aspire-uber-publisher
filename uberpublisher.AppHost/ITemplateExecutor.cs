public interface ITemplateExecutor
{
    Task<Dictionary<string, string>> ExecuteAsync(CancellationToken cancellationToken);
}
