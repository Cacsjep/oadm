using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tracking;

/// <summary>
/// A constant-velocity Kalman filter on a box: centre x, centre y, width and height, each with its speed per second. The
/// four axes are filtered apart (no cross terms), with the noise scaled to the box height as ByteTrack does.
/// </summary>
internal sealed class BoxFilter
{
    // Per axis: position, speed and the covariance [p00 p01; p01 p11].
    private readonly double[] _pos = new double[4];
    private readonly double[] _vel = new double[4];
    private readonly double[] _p00 = new double[4];
    private readonly double[] _p01 = new double[4];
    private readonly double[] _p11 = new double[4];

    // Standard deviations as shares of the box height (position, speed per second, measurement).
    private const double PositionNoise = 1.0 / 20;
    private const double SpeedNoise = 1.0 / 4;
    private const double MeasurementNoise = 1.0 / 20;

    public BoxFilter(RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        Set(box);
        var h = Math.Max(box.Height, 0.01);
        for (var i = 0; i < 4; i++)
        {
            _vel[i] = 0;
            _p00[i] = Sq(2 * MeasurementNoise * h);
            _p01[i] = 0;
            _p11[i] = Sq(10 * SpeedNoise * h);
        }
    }

    /// <summary>The filtered box now (after <see cref="Predict"/> the predicted one).</summary>
    public RelativeBox Box
    {
        get
        {
            var w = Math.Max(_pos[2], 0.001);
            var h = Math.Max(_pos[3], 0.001);
            return new RelativeBox(_pos[0] - w / 2, _pos[1] - h / 2, w, h);
        }
    }

    /// <summary>Moves the state <paramref name="seconds"/> ahead.</summary>
    public void Predict(double seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        var h = Math.Max(_pos[3], 0.01);
        var qp = Sq(PositionNoise * h) * seconds;
        var qv = Sq(SpeedNoise * h) * seconds;
        for (var i = 0; i < 4; i++)
        {
            _pos[i] += _vel[i] * seconds;
            var p00 = _p00[i] + 2 * seconds * _p01[i] + seconds * seconds * _p11[i] + qp;
            var p01 = _p01[i] + seconds * _p11[i];
            _p00[i] = p00;
            _p01[i] = p01;
            _p11[i] += qv;
        }
    }

    /// <summary>Corrects the state with a measured box.</summary>
    public void Update(RelativeBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var z = Values(box);
        var r = Sq(MeasurementNoise * Math.Max(box.Height, 0.01));
        for (var i = 0; i < 4; i++)
        {
            var s = _p00[i] + r;
            var k0 = _p00[i] / s;
            var k1 = _p01[i] / s;
            var y = z[i] - _pos[i];
            _pos[i] += k0 * y;
            _vel[i] += k1 * y;
            var p00 = (1 - k0) * _p00[i];
            var p01 = (1 - k0) * _p01[i];
            var p11 = _p11[i] - k1 * _p01[i];
            _p00[i] = p00;
            _p01[i] = p01;
            _p11[i] = p11;
        }
    }

    private void Set(RelativeBox box)
    {
        var v = Values(box);
        Array.Copy(v, _pos, 4);
    }

    private static double[] Values(RelativeBox box) => [box.X + box.Width / 2, box.Y + box.Height / 2, box.Width, box.Height];

    private static double Sq(double v) => v * v;
}
