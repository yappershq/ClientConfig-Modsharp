using System;
using SqlSugar;

namespace ClientConfig.Database;

/// <summary>
/// One row per player in <c>client_configs</c> — the latest captured snapshot of their client
/// ConVars, overwritten on every reconnect. ConfigJson is a JSON object mapping convar name → value.
/// Column names are PascalCase to match the rest of the PlayerAnalytics schema (its SqlSugar config
/// maps by property name with no snake_case convention).
/// </summary>
[SugarTable("client_configs")]
internal sealed class ClientConfigRow
{
    [SugarColumn(ColumnName = "SteamId", IsPrimaryKey = true)]
    public long SteamId { get; set; }

    [SugarColumn(ColumnName = "Name", Length = 128, IsNullable = true)]
    public string? Name { get; set; }

    [SugarColumn(ColumnName = "ConfigJson", ColumnDataType = "longtext", IsNullable = true)]
    public string? ConfigJson { get; set; }

    [SugarColumn(ColumnName = "ServerTag", Length = 64, IsNullable = true)]
    public string? ServerTag { get; set; }

    [SugarColumn(ColumnName = "CapturedAt")]
    public DateTime CapturedAt { get; set; }
}
