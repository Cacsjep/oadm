using System.Runtime.CompilerServices;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Add-ons of fake mode (docs/spec/add-ons.md): Text recognition, not installed. Install walks through Download, Check and
/// Install and adds its package (off, group Vision) to the plugin list; Remove turns it off and marks it removed. The client
/// part has no files (fake mode loads the plugins of the checkout).
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The add-on of fake mode.</summary>
    public const string TextRecognitionAddOnId = "oadm.ocr";

    private AddOnState _textRecognitionState = AddOnState.AddOnNotInstalled;

    /// <summary>Tests: the server cannot reach the release (Install stays off, the row says why).</summary>
    public bool AddOnsUnreachable { get; set; }

    /// <summary>Tests: the next install ends with this error instead of done.</summary>
    public string? NextAddOnInstallError { get; set; }

    /// <summary>Tests: hold every install step until this task completes (null: run through).</summary>
    public Func<AddOnStep, int, Task>? AddOnInstallGate { get; set; }

    /// <summary>Tests: the client part GetAddOnClientPart returns (default: no files).</summary>
    public AddOnClientPart? AddOnClientPart { get; set; }

    /// <summary>Tests: how often the client part was asked for.</summary>
    public int AddOnClientPartCalls { get; private set; }

    public Task<AddOnList> ListAddOnsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(FakeAddOnList());
        }
    }

    public async IAsyncEnumerable<AddOnProgress> InstallAddOnAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAddOn(id);
            if (_textRecognitionState is AddOnState.AddOnInstalled or AddOnState.AddOnInstalling or AddOnState.AddOnRemoved)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Text recognition is installed."));
            }

            _textRecognitionState = AddOnState.AddOnInstalling;
        }

        string? error = NextAddOnInstallError;
        NextAddOnInstallError = null;
        foreach (var (step, percent) in new[]
                 {
                     (AddOnStep.Download, 0), (AddOnStep.Download, 42), (AddOnStep.Download, 100), (AddOnStep.Check, 100), (AddOnStep.Install, 100),
                 })
        {
            if (AddOnInstallGate is { } gate)
            {
                await gate(step, percent).ConfigureAwait(false);
            }
            else if (_tick > TimeSpan.Zero)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            }

            yield return new AddOnProgress { Step = step, Percent = percent };
            if (error is not null && step == AddOnStep.Check)
            {
                lock (_gate)
                {
                    _textRecognitionState = AddOnState.AddOnNotInstalled;
                }

                yield return new AddOnProgress { Step = step, Percent = percent, Error = error };
                yield break;
            }
        }

        lock (_gate)
        {
            _textRecognitionState = AddOnState.AddOnInstalled;
        }

        yield return new AddOnProgress { Step = AddOnStep.Install, Percent = 100, Done = true };
    }

    public Task<AddOnList> RemoveAddOnAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAddOn(id);
            if (_textRecognitionState != AddOnState.AddOnInstalled)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Text recognition is not installed."));
            }

            _textRecognitionState = AddOnState.AddOnRemoved;
            _packageStates[TextRecognitionAddOnId] = false;
            return Task.FromResult(FakeAddOnList());
        }
    }

    public Task<AddOnClientPart> GetAddOnClientPartAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAddOn(id);
            AddOnClientPartCalls++;
            if (_textRecognitionState is not (AddOnState.AddOnInstalled or AddOnState.AddOnRemoved))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Text recognition is not installed."));
            }

            return Task.FromResult(AddOnClientPart ?? new AddOnClientPart { Id = TextRecognitionAddOnId, Version = FakeServerVersion });
        }
    }

    private static void CheckAddOn(string id)
    {
        if (!string.Equals(id, TextRecognitionAddOnId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Unknown optional plugin '{id}'."));
        }
    }

    /// <summary>The add-on list. Call under the lock.</summary>
    private AddOnList FakeAddOnList()
    {
        var list = new AddOnList();
        list.AddOns.Add(new AddOnInfo
        {
            Id = TextRecognitionAddOnId,
            DisplayName = "Text recognition",
            Description = "Reads text in camera pictures for the event rules.",
            SizeBytes = 38_000_000,
            State = _textRecognitionState,
            Version = FakeServerVersion,
            CanInstall = !AddOnsUnreachable,
            Message = AddOnsUnreachable && _textRecognitionState == AddOnState.AddOnNotInstalled ? "The server cannot reach github.com." : string.Empty,
        });
        return list;
    }

    /// <summary>The package of an installed (or removed, still loaded) add-on in the plugin list. Call under the lock.</summary>
    private IEnumerable<PluginPackageInfo> FakeAddOnPackages() =>
        _textRecognitionState is AddOnState.AddOnInstalled or AddOnState.AddOnRemoved
            ? [TextRecognitionPackage()]
            : [];

    private PluginPackageInfo TextRecognitionPackage()
    {
        var package = Package(TextRecognitionAddOnId, "Text recognition", "Reads text in camera pictures for the event rules.", "Vision",
            hasPage: true, tasks: 0, enabledByDefault: false);
        package.Enabled = _packageStates.GetValueOrDefault(TextRecognitionAddOnId, false);
        return package;
    }
}
