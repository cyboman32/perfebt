using System;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Dapper;

namespace PerfeBT
{
    // Unit/lance selection at mission launch: TagSetQueryExtensions.GetMatchingDataByTagSet<T> runs
    //   SELECT r.* FROM LanceDef as r ... WHERE r.TagSetID in @TagSetId ...
    // with one string per matching TagSet. Dapper's PackListParameters expands that into "(@TagSetId1,...,@TagSetIdN)"
    // and adds N named parameters. Mono.Data.Sqlite then does two linear, culture-aware case-insensitive name scans per
    // parameter (SqliteParameterCollection.Add -> IndexOf, and MapParameters -> SqliteStatement.MapParameter), so a list
    // costs O(N^2) String.Compare calls. RogueTech: "lance_type_notallvehicles" matches 1164 lance TagSets, ~620 ms per
    // RequestLance; MissionControl's extra lances request it 8 times (~5 s of ContractOverrideGenerateUnitsPatch.Prefix).
    //
    // Fix: for Mono.Data.Sqlite commands and string[] lists of at least MinItems values, write the values into the
    // command text as SQL string literals instead of parameters: "(@TagSetId1,...)" becomes "('id1',...)".
    // Same IN-list, same TEXT values in the same order, same statement shape, so SQLite returns the same rows in the
    // same order (verified offline with the game's sqlite3.dll, Mono.Data.Sqlite and Dapper.Unity). Every other case
    // (other providers, short lists, nulls, unusual characters, positional/padded/string_split Dapper modes, unexpected
    // command text) runs Dapper's original code.
    internal static class UnitGenFix
    {
        internal const int MinItems = 64;
        // SQLITE_MAX_VARIABLE_NUMBER of the game's sqlite3.dll (3.38.2). Longer lists fail in the original
        // ("too many SQL variables"); keep that behaviour by not rewriting them.
        internal const int MaxItems = 32766;
        // Dapper's PackListParameters uses RegexOptions.IgnoreCase | Multiline | CultureInvariant (515).
        private const RegexOptions DapperRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant;

        private static int rewrites;
        private static long rewrittenValues;
        private static int largest;
        private static string lastSkip;
        private static bool probed;
        private static bool disabled;

        // Dapper ships as Managed/Dapper.Unity.dll (assembly name "Dapper"). Dapper's members are used directly; the
        // first use from a patch body runs Dapper's static constructor, as the game's own first query does.
        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(SqlMapper), nameof(SqlMapper.PackListParameters), new[] { typeof(IDbCommand), typeof(string), typeof(object) });
            var info = Harmony.GetPatchInfo(target);
            if (info != null && info.Owners.Count > 0)
                throw new InvalidOperationException("PackListParameters already patched by " + string.Join(", ", info.Owners));

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(UnitGenFix), nameof(Prefix)) { priority = Priority.First });
        }

        // Dapper.SqlMapper.PackListParameters(IDbCommand command, string namePrefix, object value)
        private static bool Prefix(IDbCommand __0, string __1, object __2)
        {
            if (!(__2 is string[] values) || values.Length < MinItems || values.Length > MaxItems)
                return true;
            var command = __0;
            if (command == null || __1 == null || disabled)
                return true;
            if (command.GetType() != typeof(Mono.Data.Sqlite.SqliteCommand))
                return true;
            if (!probed)
            {
                // The rewrite relies on the regex Dapper applies; checked on first use (not in Apply, so Apply does not
                // run Dapper's static constructor).
                probed = true;
                var probe = SqlMapper.GetInListRegex("TagSetID", false);
                if (probe != @"([?@:]TagSetID)(?!\w)(\s+(?i)unknown(?-i))?")
                {
                    disabled = true;
                    Log.Write("[fix] UnitGenFix: disabled, unexpected Dapper GetInListRegex " + probe);
                    return true;
                }
            }
            try
            {
                return !Rewrite(command, __1, values);
            }
            catch (Exception e)
            {
                Skip("exception " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        // Returns false (original runs, command untouched) whenever the case is not the plain named IN-list expansion.
        internal static bool Rewrite(IDbCommand command, string name, string[] values)
        {
            if (SqlMapper.Settings.PadListExpansions)
                return Skip("PadListExpansions");
            if (SqlMapper.Settings.InListStringSplitCount >= 0)
                return Skip("InListStringSplitCount");
            var text = command.CommandText;
            if (text == null || SqlMapper.ShouldPassByPosition(text))
                return Skip("positional");

            var length = 2;
            foreach (var v in values)
            {
                if (v == null)
                    return Skip("null value");
                foreach (var c in v)
                    if (c == '\0' || char.IsSurrogate(c))
                        return Skip("unusual character");
                length += v.Length + 3;
            }

            var pattern = SqlMapper.GetInListRegex(name, false);
            var matches = Regex.Matches(text, pattern, DapperRegexOptions);
            if (matches.Count == 0)
                return Skip("no list token");
            foreach (Match m in matches)
                if (m.Groups[2].Success)
                    return Skip("unknown-suffix token");

            var sb = new StringBuilder(length);
            sb.Append('(');
            for (var i = 0; i < values.Length; i++)
            {
                if (i != 0)
                    sb.Append(',');
                sb.Append('\'');
                var v = values[i];
                if (v.IndexOf('\'') >= 0)
                    sb.Append(v.Replace("'", "''"));
                else
                    sb.Append(v);
                sb.Append('\'');
            }
            sb.Append(')');
            var list = sb.ToString();
            command.CommandText = Regex.Replace(text, pattern, _ => list, DapperRegexOptions);

            rewrites++;
            rewrittenValues += values.Length;
            if (values.Length > largest)
                largest = values.Length;
            if (rewrites == 1 || rewrites % 100 == 0)
                Log.Write($"[fix] UnitGenFix: {rewrites} IN-lists inlined ({rewrittenValues} values, largest {largest})");
            return true;
        }

        private static bool Skip(string reason)
        {
            if (reason != lastSkip)
            {
                lastSkip = reason;
                Log.Write("[fix] UnitGenFix: original list expansion used (" + reason + ")");
            }
            return false;
        }
    }
}
