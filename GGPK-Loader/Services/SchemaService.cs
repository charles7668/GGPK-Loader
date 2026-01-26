using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using GGPK_Loader.Models.Schema;

namespace GGPK_Loader.Services;

public class SchemaService : ISchemaService
{
    private Dictionary<string, List<SchemaTable>> _tables = new();

    public void LoadSchema(string schemaPath)
    {
        if (!File.Exists(schemaPath))
        {
            Debug.WriteLine($"Schema file not found at: {schemaPath}");
            return;
        }

        try
        {
            var json = File.ReadAllText(schemaPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var root = JsonSerializer.Deserialize<SchemaRoot>(json, options);

            if (root?.Tables != null)
            {
                _tables = root.Tables
                    .GroupBy(t => t.Name)
                    .ToDictionary(
                        g => g.Key,
                        g => g.ToList(),
                        StringComparer.OrdinalIgnoreCase
                    );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load schema: {ex.Message}");
        }
    }

    public List<SchemaColumn>? GetColumns(string tableName, int[]? allowedValidFor = null)
    {
        if (!_tables.TryGetValue(tableName, out var tables))
        {
            return null;
        }

        var candidates = tables.AsEnumerable();

        if (allowedValidFor is { Length: > 0 })
        {
            candidates = candidates.Where(t => allowedValidFor.Contains(t.ValidFor));
        }

        return candidates.OrderByDescending(t => t.ValidFor).FirstOrDefault()?.Columns;
    }
}