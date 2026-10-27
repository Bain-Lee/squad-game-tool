using System;
using System.Collections.Generic;

namespace SquadTools
{
    public enum ArcPreference { Automatic, High, Low }

    public sealed class ShotFootprint
    {
        public double AlongRadius { get; private set; }
        public double AcrossRadius { get; private set; }
        public double Damage100Radius { get; private set; }
        public double Damage25Radius { get; private set; }
        private ShotFootprint() { }

        public static ShotFootprint Calculate(FiringSolution solution, WeaponDefinition weapon, ShellDefinition shell, bool high)
        {
            if (solution == null || weapon == null || shell == null || !weapon.Shells.Contains(shell) || !(high ? solution.HighUsable : solution.LowUsable)) return null;
            // SquadCalc's MOA/ellipse convention, with our consistent flight time
            // and actual launch angle (the sight correction is not a launch angle).
            double velocity = weapon.EffectiveVelocity(shell, solution.Distance);
            double angle = LaunchAngle(solution, weapon, high);
            double moa = shell.Moa / 60 * Math.PI / 180;
            double gravity = Ballistics.Gravity * weapon.GravityScale;
            return new ShotFootprint {
                AlongRadius = Math.Abs(velocity * velocity * (Math.Sin(2 * (angle + moa / 2)) - Math.Sin(2 * (angle - moa / 2))) / gravity),
                AcrossRadius = Math.Max(0, Math.Sin(moa) * velocity * (high ? solution.HighTimeSeconds : solution.LowTimeSeconds)),
                Damage100Radius = DamageRadius(shell, 100), Damage25Radius = DamageRadius(shell, 25)
            };
        }

        public static double DamageRadius(ShellDefinition shell, double damage)
        {
            if (shell == null || !Finite(damage) || damage <= 0) throw new ArgumentOutOfRangeException("damage");
            if (shell.ExplosionDamage <= 0 || damage > shell.ExplosionDamage || shell.DamageFalloff <= 0) return 0;
            double radius = shell.OuterRadius - Math.Pow(damage / shell.ExplosionDamage, 1 / shell.DamageFalloff) * (shell.OuterRadius - shell.InnerRadius);
            // Same 1.8 m standing-player reference as SquadCalc. This does not model cover.
            double squared = radius * radius - Math.Pow(shell.ExplosionHeight - 1.8, 2);
            return Math.Sqrt(Math.Max(0, squared));
        }

        internal static double LaunchAngle(FiringSolution solution, WeaponDefinition weapon, bool high)
        { return (high ? solution.HighMil : solution.LowMil) * 2 * Math.PI / Ballistics.MilsPerCircle + weapon.AngleOffset * Math.PI / 180; }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    public sealed class TrajectorySample
    {
        public double Distance { get; internal set; }
        public double? Ground { get; internal set; }
        public double? High { get; internal set; }
        public double? Low { get; internal set; }
    }

    public sealed class TrajectoryProfile
    {
        public IList<TrajectorySample> Samples { get; private set; }
        public bool HasTerrain { get; private set; }
        public FiringSolution Solution { get; private set; }
        public WeaponDefinition Weapon { get; private set; }
        public double WeaponGround { get; private set; }
        public double TargetGround { get; private set; }
        private TrajectoryProfile() { }

        public static TrajectoryProfile Build(Vec2 start, Vec2 end, double startGround, double endGround,
            WeaponDefinition weapon, ShellDefinition shell, FiringSolution solution, Func<Vec2, double> terrain)
        {
            if (weapon == null || shell == null || solution == null || !weapon.Shells.Contains(shell) ||
                !(solution.HighUsable || solution.LowUsable) || !ShotFootprint.Finite(startGround) || !ShotFootprint.Finite(endGround))
                throw new ArgumentException("没有可显示的有效弹道。");
            var samples = new List<TrajectorySample>();
            // At most 5 m between samples, with a bound on work for any supported map.
            int count = Math.Max(128, Math.Min(2048, (int)Math.Ceiling(solution.Distance / 5)));
            double velocity = weapon.EffectiveVelocity(shell, solution.Distance);
            for (int i = 0; i <= count; i++) {
                double fraction = (double)i / count, distance = solution.Distance * fraction;
                var sample = new TrajectorySample { Distance = distance };
                if (terrain != null) {
                    double height = terrain(new Vec2(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction));
                    if (!ShotFootprint.Finite(height)) throw new ArgumentException("沿程地形高程无效。");
                    sample.Ground = height;
                }
                if (solution.HighUsable) sample.High = HeightAt(distance, startGround + weapon.MuzzleHeight, velocity, weapon, solution, true);
                if (solution.LowUsable) sample.Low = HeightAt(distance, startGround + weapon.MuzzleHeight, velocity, weapon, solution, false);
                samples.Add(sample);
            }
            return new TrajectoryProfile { Samples = samples.AsReadOnly(), HasTerrain = terrain != null,
                Solution = solution, Weapon = weapon, WeaponGround = startGround, TargetGround = endGround };
        }

        private static double HeightAt(double distance, double height, double velocity, WeaponDefinition weapon, FiringSolution solution, bool high)
        {
            double angle = ShotFootprint.LaunchAngle(solution, weapon, high);
            double time = distance / (velocity * Math.Cos(angle));
            return height + velocity * Math.Sin(angle) * time - 0.5 * Ballistics.Gravity * weapon.GravityScale * time * time;
        }
    }
}
