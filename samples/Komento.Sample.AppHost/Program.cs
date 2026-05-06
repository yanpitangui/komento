var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("komento-db");

var nats = builder.AddNats("nats")
    .WithJetStream();

builder.AddProject<Projects.Komento_Sample_EcommerceApi>("ecommerce-api")
    .WithReference(postgres)
    .WithReference(nats)
    .WaitFor(postgres)
    .WaitFor(nats);

builder.AddProject<Projects.Komento_Sample_AdminApi>("admin-api")
    .WithReference(postgres)
    .WithReference(nats)
    .WaitFor(postgres)
    .WaitFor(nats);

builder.Build().Run();
