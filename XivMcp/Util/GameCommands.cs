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

    /// <summary>
    /// Collects the game's system and error notices (chat and log messages) while it is alive, so a command's feedback can be
    /// returned. Battle log lines, chat and dialogue arriving at the same time are left out (<see cref="Ui.GameFeedback"/>).
    /// </summary>
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
            if (Ui.GameFeedback.IsNotice((int)m.LogKind) && Ui.GameFeedback.Clean(m.Message.TextValue) is { } text)
                messages.Enqueue(text);
        }

        private void OnLog(ILogMessage m)
        {
            // The log message's own channel (its LogKind row is the chat type) decides: damage and effect lines
            // ("... for  damage.") are not an answer.
            if (m.GameData.ValueNullable is { } row && Ui.GameFeedback.IsNotice((int)row.LogKind.RowId) &&
                Ui.GameFeedback.Clean(row.Text.ExtractText()) is { } text)
                messages.Enqueue(text);
        }

        public void Dispose()
        {
            Svc.Chat.ChatMessage -= OnChat;
            Svc.Chat.LogMessage -= OnLog;
        }
    }
}
