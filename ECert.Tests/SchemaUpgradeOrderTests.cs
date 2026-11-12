using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the startup schema-upgrade path. A database created by an older release lacks columns the
/// current EF model maps, and <c>EnsureCreated</c> never alters an existing database, so every
/// migration that loads an entity must run after the migration that creates its columns. When the
/// order is wrong the host throws at startup ("Unknown column ... in 'field list'") and the
/// deployment crash-loops. The rest of the suite only boots a fresh database, where
/// <c>EnsureCreated</c> builds the whole schema at once, so this path is otherwise untested.
/// </summary>
public class SchemaUpgradeOrderTests
{
    [Fact]
    public async Task Host_starts_against_a_database_missing_the_newest_columns()
    {
        var bootstrap = new TestAppFactory();
        var databaseName = bootstrap.DatabaseName;
        try
        {
            // Touching Services boots the host, which creates the schema and runs every migration.
            _ = bootstrap.Services;

            // Reproduce the pre-upgrade shape: strip the columns and table this release adds.
            await bootstrap.ExecuteRawAsync(@"
ALTER TABLE `Courses` DROP COLUMN `PartnerName`;
ALTER TABLE `Courses` DROP COLUMN `PartnerLogoUrl`;
ALTER TABLE `Instructors` DROP COLUMN `SignatureImageUrl`;
ALTER TABLE `CertificateDesigns` DROP COLUMN `BackgroundImageUrl`;
ALTER TABLE `Payments` DROP COLUMN `PublicId`;
ALTER TABLE `Refunds` DROP COLUMN `PublicId`;
DROP TABLE `CoursePartners`;
DROP TABLE `UserCategories`;
DROP TABLE `CustodyEntries`;
DROP TABLE `CustodyHandovers`;
DROP TABLE `FinancialAuditEntries`;");

            Assert.False(await bootstrap.ColumnExistsAsync("Courses", "PartnerName"));
            Assert.False(await bootstrap.TableExistsAsync("UserCategories"));
            Assert.False(await bootstrap.TableExistsAsync("CustodyEntries"));
        }
        finally
        {
            bootstrap.Detach(); // leave the database for the upgraded host below
            await bootstrap.DisposeAsync();
        }

        var upgraded = new TestAppFactory(databaseName);
        try
        {
            // Booting this host against the older schema is the assertion: a wrong migration order
            // throws here instead of starting.
            _ = upgraded.Services;

            Assert.True(await upgraded.ColumnExistsAsync("Courses", "PartnerName"));
            Assert.True(await upgraded.ColumnExistsAsync("Courses", "PartnerLogoUrl"));
            Assert.True(await upgraded.ColumnExistsAsync("Instructors", "SignatureImageUrl"));
            Assert.True(await upgraded.ColumnExistsAsync("CertificateDesigns", "BackgroundImageUrl"));
            Assert.True(await upgraded.ColumnExistsAsync("Payments", "PublicId"));
            Assert.True(await upgraded.ColumnExistsAsync("Refunds", "PublicId"));
            Assert.True(await upgraded.TableExistsAsync("UserCategories"));
            Assert.True(await upgraded.TableExistsAsync("CustodyEntries"));
            Assert.True(await upgraded.TableExistsAsync("CustodyHandovers"));
            Assert.True(await upgraded.TableExistsAsync("FinancialAuditEntries"));
        }
        finally
        {
            try { await upgraded.DisposeAsync(); } catch { /* host may not have started */ }
            TestAppFactory.DropDatabaseByName(databaseName);
        }
    }
}
