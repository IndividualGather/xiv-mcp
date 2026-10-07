using System;

namespace XivMcp.Api;

/// <summary>Plugin names as callers write them, made comparable.</summary>
public static class PluginNames
{
    /// <summary>
    /// Trims spaces and the dots and spaces Windows drops from the end of file and folder names ("XivMcp." is the folder "XivMcp"), so
    /// a name can't slip past a check by a spelling that leads to the same files.
    /// </summary>
    public static string Clean(string name) => name.Trim().TrimEnd('.', ' ');

    public static bool Same(string a, string b) => Clean(a).Equals(Clean(b), StringComparison.OrdinalIgnoreCase);
}
