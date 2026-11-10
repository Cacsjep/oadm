# Oadm.Client

The Avalonia client of OADM (see `CLAUDE.md` "Architecture"). Every UI check is a headless test in
`tests/Oadm.Client.Tests`; this file lists the manual tests that need a real desktop, a real browser or an account.

## Manual tests

### MyAxis sign-in (all platforms)

Spec: `docs/spec/myaxis.md`. Needs a MyAxis account and a server with `MyAxisService`.

1. Log in, click the cloud button left of Help: the window "Sign in to MyAxis" shows Axis's login page.
2. Sign in with the account (and the one-time code). After the login the page returns to
   `https://www.axis.com/ftp/pub_soft/`, the window shows "Checking the sign-in" and closes.
3. The button's tooltip says "Signed in to MyAxis"; a click opens the panel "Signed in to MyAxis since <today>" with
   Sign out. The audit log has the sign-in. The client log and the server log never contain the cookie value.
4. Close the client and open it again: still signed in. Sign out: the tooltip says "Sign in to MyAxis".
5. Sign in again: the login page asks for the password again (the browser kept nothing). Check that nothing of
   axis.com is left on disk: Windows `%LOCALAPPDATA%\Oadm\WebView2` has no new cookies of axis.com (InPrivate);
   macOS no new website data of OADM; Linux with WPE no `oadm-myaxis-*` folder in the temp folder after the window closed.
6. Log out, log in as another user: that user is signed out.

### MyAxis sign-in on WebKitGTK (Ubuntu 24.04, Debian 12)

`MyAxis/WebKitGtkCookies` reads the cookies through WebKitGTK itself (the package has no cookie manager there). Run
the client on Ubuntu 24.04 with `libwebkit2gtk-4.1-0` and without WPE WebKit (`libwpewebkit-2.0`), so the package
uses WebKitGTK:

1. Do the steps of "MyAxis sign-in" above. Step 2 must close the window: the glue read `axis_connect_session_sid`.
2. In a second run, sign in with a wrong password first, then the right one: no crash, the window closes after the
   right one.
3. Close the window while it shows "Checking the sign-in": no crash, the client keeps running.
4. Run once on Wayland (the package forces the X11 backend of GTK) and once on X11.
5. Debian 12 (`libwebkit2gtk-4.1-0`): the same as step 1.

When a package release reads WebKitGTK cookies itself, remove the glue and this section.
