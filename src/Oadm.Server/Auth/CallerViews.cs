using Grpc.Core;

using Oadm.Core.DeviceGroups;

namespace Oadm.Server.Auth;

/// <summary>
/// How a gRPC service gets its devices (docs/spec/device-scope.md, Enforcement): <c>await views.ForAsync(context)</c> gives
/// the <see cref="DeviceView"/> of the call's authenticated caller (from the call's user state, set by
/// <see cref="AuthInterceptor"/>, never from the AsyncLocal backstop). Without a caller it answers UNAUTHENTICATED; it never
/// allows everything.
/// </summary>
public static class CallerViews
{
    /// <summary>The view of the caller of <paramref name="context"/>.</summary>
    public static Task<DeviceView> ForAsync(this DeviceViewFactory views, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(context);
        return views.ForAsync(AuthInterceptor.CallerOf(context), context.CancellationToken);
    }
}
