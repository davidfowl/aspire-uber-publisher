#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal class UberPublisher([ServiceKey] string name,
    IOptionsMonitor<UberPublisherOptions> options,
    ILogger<UberPublisher> logger,
    DistributedApplicationExecutionContext executionContext,
    IPublishingActivityProgressReporter progressReporter) : IDistributedApplicationPublisher
{
    public Task PublishAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        return new WorkflowGraphPublisher(progressReporter, model,executionContext, logger).BuildExecutionGraph(cancellationToken);
    }
}