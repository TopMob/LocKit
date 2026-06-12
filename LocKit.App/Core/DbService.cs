using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace LocKit.App.Core
{
    public class DbService
    {
        private const string GlobalConnectionString = "Data Source=lockit.db";
        private string _projectDbPath = "lockit.db";
        private string ProjectConnectionString => $"Data Source={_projectDbPath}";
        private string ConnectionString => ProjectConnectionString;

        public void SetDatabasePath(string path)
        {
            _projectDbPath = path;
        }

        public string GetDatabasePath() => _projectDbPath;

        public void InitializeDatabase(bool seedDemo = false)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using (var pragmaCmd = new SqliteCommand("PRAGMA foreign_keys = ON;", connection))
            {
                pragmaCmd.ExecuteNonQuery();
            }

            string createTablesSql = @"
                CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT UNIQUE NOT NULL,
                    status TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS translation_units (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id INTEGER NOT NULL,
                    key TEXT NOT NULL,
                    character TEXT,
                    source TEXT NOT NULL,
                    target TEXT,
                    status TEXT NOT NULL,
                    FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS translation_meta (
                    unit_id INTEGER NOT NULL,
                    meta_key TEXT NOT NULL,
                    meta_value TEXT,
                    PRIMARY KEY(unit_id, meta_key),
                    FOREIGN KEY(unit_id) REFERENCES translation_units(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );
            ";

            using (var cmd = new SqliteCommand(createTablesSql, connection))
            {
                cmd.ExecuteNonQuery();
            }

            try
            {
                using var alterCmd = new SqliteCommand("ALTER TABLE translation_units ADD COLUMN character TEXT;", connection);
                alterCmd.ExecuteNonQuery();
            }
            catch { }

            if (seedDemo)
            {
                SeedDemoData(connection);
            }
        }

        private void SeedDemoData(SqliteConnection connection)
        {
            // Check if files table is empty
            long fileCount = 0;
            using (var countCmd = new SqliteCommand("SELECT COUNT(*) FROM files;", connection))
            {
                fileCount = (long)(countCmd.ExecuteScalar() ?? 0L);
            }

            if (fileCount > 0) return;

            // Insert demo files
            string[] demoFiles = { "script.rpy", "options.rpy", "gui.rpy", "screens.rpy" };
            var fileIds = new Dictionary<string, long>();

            foreach (var file in demoFiles)
            {
                using var insertFileCmd = new SqliteCommand("INSERT INTO files (name, status) VALUES (@name, 'pending'); SELECT last_insert_rowid();", connection);
                insertFileCmd.Parameters.AddWithValue("@name", file);
                long fileId = (long)(insertFileCmd.ExecuteScalar() ?? 0L);
                fileIds[file] = fileId;
            }

            // Insert demo translation rows for script.rpy
            long scriptFileId = fileIds["script.rpy"];
            var demoRows = new[]
            {
                ("ch1_start_01", "Wait... Who are you?", "Погоди... Ты кто?"),
                ("ch1_start_02", "I don't think we have met before. What brings you to this quiet town?", "Не думаю, что мы встречались раньше. Что привело тебя в этот тихий городок?"),
                ("ch1_start_03", "The train only comes here once a week.", "Поезд ходит сюда всего раз в неделю."),
                ("ch1_start_04", "Anyway, my name is Sylvia. Welcome to LocKit.", "В любом случае, меня зовут Сильвия. Добро пожаловать в ЛокКит."),
                ("ch1_option_yes", "I decided to stay here.", "Я решил остаться здесь."),
                ("ch1_option_no", "Just passing by.", "Просто проходил мимо.")
            };

            foreach (var row in demoRows)
            {
                using var insertUnitCmd = new SqliteCommand(
                    "INSERT INTO translation_units (file_id, key, source, target, status) VALUES (@file_id, @key, @source, @target, 'draft');", 
                    connection
                );
                insertUnitCmd.Parameters.AddWithValue("@file_id", scriptFileId);
                insertUnitCmd.Parameters.AddWithValue("@key", row.Item1);
                insertUnitCmd.Parameters.AddWithValue("@source", row.Item2);
                insertUnitCmd.Parameters.AddWithValue("@target", row.Item3);
                insertUnitCmd.ExecuteNonQuery();
            }

            // Add some mock units for other files too
            long optionsFileId = fileIds["options.rpy"];
            using (var insertOptCmd = new SqliteCommand(
                "INSERT INTO translation_units (file_id, key, source, target, status) VALUES (@file_id, 'config_name', 'LocKit Novel', 'Новелла ЛокКит', 'draft');",
                connection
            ))
            {
                insertOptCmd.Parameters.AddWithValue("@file_id", optionsFileId);
                insertOptCmd.ExecuteNonQuery();
            }
        }

        public List<string> GetFiles()
        {
            var result = new List<string>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using var cmd = new SqliteCommand("SELECT name FROM files ORDER BY name ASC;", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }

        public Dictionary<string, (int Total, int Translated)> GetFilesTranslationStats()
        {
            var stats = new Dictionary<string, (int Total, int Translated)>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            string sql = @"
                SELECT f.name, COUNT(u.id) as total, COUNT(CASE WHEN u.target IS NOT NULL AND u.target != '' THEN 1 END) as translated
                FROM files f
                LEFT JOIN translation_units u ON u.file_id = f.id
                GROUP BY f.name;
            ";

            using var cmd = new SqliteCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string fileName = reader.GetString(0);
                int total = reader.GetInt32(1);
                int translated = reader.GetInt32(2);
                stats[fileName] = (total, translated);
            }
            return stats;
        }

        public List<TranslationRow> GetTranslationUnits(string fileName)
        {
            var result = new List<TranslationRow>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            string selectUnitsSql = @"
                SELECT u.id, u.key, u.source, u.target, u.character 
                FROM translation_units u
                JOIN files f ON u.file_id = f.id
                WHERE f.name = @fileName
                ORDER BY u.id ASC;
            ";

            using var cmd = new SqliteCommand(selectUnitsSql, connection);
            cmd.Parameters.AddWithValue("@fileName", fileName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new TranslationRow
                {
                    Id = reader.GetInt32(0),
                    Key = reader.GetString(1),
                    Original = reader.GetString(2),
                    Translation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Character = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                };
                result.Add(row);
            }

            if (result.Count == 0) return result;

            var idsList = result.Select(r => r.Id).ToList();
            var unitIds = string.Join(",", idsList);
            string selectMetaSql = $"SELECT unit_id, meta_key, meta_value FROM translation_meta WHERE unit_id IN ({unitIds});";

            using var metaCmd = new SqliteCommand(selectMetaSql, connection);
            using var metaReader = metaCmd.ExecuteReader();
            
            var metaDict = new Dictionary<int, Dictionary<string, string>>();
            while (metaReader.Read())
            {
                int unitId = metaReader.GetInt32(0);
                string key = metaReader.GetString(1);
                string val = metaReader.IsDBNull(2) ? string.Empty : metaReader.GetString(2);

                if (!metaDict.ContainsKey(unitId))
                {
                    metaDict[unitId] = new Dictionary<string, string>();
                }
                metaDict[unitId][key] = val;
            }

            foreach (var row in result)
            {
                if (metaDict.TryGetValue(row.Id, out var meta))
                {
                    row.CustomColumns = meta;
                }
            }

            return result;
        }

        public void UpdateTranslation(int unitId, string translation)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using var cmd = new SqliteCommand(
                "UPDATE translation_units SET target = @translation, status = 'translated' WHERE id = @id;", 
                connection
            );
            cmd.Parameters.AddWithValue("@translation", translation);
            cmd.Parameters.AddWithValue("@id", unitId);
            cmd.ExecuteNonQuery();
        }

        public void SaveCustomMeta(int unitId, string metaKey, string metaValue)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            // SQLite UPSERT command
            string upsertSql = @"
                INSERT INTO translation_meta (unit_id, meta_key, meta_value) 
                VALUES (@unit_id, @meta_key, @meta_value)
                ON CONFLICT(unit_id, meta_key) DO UPDATE SET meta_value = excluded.meta_value;
            ";

            using var cmd = new SqliteCommand(upsertSql, connection);
            cmd.Parameters.AddWithValue("@unit_id", unitId);
            cmd.Parameters.AddWithValue("@meta_key", metaKey);
            cmd.Parameters.AddWithValue("@meta_value", metaValue);
            cmd.ExecuteNonQuery();
        }

        public List<string> GetCustomMetaKeys(string fileName)
        {
            var keys = new List<string>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            string selectKeysSql = @"
                SELECT DISTINCT m.meta_key 
                FROM translation_meta m
                JOIN translation_units u ON m.unit_id = u.id
                JOIN files f ON u.file_id = f.id
                WHERE f.name = @fileName;
            ";

            using var cmd = new SqliteCommand(selectKeysSql, connection);
            cmd.Parameters.AddWithValue("@fileName", fileName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }
            return keys;
        }

        public void ImportRpyFile(string fileName, IEnumerable<RpyDialogueLine> lines)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();

            long fileId;
            using (var upsertFileCmd = new SqliteCommand(
                "INSERT INTO files (name, status) VALUES (@name, 'imported') ON CONFLICT(name) DO UPDATE SET status='imported'; SELECT id FROM files WHERE name = @name;",
                connection, tx))
            {
                upsertFileCmd.Parameters.AddWithValue("@name", fileName);
                fileId = (long)(upsertFileCmd.ExecuteScalar() ?? 0L);
            }

            using (var deleteCmd = new SqliteCommand("DELETE FROM translation_units WHERE file_id = @file_id;", connection, tx))
            {
                deleteCmd.Parameters.AddWithValue("@file_id", fileId);
                deleteCmd.ExecuteNonQuery();
            }

            foreach (var line in lines)
            {
                string target = string.IsNullOrEmpty(line.Translation) ? "" : line.Translation;
                string status = string.IsNullOrEmpty(target) ? "pending" : "translated";
                using var insertCmd = new SqliteCommand(
                    "INSERT INTO translation_units (file_id, key, character, source, target, status) VALUES (@file_id, @key, @character, @source, @target, @status);",
                    connection, tx);
                insertCmd.Parameters.AddWithValue("@file_id", fileId);
                insertCmd.Parameters.AddWithValue("@key", line.Key);
                insertCmd.Parameters.AddWithValue("@character", line.Character ?? "");
                insertCmd.Parameters.AddWithValue("@source", line.Source);
                insertCmd.Parameters.AddWithValue("@target", target);
                insertCmd.Parameters.AddWithValue("@status", status);
                insertCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public List<ExportUnit> GetUnitsForExport(string fileName)
        {
            var result = new List<ExportUnit>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            string sql = @"
                SELECT u.key, u.source, COALESCE(u.target, '') as target, COALESCE(u.character, '') as character
                FROM translation_units u
                JOIN files f ON u.file_id = f.id
                WHERE f.name = @fileName
                ORDER BY u.id ASC;
            ";

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@fileName", fileName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ExportUnit
                {
                    Key = reader.GetString(0),
                    Source = reader.GetString(1),
                    Target = reader.GetString(2),
                    Character = reader.GetString(3)
                });
            }
            return result;
        }

        public string GetSetting(string key, string defaultValue = "", bool isGlobal = false)
        {
            using var connection = new SqliteConnection(isGlobal ? GlobalConnectionString : ConnectionString);
            connection.Open();

            using (var cmdCreate = new SqliteCommand("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);", connection))
            {
                cmdCreate.ExecuteNonQuery();
            }

            using var cmd = new SqliteCommand("SELECT value FROM settings WHERE key = @key;", connection);
            cmd.Parameters.AddWithValue("@key", key);
            var val = cmd.ExecuteScalar();
            return val != null ? val.ToString() : defaultValue;
        }

        public void SaveSetting(string key, string value, bool isGlobal = false)
        {
            using var connection = new SqliteConnection(isGlobal ? GlobalConnectionString : ConnectionString);
            connection.Open();

            using (var cmdCreate = new SqliteCommand("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);", connection))
            {
                cmdCreate.ExecuteNonQuery();
            }

            using var cmd = new SqliteCommand(@"
                INSERT INTO settings (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = @value;
            ", connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }

        public void DeleteFile(string fileName)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using (var pragmaCmd = new SqliteCommand("PRAGMA foreign_keys = ON;", connection))
            {
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = new SqliteCommand("DELETE FROM files WHERE name = @name;", connection);
            cmd.Parameters.AddWithValue("@name", fileName);
            cmd.ExecuteNonQuery();
        }
    }
}
