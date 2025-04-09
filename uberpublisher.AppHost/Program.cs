var builder = DistributedApplication.CreateBuilder(args);

builder.AddUberPublisher();

builder.AddAzureContainerAppEnvironment("cae");

var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");

var apiService = builder.AddProject<Projects.uberpublisher_ApiService>("apiservice")
    .WithHttpsHealthCheck("/health");

builder.AddProject<Projects.uberpublisher_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpsHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithReference(blobs);

builder.Build().Run();
