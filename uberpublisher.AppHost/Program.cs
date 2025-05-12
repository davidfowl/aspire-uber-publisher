var builder = DistributedApplication.CreateBuilder(args);

builder.AddWorkflowGraph("graph");

builder.AddAzureContainerAppEnvironment("cae");

var blobs = builder.AddAzureStorage("storage").RunAsEmulator().AddBlobs("blobs");

var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.uberpublisher_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(cache);

builder.AddProject<Projects.uberpublisher_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithReference(blobs);

builder.Build().Run();
