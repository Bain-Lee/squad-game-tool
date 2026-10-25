using System;
using System.Collections.Generic;
using System.Globalization;

namespace SquadTools
{
    public sealed class ShellDefinition
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public double Velocity { get; private set; }
        public double MinDistance { get; private set; }
        internal ShellDefinition(string id, string name, double velocity, double minDistance)
        { Id = id; Name = name; Velocity = velocity; MinDistance = minDistance; }
        public override string ToString() { return Name; }
    }

    public sealed class WeaponDefinition
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Category { get; private set; }
        public string Unit { get; private set; }
        public bool PreferHigh { get; private set; }
        public double GravityScale { get; private set; }
        public double MinDegrees { get; private set; }
        public double MaxDegrees { get; private set; }
        public double MuzzleHeight { get; private set; }
        public double AngleOffset { get; private set; }
        public double Lifespan { get; private set; }
        public double Deceleration { get; private set; }
        public double DecelerationTime { get; private set; }
        public IList<ShellDefinition> Shells { get; private set; }

        internal WeaponDefinition(string id, string name, string category, string unit, bool high,
            double gravityScale, double minDegrees, double maxDegrees, double height, double angleOffset,
            double lifespan, double deceleration, double decelerationTime, params ShellDefinition[] shells)
        {
            Id = id; Name = name; Category = category; Unit = unit; PreferHigh = high;
            GravityScale = gravityScale; MinDegrees = minDegrees; MaxDegrees = maxDegrees;
            MuzzleHeight = height; AngleOffset = angleOffset; Lifespan = lifespan;
            Deceleration = deceleration; DecelerationTime = decelerationTime;
            Shells = Array.AsReadOnly(shells);
        }
        public override string ToString() { return Name; }

        // Same distance-dependent approximation as SquadCalc's getVelocity; not a physical drag simulation.
        public double EffectiveVelocity(ShellDefinition shell, double distance)
        {
            if (Deceleration == 0) return shell.Velocity;
            double phaseDistance = shell.Velocity * DecelerationTime - 0.5 * Deceleration * DecelerationTime * DecelerationTime;
            if (distance <= phaseDistance) {
                double time = 2 * distance / (shell.Velocity + Math.Sqrt(shell.Velocity * shell.Velocity + 2 * Deceleration * distance));
                return shell.Velocity - Deceleration * time;
            }
            double cruise = shell.Velocity - Deceleration * DecelerationTime;
            return distance / (DecelerationTime + (distance - phaseDistance) / cruise);
        }

        public double NominalRange(ShellDefinition shell)
        {
            if (Deceleration == 0) return shell.Velocity * shell.Velocity / (Ballistics.Gravity * GravityScale);
            double cruise = shell.Velocity - Deceleration * DecelerationTime;
            return Deceleration * DecelerationTime * DecelerationTime + cruise * cruise / (Ballistics.Gravity * GravityScale);
        }

        public string FormatElevation(double mil)
        {
            if (Unit == "mil") return mil.ToString("F0", CultureInfo.InvariantCulture);
            double degrees = mil * 360 / 6400;
            if (Unit == "deg") return degrees.ToString("F1", CultureInfo.InvariantCulture) + "°";
            int minutes = (int)Math.Round(Math.Abs(degrees) * 60, MidpointRounding.AwayFromZero);
            return (degrees < 0 ? "−" : "") + (minutes / 60).ToString(CultureInfo.InvariantCulture) + "°" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + "′";
        }
    }

    public static class WeaponCatalog
    {
        public const string SourceCommit = "24828d6d3081c7ce52b10ef39ec86036ebf010b5";
        private static ShellDefinition Standard(double velocity, double minDistance) { return new ShellDefinition("default", "标准弹", velocity, minDistance); }
        public static readonly IList<WeaponDefinition> All = Array.AsReadOnly(new[] {
            new WeaponDefinition("mortar", "标准迫击炮 / Mortar", "工事", "mil", true, 1, 45, 88.875, 1, 0, 100, 0, 0, Standard(110, 51)),
            new WeaponDefinition("ub32", "UB-32 火箭巢", "工事", "deg", false, 2, -25, 35, 1, 0, 20, 50, 2, Standard(300, 0)),
            new WeaponDefinition("hellcannon", "地狱炮 / Hell Cannon", "工事", "deg", true, 1, 10, 90, 1.5, 0, 100, 0, 0, Standard(95, 0)),
            new WeaponDefinition("tech-mortar", "皮卡迫击炮 / Tech. Mortar", "载具", "deg", true, 1, -45, 135, 2.5, 5, 100, 0, 0, Standard(110, 51)),
            new WeaponDefinition("tech-ub32", "皮卡 UB-32 火箭巢", "载具", "deg", false, 2, -45, 135, 2.5, 0, 20, 50, 2, Standard(300, 0)),
            new WeaponDefinition("bm21", "BM-21 冰雹火箭炮", "载具", "deg", false, 2, -45, 135, 3, 0, 100, 0, 0, Standard(200, 0)),
            new WeaponDefinition("m1064", "M1064 / M121 迫击炮车", "载具", "deg", true, 1, -45, 85.3, 3, 0, 100, 0, 0,
                new ShellDefinition("impact", "触发弹 / Impact", 142, 340), new ShellDefinition("nearSurface", "近地空爆 / Near Surface", 142, 340)),
            new WeaponDefinition("mk19", "Mk19 自动榴弹发射器", "载具", "deg", false, 1, -45, 85.3, 4, 0.984, 20, 0, 0, Standard(230, 10)),
            new WeaponDefinition("btr4-ags", "BTR-4 AGS 榴弹发射器", "载具", "degMin", false, 1, -45, 135, 3.4, 0.73459689, 20, 0, 0, Standard(190, 0))
        });
        public static WeaponDefinition Default { get { return All[0]; } }
        public static WeaponDefinition Find(string id)
        {
            foreach (var item in All) if (item.Id == id) return item;
            return Default;
        }
    }
}
