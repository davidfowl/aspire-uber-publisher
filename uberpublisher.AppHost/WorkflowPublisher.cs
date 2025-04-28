#pragma warning disable ASPIRECOMPUTE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

public class WorkflowGraphPublisher(IPublishingActivityProgressReporter progressReporter, DistributedApplicationModel model, DistributedApplicationExecutionContext executionContext, ILogger logger)
{
    private readonly WorkflowGraph _graph = new();
    private readonly DistributedApplicationModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Dictionary<string, (string Description, string? DefaultValue)> _env = [];
    private readonly HashSet<string> _processedResources = [];

    public async Task BuildExecutionGraph(CancellationToken cancellationToken)
    {
        var nodeMap = new Dictionary<IResource, WorkflowNode>();

        var defaultResourceGroupParameter = new ParameterResource("resourceGroup", _ => "", secret: false);

        model.Resources.Add(defaultResourceGroupParameter);

        void ResolveDeps(object? v, WorkflowNode node) =>
            Visit(v, val =>
            {
                if (val is BicepOutputReference o)
                {
                    node.RequiresOutput(nodeMap[o.Resource].Name, o.Name);

                    if (nodeMap[o.Resource].Executor is ShellExecutor s)
                    {
                        // HACK: Pretend to have a value for this required output
                        s.Outputs[o.Name] = "";
                    }
                }

                if (val is ParameterResource p)
                {
                    // Parameters are always required outputs
                    node.RequiresOutput(nodeMap[p].Name, "value");

                    if (nodeMap[p].Executor is ShellExecutor s)
                    {
                        // HACK: Pretend to have a value for this required output
                        s.Outputs["value"] = "";
                    }
                }

                if (val is IResource r)
                {
                    node.DependsOn(nodeMap[r].Name);
                }
            });

        // Returns the output name for a dependency
        string OutputName(object? value) =>
            value switch
            {
                BicepOutputReference o => $"{nodeMap[o.Resource].Name}.{o.Name}",
                ParameterResource p => $"{nodeMap[p].Name}.value",
                _ => throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}")
            };

        // Helper to convert a name to an environment variable friendly name
        static string ToEnvName(string name) =>
            "$" + name.ToUpperInvariant().Replace("-", "_").Replace(".", "_").Replace(" ", "_");

        // Convert a value to set of environment variable
        string? ProcessValueToEnvExpression(object? value, Dictionary<string, string> sourceToEnvMap)
        {
            string ToEnvAndMap(object? value)
            {
                if (value is BicepOutputReference o)
                {
                    ProcessResource(o.Resource);

                    var envName = ToEnvName(o.ValueExpression[1..^1]);
                    var outputName = OutputName(o);
                    sourceToEnvMap[outputName] = envName;

                    return envName;
                }

                if (value is ParameterResource p)
                {
                    ProcessResource(p);

                    var envName = ToEnvName(p.Name);
                    var outputName = OutputName(p);
                    sourceToEnvMap[outputName] = envName;

                    return envName;
                }

                throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}");
            }

            return value switch
            {
                string s => s,
                BicepOutputReference o => ToEnvAndMap(o),
                ParameterResource p => ToEnvAndMap(p),
                ReferenceExpression re => string.Format(re.Format, re.ValueProviders.Select(v => ProcessValueToEnvExpression(v, sourceToEnvMap)).ToArray()),
                IManifestExpressionProvider m when m.ValueExpression.EndsWith("containerImage}") => "$CONTAINER_IMAGE",
                IManifestExpressionProvider m when m.ValueExpression.EndsWith("containerPort}") => "$CONTAINER_PORT",
                null => null,
                _ => throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}")
            };
        }

        WorkflowNode ProcessAzureResource(AzureBicepResource bicepResource)
        {
            var bicepPath = bicepResource.GetBicepTemplateFile();

            var map = new Dictionary<string, string>();

            // Set the default resource group parameter if not set
            bicepResource.Scope ??= new(defaultResourceGroupParameter);

            var rgEnv = ProcessValueToEnvExpression(bicepResource.Scope.ResourceGroup, map) ??
                throw new InvalidOperationException($"Failed to get resource group for {bicepResource.Name}");

            // TODO: How do we discovery the resource group? When scope is null? 
            // This is anoter implicit dependency that we need to flow through the graph

            var parameters = new StringBuilder($"deployment group create \\\n  --resource-group {rgEnv} \\\n  --template-file $TEMPLATE_PATH");

            bool first = true;

            foreach (var (k, v) in bicepResource.Parameters)
            {
                if (first)
                {
                    first = false;
                    parameters.Append(" \\\n  --parameters");
                }

                var value = ProcessValueToEnvExpression(v, map);

                parameters.Append($" \\\n    {k}={value}");
            }

            var publishSh = new ShellExecutor("az", parameters.ToString(), "", new()
            {
                ["TEMPLATE_PATH"] = bicepPath.Path,
            });

            foreach (var (k, v) in map)
            {
                publishSh.InputEnvMap[k] = v;
            }

            var deployBicepNode = new WorkflowNode($"deploy {bicepResource.Name}", publishSh);

            _graph.Add(deployBicepNode);

            nodeMap[bicepResource] = deployBicepNode;

            ResolveDeps(bicepResource.Scope.ResourceGroup, deployBicepNode);

            foreach (var (k, v) in bicepResource.Parameters)
            {
                ResolveDeps(v, deployBicepNode);
            }

            return deployBicepNode;
        }

        void ProcessProjectResource(ProjectResource projectResource)
        {
            if (projectResource.GetDeploymentTargetAnnotation() is { } deploymentTargetAnnotation &&
                deploymentTargetAnnotation.DeploymentTarget is AzureBicepResource b)
            {
                // We don't have build parameters yet but they would be resolved here
                // We need to create 2 workflow nodes, one for the project build and another optional one for the inner deployment target resource

                var projectPath = projectResource.GetProjectMetadata().ProjectPath;
                var projectDir = Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException($"Failed to get directory name for {projectPath}");

                var map = new Dictionary<string, string>();

                var registryEndpointEnv = ProcessValueToEnvExpression(deploymentTargetAnnotation.ContainerRegistryInfo?.Endpoint, map)
                    ?? throw new InvalidOperationException($"Failed to get registry endpoint for {projectResource.Name}");

                var dotnetPublish = new ShellExecutor(
                    "dotnet",
                    "publish $PROJECT_PATH \\\n  -c Release \\\n  /p:PublishProfile=DefaultContainer \\\n  /p:ContainerRuntimeIdentifier=linux-x64 \\\n  /p:ContainerRegistry=" + registryEndpointEnv,
                    projectDir,
                    new()
                    {
                        ["PROJECT_PATH"] = projectPath
                    });

                foreach (var (k, v) in map)
                {
                    dotnetPublish.InputEnvMap[k] = v;
                }

                var publishContainerNode = new WorkflowNode($"push {projectResource.Name}", dotnetPublish);

                _graph.Add(publishContainerNode);

                nodeMap[projectResource] = publishContainerNode;

                ResolveDeps(deploymentTargetAnnotation.ContainerRegistryInfo?.Endpoint, publishContainerNode);

                var deployBicepNode = ProcessAzureResource(b);

                // The only reason to wait on the push operation is that you need the container image or port
                // We need a first class way of getting the container image name and port from the publish container node
                // Always assume there's a dependency
                deployBicepNode.DependsOn(publishContainerNode.Name);
            }
        }

        void ProcessResource(IResource resource)
        {
            if (!_processedResources.Add(resource.Name))
            {
                _logger.LogDebug("Resource {name} already processed", resource.Name);
                return;
            }

            if (resource is ProjectResource p)
            {
                ProcessProjectResource(p);
            }

            if (resource is AzureBicepResource bicepResource)
            {
                ProcessAzureResource(bicepResource);
            }

            if (resource is ParameterResource parameterResource)
            {
                var node = new WorkflowNode($"parameter {parameterResource.Name}", new ShellExecutor(ToEnvName(parameterResource.Name), "", "", []));

                _graph.Add(node);

                nodeMap[parameterResource] = node;
            }
        }

        try
        {
            foreach (var resource in _model.Resources)
            {
                if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var lastAnnotation) &&
                    lastAnnotation == ManifestPublishingCallbackAnnotation.Ignore)
                {
                    continue;
                }

                _logger.LogDebug("Creating node for resource {name}", resource.Name);

                ProcessResource(resource);
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
                envVariables[envKey] = key;
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