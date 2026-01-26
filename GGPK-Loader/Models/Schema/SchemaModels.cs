using System.Collections.Generic;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace GGPK_Loader.Models.Schema;

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class SchemaRoot
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("tables")]
    public List<SchemaTable> Tables { get; set; } = [];
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class SchemaTable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("columns")]
    public List<SchemaColumn> Columns { get; set; } = new();

    [JsonPropertyName("validFor")]
    public int ValidFor { get; set; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class SchemaColumn
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("array")]
    public bool Array { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("unique")]
    public bool Unique { get; set; }

    [JsonPropertyName("references")]
    public SchemaReferences? References { get; set; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class SchemaReferences
{
    [JsonPropertyName("table")]
    public string? Table { get; set; }
}