namespace Oadm.Plugins.Vision.Model;

/// <summary>Every limit of Vision Center in one place (docs/spec/vision-center.md "Limits", "Load", "Models").</summary>
public static class VisionLimits
{
    public static readonly IReadOnlyList<int> FpsChoices = [1, 2, 4, 8, 12, 15];

    public const int DefaultFps = 8;

    public const int MaxPipelines = 50;

    public const int MaxNodes = 32;

    /// <summary>Detect objects and Classify nodes on one path from the camera.</summary>
    public const int MaxModelNodesPerPath = 2;

    public const int MaxNameLength = 64;

    /// <summary>Objects a detection keeps per picture, the highest scores first.</summary>
    public const int MaxObjects = 300;

    /// <summary>Objects Classify works on per picture.</summary>
    public const int MaxClassifiedPerPicture = 16;

    /// <summary>A tracked object is classified once, then again after this.</summary>
    public static readonly TimeSpan ReclassifyEvery = TimeSpan.FromSeconds(2);

    /// <summary>The margin around a box for Classify: a share of its size.</summary>
    public const double ClassifyMargin = 0.10;

    public const int MaxModels = 20;

    public const long MaxModelBytes = 500L * 1024 * 1024;

    public const long MaxAllModelsBytes = 4L * 1024 * 1024 * 1024;

    public const int MaxClasses = 1000;

    /// <summary>The class name of a gap in a model's class ids (COCO's 91 ids).</summary>
    public const string NoClassName = "N/A";

    /// <summary>The upload's chunk size (base64 in a page call).</summary>
    public const int UploadChunkBytes = 1024 * 1024;

    /// <summary>An upload without a chunk for this long ends.</summary>
    public static readonly TimeSpan UploadIdleTimeout = TimeSpan.FromMinutes(1);

    public const int DefaultInputSize = 640;

    /// <summary>The size a classification model with a free input gets (ImageNet models are trained at 224 x 224).</summary>
    public const int DefaultClassificationInputSize = 224;

    /// <summary>Non-maximum suppression of the YOLO format.</summary>
    public const double SuppressionIoU = 0.45;

    /// <summary>Watching pipelines per inference worker, and the floor and ceiling.</summary>
    public const int WatchingPerWorker = 2;

    public const int MinWatching = 4;

    public const int MaxWatching = 50;

    public const int MaxWatchingPerSite = 4;

    /// <summary>Frames a pipeline has in work while the workers have room.</summary>
    public const int MaxFramesInWork = 4;

    public const int MaxPreviews = 8;

    public const int MaxPreviewsPerUser = 2;

    public static readonly TimeSpan PreviewLease = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan PreviewKeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>The live lines of the page: at most this often per pipeline or preview.</summary>
    public static readonly TimeSpan LiveEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>Count changed: at most once per this.</summary>
    public static readonly TimeSpan CountChangedEvery = TimeSpan.FromSeconds(1);

    /// <summary>The last crossings and event fires kept per node for the page.</summary>
    public const int RecentPerNode = 20;

    /// <summary>A watching pipeline whose models fail this many times in a row has a problem.</summary>
    public const int FailedFramesForProblem = 3;

    /// <summary>The action Run pipeline: a watching pipeline answers with a frame of at most this age.</summary>
    public static readonly TimeSpan FreshFrame = TimeSpan.FromSeconds(1);

    /// <summary>Inference workers on the CPU: half the cores, at least 1.</summary>
    public static int Workers(int cores) => Math.Max(1, cores / 2);

    /// <summary>Watching pipelines on a server with <paramref name="cores"/>.</summary>
    public static int MaxWatchingFor(int cores) => Math.Clamp(Workers(cores) * WatchingPerWorker, MinWatching, MaxWatching);
}
