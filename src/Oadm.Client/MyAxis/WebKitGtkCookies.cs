using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The cookies of a WebKitGTK browser (docs/spec/myaxis.md "Reading the cookie"): Avalonia.Controls.WebView 12.1.1 has no
/// cookie manager for WebKitGTK, so the sign-in window asks WebKit itself, through the public
/// <c>IGtkWebViewPlatformHandle.WebKitWebView</c>. Linux only, used only when the package returns none; only reads.
/// The calls run on the GLib main context (the package's GTK thread) via <c>g_main_context_invoke</c>; the callbacks
/// are static unmanaged functions and the state travels as a <see cref="GCHandle"/> until the last one ran. Any failure
/// is "no cookie" (an empty list), a missing library is "no way to the cookies" (null); it never throws.
/// Manual test: <c>src/Oadm.Client/README.md</c> "MyAxis sign-in on WebKitGTK".
/// Remove once a package release reads WebKitGTK cookies itself.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class WebKitGtkCookies
{
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string Soup = "libsoup-3.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    /// <summary>
    /// The cookies WebKit holds for <paramref name="address"/>. <paramref name="webView"/> is the <c>WebKitWebView*</c>
    /// of the window's open browser.
    /// </summary>
    public static async Task<IReadOnlyList<Cookie>?> ReadAsync(IntPtr webView, Uri address, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(time);
        if (webView == IntPtr.Zero || !Loads(WebKit) || !Loads(Soup))
        {
            return null;
        }

        var read = new Read(webView, address.AbsoluteUri);
        var handle = GCHandle.Alloc(read);
        try
        {
            // Keeps the browser alive until the GLib thread took the cookie manager from it (the window may close).
            g_object_ref(webView);
            Invoke(GCHandle.ToIntPtr(handle));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            handle.Free();
            return null;
        }

        try
        {
            return await read.Done.Task.WaitAsync(timeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The callback may still run later: it frees the handle then.
            return [];
        }
    }

    /// <summary>Whether the library is there (a missing one would fail only inside a callback).</summary>
    private static bool Loads(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>Runs <see cref="Start"/> on the GLib main context (the thread that owns it, or now when none does).</summary>
    private static unsafe void Invoke(IntPtr data) => g_main_context_invoke(g_main_context_default(), &Start, data);

    /// <summary>One read: the browser, the address, the answer. Freed by the last callback.</summary>
    private sealed class Read(IntPtr webView, string address)
    {
        public IntPtr WebView { get; } = webView;
        public string Address { get; } = address;
        public TaskCompletionSource<IReadOnlyList<Cookie>?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>On the GLib thread: asks the browser's cookie manager for the cookies (GSourceFunc, runs once).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Start(IntPtr data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var read = (Read)handle.Target!;
        var started = false;
        try
        {
            var context = webkit_web_view_get_context(read.WebView);
            var manager = context == IntPtr.Zero ? IntPtr.Zero : webkit_web_context_get_cookie_manager(context);
            if (manager != IntPtr.Zero)
            {
                webkit_cookie_manager_get_cookies(manager, read.Address, IntPtr.Zero, &Finished, data);
                started = true;
            }
        }
#pragma warning disable CA1031 // an exception must never leave an unmanaged callback
        catch (Exception)
#pragma warning restore CA1031
        {
            started = false;
        }
        finally
        {
            Unref(read.WebView);
        }

        if (!started)
        {
            read.Done.TrySetResult([]);
            handle.Free();
        }

        return 0; // G_SOURCE_REMOVE
    }

    /// <summary>On the GLib thread: the answer (GAsyncReadyCallback): the GList of SoupCookie, read and freed.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Finished(IntPtr manager, IntPtr result, IntPtr data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var read = (Read)handle.Target!;
        var cookies = new List<Cookie>();
        try
        {
            IntPtr error = IntPtr.Zero;
            var list = webkit_cookie_manager_get_cookies_finish(manager, result, &error);
            if (error != IntPtr.Zero)
            {
                g_error_free(error);
            }

            for (var node = list; node != IntPtr.Zero; node = ((GList*)node)->Next)
            {
                var cookie = ((GList*)node)->Data;
                if (cookie == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var name = Marshal.PtrToStringUTF8(soup_cookie_get_name(cookie));
                    var value = Marshal.PtrToStringUTF8(soup_cookie_get_value(cookie)) ?? "";
                    var domain = Marshal.PtrToStringUTF8(soup_cookie_get_domain(cookie)) ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        cookies.Add(new Cookie { Name = name, Value = value, Domain = domain });
                    }
                }
                catch (CookieException)
                {
                    // a name or value System.Net does not take: not the session cookie
                }
                finally
                {
                    soup_cookie_free(cookie);
                }
            }

            if (list != IntPtr.Zero)
            {
                g_list_free(list);
            }
        }
#pragma warning disable CA1031 // an exception must never leave an unmanaged callback
        catch (Exception)
#pragma warning restore CA1031
        {
            cookies.Clear();
        }

        read.Done.TrySetResult(cookies);
        handle.Free();
    }

    private static void Unref(IntPtr webView)
    {
        try
        {
            g_object_unref(webView);
        }
#pragma warning disable CA1031 // an exception must never leave an unmanaged callback
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public IntPtr Data;
        public IntPtr Next;
        public IntPtr Prev;
    }

    [LibraryImport(GLib)]
    private static partial IntPtr g_main_context_default();

    [LibraryImport(GLib)]
    private static unsafe partial void g_main_context_invoke(IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, int> function, IntPtr data);

    [LibraryImport(GLib)]
    private static partial void g_list_free(IntPtr list);

    [LibraryImport(GLib)]
    private static partial void g_error_free(IntPtr error);

    [LibraryImport(GObject)]
    private static partial IntPtr g_object_ref(IntPtr instance);

    [LibraryImport(GObject)]
    private static partial void g_object_unref(IntPtr instance);

    [LibraryImport(WebKit)]
    private static partial IntPtr webkit_web_view_get_context(IntPtr webView);

    [LibraryImport(WebKit)]
    private static partial IntPtr webkit_web_context_get_cookie_manager(IntPtr context);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial void webkit_cookie_manager_get_cookies(IntPtr manager, string uri, IntPtr cancellable,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData);

    [LibraryImport(WebKit)]
    private static unsafe partial IntPtr webkit_cookie_manager_get_cookies_finish(IntPtr manager, IntPtr result, IntPtr* error);

    [LibraryImport(Soup)]
    private static partial IntPtr soup_cookie_get_name(IntPtr cookie);

    [LibraryImport(Soup)]
    private static partial IntPtr soup_cookie_get_value(IntPtr cookie);

    [LibraryImport(Soup)]
    private static partial IntPtr soup_cookie_get_domain(IntPtr cookie);

    [LibraryImport(Soup)]
    private static partial void soup_cookie_free(IntPtr cookie);
}
