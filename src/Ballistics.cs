using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SquadTools
{
    /// <summary>Map coordinates in metres: X points east and Y points south.</summary>
    public struct Vec2
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            return String.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", X, Y);
        }
    }

    public static class GridReference
    {
        public const double GridSize = 300.0;
        public const int MaxSubdivisions = 3;

        /// <summary>
        /// Reads a 300-metre grid square and up to three keypad subdivisions.
        /// The returned position is the centre of the smallest specified square.
        /// Keypad rows run 7 8 9 / 4 5 6 / 1 2 3, from north to south.
        /// </summary>
        public static Vec2 Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException("text");

            string[] parts = text.Trim().Split('-');
            if (parts.Length < 1 || parts.Length > MaxSubdivisions + 1)
                throw new FormatException("网格最多支持三级九宫格，例如 A01-5-2-9。");

            Match match = Regex.Match(parts[0], @"\A([A-Za-z])([0-9]{1,2})\z");
            int row;
            if (!match.Success ||
                !Int32.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row) ||
                row < 1 || row > 99)
                throw new FormatException("网格需使用 A01 至 Z99 的格式，行号从 01 开始。");

            int column = Char.ToUpperInvariant(match.Groups[1].Value[0]) - 'A';
            double x = column * GridSize;
            double y = (row - 1) * GridSize;
            double size = GridSize;

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length != 1 || parts[i][0] < '1' || parts[i][0] > '9')
                    throw new FormatException("每级九宫格必须是 1 至 9 的单个数字。");

                int keypad = parts[i][0] - '1';
                size /= 3.0;
                x += (keypad % 3) * size;
                y += (2 - keypad / 3) * size;
            }

            return new Vec2(x + size / 2.0, y + size / 2.0);
        }

        /// <summary>Returns the grid square containing the given map position.</summary>
        public static string Format(Vec2 position, int subdivisions)
        {
            if (subdivisions < 0 || subdivisions > MaxSubdivisions)
                throw new ArgumentOutOfRangeException("subdivisions", "九宫格精度必须在 0 至 3 之间。");
            if (!IsFinite(position.X) || !IsFinite(position.Y) ||
                position.X < 0 || position.X >= 26 * GridSize ||
                position.Y < 0 || position.Y >= 99 * GridSize)
                throw new ArgumentOutOfRangeException("position", "坐标必须位于 A01 至 Z99 范围内。");

            int column = (int)Math.Floor(position.X / GridSize);
            int row = (int)Math.Floor(position.Y / GridSize);
            StringBuilder result = new StringBuilder();
            result.Append((char)('A' + column));
            result.Append((row + 1).ToString("00", CultureInfo.InvariantCulture));

            double x = position.X - column * GridSize;
            double y = position.Y - row * GridSize;
            double size = GridSize;
            for (int i = 0; i < subdivisions; i++)
            {
                size /= 3.0;
                // Round-off at the outer map edge must not generate keypad 10.
                int localColumn = Math.Min(2, (int)Math.Floor(x / size));
                int localRow = Math.Min(2, (int)Math.Floor(y / size));
                result.Append('-');
                result.Append((2 - localRow) * 3 + localColumn + 1);
                x -= localColumn * size;
                y -= localRow * size;
            }
            return result.ToString();
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }

    public sealed class FiringSolution
    {
        public double Distance { get; private set; }
        public double BearingDegrees { get; private set; }
        /// <summary>Target ground elevation minus weapon ground elevation, in metres.</summary>
        public double HeightDifference { get; private set; }
        public double HighMil { get; private set; }
        public double LowMil { get; private set; }
        public double HighTimeSeconds { get; private set; }
        public double LowTimeSeconds { get; private set; }
        public bool HighUsable { get; private set; }
        public bool LowUsable { get; private set; }
        public string Message { get; private set; }

        internal FiringSolution(double distance, double bearing, double heightDifference,
            double highMil, double lowMil, double highTime, double lowTime,
            bool highUsable, bool lowUsable, string message)
        {
            Distance = distance;
            BearingDegrees = bearing;
            HeightDifference = heightDifference;
            HighMil = highMil;
            LowMil = lowMil;
            HighTimeSeconds = highTime;
            LowTimeSeconds = lowTime;
            HighUsable = highUsable;
            LowUsable = lowUsable;
            Message = message;
        }
    }

    /// <summary>
    /// Standard Squad mortar, using SquadCalc's calibrated velocity and gravity.
    /// Ground heights are relative to any common datum; the muzzle is 1 m above ground.
    /// No terrain collision or drag is modelled.
    /// </summary>
    public static class Ballistics
    {
        public const double Velocity = 110.0;
        public const double Gravity = 9.78;
        public const double MinMil = 800.0;
        public const double MaxMil = 1580.0;
        public const double MilsPerCircle = 6400.0;
        public const double MuzzleHeightOffset = 1.0;
        // Upstream display metadata, not a hard range limit: elevation changes it.
        public const double MinDistance = 51.0;

        public static FiringSolution Solve(Vec2 weapon, Vec2 target,
            double weaponHeight, double targetHeight)
        {
            ValidateFinite(weapon.X, "weapon.X");
            ValidateFinite(weapon.Y, "weapon.Y");
            ValidateFinite(target.X, "target.X");
            ValidateFinite(target.Y, "target.Y");
            ValidateFinite(weaponHeight, "weaponHeight");
            ValidateFinite(targetHeight, "targetHeight");

            double dx = target.X - weapon.X;
            double dy = target.Y - weapon.Y;
            double distance = Hypot(dx, dy);
            double heightDifference = targetHeight - weaponHeight;
            double bearing = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (bearing < 0) bearing += 360.0;

            if (!IsFinite(distance) || !IsFinite(heightDifference))
                return Invalid(distance, bearing, heightDifference, "坐标或高差过大，无法解算。");
            if (distance == 0.0)
                return Invalid(0, 0, heightDifference, "炮位与目标平面坐标相同，无法确定射向。");

            double projectileHeight = heightDifference - MuzzleHeightOffset;
            double velocitySquared = Velocity * Velocity;
            double horizontalFactor = Gravity * distance / velocitySquared;
            double discriminant = 1.0 - horizontalFactor * horizontalFactor
                - 2.0 * Gravity * projectileHeight / velocitySquared;

            if (!IsFinite(discriminant))
                return Invalid(distance, bearing, heightDifference, "坐标或高差过大，无法解算。");
            if (discriminant < -1e-12)
                return Invalid(distance, bearing, heightDifference, "目标超出当前高差下的理论射程。");

            // Tolerate floating-point noise where the two roots meet.
            double root = Math.Sqrt(Math.Max(0.0, discriminant));
            double highAngle = Math.Atan2(1.0 + root, horizontalFactor);
            // Rationalised low root avoids cancellation for near-level, short shots.
            double lowAngle = Math.Atan2(horizontalFactor + 2.0 * projectileHeight / distance,
                1.0 + root);
            double highMil = ClampAtLimits(highAngle * MilsPerCircle / (2.0 * Math.PI));
            double lowMil = ClampAtLimits(lowAngle * MilsPerCircle / (2.0 * Math.PI));

            // Solve flight times using the same muzzle-adjusted height as elevation.
            double timeFactor = velocitySquared - Gravity * projectileHeight + velocitySquared * root;
            double highTime = Math.Sqrt(2.0 * timeFactor) / Gravity;
            double lowTime = Math.Sqrt(2.0 * (distance * distance + projectileHeight * projectileHeight) / timeFactor);
            bool highUsable = IsUsable(highMil, highTime);
            bool lowUsable = IsUsable(lowMil, lowTime);
            string message;
            if (highUsable && lowUsable)
                message = "高抛与低抛解均处于迫击炮仰角范围。";
            else if (highUsable)
                message = "可用高抛解。";
            else if (lowUsable)
                message = "可用低抛解。";
            else
                message = "弹道存在，但仰角超出标准迫击炮范围（800 至 1580 密位）。";

            return new FiringSolution(distance, bearing, heightDifference, highMil, lowMil,
                highTime, lowTime, highUsable, lowUsable, message);
        }

        private static FiringSolution Invalid(double distance, double bearing,
            double heightDifference, string message)
        {
            return new FiringSolution(distance, bearing, heightDifference,
                Double.NaN, Double.NaN, Double.NaN, Double.NaN, false, false, message);
        }

        private static bool IsUsable(double mil, double time)
        {
            return IsFinite(mil) && IsFinite(time) && time > 0.0 && mil >= MinMil && mil <= MaxMil;
        }

        private static double ClampAtLimits(double mil)
        {
            if (Math.Abs(mil - MinMil) < 1e-7) return MinMil;
            if (Math.Abs(mil - MaxMil) < 1e-7) return MaxMil;
            return mil;
        }

        private static double Hypot(double x, double y)
        {
            x = Math.Abs(x);
            y = Math.Abs(y);
            double largest = Math.Max(x, y);
            if (largest == 0.0) return 0.0;
            if (Double.IsInfinity(largest)) return Double.PositiveInfinity;
            double smallestRatio = Math.Min(x, y) / largest;
            return largest * Math.Sqrt(1.0 + smallestRatio * smallestRatio);
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static void ValidateFinite(double value, string name)
        {
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(name, "坐标和高度必须是有限数值。");
        }
    }
}
