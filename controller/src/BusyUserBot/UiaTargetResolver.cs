using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace BusyUserBot;

/// <summary>
/// Deterministic, non-AI target resolver. Given a natural-language target
/// description (e.g. <c>the OK button of the Self-test dialog</c>) returned
/// by the executor model on an absolute <c>move</c> action, walk the Windows
/// UI Automation tree to find the matching control and return its physical
/// screen rectangle. Used in front of the existing screenshot-based
/// targeting pipeline: on a confident hit the controller clicks the UIA
/// rectangle's centre directly and skips the vision-based refinement; on a
/// miss the original AI pipeline runs unchanged.
///
/// Robustness over completeness: this class never throws to its caller and
/// always returns <c>null</c> on any UIA failure, ambiguous match, or
/// timeout, so callers can treat it as a pure "fast path" optimisation.
/// </summary>
internal static class UiaTargetResolver
{
    /// <summary>
    /// Hard wall-clock budget for the whole resolve attempt. UIA is fast
    /// against well-behaved native dialogs (~50 ms) but can stall noticeably
    /// against Electron / Chromium windows, hence the generous default.
    /// </summary>
    private const int TimeoutMs = 1500;

    /// <summary>
    /// Minimum score for the top candidate to be returned. Calibrated so a
    /// quoted-name + matching role hit clears the bar comfortably and a
    /// "control with vaguely related text somewhere" does not.
    /// </summary>
    private const double MinConfidence = 0.55;

    /// <summary>
    /// Top candidate must beat the runner-up by at least this margin or we
    /// treat the whole result as ambiguous and bail. Avoids clicking the
    /// wrong "OK" when two dialogs are visible at once.
    /// </summary>
    private const double TieMargin = 0.10;

    /// <summary>
    /// Two candidates whose centres are within this many pixels AND whose
    /// rects overlap by ≥ <see cref="DedupOverlap"/> are considered the same
    /// underlying control surfaced through different roots (foreground
    /// window vs. desktop tree). The duplicate is dropped silently.
    /// </summary>
    private const int DedupCentrePx = 4;
    private const double DedupOverlap = 0.80;

    /// <summary>
    /// When true (default), log keyword extraction, scan timings, and the
    /// top-N candidates with their scoring breakdown. Set to false once the
    /// resolver is trusted in production to keep the log quieter.
    /// </summary>
    public static bool Verbose = true;
    private const int VerboseTopN = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public sealed record UiaHit(
        Rectangle ScreenRect,
        string Name,
        string ControlType,
        string Source,
        double Confidence)
    {
        public Point Centre => new(
            ScreenRect.Left + ScreenRect.Width / 2,
            ScreenRect.Top  + ScreenRect.Height / 2);
    }

    /// <summary>
    /// Try to resolve <paramref name="targetDescription"/> to an on-screen
    /// rectangle. Returns <c>null</c> if no confident match is found or on
    /// any error. Safe to call from any thread; bounded by <see cref="TimeoutMs"/>.
    /// </summary>
    /// <param name="cursorHint">
    /// Current cursor position, used as a tiebreaker between similarly-scored
    /// candidates (the executor model has already roughly aimed the cursor
    /// near the intended target). Pass <c>null</c> to disable.
    /// </param>
    public static UiaHit? Resolve(
        string targetDescription,
        Action<string> log,
        CancellationToken ct,
        Point? cursorHint = null)
    {
        if (string.IsNullOrWhiteSpace(targetDescription)) return null;

        try
        {
            // Run on a worker thread with a hard timeout. UIA calls can
            // block indefinitely against unresponsive apps; we will not let
            // that wedge the bot loop.
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            localCts.CancelAfter(TimeoutMs);

            var task = Task.Run(() => ResolveCore(targetDescription, cursorHint, log), localCts.Token);
            if (!task.Wait(TimeoutMs, ct))
            {
                log($"UIA: lookup exceeded {TimeoutMs}ms budget for target '{Trunc(targetDescription)}' — falling back");
                return null;
            }
            return task.Result;
        }
        catch (OperationCanceledException) { throw; }
        catch (AggregateException aex) when (aex.InnerException is OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            log("UIA: resolver crashed: " + ex.Message);
            return null;
        }
    }

    // -------------------------------------------------------------------
    // Core
    // -------------------------------------------------------------------

    private static UiaHit? ResolveCore(string description, Point? cursorHint, Action<string> log)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var keywords = ExtractKeywords(description, out var quoted, out var roleHints);
        if (Verbose)
        {
            log($"UIA: target='{Trunc(description, 100)}'");
            log($"UIA:   quoted=[{string.Join(", ", quoted.Select(q => "\"" + q + "\""))}] " +
                $"keywords=[{string.Join(", ", keywords)}] " +
                $"roles=[{string.Join(", ", roleHints)}]");
            if (cursorHint is Point cur)
                log($"UIA:   cursor hint=({cur.X},{cur.Y})");
        }
        if (keywords.Count == 0 && quoted.Count == 0)
        {
            if (Verbose) log("UIA:   no keywords or quoted phrases extracted — cannot resolve");
            return null;
        }

        using var automation = new UIA3Automation();
        var cf = automation.ConditionFactory;

        // Allowed control types we consider clickable. Anything else is
        // ignored when scoring.
        var clickableTypes = new[]
        {
            ControlType.Button, ControlType.MenuItem, ControlType.TabItem,
            ControlType.ListItem, ControlType.TreeItem, ControlType.Hyperlink,
            ControlType.CheckBox, ControlType.RadioButton, ControlType.SplitButton,
            ControlType.Edit, ControlType.ComboBox, ControlType.Custom,
            ControlType.Image, ControlType.Text,
        };
        ConditionBase clickableCondition = cf.ByControlType(clickableTypes[0]);
        for (int i = 1; i < clickableTypes.Length; i++)
            clickableCondition = clickableCondition.Or(cf.ByControlType(clickableTypes[i]));

        // Phase 1: scan the foreground window only. This is what 95% of
        // executor `target` strings refer to (the dialog/window the model
        // just saw on screen) and is dramatically cheaper than walking the
        // whole desktop tree.
        var candidates = new List<Candidate>();
        AutomationElement? fgElement = null;
        string fgTitle = "";
        var swPhase = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                fgElement = automation.FromHandle(fg);
                if (fgElement is not null)
                {
                    try { fgTitle = fgElement.Properties.Name.ValueOrDefault ?? ""; } catch { }
                    var fgElements = fgElement.FindAllDescendants(clickableCondition);
                    foreach (var el in fgElements)
                    {
                        try
                        {
                            var c = ScoreCandidate(el, keywords, quoted, roleHints, cursorHint, fgTitle);
                            if (c is not null) candidates.Add(c);
                        }
                        catch { }
                    }
                    if (Verbose)
                        log($"UIA:   foreground scan: hwnd=0x{fg.ToInt64():X} title='{Trunc(fgTitle, 50)}' " +
                            $"elements={fgElements.Length} candidates={candidates.Count} ({swPhase.ElapsedMilliseconds}ms)");
                }
            }
        }
        catch (Exception ex)
        {
            log("UIA:   foreground scan error: " + ex.Message);
        }

        // Phase 2: only walk the desktop tree if the foreground scan didn't
        // produce any plausible candidates. Skipping this when we already
        // have hits eliminates the foreground-vs-desktop OK-vs-OK tie.
        if (candidates.Count == 0)
        {
            swPhase.Restart();
            int scanned = 0, windowCount = 0;
            try
            {
                var desktop = automation.GetDesktop();
                var windows = desktop.FindAllChildren(cf.ByControlType(ControlType.Window));
                foreach (var w in windows)
                {
                    try
                    {
                        if (!IsCandidateWindow(w)) continue;
                        windowCount++;
                        string wTitle = "";
                        try { wTitle = w.Properties.Name.ValueOrDefault ?? ""; } catch { }
                        var els = w.FindAllDescendants(clickableCondition);
                        scanned += els.Length;
                        foreach (var el in els)
                        {
                            try
                            {
                                var c = ScoreCandidate(el, keywords, quoted, roleHints, cursorHint, wTitle);
                                if (c is not null) candidates.Add(c);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                log("UIA:   desktop scan error: " + ex.Message);
            }
            if (Verbose)
                log($"UIA:   desktop scan: windows={windowCount} elements={scanned} " +
                    $"candidates={candidates.Count} ({swPhase.ElapsedMilliseconds}ms)");
        }

        if (candidates.Count == 0)
        {
            if (Verbose) log($"UIA:   no scoring candidates ({swTotal.ElapsedMilliseconds}ms total) — falling back");
            return null;
        }

        // Dedupe by near-identical rect: the same control sometimes appears
        // in multiple sub-trees (compound controls, transparent overlays).
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var deduped = new List<Candidate>(candidates.Count);
        foreach (var c in candidates)
        {
            bool dup = false;
            foreach (var d in deduped)
            {
                if (RectsAreSameControl(c.Rect, d.Rect)) { dup = true; break; }
            }
            if (!dup) deduped.Add(c);
        }
        if (Verbose && deduped.Count != candidates.Count)
            log($"UIA:   deduped {candidates.Count - deduped.Count} overlapping candidate(s)");

        if (Verbose)
        {
            int show = Math.Min(VerboseTopN, deduped.Count);
            log($"UIA:   top {show}/{deduped.Count} candidates after dedup:");
            for (int i = 0; i < show; i++)
            {
                var c = deduped[i];
                log($"UIA:     [{i + 1}] score={c.Score:0.00} '{Trunc(c.Name, 40)}' " +
                    $"[{c.ControlType}] rect=({c.Rect.X},{c.Rect.Y} {c.Rect.Width}x{c.Rect.Height})" +
                    (string.IsNullOrEmpty(c.SourceWindowTitle) ? "" : $" win='{Trunc(c.SourceWindowTitle, 30)}'") +
                    $" :: {c.ScoreBreakdown}");
            }
        }

        var top = deduped[0];
        if (top.Score < MinConfidence)
        {
            log($"UIA: best candidate score {top.Score:0.00} < {MinConfidence:0.00} for '{Trunc(description)}' " +
                $"(best='{Trunc(top.Name)}' [{top.ControlType}]) — falling back ({swTotal.ElapsedMilliseconds}ms)");
            return null;
        }
        if (deduped.Count >= 2 && (top.Score - deduped[1].Score) < TieMargin)
        {
            log($"UIA: ambiguous match for '{Trunc(description)}' " +
                $"(top='{Trunc(top.Name)}' {top.Score:0.00} rect=({top.Rect.X},{top.Rect.Y} {top.Rect.Width}x{top.Rect.Height}) " +
                $"vs runner-up='{Trunc(deduped[1].Name)}' {deduped[1].Score:0.00} rect=({deduped[1].Rect.X},{deduped[1].Rect.Y} {deduped[1].Rect.Width}x{deduped[1].Rect.Height})) — falling back ({swTotal.ElapsedMilliseconds}ms)");
            return null;
        }

        if (Verbose)
            log($"UIA:   resolved in {swTotal.ElapsedMilliseconds}ms");
        return new UiaHit(top.Rect, top.Name, top.ControlType, top.SourceWindowTitle, top.Score);
    }

    private static bool RectsAreSameControl(Rectangle a, Rectangle b)
    {
        int acx = a.Left + a.Width / 2, acy = a.Top + a.Height / 2;
        int bcx = b.Left + b.Width / 2, bcy = b.Top + b.Height / 2;
        if (Math.Abs(acx - bcx) > DedupCentrePx || Math.Abs(acy - bcy) > DedupCentrePx) return false;

        var inter = Rectangle.Intersect(a, b);
        if (inter.IsEmpty) return false;
        double interArea = (double)inter.Width * inter.Height;
        double minArea = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return minArea > 0 && (interArea / minArea) >= DedupOverlap;
    }

    // -------------------------------------------------------------------
    // Window filter
    // -------------------------------------------------------------------

    private static bool IsCandidateWindow(AutomationElement w)
    {
        try
        {
            if (w.Properties.IsOffscreen.ValueOrDefault) return false;
            var rect = w.Properties.BoundingRectangle.ValueOrDefault;
            if (rect.Width <= 1 || rect.Height <= 1) return false;
            // Skip the shell desktop and similar full-screen elements that
            // would otherwise force a giant tree walk.
            if (rect.Width >= System.Windows.Forms.SystemInformation.VirtualScreen.Width
             && rect.Height >= System.Windows.Forms.SystemInformation.VirtualScreen.Height)
                return false;
            return true;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------
    // Scoring
    // -------------------------------------------------------------------

    private sealed record Candidate(
        Rectangle Rect, string Name, string ControlType, string SourceWindowTitle,
        double Score, string ScoreBreakdown);

    private static Candidate? ScoreCandidate(
        AutomationElement el,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string> quoted,
        IReadOnlySet<string> roleHints,
        Point? cursorHint,
        string sourceWindowTitle)
    {
        Rectangle rect;
        string name;
        string ctName;
        bool enabled;
        try
        {
            if (el.Properties.IsOffscreen.ValueOrDefault) return null;
            var raw = el.Properties.BoundingRectangle.ValueOrDefault;
            if (raw.Width < 2 || raw.Height < 2) return null;
            rect = new Rectangle((int)raw.X, (int)raw.Y, (int)raw.Width, (int)raw.Height);

            name = (el.Properties.Name.ValueOrDefault ?? "").Trim();
            ctName = el.Properties.ControlType.ValueOrDefault.ToString();
            enabled = el.Properties.IsEnabled.ValueOrDefault;
        }
        catch { return null; }

        // 1. Name match. Quoted phrases must match exactly (case-insensitive)
        //    or as a whole-word substring; unquoted keywords contribute
        //    fractional credit per matched token.
        double nameScore = 0;
        string nameSource = "none";
        if (!string.IsNullOrEmpty(name))
        {
            var nameLower = name.ToLowerInvariant();
            foreach (var q in quoted)
            {
                var ql = q.ToLowerInvariant();
                if (nameLower == ql) { if (1.0 > nameScore) { nameScore = 1.0; nameSource = $"exact'{q}'"; } }
                else if (Regex.IsMatch(nameLower, $@"\b{Regex.Escape(ql)}\b")) { if (0.85 > nameScore) { nameScore = 0.85; nameSource = $"word'{q}'"; } }
                else if (nameLower.Contains(ql)) { if (0.55 > nameScore) { nameScore = 0.55; nameSource = $"sub'{q}'"; } }
            }
            if (quoted.Count == 0 && keywords.Count > 0)
            {
                int matched = 0;
                foreach (var kw in keywords)
                    if (nameLower.Contains(kw.ToLowerInvariant())) matched++;
                if (matched > 0)
                {
                    var s = 0.40 + 0.40 * matched / keywords.Count;
                    if (s > nameScore) { nameScore = s; nameSource = $"kw{matched}/{keywords.Count}"; }
                }
            }
        }
        if (nameScore <= 0) return null;

        // 2. Role bonus. If the description mentioned "button" and we found a
        //    Button, give a small boost. Penalise Edit/Custom unless the
        //    description hinted at them.
        double roleBonus = 0;
        var ctLower = ctName.ToLowerInvariant();
        if (roleHints.Count > 0)
        {
            foreach (var hint in roleHints)
            {
                if (ctLower.Contains(hint))
                {
                    roleBonus = 0.15;
                    break;
                }
            }
        }
        else
        {
            // No role hint: prefer Button/MenuItem/Hyperlink slightly over
            // generic Custom/Image/Text matches.
            if (ctLower is "button" or "menuitem" or "tabitem" or "hyperlink") roleBonus = 0.05;
        }

        // 3. Penalties.
        double penalty = 0;
        var penaltyReasons = new List<string>();
        if (!enabled) { penalty += 0.20; penaltyReasons.Add("disabled-0.20"); }
        if (rect.Width * rect.Height > 600 * 600) { penalty += 0.10; penaltyReasons.Add("huge-0.10"); }
        // Penalise hits inside our own controller window so its "OK"-named
        // buttons (if any) can never outrank a real dialog button on screen.
        if (sourceWindowTitle.StartsWith("Busy User Bot", StringComparison.OrdinalIgnoreCase))
        {
            penalty += 0.50;
            penaltyReasons.Add("self-0.50");
        }

        // 4. Cursor proximity tiebreak (very small contribution).
        double proximityBonus = 0;
        if (cursorHint is Point cur)
        {
            int cx = rect.Left + rect.Width / 2;
            int cy = rect.Top  + rect.Height / 2;
            double dx = cx - cur.X, dy = cy - cur.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            // Within 200 px → up to +0.05.
            proximityBonus = Math.Max(0, 0.05 * (1.0 - Math.Min(dist, 200) / 200));
        }

        double score = Math.Clamp(nameScore + roleBonus + proximityBonus - penalty, 0, 1.5);

        var breakdown = $"name={nameScore:0.00}({nameSource}) role+{roleBonus:0.00} prox+{proximityBonus:0.00}" +
                        (penalty > 0 ? $" pen-{penalty:0.00}[{string.Join(",", penaltyReasons)}]" : "");

        return new Candidate(rect, name, ctName, sourceWindowTitle, score, breakdown);
    }

    // -------------------------------------------------------------------
    // Keyword extraction
    // -------------------------------------------------------------------

    /// <summary>
    /// Parses <paramref name="description"/> into:
    ///   - <paramref name="quoted"/>: anything inside straight or curly
    ///     quotes — the strongest match signal (e.g. <c>"OK"</c>).
    ///   - <paramref name="roleHints"/>: lowercase role words
    ///     (button/menu/tab/...) used to bias toward matching ControlType.
    ///   - returned list: other content tokens (capitalised words, etc.)
    ///     useful as weaker fallback keywords.
    /// </summary>
    private static IReadOnlyList<string> ExtractKeywords(
        string description,
        out List<string> quoted,
        out HashSet<string> roleHints)
    {
        quoted = new List<string>();
        roleHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strip and capture quoted spans first.
        foreach (Match m in Regex.Matches(description, "[\"\u201C\u201D\u2018\u2019']([^\"\u201C\u201D\u2018\u2019']{1,80})[\"\u201C\u201D\u2018\u2019']"))
        {
            var v = m.Groups[1].Value.Trim();
            if (v.Length > 0) quoted.Add(v);
        }

        var lower = description.ToLowerInvariant();
        foreach (var role in RoleVocabulary)
            if (Regex.IsMatch(lower, $@"\b{role}\b"))
                roleHints.Add(role);

        // Capitalised tokens (heuristic content words like "Self-test", "OK").
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(description, @"\b[A-Z][A-Za-z0-9_\-]{1,40}\b"))
        {
            var t = m.Value;
            if (StopWords.Contains(t)) continue;
            tokens.Add(t);
        }
        // De-dup, preserve order.
        var dedup = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens) if (seen.Add(t)) dedup.Add(t);
        return dedup;
    }

    // Lowercase role words that map onto UIA ControlType names. Compared with
    // ControlType.ToString().ToLowerInvariant() in ScoreCandidate.
    private static readonly string[] RoleVocabulary =
    {
        "button", "menuitem", "menu", "tab", "tabitem", "link", "hyperlink",
        "checkbox", "radio", "edit", "textbox", "combobox", "list", "listitem",
        "tree", "treeitem", "image", "icon", "splitbutton",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "A", "An", "On", "In", "Of", "To", "And", "Or", "NOT",
        "Click", "Press", "Open", "Close", "OK", "Cancel", "Yes", "No",
        // ^^ note: OK/Cancel are deliberately excluded from the
        // "capitalised content tokens" list because they are nearly always
        // quoted in a well-formed target string; including them as weak
        // fallback keywords would inflate scores for unrelated controls.
        "Windows", "Self", "Test",
    };

    private static string Trunc(string s, int n = 60) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";
}
