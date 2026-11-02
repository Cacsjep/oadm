using System.Globalization;

namespace Oadm.Client.MyAxis;

/// <summary>The texts of the MyAxis sign-in (docs/spec/myaxis.md), one phrase per concept.</summary>
public static class MyAxisTexts
{
    public const string SignIn = "Sign in to MyAxis";
    public const string SignedIn = "Signed in to MyAxis";
    public const string SignInAgain = "Sign in to MyAxis again";
    public const string SignOut = "Sign out";
    public const string WindowTitle = "Sign in to MyAxis";
    public const string Checking = "Checking the sign-in";
    public const string NotAccepted = "MyAxis did not accept the sign-in. Try again.";
    public const string NotAvailable = "Sign-in is not available here.";

    /// <summary>"Signed in to MyAxis since 2 Nov 2026" (the local date).</summary>
    public static string SignedInSince(DateTimeOffset since) =>
        SignedIn + " since " + since.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
