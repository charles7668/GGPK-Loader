using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;
using GGPK_Loader.Models;
using GGPK_Loader.Models.Schema;

namespace GGPK_Loader.Services;

public interface IDatParsingService
{
    DatRowInfo? ParseDatFile(byte[] data, string fileName);
    int GetColumnSize(SchemaColumn col, bool is64Bit);
    string ReadColumnValue(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset);
}

public class DatParsingService(ISchemaService schemaService) : IDatParsingService
{
    private static readonly Typeface TypeFace = new("Consolas");

    public DatRowInfo? ParseDatFile(byte[] data, string fileName)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var rowCount = BitConverter.ToInt32(data, 0);
        var separatorIndex = FindSeparatorIndex(data);
        var contentEnd = separatorIndex != -1 ? separatorIndex : data.Length;
        var contentSize = contentEnd - 4;
        var actualRowSize = rowCount > 0 ? (int)(contentSize / rowCount) : 0;

        var tableName = Path.GetFileNameWithoutExtension(fileName);
        var validVersions = fileName.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase)
            ? new[] { 2, 3 }
            : new[] { 1, 3 };

        var columns = schemaService.GetColumns(tableName, validVersions);

        if (columns == null)
        {
            return ParseWithHeuristic(data, rowCount, contentSize);
        }

        var is64Bit = fileName.EndsWith("64", StringComparison.OrdinalIgnoreCase);
        var result = ParseWithSchema(data, rowCount, actualRowSize, separatorIndex, columns, is64Bit);
        return result ?? ParseWithHeuristic(data, rowCount, contentSize);
    }

    public int GetColumnSize(SchemaColumn col, bool is64Bit)
    {
        if (col.Array)
        {
            return is64Bit ? 16 : 8;
        }

        var size = GetBaseTypeSize(col.Type, is64Bit);
        return col.Interval ? size * 2 : size;
    }

    public string ReadColumnValue(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset)
    {
        var size = GetColumnSize(col, is64Bit);
        if (offset + size > data.Length)
        {
            return "ERR";
        }

        if (col.Array)
        {
            return ReadArrayColumn(data, offset, col, is64Bit, baseOffset);
        }

        return col.Interval
            ? ReadIntervalColumn(data, offset, col, is64Bit, baseOffset)
            : ReadPrimitiveColumn(data, offset, col, is64Bit, baseOffset);
    }

    private static long FindSeparatorIndex(byte[] data)
    {
        var limit = data.Length - 8;
        for (var i = 4; i <= limit; i++)
        {
            if (data[i] == 0xBB && data[i + 1] == 0xBB && data[i + 2] == 0xBB && data[i + 3] == 0xBB &&
                data[i + 4] == 0xBB && data[i + 5] == 0xBB && data[i + 6] == 0xBB && data[i + 7] == 0xBB)
            {
                return i;
            }
        }

        return -1;
    }

    private DatRowInfo? ParseWithSchema(byte[] data, int rowCount, int actualRowSize, long separatorIndex,
        List<SchemaColumn> columns, bool is64Bit)
    {
        var (effectiveRowSize, extraBytes) = CalculateEffectiveRowSize(columns, actualRowSize, is64Bit);
        if (effectiveRowSize <= 0)
        {
            return null;
        }

        var totalColumns = columns.Count + extraBytes;
        var headers = new List<string>(totalColumns);
        var columnWidths = new double[totalColumns];

        InitializeHeadersAndWidths(columns, extraBytes, headers, columnWidths);

        var context = new ParsingContext(data, rowCount, effectiveRowSize, separatorIndex, columns, is64Bit, extraBytes,
            columnWidths);
        var allRows = ParseAllRowsWithSchema(context);

        AddPaddingToWidths(columnWidths);

        var maxIndexStr = (rowCount - 1).ToString();
        var indexWidth = Math.Max(GetTextWidth("Row"), GetTextWidth(maxIndexStr)) + 20;

        var finalWidths = new List<double> { indexWidth };
        finalWidths.AddRange(columnWidths);

        var items = new ObservableCollection<DatRow>(allRows);

        return new DatRowInfo(items, headers, finalWidths, columns);
    }

    private (int effectiveRowSize, int extraBytes) CalculateEffectiveRowSize(List<SchemaColumn> columns,
        int actualRowSize, bool is64Bit)
    {
        var calculatedRowSize = 0;
        foreach (var col in columns)
        {
            calculatedRowSize += GetColumnSize(col, is64Bit);
        }

        if (calculatedRowSize <= 0)
        {
            return (0, 0);
        }

        var effectiveRowSize = actualRowSize > 0 ? actualRowSize : calculatedRowSize;
        var extraBytes = Math.Max(0, effectiveRowSize - calculatedRowSize);
        return (effectiveRowSize, extraBytes);
    }

    private static void InitializeHeadersAndWidths(List<SchemaColumn> columns, int extraBytes, List<string> headers,
        double[] columnWidths)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            headers.Add(columns[i].Name ?? "Unk");
            columnWidths[i] = GetTextWidth(headers[i]);
        }

        for (var i = 0; i < extraBytes; i++)
        {
            var h = i.ToString("X2");
            headers.Add(h);
            columnWidths[columns.Count + i] = Math.Max(GetTextWidth("00"), GetTextWidth(h));
        }
    }

    private List<DatRow> ParseAllRowsWithSchema(ParsingContext ctx)
    {
        var allRows = new List<DatRow>();
        var totalColumns = ctx.Columns.Count + ctx.ExtraBytes;

        for (var r = 0; r < ctx.RowCount; r++)
        {
            var rowStart = 4 + r * ctx.EffectiveRowSize;
            if (rowStart + ctx.EffectiveRowSize > ctx.Data.Length)
            {
                break;
            }

            var rowValues = new string[totalColumns];
            var offset = ProcessSchemaColumns(ctx.Data, rowStart, ctx.Columns, ctx.Is64Bit, ctx.SeparatorIndex,
                rowValues, ctx.ColumnWidths);
            ProcessExtraBytes(ctx.Data, rowStart + offset, ctx.ExtraBytes, ctx.Columns.Count, rowValues,
                ctx.ColumnWidths);

            allRows.Add(new DatRow(r, rowValues));
        }

        return allRows;
    }

    private int ProcessSchemaColumns(byte[] data, int rowStart, List<SchemaColumn> columns, bool is64Bit,
        long baseOffset, string[] rowValues, double[] columnWidths)
    {
        var offset = 0;
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var size = GetColumnSize(col, is64Bit);
            var val = ReadColumnValue(data, rowStart + offset, col, is64Bit, baseOffset);
            rowValues[c] = val;

            var w = GetTextWidth(val);
            if (w > columnWidths[c])
            {
                columnWidths[c] = w;
            }

            offset += size;
        }

        return offset;
    }

    private static void ProcessExtraBytes(byte[] data, int startOffset, int extraBytes, int colStartIndex,
        string[] rowValues, double[] columnWidths)
    {
        for (var i = 0; i < extraBytes; i++)
        {
            var colIdx = colStartIndex + i;
            if (startOffset + i < data.Length)
            {
                var b = data[startOffset + i];
                var val = b.ToString("X2");
                rowValues[colIdx] = val;

                var w = GetTextWidth(val);
                if (w > columnWidths[colIdx])
                {
                    columnWidths[colIdx] = w;
                }
            }
            else
            {
                rowValues[colIdx] = "??";
            }
        }
    }

    private static void AddPaddingToWidths(double[] columnWidths)
    {
        for (var i = 0; i < columnWidths.Length; i++)
        {
            columnWidths[i] += 20;
        }
    }

    private static DatRowInfo? ParseWithHeuristic(byte[] data, int rowCount, long contentSize)
    {
        if (rowCount <= 0)
        {
            return null;
        }

        var rowSize = (int)(contentSize / rowCount);
        if (rowSize == 0)
        {
            return null;
        }

        var maxIndex = rowSize - 1;
        var indexDigits = maxIndex > 255 ? (int)Math.Floor(Math.Log(maxIndex, 16)) + 1 : 2;
        var headerFormat = "X" + indexDigits;

        var allRowsFallback = new List<DatRow>();
        var widthsFallback = new double[rowSize];
        var headersFallback = new List<string>();

        for (var i = 0; i < rowSize; i++)
        {
            var h = i.ToString(headerFormat);
            headersFallback.Add(h);
            widthsFallback[i] = GetTextWidth(h);
        }

        for (var r = 0; r < rowCount; r++)
        {
            var rowStart = 4 + r * rowSize;
            var rowValues = new string[rowSize];

            for (var c = 0; c < rowSize; c++)
            {
                if (rowStart + c < data.Length)
                {
                    rowValues[c] = data[rowStart + c].ToString("X2");
                }
                else
                {
                    rowValues[c] = "??";
                }

                var w = GetTextWidth(rowValues[c]);
                if (w > widthsFallback[c])
                {
                    widthsFallback[c] = w;
                }
            }

            allRowsFallback.Add(new DatRow(r, rowValues));
        }

        for (var i = 0; i < widthsFallback.Length; i++)
        {
            widthsFallback[i] += 20;
        }

        var maxIndexStr = (rowCount - 1).ToString();
        var indexWidth = Math.Max(GetTextWidth("Row"), GetTextWidth(maxIndexStr)) + 20;

        var finalWidths = new List<double> { indexWidth };
        finalWidths.AddRange(widthsFallback);

        var itemsFallback = new ObservableCollection<DatRow>(allRowsFallback);
        return new DatRowInfo(itemsFallback, headersFallback, finalWidths);
    }

    private static int GetBaseTypeSize(string type, bool is64Bit)
    {
        return type switch
        {
            "bool" => 1,
            "i16" or "u16" => 2,
            "i32" or "u32" or "f32" or "enumrow" => 4,
            "i64" or "u64" => 8,
            "string" or "row" => is64Bit ? 8 : 4,
            "foreignrow" or "rid" => is64Bit ? 16 : 4,
            _ => 1
        };
    }

    private string ReadArrayColumn(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset)
    {
        var count = is64Bit ? BitConverter.ToInt64(data, offset) : BitConverter.ToInt32(data, offset);
        var relOffset = is64Bit ? BitConverter.ToInt64(data, offset + 8) : BitConverter.ToInt32(data, offset + 4);

        if (count < 0 || count > 10000)
        {
            return $"[{count}] (Invalid Count)"; // Sanity check
        }

        var ptr = baseOffset + relOffset;

        // Create a fake column for the element logic
        var elemCol = new SchemaColumn
        {
            Name = col.Name,
            Description = col.Description,
            Array = false,
            Type = col.Type,
            Unique = col.Unique,
            References = col.References,
            Interval = col.Interval
        };
        var elemSize = GetColumnSize(elemCol, is64Bit);

        var sb = new StringBuilder();
        sb.Append("[");

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            if (baseOffset < 0 || ptr < 0 || ptr + i * elemSize + elemSize > data.Length)
            {
                sb.Append("ERR");
                break;
            }

            var elemOffset = (int)(ptr + i * elemSize);

            // Recursive call for element
            var val = ReadColumnValue(data, elemOffset, elemCol, is64Bit, baseOffset);
            sb.Append(val);

            if (i >= 50)
            {
                sb.Append($", ... {count - i - 1} more");
                break;
            }
        }

        sb.Append("]");
        return sb.ToString();
    }

    private string ReadIntervalColumn(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset)
    {
        var subCol = new SchemaColumn
        {
            Name = col.Name,
            Type = col.Type,
            Interval = false,
            References = col.References,
            Array = false
        };
        var subSize = GetColumnSize(subCol, is64Bit);

        if (offset + subSize * 2 > data.Length)
        {
            return "ERR_INTV";
        }

        var v1 = ReadColumnValue(data, offset, subCol, is64Bit, baseOffset);
        var v2 = ReadColumnValue(data, offset + subSize, subCol, is64Bit, baseOffset);
        return $"({v1}, {v2})";
    }

    private static string ReadPrimitiveColumn(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset)
    {
        return col.Type switch
        {
            "bool" => data[offset] != 0 ? "True" : "False",
            "i16" => BitConverter.ToInt16(data, offset).ToString(),
            "i32" => BitConverter.ToInt32(data, offset).ToString(),
            "i64" => BitConverter.ToInt64(data, offset).ToString(),
            "u16" => BitConverter.ToUInt16(data, offset).ToString(),
            "u32" => BitConverter.ToUInt32(data, offset).ToString(),
            "u64" => BitConverter.ToUInt64(data, offset).ToString(),
            "f32" => BitConverter.ToSingle(data, offset).ToString("F2"),
            "f64" => BitConverter.ToDouble(data, offset).ToString("F2"),
            "string" => ReadString(data, offset, is64Bit, baseOffset),
            "foreignrow" => ReadForeignRow(data, offset, is64Bit),
            "row" => ReadRow(data, offset, is64Bit),
            "enumrow" => ReadEnumRow(data, offset),
            _ => "??"
        };
    }

    private static string ReadString(byte[] data, int offset, bool is64Bit, long baseOffset)
    {
        var sRel = is64Bit ? BitConverter.ToInt64(data, offset) : BitConverter.ToUInt32(data, offset);
        var sPtr = baseOffset + sRel;

        if (baseOffset >= 0 && sPtr >= 0 && sPtr < data.Length)
        {
            var start = (int)sPtr;
            var len = 0;
            while (start + len + 1 < data.Length)
            {
                if (data[start + len] == 0 && data[start + len + 1] == 0)
                {
                    break;
                }

                len += 2;
            }

            return Encoding.Unicode.GetString(data, start, len);
        }

        return $"Ptr({sRel:X})";
    }

    private static string ReadForeignRow(byte[] data, int offset, bool is64Bit)
    {
        if (IsAllFe(data, offset, is64Bit ? 8 : 4))
        {
            return "null";
        }

        var key = is64Bit ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt32(data, offset);
        return $"FK({key:X})";
    }

    private static string ReadRow(byte[] data, int offset, bool is64Bit)
    {
        if (IsAllFe(data, offset, is64Bit ? 8 : 4))
        {
            return "null";
        }

        var rKey = is64Bit ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt32(data, offset);
        return $"Row({rKey:X})";
    }

    private static string ReadEnumRow(byte[] data, int offset)
    {
        if (IsAllFe(data, offset, 4))
        {
            return "null";
        }

        var eKey = BitConverter.ToInt32(data, offset);
        return $"Enum({eKey:X})";
    }

    private static double GetTextWidth(string text)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TypeFace,
            14,
            null
        );
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static bool IsAllFe(byte[] data, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (data[offset + i] != 0xFE)
            {
                return false;
            }
        }

        return true;
    }

    private record struct ParsingContext(
        byte[] Data,
        int RowCount,
        int EffectiveRowSize,
        long SeparatorIndex,
        List<SchemaColumn> Columns,
        bool Is64Bit,
        int ExtraBytes,
        double[] ColumnWidths
    );
}