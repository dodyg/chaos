// Chaos online service, local development (PLAN2 §8.7, §16.2).
//
//   postgres ── chaos (database)
//      │
//      ├── chaos-migrate   `Chaos.Server migrate`, runs once and exits
//      └── chaos-server    starts after the migrator completes; https://localhost:5081 (and http://127.0.0.1:5080)
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

// Absolute from this source file: Aspire 17 resolves relative paths, and even AppHostDirectory, against the current directory.
var ServerProject = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "Chaos.Server", "Chaos.Server.csproj"));

// No launch profile: the migrator has no HTTP endpoint and must not take the server's port.
var migrate = builder.AddDotnetProject("chaos-migrate", ServerProject, options => options.ExcludeLaunchProfile = true)
    .WithArgs("migrate")
    // Without the launch profile nothing sets the environment, and outside Development the auth setup
    // insists on signing certificates, so the migrator would die before reaching the database.
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithReference(database)
    .WaitFor(database);

builder.AddDotnetProject("chaos-server", ServerProject, options => options.LaunchProfileName = "https")
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(migrate)
    .WithEnvironment("Smtp__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", smtp.Property(EndpointProperty.Port))
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();

static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
