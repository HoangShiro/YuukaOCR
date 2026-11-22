namespace YuukaOCR.Core.Physics;

/// <summary>
/// Critically-damped-ish spring integrator used by every Yuuka window.
///
/// The Python build adds velocity *per frame* (no dt multiply), so it is only correct when the timer
/// fires 60 times a second. This keeps the same scale (1 time unit = one 60 Hz frame) but multiplies by
/// real dt, so the same elastic feel stays smooth at any refresh rate (60, 144 Hz...).
/// </summary>
public sealed class SpringMotion
{
    /// <summary>Largest integration step = one 60 Hz frame; a bigger dt is subdivided.</summary>
    private const double MaxSubStepSeconds = 1.0 / 60.0;

    public double X { get; private set; }
    public double Y { get; private set; }
    public double VelocityX { get; private set; }
    public double VelocityY { get; private set; }
    public double TargetX { get; private set; }
    public double TargetY { get; private set; }

    public double SpringConstant { get; set; } = 0.10;
    public double DampingFactor { get; set; } = 0.45;
    public double BounceDamping { get; set; } = 0.30;

    public SpringMotion(double x = 0, double y = 0)
    {
        X = TargetX = x;
        Y = TargetY = y;
    }

    public void SnapTo(double x, double y)
    {
        X = TargetX = x;
        Y = TargetY = y;
        VelocityX = VelocityY = 0;
    }

    public void SetTarget(double x, double y)
    {
        TargetX = x;
        TargetY = y;
    }

    public void Nudge(double velocityY)
    {
        VelocityY += velocityY;
    }

    public bool IsSettled =>
        Math.Abs(TargetX - X) < 0.1 && Math.Abs(TargetY - Y) < 0.1 &&
        Math.Abs(VelocityX) < 0.1 && Math.Abs(VelocityY) < 0.1;

    /// <summary>Advances the simulation. Returns true when the window moved.</summary>
    public bool Step(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return false;

        var moved = false;

        foreach (var frame in SubSteps(elapsedSeconds))
            moved |= Integrate(frame);

        return moved;
    }

    /// <summary>Steps the simulation to a target position and clamps inside the given bounds.</summary>
    public bool Step(double elapsedSeconds, double minX, double minY, double maxX, double maxY)
    {
        if (elapsedSeconds <= 0) return false;

        var moved = false;

        foreach (var frame in SubSteps(elapsedSeconds))
        {
            moved |= Integrate(frame);
            Clamp(minX, minY, maxX, maxY);
        }

        return moved;
    }

    /// <summary>Split real dt into steps of at most 1/60 s, expressed in "60 Hz frame" units.</summary>
    private static IEnumerable<double> SubSteps(double elapsedSeconds)
    {
        var remaining = Math.Min(elapsedSeconds, 0.1);   // prevents a jump after a stall
        while (remaining > 1e-6)
        {
            var dt = Math.Min(remaining, MaxSubStepSeconds);
            remaining -= dt;
            yield return dt * 60.0;                       // 1.0 = exactly one 60 Hz frame
        }
    }

    private bool Integrate(double frame)
    {
        if (IsSettled)
        {
            X = TargetX;
            Y = TargetY;
            VelocityX = VelocityY = 0;
            return false;
        }

        var forceX = (TargetX - X) * SpringConstant - VelocityX * DampingFactor;
        var forceY = (TargetY - Y) * SpringConstant - VelocityY * DampingFactor;

        VelocityX += forceX * frame;
        VelocityY += forceY * frame;
        X += VelocityX * frame;
        Y += VelocityY * frame;
        return true;
    }

    private void Clamp(double minX, double minY, double maxX, double maxY)
    {
        if (X < minX)
        {
            X = minX;
            VelocityX = -VelocityX * BounceDamping;
        }
        else if (X > maxX)
        {
            X = maxX;
            VelocityX = -VelocityX * BounceDamping;
        }

        if (Y < minY)
        {
            Y = minY;
            VelocityY = -VelocityY * BounceDamping;
        }
        else if (Y > maxY)
        {
            Y = maxY;
            VelocityY = -VelocityY * BounceDamping;
        }
    }
}
