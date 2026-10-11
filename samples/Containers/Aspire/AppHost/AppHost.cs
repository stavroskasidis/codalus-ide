var builder = DistributedApplication.CreateBuilder(args);

// A Redis container, started from the resource tree or the dashboard (the tests never pull its image).
builder.AddRedis("cache").WithExplicitStart();

// HTTP endpoints on ports Aspire picks (the projects' launch profiles name none), so runs never collide.
var api = builder.AddProject<Projects.Api>("api").WithHttpEndpoint();

// WaitFor(api) needs a container runtime: without one, Aspire 13.1 never reports the IDE-run api as ready (checkpoint m17b).
builder.AddProject<Projects.Frontend>("frontend")
    .WithHttpEndpoint()
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
