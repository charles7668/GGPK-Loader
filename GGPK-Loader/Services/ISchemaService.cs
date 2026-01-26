using System.Collections.Generic;
using GGPK_Loader.Models.Schema;

namespace GGPK_Loader.Services;

public interface ISchemaService
{
    void LoadSchema(string schemaPath);
    List<SchemaColumn>? GetColumns(string tableName, int[]? allowedValidFor = null);
}