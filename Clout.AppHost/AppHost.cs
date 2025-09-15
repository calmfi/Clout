var builder = DistributedApplication.CreateBuilder(args);

var cloutHost = builder.AddProject<Projects.Clout_Host>("clout-host");

builder.AddProject<Projects.Clout_UI>("clout-ui").WithReference(cloutHost);

builder.Build().Run();
