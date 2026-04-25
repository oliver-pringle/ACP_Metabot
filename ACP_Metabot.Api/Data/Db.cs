using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class Db
{
    private readonly string _connectionString;

    public Db(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Sqlite")
            ?? throw new InvalidOperationException("ConnectionStrings:Sqlite not configured");
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeSchemaAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS offerings (
                id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_address            TEXT    NOT NULL,
                agent_name               TEXT    NOT NULL,
                offering_name            TEXT    NOT NULL,
                description              TEXT    NOT NULL,
                requirement_schema_json  TEXT,
                price_usdc               REAL    NOT NULL,
                price_type               TEXT    NOT NULL,
                is_private               INTEGER NOT NULL DEFAULT 0,
                chain                    TEXT    NOT NULL,
                content_hash             TEXT    NOT NULL,
                first_seen_at            TEXT    NOT NULL,
                last_seen_at             TEXT    NOT NULL,
                UNIQUE(agent_address, offering_name)
            );

            CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);
            CREATE INDEX IF NOT EXISTS ix_offerings_last_seen   ON offerings(last_seen_at);

            CREATE TABLE IF NOT EXISTS offering_embeddings (
                offering_id     INTEGER PRIMARY KEY,
                model           TEXT    NOT NULL,
                dimension       INTEGER NOT NULL,
                embedding_blob  BLOB    NOT NULL,
                embedded_at     TEXT    NOT NULL,
                FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
            );";
        await cmd.ExecuteNonQueryAsync();
    }
}
