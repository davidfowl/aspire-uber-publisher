public static class UberResourceExtensions
{
    internal static IResourceBuilder<UberResource> AddUberResource(this IDistributedApplicationBuilder builder, string name)
    {
        var resource = new UberResource(name);
        if (builder.ExecutionContext.IsPublishMode)
        {
            return builder.AddResource(resource);
        }
        return builder.CreateResourceBuilder(resource);
    }
}
