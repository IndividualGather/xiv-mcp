using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace XivMcp.Util;

/// <summary>Reflection helpers to work with any Lumina Excel sheet generically.</summary>
internal static class Excel
{
    private static readonly Lazy<Dictionary<string, Type>> SheetTypes = new(() =>
        typeof(Lumina.Excel.Sheets.Item).Assembly.GetTypes()
            .Where(t => t.IsValueType && t.Namespace == "Lumina.Excel.Sheets" && t.GetCustomAttribute<SheetAttribute>() is not null)
            .Where(t => !IsSubrowType(t))
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase));

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly ConcurrentDictionary<Type, IEnumerable> SheetCache = new();

    private static readonly string[] NameProperties = ["Name", "Singular", "Masculine", "Text", "Description", "Title"];

    public static IReadOnlyDictionary<string, Type> Sheets => SheetTypes.Value;

    public static Type ResolveSheet(string name) =>
        SheetTypes.Value.TryGetValue(name.Replace("_", ""), out var t)
            ? t
            : throw new Mcp.ToolException($"Unknown sheet '{name}'. Use list_game_sheets to see available sheets.");

    private static bool IsSubrowType(Type t) =>
        t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IExcelSubrow", StringComparison.Ordinal));

    /// <summary>Returns the ExcelSheet&lt;T&gt; for a row type as a non-generic enumerable of boxed rows.</summary>
    public static IEnumerable GetSheet(Type rowType) => SheetCache.GetOrAdd(rowType, t =>
    {
        var method = typeof(Dalamud.Plugin.Services.IDataManager).GetMethod(nameof(Dalamud.Plugin.Services.IDataManager.GetExcelSheet))!
            .MakeGenericMethod(t);
        return (IEnumerable)method.Invoke(Svc.Data, [null, null])!;
    });

    public static object? GetRow(Type rowType, uint rowId)
    {
        var sheet = GetSheet(rowType);
        var method = sheet.GetType().GetMethod("GetRowOrDefault", [typeof(uint)]);
        return method?.Invoke(sheet, [rowId]); // boxed Nullable<T> -> either null or boxed T
    }

    public static uint RowId(object row) => row.GetType().GetProperty("RowId")?.GetValue(row) is uint id ? id : 0;

    /// <summary>A best-effort human readable name for a row (Name, Singular, ... or a well known reference).</summary>
    public static string? DisplayName(object? row, int depth = 0)
    {
        if (row is null || depth > 2) return null;
        var type = row.GetType();
        foreach (var propName in NameProperties)
        {
            var prop = type.GetProperty(propName);
            if (prop?.PropertyType == typeof(ReadOnlySeString))
            {
                var text = SafeGet(prop, row) is ReadOnlySeString s ? s.ExtractText() : null;
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        // Rows that are named through a reference, e.g. Aetheryte.PlaceName, Recipe.ItemResult, Leve.Name.
        foreach (var refName in new[] { "PlaceName", "ItemResult", "Item", "Quest", "Content" })
        {
            var prop = type.GetProperty(refName);
            if (prop is not null && IsRowRef(prop.PropertyType) && SafeGet(prop, row) is { } rowRef)
            {
                var name = DisplayName(RefValue(rowRef), depth + 1);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return null;
    }

    public static string? Name<T>(RowRef<T> rowRef) where T : struct, IExcelRow<T> =>
        rowRef.IsValid ? DisplayName(rowRef.Value) : null;

    /// <summary>{ id, name } for a row reference — the standard shape used across all tools.</summary>
    public static object? Ref<T>(RowRef<T> rowRef) where T : struct, IExcelRow<T> =>
        rowRef.RowId == 0 && !rowRef.IsValid ? null : new { id = rowRef.RowId, name = Name(rowRef) };

    public static object Ref<T>(uint rowId) where T : struct, IExcelRow<T> =>
        new { id = rowId, name = Svc.Data.GetExcelSheet<T>().GetRowOrDefault(rowId) is { } row ? DisplayName(row) : null };

    private static bool IsRowRef(Type t) =>
        t == typeof(RowRef) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(RowRef<>));

    private static object? RefValue(object rowRef)
    {
        var t = rowRef.GetType();
        if (!t.IsGenericType) return null;
        return t.GetProperty("ValueNullable")?.GetValue(rowRef);
    }

    private static object? SafeGet(PropertyInfo prop, object target)
    {
        try { return prop.GetValue(target); }
        catch { return null; }
    }

    private static PropertyInfo[] Properties(Type t) => PropertyCache.GetOrAdd(t, type =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.Name is not ("ExcelPage" or "Offset" or "Subrow" or "SubrowId"))
            .Where(p => p.PropertyType != typeof(ExcelPage))
            .ToArray());

    /// <summary>Converts a Lumina row (or any value inside it) into plain JSON-friendly objects.</summary>
    public static object? ToPlain(object? value, int depth = 0, int maxDepth = 2)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or char:
                return value;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return value;
            case float f:
                return float.IsFinite(f) ? f : null;
            case double d:
                return double.IsFinite(d) ? d : null;
            case Enum e:
                return e.ToString();
            case ReadOnlySeString s:
                return s.ExtractText();
            case RowRef untyped:
                return new Dictionary<string, object?> { ["id"] = untyped.RowId };
        }

        var type = value.GetType();
        if (IsRowRef(type))
        {
            var rowId = type.GetProperty("RowId")?.GetValue(value);
            var target = RefValue(value);
            var result = new Dictionary<string, object?> { ["id"] = rowId, ["sheet"] = type.GetGenericArguments()[0].Name };
            if (target is not null && DisplayName(target) is { } name) result["name"] = name;
            return result;
        }

        if (value is IEnumerable enumerable)
        {
            if (depth >= maxDepth + 1) return "[...]";
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                if (list.Count >= 64) { list.Add("..."); break; }
                list.Add(ToPlain(item, depth + 1, maxDepth));
            }
            return list;
        }

        if (depth > maxDepth) return value.ToString();

        var dict = new Dictionary<string, object?>();
        foreach (var prop in Properties(type))
        {
            var v = SafeGet(prop, value);
            dict[prop.Name] = ToPlain(v, depth + 1, maxDepth);
        }
        return dict;
    }

    /// <summary>Converts "TripleTriadCard" to "triple_triad_card".</summary>
    public static string SnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
