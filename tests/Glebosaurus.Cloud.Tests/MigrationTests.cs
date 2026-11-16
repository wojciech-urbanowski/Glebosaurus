using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Cloud.Tests;

public sealed class MigrationTests(CloudDatabase db)
{
    [Fact]
    public void EmbeddedMigrations_AreOrderedAndNamed()
    {
        var migrations = Migration.Embedded();

        Assert.NotEmpty(migrations);
        Assert.Equal(migrations.OrderBy(m => m.Version, StringComparer.Ordinal), migrations);
        Assert.All(migrations, m => Assert.Matches("^[0-9]{14}$", m.Version));
        Assert.Equal(migrations.Count, Directory.GetFiles(Path.Combine(db.RepositoryRoot, "supabase", "migrations"), "*.sql").Length);
    }

    [Fact]
    public async Task Reapplying_DoesNothing()
    {
        db.SkipIfUnavailable();

        var applied = await MigrationRunner.ApplyAsync(db.DataSource, Migration.Embedded(), null, CancellationToken.None);

        Assert.Empty(applied);
    }

    [Fact]
    public async Task ChangedAppliedMigration_StopsEverything()
    {
        db.SkipIfUnavailable();
        var changed = Migration.Embedded().Select((m, i) => i == 0 ? m with { Sql = m.Sql + "\n-- zmiana" } : m).ToList();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MigrationRunner.ApplyAsync(db.DataSource, changed, null, CancellationToken.None));
        Assert.Contains("nową migracją", error.Message);
    }
}
