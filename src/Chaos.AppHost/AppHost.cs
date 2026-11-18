// Chaos online service, local development (PLAN2 §8.7, §16.2).
//
//   postgres ── chaos (database)
//      │
//      ├── chaos-migrate   `Chaos.Server migrate`, runs once and exits
//      └── chaos-server    starts after the migrator completes; http://127.0.0.1:5080
//   mailpit                SMTP sink for sign-in codes; web UI on its http endpoint
//
// Later packages add their dependencies here: MinIO and ClamAV with attachment sync (P16).

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18.7")
    .WithDataVolume("chaos-dev-postgres")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("chaos");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.34.0")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");

var smtp = mailpit.GetEndpoint("smtp");

const string ServerProject = "../Chaos.Server/Chaos.Server.csproj";

// No launch profile: the migrator has no HTTP endpoint and must not take the server's port.
var migrate = builder.AddDotnetProject("chaos-migrate", ServerProject, options => options.ExcludeLaunchProfile = true)
    .WithArgs("migrate")
    .WithReference(database)
    .WaitFor(database);

builder.AddDotnetProject("chaos-server", ServerProject)
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(migrate)
    .WithEnvironment("Smtp__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", smtp.Property(EndpointProperty.Port))
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
