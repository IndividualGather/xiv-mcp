using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Reflection into another loaded plugin that offers no IPC for what XIV MCP needs: its static members and its settings, looked up
/// by name so an update that renames one gives a clear error instead of wrong behaviour. Framework thread.
/// </summary>
internal sealed class ForeignPlugin(string internalName, string displayName)
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The loaded plugin object, or null.</summary>
    private object? Plugin()
    {
        var local = DalamudInternals.InstalledPlugins().FirstOrDefault(p => DalamudInternals.InternalName(p) == internalName && DalamudInternals.IsLoaded(p));
        return local?.GetType().GetField("instance", Instance)?.GetValue(local);
    }

    public bool Loaded => Plugin() is not null;

    /// <summary>The folder the plugin's DLL was installed to (its Assembly.Location can be empty when loaded from memory).</summary>
    public string? Directory
    {
        get
        {
            var local = DalamudInternals.InstalledPlugins().FirstOrDefault(p => DalamudInternals.InternalName(p) == internalName && DalamudInternals.IsLoaded(p));
            if (local is not null && DalamudInternals.Prop(local, "DllFile") is System.IO.FileInfo dll) return dll.DirectoryName;
            var location = Plugin()?.GetType().Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : System.IO.Path.GetDirectoryName(location);
        }
    }

    public Assembly Assembly =>
        Plugin()?.GetType().Assembly ?? throw new ToolException($"{displayName} is not loaded. Install or enable it in the plugin installer.");

    public ToolException Unsupported(string what) =>
        new($"This version of {displayName} is not supported ({what} is missing). Update XIV MCP, or tell its developers.");

    public Type Type(string fullName) => Assembly.GetType(fullName) ?? throw Unsupported(fullName);

    public object? StaticValue(string type, string member)
    {
        var t = Type(type);
        if (t.GetField(member, Static) is { } f) return f.GetValue(null);
        if (t.GetProperty(member, Static) is { } p) return p.GetValue(null);
        throw Unsupported($"{type}.{member}");
    }

    public void CallStatic(string type, string method)
    {
        var m = Type(type).GetMethod(method, Static, []) ?? throw Unsupported($"{type}.{method}()");
        m.Invoke(null, null);
    }

    public object? Get(object target, string member)
    {
        var t = target.GetType();
        if (t.GetProperty(member, Instance) is { } p) return p.GetValue(target);
        if (t.GetField(member, Instance) is { } f) return f.GetValue(target);
        throw Unsupported($"{t.Name}.{member}");
    }

    public void Set(object target, string member, object? value)
    {
        var t = target.GetType();
        if (t.GetProperty(member, Instance) is { CanWrite: true } p) p.SetValue(target, Coerce(value, p.PropertyType));
        else if (t.GetField(member, Instance) is { } f) f.SetValue(target, Coerce(value, f.FieldType));
        else throw Unsupported($"{t.Name}.{member}");
    }

    private static object? Coerce(object? value, Type type) =>
        value is null || type.IsInstanceOfType(value) ? value : type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type);

    /// <summary>Sets members of <paramref name="target"/> until the result is disposed, then puts the old values back.</summary>
    public IDisposable Override(object target, params (string Member, object? Value)[] settings)
    {
        var undo = new List<Action>();
        try
        {
            foreach (var (member, value) in settings)
            {
                var old = Get(target, member);
                Set(target, member, value);
                undo.Add(() => Set(target, member, old));
            }
        }
        catch
        {
            foreach (var u in Enumerable.Reverse(undo)) u();
            throw;
        }
        return new Restore(undo);
    }

    private sealed class Restore(List<Action> undo) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            foreach (var u in Enumerable.Reverse(undo))
            {
                try { u(); }
                catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not restore a setting of another plugin: {ex.Message}"); }
            }
        }
    }
}
