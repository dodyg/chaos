using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sqlite;

/// <summary>
/// Work package 0 spike: prove the bundled native SQLite supports FTS5 and that
/// Unicode text survives a close/reopen cycle on a real file-backed database
/// (PLAN §15 WP0 step 3). In-memory SQLite would not prove restart behavior.
/// </summary>
public sealed class SqliteStackSpikeTests
{
    private static string NewDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Test]
    public async Task Sqlite_UnicodeAndFts5_PersistsAcrossReopen()
    {
        var root = NewDataRoot();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "chaos.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        const string arabicAndEmoji = "Call السبّاك غدًا 🛠️ about the \U0001F6A7 site";

        using (var first = new SqliteConnection(connectionString))
        {
            first.Open();

            using var journal = first.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            await journal.ExecuteScalarAsync();

            // Throws if the bundled native binary lacks FTS5.
            using var createFts = first.CreateCommand();
            createFts.CommandText = "CREATE VIRTUAL TABLE docs USING fts5(text);";
            createFts.ExecuteNonQuery();

            using var createTable = first.CreateCommand();
            createTable.CommandText = "CREATE TABLE tasks (id INTEGER PRIMARY KEY, title TEXT NOT NULL);";
            createTable.ExecuteNonQuery();

            using var insert = first.CreateCommand();
            insert.CommandText = "INSERT INTO tasks (title) VALUES ($t); INSERT INTO docs (text) VALUES ($t);";
            insert.Parameters.AddWithValue("$t", arabicAndEmoji);
            insert.ExecuteNonQuery();
        }

        using (var second = new SqliteConnection(connectionString))
        {
            second.Open();

            using var read = second.CreateCommand();
            read.CommandText = "SELECT title FROM tasks;";
            var stored = await read.ExecuteScalarAsync() as string;

            await Assert.That(stored).IsEqualTo(arabicAndEmoji);

            using var match = second.CreateCommand();
            match.CommandText = "SELECT count(*) FROM docs WHERE docs MATCH 'السبّاك';";
            var matches = Convert.ToInt64(await match.ExecuteScalarAsync());

            await Assert.That(matches).IsEqualTo(1L);
        }
    }
}
