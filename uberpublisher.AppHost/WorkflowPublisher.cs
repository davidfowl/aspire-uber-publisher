using System.Text;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
public class WorkflowGraphPublisher(IPublishingActivityProgressReporter progressReporter,  DistributedApplicationModel model, DistributedApplicationExecutionContext executionContext, ILogger logger)
#pragma warning restore ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
{
    private readonly WorkflowGraph _graph = new();
    private readonly DistributedApplicationModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Dictionary<string, (string Description, string? DefaultValue)> _env = [];
    private readonly HashSet<string> _processedResources = [];

    public async Task BuildExecutionGraph(CancellationToken cancellationToken)
    {
        var nodeMap = new Dictionary<object, WorkflowNode>();

        try
        {
            foreach (var resource in _model.Resources)
            {
                if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var lastAnnotation) &&
                    lastAnnotation == ManifestPublishingCallbackAnnotation.Ignore)
                {
                    continue;
                }

                if (!_processedResources.Add(resource.Name))
                {
                    _logger.LogDebug("Resource {name} already processed", resource.Name);
                    continue;
                }


                _logger.LogDebug("Creating node for resource {name}", resource.Name);

                if (resource is ProjectResource p)
                {
                    // We don't have build parameters yet but they would be resolved here
                    // We need to create 2 workflow nodes, one for the project build and another optional one for the inner deployment target resource

                    var projectPath = p.GetProjectMetadata().ProjectPath;
                    var projectDir = Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException($"Failed to get directory name for {projectPath}");

                    var publishContainerNode = new WorkflowNode($"push {p.Name}", new ShellExecutor("dotnet", $"publish $PROJECT_PATH -c Release /p:PublishProfile=DefaultContainer /p:ContainerRuntimeIdentifier=linux-x64 /p:ContainerRegistry=$AZURE_CONTAINER_REGISTRY_ENDPOINT", projectDir, []));
                    _graph.Add(publishContainerNode);

                    // The only reason to wait on the push operation is that you need the container image or port
                    nodeMap[p] = publishContainerNode;

                    if (p.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var deploymentTargetAnnotation) &&
                       deploymentTargetAnnotation.DeploymentTarget is AzureBicepResource b)
                    {
                        var bicepPath = b.GetBicepTemplateFile();
                        var parameters = new StringBuilder($"deployment group create --resource-group $RG --template-file $TEMPLATE_PATH");
                        var map = new Dictionary<string, string>();
                        bool first = true;

                        foreach (var (k, v) in b.Parameters)
                        {
                            if (first)
                            {
                                first = false;
                                parameters.Append(" --parameters ");
                            }

                            if (v is string s)
                            {
                                parameters.Append($"{k}={s} ");
                            }
                            else if (v is BicepOutputReference o)
                            {
                                var name = $"{o.Resource.Name}_{o.Name}".ToUpperInvariant();
                                parameters.Append($"{k}={name} ");

                                map[$"deploy {o.Resource.Name}.{o.Name}"] = name;
                            }
                            else if (v is ParameterResource pp)
                            {
                                parameters.Append($"{k}=${pp.Name} ");
                            }
                        }

                        var publishSh = new ShellExecutor("az", parameters.ToString(), "", new()
                        {
                            ["TEMPLATE_PATH"] = bicepPath.Path
                        });

                        foreach (var (k, v) in map)
                        {
                            publishSh.InputEnvMap[k] = v;
                        }

                        var publishBicepNode = new WorkflowNode($"deploy {b.Name}", publishSh);
                        _graph.Add(publishBicepNode);

                        nodeMap[b] = publishBicepNode;

                        // We need a first class way of getting the container image name and port from the publish container node
                        // Always assume there's a dependency
                        publishBicepNode.DependsOn(publishContainerNode.Name);
                    }
                }

                if (resource is AzureBicepResource bicepResource)
                {
                    var bicepPath = bicepResource.GetBicepTemplateFile();

                    var parameters = new StringBuilder("deployment group create --resource-group $RG --template-file $TEMPLATE_PATH");
                    var map = new Dictionary<string, string>();
                    bool first = true;

                    foreach (var (k, v) in bicepResource.Parameters)
                    {
                        if (first)
                        {
                            first = false;
                            parameters.Append(" --parameters ");
                        }

                        if (v is string s)
                        {
                            parameters.Append($"{k}={s} ");
                        }
                        else if (v is BicepOutputReference o)
                        {
                            var n = nodeMap[o.Resource];

                            var name = $"{o.Resource.Name}_{o.Name}".ToUpperInvariant();
                            parameters.Append($"{k}={name} ");

                            // HACK: this is a workaround for the fact that we don't have a way to get the output name from the resource
                            map[$"{n.Name}.{o.Name}"] = name;
                        }
                        else if (v is ParameterResource pp)
                        {
                            parameters.Append($"{k}=${pp.Name} ");
                        }
                    }

                    var publishSh = new ShellExecutor("az", parameters.ToString(), "", new()
                    {
                        ["TEMPLATE_PATH"] = bicepPath.Path
                    });

                    foreach (var (k, v) in map)
                    {
                        publishSh.InputEnvMap[k] = v;
                    }

                    var publishBicepNode = new WorkflowNode($"deploy {bicepResource.Name}", publishSh);

                    _graph.Add(publishBicepNode);

                    nodeMap[bicepResource] = publishBicepNode;
                }
            }
            // HACK!
            var aca = _model.Resources.OfType<AzureContainerAppEnvironmentResource>().FirstOrDefault();
            BicepOutputReference? acr = null;

            if (aca is not null)
            {
                acr = new BicepOutputReference("AZURE_CONTAINER_REGISTRY_ENDPOINT", aca);
            }

            // Second pass to resolve dependencies
            foreach (var resource in _model.Resources)
            {
                if (!nodeMap.TryGetValue(resource, out var node))
                {
                    continue;
                }

                // Magic ACR reference (this should be in the app model)
                if (acr is not null && resource is ProjectResource p)
                {
                    node.RequiresOutput(nodeMap[acr.Resource].Name, acr.Name);
                }

                if (resource is AzureBicepResource bicepResource)
                {
                    foreach (var (k, v) in bicepResource.Parameters)
                    {
                        Visit(v, val =>
                        {
                            if (val is BicepOutputReference o)
                            {
                                node.RequiresOutput(nodeMap[o.Resource].Name, o.Name);

                                (nodeMap[o.Resource].Executor as ShellExecutor).Outputs[o.Name] = "";
                            }

                            if (val is IResource r)
                            {
                                node.DependsOn(nodeMap[r].Name);
                            }
                        });
                    }
                }

                if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var deploymentTargetAnnotation) &&
                       deploymentTargetAnnotation.DeploymentTarget is AzureBicepResource b && nodeMap.TryGetValue(b, out var bNode))
                {
                    foreach (var (k, v) in b.Parameters)
                    {
                        Visit(v, val =>
                        {
                            if (val is BicepOutputReference o)
                            {
                                bNode.RequiresOutput(nodeMap[o.Resource].Name, o.Name);

                                // HACK:
                                (nodeMap[o.Resource].Executor as ShellExecutor).Outputs[o.Name] = "";
                            }

                            if (val is ParameterResource p)
                            {
                                node.RequiresOutput(p.Name, "");
                            }

                            if (val is IResource r)
                            {
                                bNode.DependsOn(nodeMap[r].Name);
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build execution graph");
            throw;
        }

        await _graph.ExecuteAsync(progressReporter, cancellationToken);
        
        var dump = _graph.Dump();

        _logger.LogInformation("Execution graph dump:\n{dump}", dump);
    }

    private static void Visit(object? value, Action<object> visitor) =>
        Visit(value, visitor, []);

    private static void Visit(object? value, Action<object> visitor, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            return;
        }

        visitor(value);

        if (value is IValueWithReferences vwr)
        {
            foreach (var reference in vwr.References)
            {
                Visit(reference, visitor, visited);
            }
        }
    }
}

internal class ShellExecutor(string command,
                             string args,
                             string workingDirectory,
                             Dictionary<string, string> envVariables) : IWorkflowExecutor
{
    public Dictionary<string, string> InputEnvMap { get; } = [];

    public Dictionary<string, string> Outputs { get; } = [];

    public async Task<IDictionary<string, string>> ExecuteAsync(IDictionary<string, string> requiredInputs, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Would execute command: {command} {args}");
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            Console.WriteLine($"Working directory: {workingDirectory}");
        }

        // Console.WriteLine("Required inputs:");
        // foreach (var input in requiredInputs)
        // {
        //     Console.WriteLine($"  {input.Key}={input.Value}");
        // }

        // await Task.Delay(Random.Shared.Next(1000, 10000), cancellationToken); // Simulate async work

        // Map required inputs to environment variables
        foreach (var (key, value) in requiredInputs)
        {
            if (InputEnvMap.TryGetValue(key, out var envKey))
            {
                envVariables[envKey] = value;
            }
        }

        // As we're processing paramters, we map required outputs from a specific node execution as inputs
        // any node that declares a required output.
        Console.WriteLine("Environment variables:");
        foreach (var env in envVariables)
        {
            Console.WriteLine($"  {env.Key}={env.Value}");
        }

        return Outputs;
    }

    public override string ToString()
    {
        return $"{command}";
    }
}