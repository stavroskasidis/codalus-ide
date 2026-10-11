var builder = DistributedApplication.CreateBuilder(args);

// A Redis container, started from the resource tree or the dashboard (the tests never pull its image).
builder.AddRedis("cache").WithExplicitStart();

var api = builder.AddProject<Projects.Api>("api");

builder.AddProject<Projects.Frontend>("frontend")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
