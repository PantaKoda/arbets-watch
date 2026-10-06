using System.Globalization;
using System.Text;
using ArbetsWatch.Core.Places;
using Microsoft.Data.Sqlite;

namespace ArbetsWatch.Core.Filtering;

/// <summary>
/// The SQL form of <see cref="AdMatcher"/> for the <c>ad_summary</c> table. Values are always parameters.
/// The expression is two-valued (never NULL), so it can be negated, and several filters can share a command.
/// </summary>
public static class AdFilterSql
{
    /// <summary>Returns a boolean SQL expression (0 or 1) and adds its parameters to <paramref name="command"/>.</summary>
    /// <param name="alias">Table alias of <c>ad_summary</c>, e.g. <c>s</c>.</param>
    public static string Where(AdFilter filter, SqliteCommand command, string alias = "s")
    {
        var prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        string Param(object value)
        {
            // Continue numbering after any parameters already on the command, so filters can be combined.
            var name = string.Create(CultureInfo.InvariantCulture, $"$f{command.Parameters.Count}");
            command.Parameters.AddWithValue(name, value);
            return name;
        }

        var geo = new List<string>();
        if (filter.AllSweden)
        {
            geo.Add($"({prefix}country_id = {Param(PlaceCatalog.SwedenId)} OR {prefix}country_id IS NULL)");
        }

        if (filter.RegionIds.Count > 0)
        {
            geo.Add($"{prefix}region_id IN ({string.Join(", ", filter.RegionIds.Order(StringComparer.Ordinal).Select(Param))})");
        }

        if (filter.MunicipalityIds.Count > 0)
        {
            geo.Add($"{prefix}municipality_id IN ({string.Join(", ", filter.MunicipalityIds.Order(StringComparer.Ordinal).Select(Param))})");
        }

        var sql = new StringBuilder();
        sql.Append('(').Append(geo.Count == 0 ? "0" : string.Join(" OR ", geo)).Append(')');

        if ((filter.Worktime & WorktimeSet.All) != WorktimeSet.All)
        {
            var worktime = new List<string>();
            if (filter.Worktime.HasFlag(WorktimeSet.FullTime))
            {
                worktime.Add($"{prefix}worktime_id = {Param(WorktimeConcepts.FullTimeId)}");
            }

            if (filter.Worktime.HasFlag(WorktimeSet.PartTime))
            {
                worktime.Add($"{prefix}worktime_id = {Param(WorktimeConcepts.PartTimeId)}");
            }

            if (filter.Worktime.HasFlag(WorktimeSet.NotSpecified))
            {
                worktime.Add($"{prefix}worktime_id IS NULL");
            }

            sql.Append(" AND (").Append(worktime.Count == 0 ? "0" : string.Join(" OR ", worktime)).Append(')');
        }

        // IN and = yield NULL for NULL columns; COALESCE makes the result 0 instead, so NOT works as expected.
        return $"COALESCE({sql}, 0)";
    }
}
