using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// The language of the interface: English (US, the language the texts are written in) or Vietnamese. The English text itself is the key
    /// (<see cref="T"/> looks it up in <see cref="LocVi"/>), so a text with no translation simply stays English.
    /// Texts written in XAML or in code before an element is shown are translated when the element loads (<see cref="Attach"/>); texts set later
    /// (status lines, messages) go through <see cref="T"/> where they are set. Choosing another language in Settings translates every open window at once.
    /// </summary>
    internal static class Loc
    {
        public const string English = "en", Vietnamese = "vi";

        private static string _language = English;
        private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, (string Source, string Shown)>> Slots = new();
        private static Regex[]? _patternRegex;
        private static string[]? _prefixKeys;
        private static bool _attached;

        /// <summary>For tests: when not null, collects the English texts that were shown without a translation.</summary>
        internal static HashSet<string>? Missing { get; set; }

        public static string Language => _language;
        public static bool IsVietnamese => _language == Vietnamese;

        /// <summary>Raised after the language changed and the open windows were translated.</summary>
        public static event Action? Changed;

        /// <summary>The saved choice; the first run follows the language of Windows (Vietnamese on a Vietnamese Windows, English otherwise).</summary>
        public static void Initialize()
        {
            string saved = AppSettings.Language;
            if (saved is not (English or Vietnamese))
                saved = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? Vietnamese : English;
            _language = saved;
            Attach();
        }

        /// <summary>For tests and tools: sets the language without saving it or touching any window.</summary>
        public static void Use(string language) => _language = language == Vietnamese ? Vietnamese : English;

        public static void Set(string language)
        {
            language = language == Vietnamese ? Vietnamese : English;
            if (language == _language) return;
            _language = language;
            AppSettings.Language = language;
            if (Application.Current != null)
                foreach (Window window in Application.Current.Windows)
                    ApplyTree(window);
            Changed?.Invoke();
        }

        /// <summary>
        /// Starts translating every window as it opens (once per process): the whole window when it loads, and again when the dispatcher is idle
        /// (content that is built a moment later). Content shown later still (panels, pages) is translated where it is shown, with <see cref="ApplyTree"/>.
        /// </summary>
        public static void Attach()
        {
            if (_attached) return;
            _attached = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (_language != Vietnamese || sender is not Window window) return;
                ApplyTree(window);
                window.Dispatcher.BeginInvoke(new Action(() => ApplyTree(window)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }));
        }

        // ── the lookup ───────────────────────────────────────────────

        /// <summary>The text in the language of the interface. English text with no translation comes back unchanged.</summary>
        public static string T(string? text)
        {
            if (string.IsNullOrEmpty(text) || _language != Vietnamese) return text ?? "";
            string whole = Part(text);
            if (whole != text) return whole;

            // a message of several sentences: each sentence is looked up on its own ("Stamped 3 pages. Ctrl+S saves them; Undo takes them all back.")
            var pieces = Sentences.Split(text);
            if (pieces.Length < 3) return text;
            bool changed = false;
            for (int i = 0; i < pieces.Length; i += 2)
            {
                string translated = Part(pieces[i]);
                if (translated != pieces[i]) { pieces[i] = translated; changed = true; }
            }
            return changed ? string.Concat(pieces) : text;
        }

        private static readonly Regex Sentences = new(@"(?<=[.!?;])(\s+)", RegexOptions.Compiled);

        /// <summary>One sentence: as it is written, or without its closing full stop / semicolon (the patterns are written without it).</summary>
        private static string Part(string text)
        {
            string found = Lookup(text);
            if (found != text) return found;
            string core = text.TrimEnd('.', ';', '!', '?');
            if (core.Length == text.Length || core.Length == 0) return text;
            found = Lookup(core);
            return found != core ? found + text[core.Length..] : text;
        }

        private static string Lookup(string text)
        {
            if (LocVi.Words.TryGetValue(text, out var exact)) return exact;

            // the same text with spaces around it
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return text;
            if (trimmed.Length != text.Length)
            {
                string translated = Lookup(trimmed);
                if (translated != trimmed)
                    return text[..(text.Length - text.TrimStart().Length)] + translated + text[text.TrimEnd().Length..];
                return text;
            }

            // messages with numbers or names in them
            _patternRegex ??= LocVi.Patterns.Select(p => new Regex(p.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline)).ToArray();
            for (int i = 0; i < _patternRegex.Length; i++)
                if (_patternRegex[i].IsMatch(text)) return _patternRegex[i].Replace(text, LocVi.Patterns[i].Replacement, 1);

            // "Could not open the file:" + the reason: the fixed start is translated, the reason stays as it is
            _prefixKeys ??= LocVi.Words.Keys.Where(k => k.Length >= 8 && (k.EndsWith('\n') || k.EndsWith(": ") || k.EndsWith(":\n\n") || k.EndsWith("… "))).OrderByDescending(k => k.Length).ToArray();
            foreach (string key in _prefixKeys)
                if (text.StartsWith(key, StringComparison.Ordinal)) return LocVi.Words[key] + text[key.Length..];
            return text;
        }

        // ── the windows ──────────────────────────────────────────────

        /// <summary>Translates one element (its text, tool tip, header, window title). Safe to call again: the English text is remembered.</summary>
        public static void Translate(DependencyObject d)
        {
            switch (d)
            {
                case TextBlock tb when tb.Inlines.Count <= 1 && tb.TemplatedParent is not Control && !IsBound(tb, TextBlock.TextProperty):
                    Slot(tb, "text", tb.Text, v => tb.Text = v);
                    break;
                case XTButton button when !IsBound(button, XTButton.TextProperty):
                    Slot(button, "text", button.Text, v => button.Text = v);
                    break;
                case HeaderedContentControl hc when hc.Header is string header && !IsBound(hc, HeaderedContentControl.HeaderProperty):
                    Slot(hc, "header", header, v => hc.Header = v);
                    break;
                case HeaderedItemsControl hi when hi.Header is string header && !IsBound(hi, HeaderedItemsControl.HeaderProperty):
                    Slot(hi, "header", header, v => hi.Header = v);
                    break;
                case ContentControl cc when cc.Content is string content && !IsBound(cc, ContentControl.ContentProperty):
                    Slot(cc, "content", content, v => cc.Content = v);
                    break;
            }
            if (d is FrameworkElement fe && fe.ToolTip is string tip && !IsBound(fe, FrameworkElement.ToolTipProperty))
                Slot(fe, "tip", tip, v => fe.ToolTip = v);
            if (d is Window window && !IsBound(window, Window.TitleProperty))
                Slot(window, "title", window.Title, v => window.Title = v);
        }

        /// <summary>Translates an element and everything below it (including the context menus, which are not in the tree until opened).</summary>
        public static void ApplyTree(DependencyObject root) => ApplyTree(root, new HashSet<DependencyObject>());

        private static void ApplyTree(DependencyObject node, HashSet<DependencyObject> seen)
        {
            if (!seen.Add(node)) return;
            Translate(node);
            if (node is FrameworkElement { ContextMenu: { } menu }) ApplyTree(menu, seen);
            if (node is ContextMenu or MenuItem) foreach (var item in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) ApplyTree(item, seen);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = VisualTreeHelper.GetChildrenCount(node); i < n; i++) ApplyTree(VisualTreeHelper.GetChild(node, i), seen);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) ApplyTree(child, seen);
        }

        private static bool IsBound(DependencyObject d, DependencyProperty property) => DependencyPropertyHelper.GetValueSource(d, property).IsExpression;

        private static void Slot(DependencyObject d, string name, string? current, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(current)) return;
            var slots = Slots.GetOrCreateValue(d);
            string source = current;
            // The English text is the one that was there before we wrote the translation; when the code has put a new text since, that one is the source.
            if (slots.TryGetValue(name, out var known) && known.Shown == current) source = known.Source;
            string shown = T(source);
            if (shown != current) set(shown);
            else if (Missing != null && _language == Vietnamese && shown == source && source.Any(char.IsLetter) && source.All(c => c < 0x80 || !char.IsLetter(c))) Missing.Add(source);
            slots[name] = (source, shown);
        }
    }
}
