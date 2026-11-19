using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Core.Mail;

/// <summary>Załącznik wiadomości (np. pełny dziennik agenta jako plik tekstowy).</summary>
public sealed record MailAttachment(string FileName, byte[] Content, string ContentType = "text/plain; charset=utf-8");

/// <summary>Gotowa wiadomość: tekst i HTML jako alternatywy tej samej treści, opcjonalnie z załącznikami.</summary>
public sealed record MailMessage(
    IReadOnlyList<string> To, string Subject, string TextBody, string HtmlBody, IReadOnlyList<MailAttachment>? Attachments = null)
{
    /// <summary>Stały identyfikator wiadomości (bez nawiasów &lt;&gt;) – wyliczany z klucza kolejki maili,
    /// więc ewentualny drugi egzemplarz ma ten sam Message-ID (Gmail go scala). null = nada go biblioteka poczty.</summary>
    public string? MessageId { get; init; }
}

public interface IMailGateway
{
    /// <exception cref="MailTransportException">Błąd połączenia lub logowania do serwera poczty.</exception>
    Task SendAsync(MailMessage message, CancellationToken cancellationToken);
}

/// <summary>Błąd połączenia lub uwierzytelnienia SMTP; ponowna próba ma sens dopiero później.</summary>
/// <param name="maybeDelivered">false – serwer na pewno nie dostał treści (błąd połączenia albo logowania), więc mail można
/// wysłać jeszcze raz bez ryzyka duplikatu; true – przerwa w trakcie wysyłania treści, wynik niepewny.</param>
public sealed class MailTransportException(string message, Exception innerException, bool maybeDelivered = false)
    : Exception(message, innerException)
{
    public bool MaybeDelivered { get; } = maybeDelivered;
}

/// <param name="IsReminder">true = przypomnienie o alercie, o którym już wysłano wiadomość.</param>
public sealed record AlertNotice(SensorConfig Sensor, AlertRecord Alert, StoredReading? Latest, bool IsReminder);

/// <summary>Stan jednego czujnika do raportu: ostatni odczyt i otwarte alerty.</summary>
public sealed record SensorSnapshot(SensorConfig Sensor, StoredReading? Latest, IReadOnlyList<AlertKind> OpenAlerts)
{
    /// <summary>
    /// Ile historii trafia do raportu: wykres obejmuje 3 doby, a zapas pozwala wypełnić pierwszy przedział, który zaczyna się
    /// na pełnej godzinie przed tym oknem.
    /// </summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(75);

    /// <summary>
    /// Odczyty z ostatnich dób (dowolna kolejność) do wykresu w raporcie (<see cref="MailChart"/>); pusta lista = karta bez
    /// wykresu. Nadawca wypełnia ją odczytami z <see cref="HistoryWindow"/>.
    /// </summary>
    public IReadOnlyList<StoredReading> History { get; init; } = [];
}
