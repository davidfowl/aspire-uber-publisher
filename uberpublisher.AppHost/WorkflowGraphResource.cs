#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

internal class WorkflowGraphResource : Resource
{
    public WorkflowGraphResource(string name) : base(name)
    {
        Annotations.Add(new PublishingCallbackAnnotation(PublishAsync));
    }

    private Task PublishAsync(PublishingContext context)
    {
        var progressReporter = context.Services.GetRequiredService<IPublishingActivityProgressReporter>();

        return new WorkflowGraphPublishingContext(
            progressReporter,
            context.Model,
            context.ExecutionContext,
            context.OutputPath,
            context.Logger).BuildExecutionGraph(context.CancellationToken);
    }
}