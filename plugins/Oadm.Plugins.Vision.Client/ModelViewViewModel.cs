using System.Collections.ObjectModel;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// "What the model sees" of the selected Detect objects or Classify node (docs/spec/vision-center.md "Check and tune"):
/// the model's input of the preview's latest (or held) frame as the server draws it, a Classify node on objects one tile per
/// object. Read when the node is selected, after its model changed (with the next frame, so the server has the change) and
/// on Refresh; never live. Only for users with Live view.
/// </summary>
public sealed partial class ModelViewViewModel : ObservableObject
{
    private readonly Func<string, string, Task<ModelViewReply?>> _read;
    private readonly Func<byte[], Task<Bitmap?>> _decode;
    private readonly bool _maySee;
    private string? _key;
    private bool _pending;
    private int _version;

    /// <param name="read">The server's model view of (preview id, node id); null when no preview runs.</param>
    /// <param name="previewId">The preview's id now.</param>
    public ModelViewViewModel(Func<string, string, Task<ModelViewReply?>> read, Func<string?> previewId, Func<byte[], Task<Bitmap?>> decode, bool maySee)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        PreviewIdOf = previewId ?? throw new ArgumentNullException(nameof(previewId));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _maySee = maySee;
    }

    public static string Title => VisionTexts.WhatTheModelSees;

    public static string RefreshTip => VisionTexts.RefreshModelView;

    private Func<string?> PreviewIdOf { get; }

    /// <summary>A Detect objects or Classify node is selected and the user may see pictures.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>The model's input of the whole picture (Detect objects, Classify on a picture).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    public partial Bitmap? Picture { get; private set; }

    public bool HasPicture => Picture is not null;

    /// <summary>A Classify node on objects: one tile per object, at most 8.</summary>
    public ObservableCollection<Bitmap> Tiles { get; } = [];

    [ObservableProperty]
    public partial bool HasTiles { get; private set; }

    /// <summary>"576x576 · Stretch".</summary>
    [ObservableProperty]
    public partial string? Caption { get; private set; }

    /// <summary>Why there is no picture ("No picture yet.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The node this view is of.</summary>
    public string? NodeId { get; private set; }

    /// <summary>The selection changed: shown for a model node, read at once.</summary>
    public void Select(NodeViewModel? node)
    {
        var shows = _maySee && node is { Kind: NodeKinds.Detect or NodeKinds.Classify };
        IsShown = shows;
        var key = shows ? KeyOf(node!) : null;
        if (!shows)
        {
            _key = null;
            NodeId = null;
            Clear(null);
            return;
        }

        if (key == _key)
        {
            return;
        }

        _key = key;
        NodeId = node!.Id;
        Clear(null);
        _ = ReadAsync();
    }

    /// <summary>The draft changed: a new model of the node is read with the next frame (the server has the change then).</summary>
    public void DraftChanged(NodeViewModel? node)
    {
        if (IsShown && node is not null && node.Id == NodeId && KeyOf(node) != _key)
        {
            _key = KeyOf(node);
            _pending = true;
        }
    }

    /// <summary>A preview frame came: read when a change waits, or when there was no picture yet.</summary>
    public void FrameArrived()
    {
        if (IsShown && !IsLoading && (_pending || Problem == PipelineRules.NoPictureYet))
        {
            _pending = false;
            _ = ReadAsync();
        }
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReadAsync();

    /// <summary>Reads the view again (tests await it).</summary>
    public async Task ReadAsync()
    {
        if (NodeId is not { } nodeId || PreviewIdOf() is not { } previewId)
        {
            Clear(PipelineRules.NoPictureYet);
            return;
        }

        var version = ++_version;
        IsLoading = true;
        try
        {
            ModelViewReply? reply;
            try
            {
                reply = await _read(previewId, nodeId).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // A failed read: the reason below the title, Refresh stays.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (version == _version)
                {
                    Clear(ex.Message);
                }

                return;
            }

            var pictures = new List<Bitmap>();
            foreach (var bytes in reply?.Pictures ?? [])
            {
                if (await _decode(bytes).ConfigureAwait(true) is { } bitmap)
                {
                    pictures.Add(bitmap);
                }
            }

            if (version != _version || NodeId != nodeId)
            {
                return; // a newer read or another node
            }

            Clear(reply?.Problem ?? (pictures.Count == 0 ? PipelineRules.NoPictureYet : null));
            Caption = reply is { InputWidth: > 0 } r ? VisionTexts.ModelViewCaption(r.InputWidth, r.InputHeight, r.Fit) : null;
            if (pictures.Count == 1 && reply?.Whole == true)
            {
                Picture = pictures[0];
            }
            else
            {
                foreach (var picture in pictures)
                {
                    Tiles.Add(picture);
                }

                HasTiles = Tiles.Count > 0;
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
            }
        }
    }

    private void Clear(string? problem)
    {
        Picture = null;
        Tiles.Clear();
        HasTiles = false;
        Problem = problem;
        if (problem is not null)
        {
            Caption = null;
        }
    }

    private static string KeyOf(NodeViewModel node) =>
        node.Id + "\u001f" + (node.Settings[NodeKinds.ModelKey]?.ToJsonString() ?? string.Empty)
        + "\u001f" + (node.Settings[NodeKinds.PointsKey]?.ToJsonString() ?? string.Empty);
}
