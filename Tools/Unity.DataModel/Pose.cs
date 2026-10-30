using System;

namespace Unity.DataModel
{
    public readonly struct Vector3
    {
        public Vector3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
        public static readonly Vector3 Zero = new Vector3(0, 0, 0);
        public static readonly Vector3 One = new Vector3(1, 1, 1);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }

    public readonly struct Quaternion
    {
        public Quaternion(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double W { get; }
        public double this[int i] => i == 0 ? X : i == 1 ? Y : i == 2 ? Z : W;
        public static readonly Quaternion Identity = new Quaternion(0, 0, 0, 1);

        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public Quaternion Inverse => new Quaternion(-X, -Y, -Z, W);

        public Quaternion Normalized
        {
            get
            {
                var l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                if (l == 0) l = 1;
                return new Quaternion(X / l, Y / l, Z / l, W / l);
            }
        }

        public Vector3 Rotate(Vector3 v)
        {
            var p = this * new Quaternion(v.X, v.Y, v.Z, 0) * Inverse;
            return new Vector3(p.X, p.Y, p.Z);
        }

        /// <summary>Unity's Quaternion.Euler (degrees; Z, then X, then Y).</summary>
        public static Quaternion Euler(double x, double y, double z)
        {
            var r = Math.PI / 360;
            return new Quaternion(0, Math.Sin(y * r), 0, Math.Cos(y * r))
                 * new Quaternion(Math.Sin(x * r), 0, 0, Math.Cos(x * r))
                 * new Quaternion(0, 0, Math.Sin(z * r), Math.Cos(z * r));
        }

        /// <summary>Euler angles in degrees, in Unity's order.</summary>
        public Vector3 EulerAngles
        {
            get
            {
                var sinX = 2 * (W * X - Y * Z);
                var ex = Math.Abs(sinX) >= 1 ? Math.Sign(sinX) * 90 : Math.Asin(sinX) * 180 / Math.PI;
                var ey = Math.Atan2(2 * (W * Y + X * Z), 1 - 2 * (X * X + Y * Y)) * 180 / Math.PI;
                var ez = Math.Atan2(2 * (W * Z + X * Y), 1 - 2 * (X * X + Z * Z)) * 180 / Math.PI;
                return new Vector3(ex, ey, ez);
            }
        }
    }

    /// <summary>Position, rotation and scale (a Transform's local values, or a world placement).</summary>
    public readonly struct Pose
    {
        public Pose(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Scale { get; }

        public static readonly Pose Identity = new Pose(Vector3.Zero, Quaternion.Identity, Vector3.One);

        /// <summary>parent ∘ child (no skew: scale multiplies per axis).</summary>
        public static Pose Compose(Pose parent, Pose child)
        {
            var scaled = new Vector3(parent.Scale.X * child.Position.X, parent.Scale.Y * child.Position.Y, parent.Scale.Z * child.Position.Z);
            var r = parent.Rotation.Rotate(scaled);
            return new Pose(
                new Vector3(parent.Position.X + r.X, parent.Position.Y + r.Y, parent.Position.Z + r.Z),
                (parent.Rotation * child.Rotation).Normalized,
                new Vector3(parent.Scale.X * child.Scale.X, parent.Scale.Y * child.Scale.Y, parent.Scale.Z * child.Scale.Z));
        }

        /// <summary>The child's local pose that puts it at <paramref name="world"/> under <paramref name="parent"/>.</summary>
        public static Pose ToLocal(Pose parent, Pose world)
        {
            var d = parent.Rotation.Inverse.Rotate(new Vector3(world.Position.X - parent.Position.X,
                world.Position.Y - parent.Position.Y, world.Position.Z - parent.Position.Z));
            static double Div(double a, double b) => Math.Abs(b) < 1e-12 ? a : a / b;
            return new Pose(
                new Vector3(Div(d.X, parent.Scale.X), Div(d.Y, parent.Scale.Y), Div(d.Z, parent.Scale.Z)),
                (parent.Rotation.Inverse * world.Rotation).Normalized,
                new Vector3(Div(world.Scale.X, parent.Scale.X), Div(world.Scale.Y, parent.Scale.Y), Div(world.Scale.Z, parent.Scale.Z)));
        }
    }
}
