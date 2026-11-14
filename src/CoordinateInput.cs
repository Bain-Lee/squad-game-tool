using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;

namespace SquadTools
{
    internal static class CoordinateInput
    {
        public static void Attach(TextBox box, Action changed)
        {
            InputMethod.SetIsInputMethodEnabled(box, false);
            bool formatting = false;
            box.TextChanged += delegate {
                if (formatting) return;
                string original = box.Text;
                string formatted = Format(original);
                if (formatted != original)
                {
                    int start = MapPosition(original, box.SelectionStart, formatted);
                    int end = MapPosition(original, box.SelectionStart + box.SelectionLength, formatted);
                    formatting = true;
                    box.BeginChange();
                    try
                    {
                        // SelectedText keeps formatting in the edit's undo transaction.
                        box.SelectAll();
                        box.SelectedText = formatted;
                        box.Select(start, end - start);
                    }
                    finally { box.EndChange(); formatting = false; }
                }
                changed();
            };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (Keyboard.Modifiers != ModifierKeys.None || (e.Key != Key.Back && e.Key != Key.Delete)) return;
                int start = box.SelectionStart, end = start + box.SelectionLength;
                if (box.SelectionLength == 0 || box.SelectedText.Trim('-').Length == 0)
                {
                    // Skip separators when finding the character to delete.
                    if (e.Key == Key.Back)
                    {
                        if (start > 0) start--;
                        while (start > 0 && box.Text[start] == '-') start--;
                    }
                    else
                    {
                        while (end < box.Text.Length && box.Text[end] == '-') end++;
                        if (end < box.Text.Length) end++;
                    }
                }
                if (start == end) return;
                // Remove the subdivision's separator with its digit.
                if (start > 0 && box.Text[start - 1] == '-' && (end == box.Text.Length || box.Text[end] == '-')) start--;
                box.Select(start, end - start);
                box.SelectedText = "";
                e.Handled = true;
            };
        }

        internal static string Format(string text)
        {
            string trimmed = text.Trim();
            // An explicit separator still permits the parser's legacy one-digit row.
            Match match = Regex.Match(trimmed, @"\A([A-Za-z][0-9]{0,2})((?:-?[0-9])*)(-?)\z");
            if (!match.Success) return text;
            var result = new StringBuilder(match.Groups[1].Value.ToUpperInvariant());
            foreach (char digit in match.Groups[2].Value)
                if (digit != '-') result.Append('-').Append(digit);
            result.Append(match.Groups[3].Value);
            return result.ToString();
        }

        private static int MapPosition(string original, int position, string formatted)
        {
            int characters = 0;
            for (int i = 0; i < position; i++)
                if (original[i] != '-' && !Char.IsWhiteSpace(original[i])) characters++;
            int mapped = 0;
            while (mapped < formatted.Length && characters > 0)
                if (formatted[mapped++] != '-') characters--;
            if (position > 0 && original[position - 1] == '-' && mapped < formatted.Length && formatted[mapped] == '-') mapped++;
            return mapped;
        }
    }
}
