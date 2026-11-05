using System.Runtime.CompilerServices;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Add-ons of fake mode (docs/spec/add-ons.md): Text recognition, not installed. Install walks through Upload, Check and
/// Install and adds its package (off, group Vision) to the plugin list; Remove turns it off and marks it removed. The client
/// part has no files (fake mode loads the plugins of the checkout).
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The add-on of fake mode.</summary>
    public const string TextRecognitionAddOnId = "oadm.ocr";

    private AddOnState _textRecognitionState = AddOnState.AddOnNotInstalled;
    private AddOnState _previousState = AddOnState.AddOnNotInstalled;

    /// <summary>Tests: a server build without the hash (Install stays off, the row says why, no link).</summary>
    public bool AddOnsWithoutHash { get; set; }

    /// <summary>Tests: the link the server gives (default: the release of the fake version).</summary>
    public string? AddOnDownloadUrl { get; set; }

    /// <summary>Tests: right after a Cancel the server still lists the add-on as Installing.</summary>
    public bool AddOnServerStillClearsUp { get; set; }

    /// <summary>Tests: the file the last install was given.</summary>
    public string? LastAddOnUpload { get; private set; }

    /// <summary>Tests: the next install ends with this error instead of done.</summary>
    public string? NextAddOnInstallError { get; set; }

    /// <summary>Tests: hold every install step until this task completes (null: run through).</summary>
    public Func<AddOnStep, int, Task>? AddOnInstallGate { get; set; }

    /// <summary>Tests: the client part GetAddOnClientPart returns (default: no files).</summary>
    public AddOnClientPart? AddOnClientPart { get; set; }

    /// <summary>Tests: the version of the add-on on the server (a server update brings a newer one).</summary>
    public string AddOnVersion { get; set; } = FakeServerVersion;

    /// <summary>Tests: the add-on of an older OADM version runs and an update is offered.</summary>
    public bool AddOnUpdateAvailable { get; set; }

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

    /// <summary>Tests: the state of the add-on (an install another administrator started, after an update, ...).</summary>
    public AddOnState TextRecognitionState
    {
        get
        {
            lock (_gate)
            {
                return _textRecognitionState;
            }
        }

        set
        {
            lock (_gate)
            {
                _textRecognitionState = value;
            }
        }
    }

    public async IAsyncEnumerable<AddOnProgress> InstallAddOnAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAddOn(id);
            if (_textRecognitionState != AddOnState.AddOnRemoved)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Update this client to install optional plugins."));
            }

            _textRecognitionState = AddOnState.AddOnInstalled;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        yield return new AddOnProgress { Step = AddOnStep.Install, Percent = 100, Done = true };
    }

    public async IAsyncEnumerable<AddOnProgress> UploadAddOnAsync(string id, string path, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAddOn(id);
            if (_textRecognitionState is AddOnState.AddOnInstalled or AddOnState.AddOnInstalling)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Text recognition is installed."));
            }

            LastAddOnUpload = path;
            _previousState = _textRecognitionState;
            _textRecognitionState = AddOnState.AddOnInstalling;
        }

        string? error = NextAddOnInstallError;
        NextAddOnInstallError = null;
        foreach (var (step, percent) in new[]
                 {
                     (AddOnStep.Upload, 0), (AddOnStep.Upload, 42), (AddOnStep.Upload, 100), (AddOnStep.Check, 100), (AddOnStep.Install, 100),
                 })
        {
            if (AddOnInstallGate is { } gate)
            {
                try
                {
                    await gate(step, percent).WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TextRecognitionState = AddOnServerStillClearsUp ? AddOnState.AddOnInstalling : _previousState; // stopped: the state as it was
                    throw;
                }
            }
            else if (_tick > TimeSpan.Zero)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            }

            yield return new AddOnProgress { Step = step, Percent = percent };
            if (error is not null && step == AddOnStep.Check)
            {
                TextRecognitionState = _previousState; // refused: the state as it was
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
            if (_textRecognitionState is not (AddOnState.AddOnInstalled or AddOnState.AddOnFailed))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Text recognition is not installed."));
            }

            _textRecognitionState = _textRecognitionState == AddOnState.AddOnFailed ? AddOnState.AddOnNotInstalled : AddOnState.AddOnRemoved;
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

            return Task.FromResult(AddOnClientPart ?? new AddOnClientPart { Id = TextRecognitionAddOnId, Version = AddOnVersion });
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
            Description = "Reads text in camera pictures.",
            State = _textRecognitionState,
            Version = AddOnVersion,
            UpdateAvailable = AddOnUpdateAvailable,
            CanInstall = _textRecognitionState == AddOnState.AddOnRemoved || (!AddOnsWithoutHash && _textRecognitionState != AddOnState.AddOnInstalling),
            Message = _textRecognitionState switch
            {
                AddOnState.AddOnRemoved => "Removed. The files go at the next restart of the server.",
                AddOnState.AddOnFailed => "Not loaded. Install it again for this OADM version.",
                AddOnState.AddOnNotInstalled when AddOnsWithoutHash => "This OADM build cannot check optional plugins. Put the plugin into its folder by hand.",
                _ => string.Empty,
            },
            DownloadUrl = AddOnsWithoutHash
                ? string.Empty
                : AddOnDownloadUrl ?? $"{Oadm.Contracts.Updates.GitHubReleaseFeed.DownloadPrefix}v{AddOnVersion}/oadm-addon-{TextRecognitionAddOnId}-{AddOnVersion}-win-x64.zip",
            ClientDigest = _textRecognitionState == AddOnState.AddOnInstalled ? AddOnClientPart?.Digest ?? string.Empty : string.Empty,
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
        var package = Package(TextRecognitionAddOnId, "Text recognition", "Reads text in camera pictures.", "Vision",
            hasPage: true, tasks: 0, enabledByDefault: false);
        package.Enabled = _packageStates.GetValueOrDefault(TextRecognitionAddOnId, false);
        return package;
    }
}
