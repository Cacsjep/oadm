using System.Reflection;
using System.Text.RegularExpressions;

using Grpc.Core;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// The capability type (docs/spec/device-scope.md, Tests): the gRPC classes get their devices from a <see cref="DeviceView"/>,
/// never from the global repository, and the services they use take <see cref="ViewedDevices"/>, never raw device ids.
/// Every exception is on an allow-list with a reason; today's services that are not converted yet say which package
/// converts them, and each package removes its entries.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>The types that read devices by id without a view (system work only).</summary>
    private static readonly Type[] GlobalDeviceAccess =
    [
        typeof(DeviceRepository), typeof(Sdk.Devices.IDeviceRepository), typeof(IDeviceChangeFeed), typeof(DeviceChangeFeed),
    ];

    /// <summary>
    /// gRPC classes that may take the global device access, with the reason. "converted in &lt;package&gt;" entries are today's
    /// services; the package removes the entry when it moves the class to <see cref="DeviceViewFactory"/>.
    /// </summary>
    internal static readonly Dictionary<string, string> GrpcAllowList = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// Service methods behind the gRPC classes that still take raw device ids, with the reason (type.method). Each package
    /// adds a <see cref="ViewedDevices"/> overload, moves its gRPC class to it and removes the entry.
    /// </summary>
    internal static readonly Dictionary<string, string> ServiceAllowList = new(StringComparer.Ordinal)
    {
    };

    private static IEnumerable<Type> GrpcServiceClasses() =>
        typeof(Auth.AuthInterceptor).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.BaseType?.GetCustomAttribute<BindServiceMethodAttribute>() is not null);

    [Fact]
    public void TheGrpcClassesGetTheirDevicesFromAViewNotFromTheGlobalRepository()
    {
        var found = new List<string>();
        foreach (var type in GrpcServiceClasses())
        {
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                if (GlobalDeviceAccess.Contains(parameter.ParameterType))
                {
                    found.Add($"{type.Name}({parameter.ParameterType.Name})");
                }
            }
        }

        var unlisted = found.Where(f => !GrpcAllowList.ContainsKey(f)).ToList();
        Assert.True(unlisted.Count == 0, "gRPC classes that take the global device access without a reason on the allow-list: " + string.Join(", ", unlisted));

        // The allow-list shrinks: an entry whose class no longer takes it must go.
        var stale = GrpcAllowList.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that are no longer needed (remove them): " + string.Join(", ", stale));
        Assert.All(GrpcAllowList.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void TheServicesBehindTheGrpcClassesTakeViewedDevicesNotRawDeviceIds()
    {
        var services = GrpcServiceClasses()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType)
            .Where(t => t.Assembly == typeof(DeviceRepository).Assembly || t.Assembly == typeof(Auth.AuthInterceptor).Assembly)
            .Where(t => !GlobalDeviceAccess.Contains(t))
            .Distinct()
            .ToList();

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            foreach (var method in service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetParameters().Any(IsRawDeviceIds))
                {
                    found.Add($"{service.Name}.{method.Name}");
                }
            }
        }

        var unlisted = found.Where(f => !ServiceAllowList.ContainsKey(f) && !NotConvertedServiceMethods.ContainsKey(f)).ToList();
        Assert.True(unlisted.Count == 0, "Service methods that take raw device ids without a reason on the allow-list: " + string.Join(", ", unlisted));
        var stale = ServiceAllowList.Keys.Concat(NotConvertedServiceMethods.Keys).Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that are no longer needed (remove them): " + string.Join(", ", stale));
    }

    [Fact]
    public void ViewedDevicesAreMadeOnlyByTheView()
    {
        var sources = Directory.EnumerateFiles(TestRepository.Path("src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(TestRepository.Path("plugins"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var makers = sources.Where(f => NewViewedDevices().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        Assert.Equal(["DeviceView.cs"], makers);

        // Its constructor is not public: only the core (DeviceView) can call it.
        Assert.All(typeof(ViewedDevices).GetConstructors(BindingFlags.Public | BindingFlags.Instance), _ => Assert.Fail("ViewedDevices has a public constructor."));
    }

    /// <summary>Service methods with raw device ids that a later package converts (type.method → package).</summary>
    internal static readonly Dictionary<string, string> NotConvertedServiceMethods = new(StringComparer.Ordinal)
    {
        ["DeviceTagStore.SetDeviceTagsAsync"] = "shared-core",
        ["TaskEngine.RunAsync"] = "tasks",
        ["TaskPluginQueries.QueryAsync"] = "tasks",
        ["ActionRunner.TestAsync"] = "shared-core",
        ["ComplianceService.ListChoicesAsync"] = "shared-core",
        ["EventLog.Write"] = "shared-core",
    };

    private static bool IsRawDeviceIds(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var isIds = type == typeof(Guid) || type == typeof(Guid?)
            || (type != typeof(string) && type.GetInterfaces().Concat([type]).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && i.GetGenericArguments()[0] == typeof(Guid)));
        return isIds && (parameter.Name ?? string.Empty).Contains("device", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"new\s+ViewedDevices\s*\(")]
    private static partial Regex NewViewedDevices();
}
