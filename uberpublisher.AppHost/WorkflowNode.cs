public class WorkflowNode
{
    private readonly IWorkflowExecutor _executor;
    private readonly HashSet<string> _dependencies = new();
    private IDictionary<string, string> _outputs = new Dictionary<string, string>();
    private readonly Dictionary<string, HashSet<string>> _dependencyOutputs = new();

    public string Name { get; }
    public IReadOnlyDictionary<string, string> Outputs => (IReadOnlyDictionary<string, string>)_outputs;
    public IEnumerable<string> Dependencies => _dependencies;
    public IReadOnlyDictionary<string, IEnumerable<string>> DependencyOutputs =>
        _dependencyOutputs.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<string>)kvp.Value);


    internal IWorkflowExecutor Executor => _executor;

    public WorkflowNode(string name, IWorkflowExecutor executor)
    {
        Name = name;
        _executor = executor;
    }

    public void DependsOn(string nodeName) => _dependencies.Add(nodeName);

    public void RequiresOutput(string dependencyName, string outputKey)
    {
        _dependencies.Add(dependencyName);

        if (!_dependencyOutputs.ContainsKey(dependencyName))
        {
            _dependencyOutputs[dependencyName] = [];
        }

        _dependencyOutputs[dependencyName].Add(outputKey);
    }

    public IEnumerable<string> GetRequiredOutputs(string dependencyName)
    {
        if (_dependencyOutputs.TryGetValue(dependencyName, out var outputs))
        {
            return outputs;
        }

        return Enumerable.Empty<string>();
    }

    public async Task ExecuteAsync(IDictionary<string, string> inputs, CancellationToken cancellationToken = default)
    {
        _outputs = await _executor.ExecuteAsync(inputs, cancellationToken);
    }

    public override string ToString()
    {
        return _executor.ToString();
    }
}
