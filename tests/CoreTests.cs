using System;
using SquadTools;

internal static class CoreTests
{
    private static int count;

    public static int Main()
    {
        try
        {
            TestGridCentresAndDirections();
            TestGridValidationAndRoundTrips();
            TestAnalyticLevelTrajectory();
            TestBearings();
            TestFlightEquationAndHeights();
            TestBoundariesAndFailures();
            Console.WriteLine("PASS: " + count + " assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static void TestGridCentresAndDirections()
    {
        Position("A01", 150, 150);
        Position("B02", 450, 450);
        Position(" a1-7 ", 50, 50);
        Position("A01-9", 250, 50);
        Position("A01-1", 50, 250);
        Position("A01-3", 250, 250);
        Position("A01-5", 150, 150);
        Position("A01-7-1", 100.0 / 6.0, 500.0 / 6.0);
        Position("Z99-3-3-3", 7800.0 - 50.0 / 9.0, 29700.0 - 50.0 / 9.0);
        Equal("A01-7", GridReference.Format(new Vec2(0, 0), 1), "northwest keypad");
        Equal("A01-1", GridReference.Format(new Vec2(0, 299.99), 1), "southwest keypad");
        Equal("B02", GridReference.Format(new Vec2(300, 300), 0), "major grid boundary");
        Equal("A01-8", GridReference.Format(new Vec2(100, 0), 1), "subgrid boundary");
    }

    private static void TestGridValidationAndRoundTrips()
    {
        string[] invalid = { "", "A00", "A-1", "A001", "AA01", "A100", "A01-0", "A01-10", "A01-5-5-5-5", "A01--5", "A 01", "A01-5x" };
        foreach (string grid in invalid)
            Throws<FormatException>(delegate { GridReference.Parse(grid); }, "invalid grid " + grid);
        Throws<ArgumentNullException>(delegate { GridReference.Parse(null); }, "null grid");
        Throws<ArgumentOutOfRangeException>(delegate { GridReference.Format(new Vec2(-0.01, 0), 0); }, "negative coordinate");
        Throws<ArgumentOutOfRangeException>(delegate { GridReference.Format(new Vec2(7800, 0), 0); }, "column overflow");
        Throws<ArgumentOutOfRangeException>(delegate { GridReference.Format(new Vec2(0, 29700), 0); }, "row overflow");
        Throws<ArgumentOutOfRangeException>(delegate { GridReference.Format(new Vec2(0, 0), 4); }, "four subdivisions");
        Throws<ArgumentOutOfRangeException>(delegate { GridReference.Format(new Vec2(Double.NaN, 0), 0); }, "NaN coordinate");

        string[] prefixes = { "A01", "K07", "Z99" };
        foreach (string prefix in prefixes)
        {
            Equal(prefix, GridReference.Format(GridReference.Parse(prefix), 0), "major round trip");
            for (int a = 1; a <= 9; a++)
            {
                string first = prefix + "-" + a;
                Equal(first, GridReference.Format(GridReference.Parse(first), 1), "first round trip");
                for (int b = 1; b <= 9; b++)
                {
                    string second = first + "-" + b;
                    Equal(second, GridReference.Format(GridReference.Parse(second), 2), "second round trip");
                    for (int c = 1; c <= 9; c++)
                    {
                        string third = second + "-" + c;
                        Equal(third, GridReference.Format(GridReference.Parse(third), 3), "third round trip");
                    }
                }
            }
        }
    }

    private static void TestAnalyticLevelTrajectory()
    {
        // Independent textbook fixture: at equal projectile heights, 60 and 30
        // degrees have range v^2*sin(120 degrees)/g and known flight times.
        double range = 110.0 * 110.0 * Math.Sqrt(3.0) / (2.0 * 9.78);
        FiringSolution solution = Ballistics.Solve(new Vec2(0, 0), new Vec2(range, 0), 0, 1);
        Near(60.0 * 6400.0 / 360.0, solution.HighMil, 1e-8, "analytic high elevation");
        Near(30.0 * 6400.0 / 360.0, solution.LowMil, 1e-8, "analytic low elevation");
        Near(110.0 * Math.Sqrt(3.0) / 9.78, solution.HighTimeSeconds, 1e-9, "analytic high flight time");
        Near(110.0 / 9.78, solution.LowTimeSeconds, 1e-9, "analytic low flight time");
        True(solution.HighUsable, "60-degree mortar shot usable");
        True(!solution.LowUsable, "30-degree mortar shot outside angle range");
        Near(1, solution.HeightDifference, 0, "ground height difference preserved");
    }

    private static void TestBearings()
    {
        Vec2 origin = new Vec2(500, 500);
        Near(0, Ballistics.Solve(origin, new Vec2(500, 0), 0, 0).BearingDegrees, 1e-10, "north bearing");
        Near(90, Ballistics.Solve(origin, new Vec2(1000, 500), 0, 0).BearingDegrees, 1e-10, "east bearing");
        Near(180, Ballistics.Solve(origin, new Vec2(500, 1000), 0, 0).BearingDegrees, 1e-10, "south bearing");
        Near(270, Ballistics.Solve(origin, new Vec2(0, 500), 0, 0).BearingDegrees, 1e-10, "west bearing");
        Near(45, Ballistics.Solve(origin, new Vec2(1000, 0), 0, 0).BearingDegrees, 1e-10, "northeast bearing");
    }

    private static void TestFlightEquationAndHeights()
    {
        double[] distances = { 50, 300, 750, 1000 };
        double[] targetHeights = { -150, 0, 80, 160 };
        foreach (double distance in distances)
        {
            foreach (double targetHeight in targetHeights)
            {
                FiringSolution solution = Ballistics.Solve(new Vec2(10, 20), new Vec2(10 + distance, 20), 25, targetHeight);
                if (Double.IsNaN(solution.HighMil)) continue;
                VerifyLanding(solution.HighMil, solution.HighTimeSeconds, distance, targetHeight - 26, "high trajectory");
                VerifyLanding(solution.LowMil, solution.LowTimeSeconds, distance, targetHeight - 26, "low trajectory");
            }
        }
        FiringSolution level = Ballistics.Solve(new Vec2(0, 0), new Vec2(700, 0), 0, 0);
        FiringSolution uphill = Ballistics.Solve(new Vec2(0, 0), new Vec2(700, 0), 0, 100);
        FiringSolution downhill = Ballistics.Solve(new Vec2(0, 0), new Vec2(700, 0), 0, -100);
        True(uphill.HighMil < level.HighMil && downhill.HighMil > level.HighMil, "high arc responds to elevation");
        FiringSolution shiftedDatum = Ballistics.Solve(new Vec2(0, 0), new Vec2(700, 0), 1234, 1334);
        Near(uphill.HighMil, shiftedDatum.HighMil, 1e-10, "height datum independence");
        Near(uphill.HighTimeSeconds, shiftedDatum.HighTimeSeconds, 1e-10, "time datum independence");
    }

    private static void TestBoundariesAndFailures()
    {
        // For a target at muzzle height, the 45-degree maximum range is v^2/g.
        double maximum = 12100.0 / 9.78;
        FiringSolution boundary = Ballistics.Solve(new Vec2(0, 0), new Vec2(maximum, 0), 0, 1);
        Near(800, boundary.HighMil, 1e-6, "45-degree range boundary");
        True(boundary.HighUsable && boundary.LowUsable, "coincident 45-degree roots usable");
        FiringSolution outside = Ballistics.Solve(new Vec2(0, 0), new Vec2(maximum + 0.01, 0), 0, 1);
        True(!outside.HighUsable && !outside.LowUsable, "beyond maximum unreachable");
        True(Double.IsNaN(outside.HighMil), "unreachable elevation unavailable");
        FiringSolution same = Ballistics.Solve(new Vec2(0, 0), new Vec2(0, 0), 0, 0);
        True(!same.HighUsable && !same.LowUsable, "coincident map point rejected");
        True(same.Message.Length > 0, "coincident point explanation");

        // A 1580-mil shot has an independently constructed horizontal/vertical landing.
        double maxAngle = 1580.0 * Math.PI / 3200.0;
        double angleRange = 12100.0 * Math.Sin(2.0 * maxAngle) / 9.78;
        FiringSolution steepLimit = Ballistics.Solve(new Vec2(0, 0), new Vec2(angleRange, 0), 0, 1);
        Near(1580, steepLimit.HighMil, 1e-7, "maximum elevation boundary");
        True(steepLimit.HighUsable, "maximum elevation inclusive");
        FiringSolution tooSteep = Ballistics.Solve(new Vec2(0, 0), new Vec2(angleRange - 0.1, 0), 0, 1);
        True(!tooSteep.HighUsable, "above maximum elevation rejected");
        True(steepLimit.Distance < 51, "metadata minimum does not override angle solution");
        FiringSolution tooHigh = Ballistics.Solve(new Vec2(0, 0), new Vec2(100, 0), 0, 1000);
        True(!tooHigh.HighUsable && !tooHigh.LowUsable, "excessive uphill unreachable");
        Throws<ArgumentOutOfRangeException>(delegate { Ballistics.Solve(new Vec2(Double.NaN, 0), new Vec2(1, 0), 0, 0); }, "NaN input");
        Throws<ArgumentOutOfRangeException>(delegate { Ballistics.Solve(new Vec2(0, 0), new Vec2(1, 0), 0, Double.PositiveInfinity); }, "infinite height");
    }

    private static void VerifyLanding(double mil, double time, double distance, double height, string label)
    {
        double radians = mil * Math.PI / 3200.0;
        double x = 110.0 * Math.Cos(radians) * time;
        double y = 110.0 * Math.Sin(radians) * time - 0.5 * 9.78 * time * time;
        Near(distance, x, 1e-6, label + " horizontal landing");
        Near(height, y, 1e-6, label + " vertical landing");
        True(time > 0 && !Double.IsInfinity(time), label + " finite positive time");
    }

    private static void Position(string grid, double x, double y)
    {
        Vec2 position = GridReference.Parse(grid);
        Near(x, position.X, 1e-8, grid + " X");
        Near(y, position.Y, 1e-8, grid + " Y");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        count++;
        if (Double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception(label + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(string expected, string actual, string label)
    {
        count++;
        if (expected != actual)
            throw new Exception(label + ": expected " + expected + ", actual " + actual);
    }

    private static void True(bool value, string label)
    {
        count++;
        if (!value) throw new Exception(label);
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        count++;
        try { action(); }
        catch (T) { return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
}
