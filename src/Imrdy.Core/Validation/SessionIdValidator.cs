namespace Imrdy.Core.Validation;

/// <summary>
/// The codebase's one definition of an acceptable session id: alphanumerics, hyphen and
/// underscore, which is the shape Claude Code's UUID-format ids already have.
/// <para>
/// It exists because a session id becomes a filename. Rejecting path separators, <c>..</c>,
/// drive letters and every other unsafe character here is what keeps a composed path inside
/// the sessions directory. The hook has validated on this rule since before the network
/// existed; the ingest seam validates on the same one, because an id arriving off the wire or
/// off another machine's mount is strictly lower trust than one the local hook supplied.
/// </para>
/// <para>
/// It is also why <c>session_id</c> is the one field the hook log leaves unescaped: nothing
/// that passes here can carry a control character.
/// </para>
/// </summary>
public static class SessionIdValidator
{
    /// <summary>True when this id is safe to compose into a filename.</summary>
    public static bool IsValid(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        foreach (var c in sessionId)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
