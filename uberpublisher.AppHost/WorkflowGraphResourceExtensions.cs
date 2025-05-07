public static class WorkflowGraphResourceExtensions
{
    internal static IResourceBuilder<WorkflowGraphResource> AddWorkflowGraph(this IDistributedApplicationBuilder builder, string name)
    {
        var resource = new WorkflowGraphResource(name);
        if (builder.ExecutionContext.IsPublishMode)
        {
            return builder.AddResource(resource);
        }
        return builder.CreateResourceBuilder(resource);
    }
}
