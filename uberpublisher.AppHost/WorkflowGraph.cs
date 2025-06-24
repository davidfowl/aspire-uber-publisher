using Aspire.Hosting.Publishing;

public class WorkflowGraph(TextWriter textWriter)
{
    private readonly Dictionary<string, WorkflowNode> _nodes = new();

    public void Add(WorkflowNode node)
    {
        if (_nodes.ContainsKey(node.Name))
        {
            throw new InvalidOperationException($"Node {node.Name} already exists in the graph");
        }
        _nodes[node.Name] = node;
    }

#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    public async Task ExecuteAsync(IPublishingActivityProgressReporter progressReporter, CancellationToken cancellationToken = default)
    {
        ValidateGraph();

        var completed = new HashSet<string>();
        var inProgress = new HashSet<string>();
        var outputs = new Dictionary<(string NodeName, string OutputName), string>();

        while (completed.Count < _nodes.Count)
        {
            var availableNodes = _nodes
                .Where(n => !completed.Contains(n.Key) &&
                           !inProgress.Contains(n.Key) &&
                           n.Value.Dependencies.All(d => completed.Contains(d)))
                .Select(n => n.Value)
                .ToList();

            if (!availableNodes.Any() && inProgress.Count == 0)
            {
                var unresolvedNodes = _nodes
                    .Where(n => !completed.Contains(n.Key))
                    .Select(n => n.Key)
                    .ToList();

                throw new InvalidOperationException(
                    $"Deadlock detected in workflow graph. Unresolved nodes: {string.Join(", ", unresolvedNodes)}");
            }

            var step = await progressReporter.CreateStepAsync($"Executing stage", cancellationToken);

            // Parallel execution
            var tasks = availableNodes.Select(async node =>
            {
                var task = await progressReporter.CreateTaskAsync(step, $"Executing node {node.Name}", cancellationToken);

                lock (inProgress)
                {
                    inProgress.Add(node.Name);
                }

                try
                {
                    // Create a dictionary of required inputs with source node context
                    var requiredInputs = new Dictionary<string, string>();
                    foreach (var dep in node.Dependencies)
                    {
                        foreach (var output in node.GetRequiredOutputs(dep))
                        {
                            var key = (dep, output);
                            lock (outputs)
                            {
                                if (outputs.TryGetValue(key, out var value))
                                {
                                    requiredInputs[$"{dep}.{output}"] = value;
                                }
                            }
                        }
                    }
                    var context = new WorkflowExecutionContext
                    {
                        OutputStream = textWriter,
                        NodeName = node.Name,
                        RequiredInputs = requiredInputs,
                        CancellationToken = cancellationToken
                    };

                    await textWriter.WriteLineAsync($"Executing node {node.Name} with inputs: {string.Join(", ", requiredInputs.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

                    await node.ExecuteAsync(context);

                    await textWriter.WriteLineAsync($"Node {node.Name} executed successfully.");
                    await textWriter.WriteLineAsync();

                    lock (outputs)
                    {
                        foreach (var (outputName, outputValue) in node.Outputs)
                        {
                            outputs[(node.Name, outputName)] = outputValue;
                        }
                    }

                    lock (completed)
                    {
                        completed.Add(node.Name);
                    }
                }
                catch (Exception)
                {
                    // Handle exceptions
                }
                finally
                {
                    lock (inProgress)
                    {
                        inProgress.Remove(node.Name);
                    }

                    await progressReporter.CompleteTaskAsync(task, TaskCompletionState.Completed);
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            await progressReporter.CompleteStepAsync(step, completionText: "Done executing graph");
        }
    }

    private void ValidateGraph()
    {
        // Check for missing dependencies
        foreach (var node in _nodes.Values)
        {
            foreach (var dep in node.Dependencies)
            {
                if (!_nodes.ContainsKey(dep))
                {
                    throw new InvalidOperationException($"Node {node.Name} depends on missing node {dep}");
                }
            }
        }

        // Check for cycles
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var node in _nodes.Keys)
        {
            if (!visited.Contains(node))
            {
                DetectCycle(node, visited, recursionStack);
            }
        }
    }

    private void DetectCycle(string nodeName, HashSet<string> visited, HashSet<string> recursionStack)
    {
        visited.Add(nodeName);
        recursionStack.Add(nodeName);

        foreach (var dep in _nodes[nodeName].Dependencies)
        {
            if (!visited.Contains(dep))
            {
                DetectCycle(dep, visited, recursionStack);
            }
            else if (recursionStack.Contains(dep))
            {
                throw new InvalidOperationException($"Cycle detected in workflow graph involving node {nodeName}");
            }
        }

        recursionStack.Remove(nodeName);
    }

    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
        var executionLevels = GetExecutionLevels();

        foreach (var (level, nodes) in executionLevels.Select((n, i) => (i + 1, n)))
        {
            sb.AppendLine($"Stage {level}:");
            foreach (var node in nodes)
            {
                var requiredOutputs = node.Dependencies
                    .SelectMany(dep => node.GetRequiredOutputs(dep)
                    .Select(output => $"{dep}.{output}"))
                    .ToList();

                var outputInfo = requiredOutputs.Any()
                    ? $" [Requires Outputs: {string.Join(", ", requiredOutputs)}]"
                    : "";

                sb.AppendLine($"  - {node.Name} ({node}){outputInfo}");
            }
        }

        return sb.ToString();
    }

    private List<List<WorkflowNode>> GetExecutionLevels()
    {
        var levels = new List<List<WorkflowNode>>();
        var completed = new HashSet<string>();

        while (completed.Count < _nodes.Count)
        {
            var availableNodes = _nodes
                .Where(n => !completed.Contains(n.Key) &&
                            n.Value.Dependencies.All(d => completed.Contains(d)))
                .Select(n => n.Value)
                .OrderBy(n => n.Name)
                .ToList();

            if (!availableNodes.Any())
            {
                var unresolvedNodes = _nodes
                    .Where(n => !completed.Contains(n.Key))
                    .Select(n => n.Key)
                    .ToList();

                throw new InvalidOperationException(
                    $"Deadlock detected in workflow graph. Unresolved nodes: {string.Join(", ", unresolvedNodes)}");
            }

            levels.Add(availableNodes);
            foreach (var node in availableNodes)
            {
                completed.Add(node.Name);
            }
        }

        return levels;
    }

    private void DumpNode(WorkflowNode node, System.Text.StringBuilder sb, string indent, HashSet<string> visited)
    {
        if (visited.Contains(node.Name))
        {
            sb.AppendLine($"{indent}+-- {node.Name} ({node}) \\-- (Already Processed)");
            return;
        }

        sb.AppendLine($"{indent}+-- {node.Name} ({node})");
        visited.Add(node.Name);

        var dependencies = node.Dependencies.Select(d => _nodes[d]).OrderBy(n => n.Name).ToList();
        for (int i = 0; i < dependencies.Count; i++)
        {
            var dependency = dependencies[i];
            var isLast = i == dependencies.Count - 1;
            var newIndent = isLast ? indent + "    " : indent + "|   ";

            var requiredOutputs = string.Join(", ", node.GetRequiredOutputs(dependency.Name));
            var outputInfo = string.IsNullOrEmpty(requiredOutputs) ? "" : $" [Outputs: {requiredOutputs}]";

            sb.AppendLine($"{newIndent}+-- {dependency.Name} ({dependency}){outputInfo}");
            DumpNode(dependency, sb, newIndent, visited);
        }
    }
}
