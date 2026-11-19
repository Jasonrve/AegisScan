namespace AegisScan.KubeOps.Data;

/// <summary>Optional demo repositories for local evaluation (AEGIS_SEED_DEMO=true). Never part of the production migrations.</summary>
public static class DemoData
{
    public static void Seed(AegisDbContext db)
    {
        if (db.Repositories.Any()) return;
        db.Repositories.AddRange(
            new RepositoryEntity { Id = "acme-web", Name = "acme-web", Provider = "github", CloneUrl = "https://github.com/acme/acme-web.git", DefaultBranch = "main" },
            new RepositoryEntity { Id = "platform-api", Name = "platform-api", Provider = "gitlab", CloneUrl = "https://gitlab.com/acme/platform-api.git", DefaultBranch = "main" },
            new RepositoryEntity { Id = "mobile-app", Name = "mobile-app", Provider = "github", CloneUrl = "https://github.com/acme/mobile-app.git", DefaultBranch = "main" });
        db.SaveChanges();
    }
}
