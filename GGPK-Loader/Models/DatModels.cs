using System.Collections.Generic;
using System.Collections.ObjectModel;
using GGPK_Loader.Models.Schema;
using JetBrains.Annotations;

namespace GGPK_Loader.Models;

public record DatRowInfo(
    ObservableCollection<DatRow> Rows,
    List<string> Headers,
    List<double> ColumnWidths,
    List<SchemaColumn>? Columns = null);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public record DatRow(int Index, string[] Values);