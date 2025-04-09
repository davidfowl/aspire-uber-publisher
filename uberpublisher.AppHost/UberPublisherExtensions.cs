public static class UberPublisherExtensions
{
    public static IDistributedApplicationBuilder AddUberPublisher(this IDistributedApplicationBuilder builder)
    {
#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder.AddPublisher<UberPublisher, UberPublisherOptions>("uber");
#pragma warning restore ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        return builder;
    }
}
