namespace S2AWH;

/// <summary>
/// A zero-allocation flat memory structure to hold all necessary player spatial state per tick.
/// Prevents O(N^2) native property reads across the interop boundary in visibility evaluators.
/// </summary>
internal struct PlayerTransformSnapshot
{
    public bool IsValid;

    // Absolute Origin
    public float OriginX;
    public float OriginY;
    public float OriginZ;

    // Collision Bounding Box offsets
    public float MinsX;
    public float MinsY;
    public float MinsZ;
    public float MaxsX;
    public float MaxsY;
    public float MaxsZ;

    // Cached center
    public float CenterX;
    public float CenterY;
    public float CenterZ;

    // ViewOffset (Eye Position relative to Origin)
    public float ViewOffsetX;
    public float ViewOffsetY;
    public float ViewOffsetZ;

    // Absolute Velocity
    public float VelocityX;
    public float VelocityY;
    public float VelocityZ;

    // View Angles
    public float EyeAnglesPitch;
    public float EyeAnglesYaw;

    // Pre-calculated FOV normal vector from EyeAngles
    public float FovNormalX;
    public float FovNormalY;
    public float FovNormalZ;

    // Eye position (Origin + ViewOffset)
    public float EyeX;
    public float EyeY;
    public float EyeZ;
}
