# Configuration

Chart defaults are documented in `deploy/helm/aegisscan/values.yaml`.

The operator watches a single namespace, set via the `AEGIS_KUBE_NAMESPACE`
environment variable (defaults to `default`). The Helm chart sets this
automatically to the release namespace using the Kubernetes downward API, so
it does not need to be configured manually.

## Database

Repositories, scan executions, and findings persist in PostgreSQL via EF
Core. The connection string is read from the standard ASP.NET Core
configuration key `ConnectionStrings:Postgres` (environment variable
`ConnectionStrings__Postgres`, or `appsettings.Development.json` for local
`dotnet run`). The Helm chart wires this from a Secret you provide via
`postgresql.existingSecret` / `postgresql.secretKey`; set
`postgresql.enabled: false` to omit it entirely (for example, to inject the
connection string through `env` instead).

The schema is created via EF Core migrations (`Database.MigrateAsync()` on
startup, `src/AegisScan.Operator/Data/Migrations`). `/readyz` reflects
database reachability; `/healthz` does not, so a database outage marks the
pod not-ready instead of restarting it.

`dotnet ef migrations add` cannot run inside this project — its Roslyn
workspace packages are pinned to a version that conflicts with
`KubeOps.Cli`'s. To add a migration: copy `Data/AegisDbContext.cs` into a
throwaway classlib referencing only `Npgsql.EntityFrameworkCore.PostgreSQL`
and `Microsoft.EntityFrameworkCore.Design` (plus an
`IDesignTimeDbContextFactory<AegisDbContext>` supplying any connection
string), run `dotnet ef migrations add <Name>` there, then copy the
generated files into `Data/Migrations` here.

OIDC auth and the git-cache (repository clone/mirror) layer are on the
roadmap but not implemented yet; see `docs/architecture` for the target
design.
