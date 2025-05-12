#pragma warning disable ASPIRECOMPUTE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

public class WorkflowGraphPublishingContext(
    IPublishingActivityProgressReporter progressReporter,
    DistributedApplicationModel model,
    string outputPath,
    ILogger logger)
{
    private readonly DistributedApplicationModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Dictionary<string, (string Description, string? DefaultValue)> _env = [];
    private readonly HashSet<string> _processedResources = [];

    public async Task BuildExecutionGraph(CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(Path.Combine(outputPath, "exec.txt"));
        var graph = new WorkflowGraph(writer);

        var nodeMap = new Dictionary<IResource, WorkflowNode>();

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

                if (val is ContainerImageReference cir)
                {
                    node.RequiresOutput(nodeMap[cir.Resource].Name, "image");

                    if (nodeMap[cir.Resource].Executor is ShellExecutor s)
                    {
                        s.Outputs["image"] = "";
                    }
                }

                if (val is ContainerPortReference cpr)
                {
                    node.RequiresOutput(nodeMap[cpr.Resource].Name, "port");

                    if (nodeMap[cpr.Resource].Executor is ShellExecutor s)
                    {
                        s.Outputs["port"] = "";
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
                ContainerImageReference cir => $"{nodeMap[cir.Resource].Name}.image",
                ContainerPortReference cpr => $"{nodeMap[cpr.Resource].Name}.port",
                _ => throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}")
            };

        // Helper to convert a name to an environment variable friendly name
        static string ToEnvName(string name) =>
            name.ToUpperInvariant().Replace("-", "_").Replace(".", "_").Replace(" ", "_");

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

                    return $"${envName}";
                }

                if (value is ParameterResource p)
                {
                    ProcessResource(p);

                    var envName = ToEnvName(p.Name);
                    var outputName = OutputName(p);
                    sourceToEnvMap[outputName] = envName;

                    return $"${envName}";
                }

                if (value is ContainerImageReference cir)
                {
                    ProcessResource(cir.Resource);

                    var envName = ToEnvName(cir.Resource.Name + "_" + "image");
                    var outputName = OutputName(cir);
                    sourceToEnvMap[outputName] = envName;

                    return $"${envName}";
                }

                if (value is ContainerPortReference cpr)
                {
                    ProcessResource(cpr.Resource);

                    var envName = ToEnvName(cpr.Resource.Name + "_" + "port");
                    var outputName = OutputName(cpr);
                    sourceToEnvMap[outputName] = envName;

                    return $"${envName}";
                }

                throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}");
            }

            return value switch
            {
                string s => s,
                BicepOutputReference o => ToEnvAndMap(o),
                ParameterResource p => ToEnvAndMap(p),
                ReferenceExpression re => string.Format(re.Format, re.ValueProviders.Select(v => ProcessValueToEnvExpression(v, sourceToEnvMap)).ToArray()),
                ContainerImageReference cir => ToEnvAndMap(cir),
                ContainerPortReference cpr => ToEnvAndMap(cpr),
                null => null,
                _ => throw new NotSupportedException($"Unsupported value type: {value?.GetType().Name}")
            };
        }

        AzureEnvironmentResource? azEnv = null;
        WorkflowNode ProcessAzureResource(AzureBicepResource bicepResource)
        {
            // This will force parameter resolution
            using var bt = bicepResource.GetBicepTemplateFile();

            var map = new Dictionary<string, string>();

            azEnv ??= _model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault()
                ?? throw new InvalidOperationException("Azure environment resource not found");

            object? resourceGroup = azEnv.ResourceGroupName;

            if (bicepResource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingResourceAnnotation) &&
                existingResourceAnnotation.ResourceGroup is { } rgName)
            {
                resourceGroup = rgName;
            }

            var rgEnv = ProcessValueToEnvExpression(resourceGroup, map) ??
                throw new InvalidOperationException($"Failed to get resource group for {bicepResource.Name}");

            var locationEnv = ProcessValueToEnvExpression(azEnv.Location, map) ??
                throw new InvalidOperationException($"Failed to get location for {bicepResource.Name}");

            var parameters = new StringBuilder($"deployment group create \\\n  --resource-group {rgEnv} \\\n  --location {locationEnv} \\\n  --template-file $TEMPLATE_PATH");

            bool first = true;

            if (bicepResource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.UserPrincipalId, out var userPrincipalId)
                && userPrincipalId is null)
            {
                bicepResource.Parameters[AzureBicepResource.KnownParameters.UserPrincipalId] = azEnv.PrincipalId;
            }

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
                ["TEMPLATE_PATH"] = Path.Combine(bicepResource.Name, $"{bicepResource.Name}.bicep"),
            });

            foreach (var (k, v) in map)
            {
                publishSh.InputEnvMap[k] = v;
            }

            var deployBicepNode = new WorkflowNode($"deploy {bicepResource.Name}", publishSh);

            graph.Add(deployBicepNode);

            nodeMap[bicepResource] = deployBicepNode;

            ResolveDeps(resourceGroup, deployBicepNode);

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

                var registryEndpointEnv = ProcessValueToEnvExpression(deploymentTargetAnnotation.ContainerRegistry?.Endpoint, map)
                    ?? throw new InvalidOperationException($"Failed to get registry endpoint for {projectResource.Name}");

                var dotnetPublish = new ShellExecutor(
                    "dotnet",
                    $"publish $PROJECT_PATH \\\n  -c Release \\\n  /p:PublishProfile=DefaultContainer \\\n  /p:ContainerRuntimeIdentifier=linux-x64 \\\n  /p:ContainerRegistry={registryEndpointEnv}",
                    projectDir,
                    new()
                    {
                        ["PROJECT_PATH"] = Path.GetRelativePath(outputPath, projectPath)
                    });

                foreach (var (k, v) in map)
                {
                    dotnetPublish.InputEnvMap[k] = v;
                }

                var publishContainerNode = new WorkflowNode($"push {projectResource.Name}", dotnetPublish);

                graph.Add(publishContainerNode);

                nodeMap[projectResource] = publishContainerNode;

                ResolveDeps(deploymentTargetAnnotation.ContainerRegistry?.Endpoint, publishContainerNode);

                ProcessAzureResource(b);
            }
        }

        void ProcessContainerResource(ContainerResource containerResource)
        {
            if (containerResource.GetDeploymentTargetAnnotation() is { } deploymentTargetAnnotation &&
                deploymentTargetAnnotation.DeploymentTarget is AzureBicepResource b)
            {
                if (containerResource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation))
                {
                    // Docker build
                    var dockerfilePath = buildAnnotation.DockerfilePath;
                    var dockerContext = buildAnnotation.ContextPath;

                    var map = new Dictionary<string, string>();

                    var dockerBuild = new ShellExecutor(
                        "docker",
                        $"build \\\n  -t {containerResource.Name} \\\n  --file {dockerfilePath} \\\n  {dockerContext}",
                        dockerContext,
                        []
                        );

                    var registryEndpointEnv = ProcessValueToEnvExpression(deploymentTargetAnnotation.ContainerRegistry?.Endpoint, map)
                        ?? throw new InvalidOperationException($"Failed to get registry endpoint for {containerResource.Name}");

                    var dockerPush = new ShellExecutor(
                        "docker",
                        $"push {containerResource.Name}",
                        dockerContext,
                        new()
                        {
                            ["REGISTRY_ENDPOINT"] = registryEndpointEnv
                        });

                    var publishContainerNode = new WorkflowNode($"push {containerResource.Name}", new ShellExecutor("az", "container create", "", new()));

                    graph.Add(publishContainerNode);

                    nodeMap[containerResource] = publishContainerNode;
                }

                ProcessAzureResource(b);
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

            if (resource is ContainerResource c)
            {
                ProcessContainerResource(c);
            }

            if (resource is AzureBicepResource bicepResource)
            {
                ProcessAzureResource(bicepResource);
            }

            if (resource is ParameterResource parameterResource)
            {
                var node = new WorkflowNode($"parameter {parameterResource.Name}", new ShellExecutor(ToEnvName(parameterResource.Name), "", "", []));

                graph.Add(node);

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

        await graph.ExecuteAsync(progressReporter, cancellationToken);

        var dump = graph.Dump();

        Directory.CreateDirectory(outputPath);

        File.WriteAllText(Path.Combine(outputPath, "graph.txt"), dump);
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

    public async Task<IDictionary<string, string>> ExecuteAsync(WorkflowExecutionContext context)
    {
        context.OutputStream.WriteLine($"{command} {args}");
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            context.OutputStream.WriteLine($"Working directory: {workingDirectory}");
        }

        // Console.WriteLine("Required inputs:");
        // foreach (var input in requiredInputs)
        // {
        //     Console.WriteLine($"  {input.Key}={input.Value}");
        // }

        // await Task.Delay(Random.Shared.Next(1000, 10000), cancellationToken); // Simulate async work

        // Map required inputs to environment variables
        foreach (var (key, value) in context.RequiredInputs)
        {
            if (InputEnvMap.TryGetValue(key, out var envKey))
            {
                envVariables[envKey] = $"OUTPUT('{key}')";
            }
        }

        // As we're processing paramters, we map required outputs from a specific node execution as inputs
        // any node that declares a required output.
        context.OutputStream.WriteLine("Environment variables:");
        foreach (var env in envVariables)
        {
            context.OutputStream.WriteLine($"  {env.Key}={env.Value}");
        }

        return Outputs;
    }

    public override string ToString()
    {
        return $"{command}";
    }
}