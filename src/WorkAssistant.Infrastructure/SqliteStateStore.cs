using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Infrastructure
{
    /// <summary>Versioned, transactional state. JSON remains a migration/export format, not the live database.</summary>
    internal sealed class SqliteStateStore
    {
        private readonly string _path;
        public SqliteStateStore(string root)
        {
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, "state.sqlite3");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) > 1)
                throw new InvalidOperationException("状态库来自较新版本，禁止降级写入。请恢复对应版本或升级前备份。 ");
            command.CommandText = "CREATE TABLE IF NOT EXISTS documents(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id));" +
                "CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,task TEXT NOT NULL,event_id TEXT NOT NULL UNIQUE,payload TEXT NOT NULL);" +
                "CREATE INDEX IF NOT EXISTS events_task ON events(task,sequence); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
            ImportLegacy(root);
        }
        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder {
                DataSource = _path, Version = 3, DefaultTimeout = 10, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        public void Put(string kind, string id, string payload)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Put(connection, transaction, kind, id, payload);
            transaction.Commit();
        }
        private static void Put(SQLiteConnection c, SQLiteTransaction tx, string kind, string id, string payload)
        {
            using var command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT OR REPLACE INTO documents(kind,id,payload) VALUES(@kind,@id,@payload)";
            command.Parameters.AddWithValue("@kind", kind); command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@payload", payload); command.ExecuteNonQuery();
        }
        public string? Get(string kind, string id)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = "SELECT payload FROM documents WHERE kind=@kind AND id=@id";
            command.Parameters.AddWithValue("@kind", kind); command.Parameters.AddWithValue("@id", id);
            return command.ExecuteScalar() as string;
        }
        public List<string> List(string kind)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = "SELECT payload FROM documents WHERE kind=@kind ORDER BY id";
            command.Parameters.AddWithValue("@kind", kind);
            using var reader = command.ExecuteReader(); var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values;
        }
        public void Append(string task, string id, string payload)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO events(task,event_id,payload) VALUES(@task,@id,@payload)";
            command.Parameters.AddWithValue("@task", task); command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@payload", payload); command.ExecuteNonQuery();
        }
        public List<string> Events(string task)
        {
            using var c = Open(); using var command = c.CreateCommand();
            command.CommandText = "SELECT payload FROM events WHERE task=@task ORDER BY sequence";
            command.Parameters.AddWithValue("@task", task);
            using var reader = command.ExecuteReader(); var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0)); return values;
        }
        public void Backup(string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            using var source = Open(); using var destination = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            destination.Open(); source.BackupDatabase(destination, "main", "main", -1, null, 0);
        }
        public void Restore(string path)
        {
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 512L * 1024 * 1024) throw new IOException("备份路径或大小无效。");
            using var source = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = path, ReadOnly = true, FailIfMissing = true, Pooling = false }.ToString());
            source.Open(); using var command = source.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new IOException("备份状态库版本不兼容。");
            command.CommandText = "PRAGMA quick_check";
            if ((string)command.ExecuteScalar() != "ok") throw new IOException("备份状态库损坏。");
            using var target = Open(); source.BackupDatabase(target, "main", "main", -1, null, 0);
        }
        public void DeleteTask(string id)
        {
            using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM documents WHERE id=@id AND kind IN ('task','receipts','host-failure'); DELETE FROM events WHERE task=@id";
            cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery(); tx.Commit();
        }
        public void DeleteProjectMetadata(string id)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            var sessions = new List<(string Id, JObject Payload)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction; select.CommandText = "SELECT id,payload FROM documents WHERE kind='session'";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var payload = JObject.Parse(reader.GetString(1));
                    if ((string?)payload["project_id"] == id) sessions.Add((reader.GetString(0), payload));
                }
            }
            foreach (var session in sessions)
            {
                session.Payload["project_id"] = "";
                Put(connection, transaction, "session", session.Id, session.Payload.ToString(Newtonsoft.Json.Formatting.None));
            }
            using var delete = connection.CreateCommand(); delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM documents WHERE kind='project' AND id=@id";
            delete.Parameters.AddWithValue("@id", id); delete.ExecuteNonQuery(); transaction.Commit();
        }
        public void DeleteConversationMetadata(string id)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM documents WHERE kind='session' AND id=@id";
            command.Parameters.AddWithValue("@id", id); command.ExecuteNonQuery();
        }
        private void ImportLegacy(string root)
        {
            if (Get("meta", "legacy-import") != null) return;
            var documents = new List<(string Kind, string Id, string Payload)>();
            var events = new List<(string Task, string Id, string Payload)>();
            var failures = new JArray();
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(folder).StartsWith("_", StringComparison.Ordinal)) continue;
                var id = Path.GetFileName(folder);
                foreach (var file in new[] { "task.json", "receipts.json" })
                {
                    var path = Path.Combine(folder, file); if (!File.Exists(path)) continue;
                    try { var payload = File.ReadAllText(path); JToken.Parse(payload); documents.Add((file == "task.json" ? "task" : "receipts", id, payload)); }
                    catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException) { failures.Add(path); }
                }
                var eventPath = Path.Combine(folder, "events.jsonl");
                if (File.Exists(eventPath)) foreach (var line in File.ReadLines(eventPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var item = JObject.Parse(line); events.Add((id, item.Value<string>("event_id") ?? Guid.NewGuid().ToString("N"), line)); }
                    catch (Newtonsoft.Json.JsonException) { failures.Add(eventPath); }
                }
            }
            var sessions = Path.Combine(root, "_sessions");
            if (Directory.Exists(sessions)) foreach (var folder in Directory.GetDirectories(sessions))
            {
                var path = Path.Combine(folder, "session.json"); if (!File.Exists(path)) continue;
                try { var payload = File.ReadAllText(path); JObject.Parse(payload); documents.Add(("session", Path.GetFileName(folder), payload)); }
                catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException) { failures.Add(path); }
            }
            // Original JSON files are preserved in place; a DB backup follows the committed migration.
            using var c = Open(); using var tx = c.BeginTransaction();
            using (var check = c.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM documents WHERE kind='meta' AND id='legacy-import'";
                if (check.ExecuteScalar() != null) { tx.Commit(); return; }
            }
            foreach (var d in documents) Put(c, tx, d.Kind, d.Id, d.Payload);
            foreach (var e in events)
            {
                using var command = c.CreateCommand(); command.Transaction = tx;
                command.CommandText = "INSERT OR IGNORE INTO events(task,event_id,payload) VALUES(@task,@id,@payload)";
                command.Parameters.AddWithValue("@task", e.Task); command.Parameters.AddWithValue("@id", e.Id);
                command.Parameters.AddWithValue("@payload", e.Payload); command.ExecuteNonQuery();
            }
            Put(c, tx, "meta", "legacy-import", new JObject { ["utc"] = DateTime.UtcNow, ["unreadable_files"] = failures }.ToString());
            tx.Commit();
            Backup(Path.Combine(root, "_backups", "post-legacy-import.sqlite3"));
        }
    }
}
