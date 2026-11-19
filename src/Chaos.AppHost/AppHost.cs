// Chaos online service, local development (PLAN2 §8.7, §16.2).
//
//   postgres ── chaos (database)
//      │
//      ├── chaos-migrate   `Chaos.Server migrate`, runs once and exits
//      └── chaos-server    starts after the migrator completes; https://localhost:5081 (and http://127.0.0.1:5080)
//   mailpit                SMTP sink for sign-in codes; web UI on its http endpoint
//   less3                  S3-compatible object store for encrypted attachment blobs (Blob__S3__*)
//
// ClamAV joins with attachment sync (P16); until then development uses the built-in EICAR scanner.

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

// Less3 (github.com/jchristn/less3) is the S3-compatible store for encrypted attachment blobs. On first start it seeds the
// `default` bucket and the `default`/`default` access key pair, which are development-only values. Signature checks are off in
// less3/system.json, so the credentials only have to be present, not secret.
var less3 = builder.AddContainer("less3", "jchristn77/less3", "v4.1.0")
    .WithHttpEndpoint(targetPort: 8000, name: "http")
    .WithBindMount(Path.GetFullPath(Path.Combine(SourceDirectory(), "less3", "system.json")), "/app/system.json", isReadOnly: true)
    .WithVolume("chaos-dev-less3", "/less3")
    .WithHttpHealthCheck("/healthz", endpointName: "http");

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
    .WaitFor(less3)
    .WithEnvironment("Blob__S3__ServiceUrl", less3.GetEndpoint("http"))
    .WithEnvironment("Blob__S3__Bucket", "default")
    .WithEnvironment("Blob__S3__AccessKey", "default")
    .WithEnvironment("Blob__S3__SecretKey", "default")
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();

static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
