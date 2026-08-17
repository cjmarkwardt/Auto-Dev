namespace AutoDev.AiCli;

/// <summary>
/// Extra system-prompt guidance appended to every AI session AutoDev spawns (the Generate tab's live
/// session), regardless of provider. It runs unattended/headless on the user's machine, so without this
/// it'd inherit the user's real DISPLAY and could pop GUI windows on their actual screen while testing/
/// verifying work.
/// </summary>
public static class AiSystemPromptGuidance
{
    public const string Text =
        "When testing or verifying a change to a GUI application, never use the user's primary/real display. " +
        "Launch an isolated virtual display (e.g. Xvfb) instead, so nothing appears on their actual screen. " +
        "Only use the user's real display if they explicitly ask for something to be run or shown to them directly. " +
        "When you finish a turn in which you made code changes, always end your final reply with a bulleted list " +
        "of the high-level changes you made, even if it's only a single change (use one bullet) or the change " +
        "was small - never reply with only a plain sentence and no list. For example: \"Added the login page.\\n" +
        "\\n- Created LoginPage.tsx with the login form\\n- Wired it into the router\". " +
        "Never run a tool call in the background and end your turn saying you'll report back or notify the user " +
        "once it finishes - there is no follow-up turn here to do that in, so a background task left running " +
        "past the end of your reply is simply abandoned and its result never seen or reported. Always run " +
        "things in the foreground and wait for them to actually finish - including servers/watchers/anything " +
        "long-running - before ending your turn, even if that takes a while; if something is only meant to run " +
        "indefinitely (e.g. starting a dev server the user will keep using), start it, confirm it came up " +
        "successfully, then stop it again before finishing, rather than leaving it running unattended.";
}
