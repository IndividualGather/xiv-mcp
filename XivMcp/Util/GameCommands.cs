using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>Runs game (not Dalamud) text commands, as if typed into the chat box.</summary>
internal static class GameCommands
{
    /// <summary>Must be called on the framework thread.</summary>
    public static unsafe void Execute(string command)
    {
        if (!command.StartsWith('/') || command.Length > 400 || command.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ToolException("Refusing to send malformed command.");
        if (!Svc.Framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Game commands must run on the framework thread.");

        var str = Utf8String.FromString(command);
        try { UIModule.Instance()->ProcessChatBoxEntry(str, IntPtr.Zero, false); }
        finally { str->Dtor(true); }
    }

    /// <summary>Collects system/error chat and log messages while it is alive, so a command's feedback can be returned.</summary>
    public sealed class Feedback : IDisposable
    {
        private readonly ConcurrentQueue<string> messages = new();

        public Feedback()
        {
            Svc.Chat.ChatMessage += OnChat;
            Svc.Chat.LogMessage += OnLog;
        }

        public List<string> Messages => [.. messages];

        private void OnChat(IChatMessage m)
        {
            if (m.LogKind is XivChatType.ErrorMessage or XivChatType.SystemMessage)
                messages.Enqueue(m.Message.TextValue);
        }

        private void OnLog(ILogMessage m)
        {
            if (m.GameData.ValueNullable is { } row && row.Text.ExtractText() is { Length: > 0 } text)
                messages.Enqueue(text);
        }

        public void Dispose()
        {
            Svc.Chat.ChatMessage -= OnChat;
            Svc.Chat.LogMessage -= OnLog;
        }
    }
}
