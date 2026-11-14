using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SquadTools;

internal static class CoordinateInputTests
{
    private static int assertions;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Check(bool value, string label)
    {
        assertions++;
        if (!value) throw new Exception(label);
    }

    private static void Equal(string expected, string actual, string label)
    {
        Check(expected == actual, label + ": expected " + expected + ", actual " + actual);
    }

    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, Private).GetValue(target);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
    }

    private static void Type(TextBox box, string text)
    {
        box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, box, text)) { RoutedEvent = TextCompositionManager.TextInputEvent });
    }

    private static void Delete(TextBox box, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        box.RaiseEvent(args);
        if (!args.Handled)
            (key == Key.Back ? EditingCommands.Backspace : EditingCommands.Delete).Execute(null, box);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo previousLanguage = null;
        try
        {
            new Application();
            previousLanguage = InputLanguageManager.Current.CurrentInputLanguage;
            TestFormats();
            TestEditing();
            TestMainWindow(args[0], args[1]);
            Console.WriteLine("PASS " + assertions + " coordinate/input assertions. Typing, caret, deletion, undo, paste, IME preference, precise positions and settings.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace); return 1; }
        finally { if (previousLanguage != null) InputLanguageManager.Current.CurrentInputLanguage = previousLanguage; }
    }

    private static void TestFormats()
    {
        string[,] cases = {
            { "", "" }, { "d", "D" }, { "D0", "D0" }, { "D05", "D05" },
            { "D056", "D05-6" }, { "D0568", "D05-6-8" }, { "d05689", "D05-6-8-9" },
            { "D05-6-8", "D05-6-8" }, { "D05-68", "D05-6-8" }, { "D056-8", "D05-6-8" },
            { "D05-", "D05-" }, { "D05-6-", "D05-6-" }, { "D5-6-8", "D5-6-8" },
            { "  d0568\r\n", "D05-6-8" }, { "Z99999", "Z99-9-9-9" },
            { "D050", "D05-0" }, { "D056891", "D05-6-8-9-1" },
            { "D05--6", "D05--6" }, { "D05-6x", "D05-6x" }, { "中文", "中文" }
        };
        for (int i = 0; i < cases.GetLength(0); i++)
            Equal(cases[i, 1], CoordinateInput.Format(cases[i, 0]), "format partial, pasted or invalid input");
        for (int row = 1; row <= 99; row++)
            foreach (string suffix in new[] { "", "6", "68", "689" })
            {
                string compact = "D" + row.ToString("00", CultureInfo.InvariantCulture) + suffix;
                string formatted = CoordinateInput.Format(compact);
                Equal(formatted, CoordinateInput.Format(formatted), "formatting is idempotent");
                var expected = GridReference.Parse("D" + row.ToString("00", CultureInfo.InvariantCulture) +
                    String.Concat(suffix.Select(digit => "-" + digit)));
                var actual = GridReference.Parse(formatted);
                Check(Math.Abs(expected.X - actual.X) < 1e-9 && Math.Abs(expected.Y - actual.Y) < 1e-9,
                    "all row numbers retain their coordinate meaning");
            }
    }

    private static void TestEditing()
    {
        var box = new TextBox();
        int changes = 0;
        CoordinateInput.Attach(box, delegate { changes++; });
        var window = new Window { Content = box, Width = 400, Height = 150, ShowInTaskbar = false };
        try
        {
            window.Show(); box.Focus(); Pump(window);
            foreach (char digit in "d0568")
            {
                int before = changes;
                Type(box, digit.ToString());
                Check(changes == before + 1, "one callback per typed character");
                Check(box.CaretIndex == box.Text.Length && box.SelectionLength == 0, "typing keeps caret at the end");
            }
            Equal("D05-6-8", box.Text, "consecutive actual WPF text input");
            Type(box, "9"); Equal("D05-6-8-9", box.Text, "third keypad subdivision");
            foreach (string expected in new[] { "D05-6-8", "D05-6", "D05", "D0", "D", "" })
            {
                Delete(box, Key.Back);
                Equal(expected, box.Text, "backspace progresses through generated separators");
            }
            box.Text = "D05-6-8"; box.CaretIndex = 3;
            Delete(box, Key.Delete); Equal("D05-8", box.Text, "delete skips generated separator and removes next digit");
            box.Text = "D05-6-8"; box.CaretIndex = 4;
            Delete(box, Key.Delete); Equal("D05-8", box.Text, "delete on a subdivision digit also removes its separator");
            box.Text = "D05-6-8"; box.Select(4, 1);
            Delete(box, Key.Back); Equal("D05-8", box.Text, "deleting a selected subdivision keeps valid separators");
            box.Text = "D05-6-8"; box.CaretIndex = 4;
            Delete(box, Key.Back); Equal("D06-8", box.Text, "backspace immediately after separator removes preceding digit");
            box.Text = "D05-6-8"; box.Select(3, 1);
            Delete(box, Key.Delete); Equal("D05-8", box.Text, "deleting a selected separator progresses");
            box.Text = "D05-6-8"; box.Select(4, 1);
            Type(box, "9"); Equal("D05-9-8", box.Text, "replace a digit in the middle");
            Check(box.CaretIndex == 5, "replacement preserves caret in the middle");
            box.CaretIndex = 3; Type(box, "7");
            Equal("D05-7-9-8", box.Text, "insert before a separator");
            Check(box.CaretIndex == 5, "inserted digit retains its caret position");
            box.SelectAll(); Type(box, "d0568");
            Equal("D05-6-8", box.Text, "compact paste replaces a selection");
            box.SelectAll(); Type(box, "F06-5-5");
            Equal("F06-5-5", box.Text, "formatted paste remains valid");
            box.SelectAll(); Type(box, "D05"); Type(box, "-"); Type(box, "6"); Type(box, "-"); Type(box, "8");
            Equal("D05-6-8", box.Text, "manual separators remain supported while typing");
            box.Text = "D05"; box.CaretIndex = 3; box.IsUndoEnabled = false; box.IsUndoEnabled = true; Type(box, "6");
            Equal("D05-6", box.Text, "format an undoable edit");
            box.Undo(); Equal("D05", box.Text, "one undo removes the complete formatted edit");
            box.Redo(); Equal("D05-6", box.Text, "redo restores the formatted edit");
            Check(!InputMethod.GetIsInputMethodEnabled(box), "coordinate entry bypasses IME composition");
        }
        finally { window.Close(); }
    }

    private static void TestMainWindow(string maps, string build)
    {
        string settings = Path.Combine(build, "coordinate-input-settings");
        var window = new MainWindow(true, maps, settings);
        try
        {
            window.Show(); Pump(window);
            Check(InputMethod.GetPreferredImeState(window) == InputMethodState.Off, "startup prefers English IME state");
            Check(InputMethod.GetPreferredImeConversionMode(window) == ImeConversionModeValues.Alphanumeric, "startup prefers alphanumeric conversion");
            var english = InputLanguageManager.Current.AvailableInputLanguages.Cast<CultureInfo>()
                .FirstOrDefault(language => language.TwoLetterISOLanguageName == "en");
            if (english != null)
            {
                Check(InputLanguageManager.GetInputLanguage(window).Equals(english), "window requests an installed English keyboard");
                Check(InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName == "en", "native window creation selects English keyboard");
            }
            var weapon = Field<TextBox>(window, "weaponGrid");
            var target = Field<TextBox>(window, "targetGrid");
            foreach (var box in new[] { weapon, target })
            {
                box.Focus(); box.SelectAll(); Type(box, "d0568");
                Equal("D05-6-8", box.Text, "both real coordinate fields format compact input");
                Check(!InputMethod.GetIsInputMethodEnabled(box), "both real coordinate fields disable composition");
            }
            target.SelectAll(); Type(target, "F0655");
            var map = Field<MapView>(window, "map");
            var expected = GridReference.Parse("F06-5-5");
            Check(Math.Abs(map.Target.X - expected.X) < 1e-9 && Math.Abs(map.Target.Y - expected.Y) < 1e-9,
                "normal calculator entry recalculates the formatted target");
            var point = new Vec2(1050.12345, 1350.98765);
            typeof(MainWindow).GetMethod("PlacePoint", Private).Invoke(window, new object[] { point, false });
            Check(Field<Vec2?>(window, "preciseTarget").HasValue && Math.Abs(map.Target.X - point.X) < 1e-9,
                "map placement retains its exact position through the formatter");
            typeof(MainWindow).GetMethod("SaveSettings", Private).Invoke(window, null);
        }
        finally { window.Close(); }
        var restored = new MainWindow(false, maps, settings);
        try
        {
            Equal("D05-6-8", Field<TextBox>(restored, "weaponGrid").Text, "formatted coordinate persists across restart");
            Check(Field<Vec2?>(restored, "preciseTarget").HasValue, "precise coordinate persists across restart");
            var point = Field<MapView>(restored, "map").Target;
            Check(Math.Abs(point.X - 1050.12345) < 1e-9 && Math.Abs(point.Y - 1350.98765) < 1e-9,
                "restored map coordinate retains its sub-grid precision");
        }
        finally { restored.Close(); }
    }
}
