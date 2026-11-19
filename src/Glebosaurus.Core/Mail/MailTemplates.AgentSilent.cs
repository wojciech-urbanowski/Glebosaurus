using System.Text;
using static Glebosaurus.Core.Mail.MailLayout;
using static Glebosaurus.Core.Mail.MailText;

namespace Glebosaurus.Core.Mail;

public static partial class MailTemplates
{
    /// <summary>
    /// Mail dla administratora: agent od dłuższego czasu nie synchronizuje się z chmurą – wyłączony, bez
    /// sieci albo zabity przez system (Android). Jego czujniki przejmą inni agenci, jeśli je słyszą.
    /// </summary>
    public static MailMessage AgentSilent(string agentName, DateTimeOffset lastSeenAt, IReadOnlyList<string> to, string monitorName,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        var when = Local(now, zone);
        var since = Local(lastSeenAt, zone);
        var silent = Age(now, lastSeenAt);
        const string title = "Agent milczy";
        var subject = $"Agent {agentName} milczy";
        var detail = $"Ostatni kontakt: {since} ({silent})";
        var body = "Agent nie łączy się z chmurą: może być wyłączony, bez sieci albo zatrzymany przez system. " +
                   "Jego czujniki przejmą inni agenci, jeśli je słyszą; inaczej przyjdą alerty o braku odczytów.";

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine($"{monitorName} · {when}");
        text.AppendLine();
        text.AppendLine(subject);
        foreach (var line in Wrap(detail, 38)) text.AppendLine(line);
        text.AppendLine();
        foreach (var line in Wrap(body, 38)) text.AppendLine(line);

        var rows = Header(title, monitorName, when) + Banner(subject, detail, Amber) + Paragraph(body) + Footer();
        return new(to, subject, WithSignature(text), Page(subject, detail, rows));
    }
}
