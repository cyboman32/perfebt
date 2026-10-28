using System;
using System.Data;
using System.Diagnostics;
using BattleTech.Data;

namespace PerfeBT
{
    // Tags_MDDExtenstions.GetTagsForSet runs
    //   SELECT ... FROM TagSetTag tst ... WHERE tst.TagSetID = @TagSetID COLLATE NOCASE
    // The only TagSetTag indexes use BINARY collation, so every call scans the whole table
    // (432k rows in RogueTech, ~11 ms per call). A NOCASE index turns it into an index seek.
    // The in-memory MDD connection can be replaced (ReloadFromDisk), so the index is ensured per connection.
    internal static class TagSetIndex
    {
        private const string Sql = "CREATE INDEX IF NOT EXISTS PerfeBT_TagSetTag_TagSetID_NoCase ON TagSetTag (TagSetID COLLATE NOCASE)";
        private static object indexedConnection;

        internal static void Prefix(MetadataDatabase __0)
        {
            if (__0 != null && __0.inMemoryDB is IDbConnection conn)
                Ensure(conn);
        }

        internal static void Ensure(IDbConnection conn)
        {
            if (ReferenceEquals(conn, indexedConnection))
                return;
            indexedConnection = conn;
            var sw = Stopwatch.StartNew();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = Sql;
                    cmd.ExecuteNonQuery();
                }
                Log.Write($"[fix] TagSetTag NOCASE index ensured in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                Log.Write("[fix] TagSetTag index failed: " + e.Message);
            }
        }
    }
}
