namespace Bridge.Protocol;

public sealed record CapabilitiesPayload(string[] Features);

public sealed record DeviceStatusPayload(int BatteryPercent, bool Charging, string ConnectionQuality);

public sealed record ClipboardUpdatePayload(string Format, string Data);

/// <summary>
/// Announces a file. Optional fields (null = absent on the wire):
/// <see cref="Resumable"/> — the sender waits for <c>file.resume</c> before sending chunks (peer advertised <c>files.resume</c>);
/// <see cref="RelativePath"/> — folder path of this file inside a folder transfer, '/'-separated, without the file name;
/// <see cref="BatchId"/>/<see cref="BatchIndex"/>/<see cref="BatchCount"/> — groups the files of one folder transfer.
/// </summary>
public sealed record FileStartPayload(string TransferId, string FileName, long Size, string Sha256, int TotalChunks)
{
    public bool? Resumable { get; init; }
    public string? RelativePath { get; init; }
    public string? BatchId { get; init; }
    public int? BatchIndex { get; init; }
    public int? BatchCount { get; init; }
}

/// <summary>Receiver → sender: continue from chunk <see cref="NextIndex"/> (0 = from the start).</summary>
public sealed record FileResumePayload(string TransferId, int NextIndex);

public sealed record FileChunkPayload(string TransferId, int Index, string Data);

public sealed record FileEndPayload(string TransferId);

public sealed record FileAckPayload(string TransferId, string Status); // ok | hash_mismatch | cancelled

public sealed record FileCancelPayload(string TransferId);

/// <summary>
/// A phone notification. Optional fields (null = absent on the wire, sent by phones with
/// <c>notifications.actions</c>): <see cref="AppId"/> — the app's package name (for muting it);
/// <see cref="Actions"/> — up to 3 buttons the PC can press; <see cref="CanDismiss"/> — the PC may clear it on the phone.
/// </summary>
public sealed record NotificationPostPayload(string AppName, string Title, string Text, string Key)
{
    public string? AppId { get; init; }
    public NotificationActionInfo[]? Actions { get; init; }
    public bool? CanDismiss { get; init; }
}

/// <summary>A notification button. <see cref="Reply"/> = true: it takes typed text (an inline reply).</summary>
public sealed record NotificationActionInfo(string Id, string Title)
{
    public bool? Reply { get; init; }
}

/// <summary>Phone → PC: the notification is gone. PC → phone (<c>notifications.actions</c>): clear it on the phone.</summary>
public sealed record NotificationDismissPayload(string Key);

/// <summary>PC → phone: press button <see cref="ActionId"/> of notification <see cref="Key"/>; <see cref="ReplyText"/> only for reply buttons.</summary>
public sealed record NotificationActionPayload(string RequestId, string Key, string ActionId)
{
    public string? ReplyText { get; init; }
}

/// <summary>Phone → PC: outcome of a <c>notification.action</c> (see <see cref="NotificationActionStatus"/>).</summary>
public sealed record NotificationActionResultPayload(string RequestId, string Status);

/// <summary>PC → phone: stop mirroring notifications from this app (the user can undo it on the phone).</summary>
public sealed record NotificationMutePayload(string AppId);

public sealed record ErrorPayload(string Code, string Message);

public sealed record PairingRequestPayload(string PairingToken, string DeviceName, string PublicKeyBase64);

public sealed record PairingAcceptPayload(string DeviceName, string PublicKeyBase64);

public sealed record PairingRejectPayload(string Reason);

/// <summary>
/// Contents of the pairing QR code. <see cref="PublicKeyBase64"/> is the PC's
/// long-term identity key (SubjectPublicKeyInfo); the phone uses it to check
/// the PC's handshake signature before revealing the pairing token.
/// </summary>
public sealed record PairingQrPayload(string PairingToken, string Host, int Port, string DeviceId, string DeviceName, string PublicKeyBase64);

public static class FileAckStatus
{
    public const string Ok = "ok";
    public const string HashMismatch = "hash_mismatch";
    public const string Cancelled = "cancelled";
}

public static class NotificationActionStatus
{
    /// <summary>Done.</summary>
    public const string Ok = "ok";
    /// <summary>The notification, or that button, is no longer on the phone.</summary>
    public const string Gone = "gone";
    /// <summary>The phone doesn't mirror that app/notification, so it won't act on it.</summary>
    public const string Refused = "refused";
    /// <summary>Malformed request (reply text missing or too long, text for a non-reply button…).</summary>
    public const string Invalid = "invalid";
    /// <summary>The app on the phone didn't accept it.</summary>
    public const string Failed = "failed";
    /// <summary>Local only (never on the wire): no answer, or no connected phone that supports actions.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>One photo on the phone. <see cref="Id"/> is the phone's library id (digits); <see cref="TakenAt"/> is epoch ms.</summary>
public sealed record PhotoInfo(string Id, string Name, long TakenAt, int Width, int Height, long Size, string Mime);

/// <summary>PC → phone: newest photos first, up to <see cref="Limit"/> (≤ 100), older than cursor <see cref="Before"/>.</summary>
public sealed record PhotosListPayload(string RequestId, int Limit)
{
    public string? Before { get; init; }
}

/// <summary>Phone → PC: a page of <see cref="PhotosListPayload"/>; <see cref="Next"/> is the cursor for the next page (absent at the end).</summary>
public sealed record PhotosPagePayload(string RequestId, string Status, PhotoInfo[] Items)
{
    public string? Next { get; init; }
}

/// <summary>PC → phone: a small JPEG preview of photo <see cref="Id"/>.</summary>
public sealed record PhotosThumbPayload(string RequestId, string Id);

/// <summary>Phone → PC: the preview (base64 JPEG, at most 320 px on its long side) or why not.</summary>
public sealed record PhotosThumbnailPayload(string RequestId, string Id, string Status)
{
    public string? Mime { get; init; }
    public string? Data { get; init; }
}

/// <summary>PC → phone: send the original of photo <see cref="Id"/> (it arrives as a normal file transfer).</summary>
public sealed record PhotosFetchPayload(string RequestId, string Id);

/// <summary>Phone → PC: the fetch was accepted (<see cref="TransferId"/> is the file transfer that follows) or why not.</summary>
public sealed record PhotosFetchedPayload(string RequestId, string Id, string Status)
{
    public string? TransferId { get; init; }
}

public static class PhotoStatus
{
    public const string Ok = "ok";
    /// <summary>The user hasn't turned on "Let your PC browse photos" on the phone.</summary>
    public const string Disabled = "disabled";
    /// <summary>Android hasn't given Bridge access to photos.</summary>
    public const string NoPermission = "no_permission";
    public const string NotFound = "not_found";
    /// <summary>Too many requests waiting; ask again shortly.</summary>
    public const string Busy = "busy";
    public const string Invalid = "invalid";
    public const string Error = "error";
    /// <summary>Local only (never on the wire): no answer, or no connected phone with photos.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Handoff: a link (<see cref="Kind"/> "url": <see cref="Url"/>, optional <see cref="Title"/>) or a piece of text
/// ("text": <see cref="Text"/>) to continue with on the other device. Answered by handoff.ack with the same id.
/// </summary>
public sealed record HandoffPayload(string Id, string Kind)
{
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
}

/// <summary>Outcome of a handoff (see <see cref="HandoffStatus"/>).</summary>
public sealed record HandoffAckPayload(string Id, string Status);

public static class HandoffKind
{
    public const string Url = "url";
    public const string Text = "text";
}

public static class HandoffStatus
{
    /// <summary>A notification offers to open the link.</summary>
    public const string Shown = "shown";
    /// <summary>The link was opened (only where the user allowed it).</summary>
    public const string Opened = "opened";
    /// <summary>The text is now on the clipboard.</summary>
    public const string Copied = "copied";
    /// <summary>The receiving side has handoff turned off.</summary>
    public const string Disabled = "disabled";
    /// <summary>Not an http(s) link, a link with a password in it, too long, empty…</summary>
    public const string Invalid = "invalid";
    /// <summary>Too many in a short time.</summary>
    public const string Busy = "busy";
    /// <summary>Local only: not sent (no phone, or it doesn't support handoff) or no answer.</summary>
    public const string Unavailable = "unavailable";
}
