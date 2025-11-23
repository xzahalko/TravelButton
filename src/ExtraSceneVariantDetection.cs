// Replace your current detection file with this (only the detection part shown).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ExtraSceneVariantDetection
{
    public enum VariantConfidence { Unknown = 0, Low = 1, Medium = 2, High = 3 }

    // Lightweight fallback tokens (kept minimal). These are used only if no scene-derived tokens found.
    static readonly string[] FallbackNormalTokens = new[] { "Normal", "Intact", "Default" };
    static readonly string[] FallbackDestroyedTokens = new[] { "Destroyed", "Ruined", "Broken", "Ruin" };

    // ... existing helpers (Regexes, IsPlausibleVariantName, GatherSceneNamesBounded) unchanged ...

    public static (string normalName, string destroyedName) DetectVariantNames(UnityEngine.SceneManagement.Scene scene, string baseHint = null)
    {
        var (normal, destroyed, _) = DetectVariantNamesWithConfidence(scene, baseHint);
        return (normal, destroyed);
    }

    public static bool IsPlausibleVariantName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = name.Trim();

        // must contain at least one alphabetic character
        if (!RegexHelpers.ContainsAlphaPattern.IsMatch(s)) return false;

        // reject coordinate-like names: "(0.0, 0.0)" etc.
        if (RegexHelpers.CoordinatePattern.IsMatch(s)) return false;

        // reject explicit "Default Chunk" / chunk-like names
        var lower = s.ToLowerInvariant();
        if (lower.Contains("default chunk") || lower.StartsWith("chunk_") || lower.Contains("defaultchunk")) return false;

        // reject names that are mostly non-alphabetic
        if (RegexHelpers.NonAlphaPattern.IsMatch(s)) return false;

        // length bounds to avoid extremely short/long garbage
        if (s.Length < 3 || s.Length > 200) return false;

        return true;
    }

    /// <summary>
    /// Centralized regex helpers used by ExtraSceneVariantDetection.
    /// Use these compiled Regex instances to avoid recompiling patterns repeatedly.
    /// </summary>
    public static class RegexHelpers
    {
        // Matches coordinate-like groups: "(0.0, 0.0)" or "(1,2,3)" etc.
        public static readonly Regex CoordinatePattern =
            new Regex(@"\(\s*-?\d+(\.\d+)?\s*,\s*-?\d+(\.\d+)?\s*(,\s*-?\d+(\.\d+)?\s*)?\)",
                      RegexOptions.Compiled);

        // True when a string is composed mostly of non-alphabetic characters.
        public static readonly Regex NonAlphaPattern =
            new Regex(@"^[^A-Za-z]{2,}$", RegexOptions.Compiled);

        // Simple presence check for any ASCII alphabetic character.
        public static readonly Regex ContainsAlphaPattern =
            new Regex(@"[A-Za-z]", RegexOptions.Compiled);

        // Split on non-alphanumeric characters to get candidate tokens.
        public static readonly Regex SplitNonAlnumRegex =
            new Regex(@"[^A-Za-z0-9]+", RegexOptions.Compiled);

        // Break camelCase / PascalCase / mixed tokens into readable parts:
        // matches "HtmlParser" -> ["Html","Parser"], "XMLHttp2" -> ["XML","Http","2"]
        public static readonly Regex SplitCamelCaseTokenRegex =
            new Regex(@"([A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+)", RegexOptions.Compiled);
    }

    public static HashSet<string> GatherSceneNamesBounded(Scene scene, int maxObjects, int maxDepth)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root == null) continue;
            TraverseTransform(root.transform, 0);
            if (scanned >= maxObjects) break;
        }

        void TraverseTransform(Transform t, int depth)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) return;
            set.Add(t.name);
            scanned++;
            if (scanned >= maxObjects) return;
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; ++i)
            {
                TraverseTransform(t.GetChild(i), depth + 1);
                if (scanned >= maxObjects) return;
            }
        }

        return set;
    }

    public static (string normalName, string destroyedName, VariantConfidence confidence) DetectVariantNamesWithConfidence(Scene scene, string baseHint = null)
    {
        if (!scene.IsValid() || !scene.isLoaded) return (null, null, VariantConfidence.Unknown);

        // 1) Try to seed tokens from explicit stored config / JSON for this scene first (if you store variant names).
        // Example: read TravelButton_Cities.json entry for this scene and use variantNormalName/variantDestroyedName if present.
        // (Implementation omitted here — call your existing JSON loader if available)

        // 2) Attempt to derive tokens from scene names
        var sceneNames = GatherSceneNamesBounded(scene, maxObjects: 4000, maxDepth: 6).ToList();
        var (derivedNormalTokens, derivedDestroyedTokens) = DeriveTokensFromNamePairs(sceneNames, baseHint);

        // Choose token lists: derived if available, otherwise fallback
        var normalTokens = (derivedNormalTokens != null && derivedNormalTokens.Any())
            ? derivedNormalTokens.ToArray()
            : FallbackNormalTokens;

        var destroyedTokens = (derivedDestroyedTokens != null && derivedDestroyedTokens.Any())
            ? derivedDestroyedTokens.ToArray()
            : FallbackDestroyedTokens;

        // 3) Use fast pattern matching with chosen tokens
        // (reuse TryFastPatternMatch style approach but using our tokens)
        // ... implement fast search using normalTokens/destroyedTokens (omitted to keep snippet short) ...

        // 4) If nothing found, fallback to looser heuristics (contains token, pairs, etc.)
        // 5) Assess confidence and return (normal, destroyed, confidence)
        // (You can reuse logic from previous implementation but using the dynamic tokens.)
        return (null, null, VariantConfidence.Unknown);
    }

    // Derive token candidates by finding name pairs that differ only by one (small) substring.
    public static (List<string> normalTokens, List<string> destroyedTokens) DeriveTokensFromNamePairs(IEnumerable<string> namesEnumerable, string baseHint)
    {
        var names = namesEnumerable.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count < 2) return (null, null);

        // Normalize: optionally strip baseHint occurrences and scene words
        var cleaned = names.Select(n => new { original = n, stripped = StripBaseHint(n, baseHint) }).ToList();

        // Tokenize by non-alpha chars and camelCase boundaries
        var tokenized = cleaned.Select(x => new { x.original, parts = SplitIntoParts(x.stripped) }).ToList();

        // Build maps of signature->originals to find candidates that differ by one token
        var pairs = new List<(string normalToken, string destroyedToken)>();

        // Compare each pair of names (bounded by N^2 but names list is limited)
        int maxCompare = Math.Min(names.Count, 1000);
        for (int i = 0; i < maxCompare; i++)
        {
            for (int j = i + 1; j < maxCompare; j++)
            {
                var a = tokenized[i].parts;
                var b = tokenized[j].parts;

                // if lengths equal and differ by exactly one token that's a good candidate
                if (a.Length == b.Length)
                {
                    int diffCount = 0;
                    int diffIndex = -1;
                    for (int k = 0; k < a.Length; k++)
                    {
                        if (!string.Equals(a[k], b[k], StringComparison.OrdinalIgnoreCase))
                        {
                            diffCount++;
                            diffIndex = k;
                            if (diffCount > 1) break;
                        }
                    }
                    if (diffCount == 1)
                    {
                        var tokenA = a[diffIndex];
                        var tokenB = b[diffIndex];

                        if (IsPotentialVariantToken(tokenA) && IsPotentialVariantToken(tokenB))
                        {
                            // Decide which looks normal vs destroyed by keyword heuristics
                            if (LooksDestroyed(tokenA) && !LooksDestroyed(tokenB))
                                pairs.Add((tokenB, tokenA));
                            else if (LooksDestroyed(tokenB) && !LooksDestroyed(tokenA))
                                pairs.Add((tokenA, tokenB));
                            else
                                pairs.Add((tokenA, tokenB)); // unknown ordering, we'll rank later
                        }
                    }
                }

                // simple substring diff: if one contains the other plus a small token, consider that too
                // (left out for brevity; you can add heuristics here)
            }
        }

        if (pairs.Count == 0)
        {
            // fallback: try to find words that frequently appear with scene baseHint + suffix/prefix
            var hint = baseHint ?? "";
            var candidates = names.Where(n => n.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                                  .Select(n => ExtractLikelySuffix(n, hint))
                                  .Where(s => !string.IsNullOrEmpty(s) && IsPotentialVariantToken(s))
                                  .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                                  .OrderByDescending(g => g.Count())
                                  .Select(g => g.Key)
                                  .ToList();
            if (candidates.Count >= 2)
            {
                return (new List<string> { candidates[0] }, new List<string> { candidates[1] });
            }
            return (null, null);
        }

        // Rank the pairs by frequency and plausibility
        var grouped = pairs.GroupBy(p => (p.normalToken.ToLowerInvariant(), p.destroyedToken.ToLowerInvariant()))
                           .OrderByDescending(g => g.Count())
                           .Select(g => g.Key)
                           .ToList();

        // produce token lists (top-K)
        var normalList = grouped.Select(g => g.Item1).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        var destroyedList = grouped.Select(g => g.Item2).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

        return (normalList, destroyedList);
    }

    // Helper: split by non-alpha and camelCase
    static string[] SplitIntoParts(string name)
    {
        // split on non-alpha/digit and camel case boundary
        var tokens = Regex.Split(name, @"[^A-Za-z0-9]+")
                          .Where(t => !string.IsNullOrEmpty(t))
                          .SelectMany(t => Regex.Matches(t, @"([A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+)").Cast<Match>().Select(m => m.Value))
                          .Where(s => !string.IsNullOrEmpty(s))
                          .ToArray();
        return tokens;
    }

    static string StripBaseHint(string s, string baseHint)
    {
        if (string.IsNullOrEmpty(baseHint)) return s;
        return Regex.Replace(s, Regex.Escape(baseHint), "", RegexOptions.IgnoreCase).Trim(new char[] { ' ', '_', '-' });
    }

    static bool IsPotentialVariantToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Length < 2 || token.Length > 80) return false;
        if (!Regex.IsMatch(token, "[A-Za-z]")) return false;
        var low = token.ToLowerInvariant();
        if (low.Contains("default") || low.Contains("chunk") || low.Contains("placeholder")) return false;
        return true;
    }

    static bool LooksDestroyed(string token)
    {
        var low = token.ToLowerInvariant();
        return low.Contains("destroy") || low.Contains("ruin") || low.Contains("broken") || low.Contains("damag");
    }

    static string ExtractLikelySuffix(string fullName, string hint)
    {
        if (string.IsNullOrEmpty(hint)) return null;
        var idx = fullName.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var s = fullName.Substring(idx + hint.Length).Trim(new char[] { '_', '-', ' ' });
        if (string.IsNullOrEmpty(s)) return null;
        // take first token
        var parts = Regex.Split(s, @"[^A-Za-z0-9]+").Where(x => !string.IsNullOrEmpty(x)).ToArray();
        return parts.Length > 0 ? parts[0] : null;
    }
}

public static class VariantDetectDiagnostics
{
    // Returns the detected normal/destroyed values and the confidence as string for logging
    public static (string normalName, string destroyedName, string confidence) DumpDiagnostics(UnityEngine.SceneManagement.Scene scene)
    {
        try
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.Log("[VariantDetectDiag] scene invalid or not loaded: " + (scene.name ?? "<null>"));
                return (null, null, "Unknown");
            }

            Debug.Log($"[VariantDetectDiag] Dump for scene='{scene.name}'");

            // public detector quick result
            var (normal, destroyed, confidenceEnum) = ExtraSceneVariantDetection.DetectVariantNamesWithConfidence(scene, scene.name);
            Debug.Log($"[VariantDetectDiag] DetectVariantNamesWithConfidence -> normal='{normal ?? ""}' destroyed='{destroyed ?? ""}' confidence={confidenceEnum}");

            // Gather scene object names (bounded)
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            const int MaxObjects = 10000;
            const int MaxDepth = 12;

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                void Traverse(Transform t, int depth)
                {
                    if (t == null) return;
                    if (!string.IsNullOrEmpty(t.name)) set.Add(t.name);
                    scanned++;
                    if (scanned >= MaxObjects) return;
                    if (depth >= MaxDepth) return;
                    for (int i = 0; i < t.childCount; ++i)
                    {
                        Traverse(t.GetChild(i), depth + 1);
                        if (scanned >= MaxObjects) return;
                    }
                }
                Traverse(root.transform, 0);
                if (scanned >= MaxObjects) break;
            }

            var names = set.ToList();
            Debug.Log($"[VariantDetectDiag] Gathered {names.Count} unique object names (sample up to 50):");
            for (int i = 0; i < names.Count && i < 50; i++)
                Debug.Log($"[VariantDetectDiag]   name[{i}] = '{names[i]}'");

            // Tokenize & frequency (split non-alnum + camel-case)
            var tokenFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var splitNonAlnum = new Regex(@"[^A-Za-z0-9]+");
            var splitCamel = new Regex(@"([A-Z]+(?=$|[A-Z][a-z])|[A-Z]?[a-z]+|[0-9]+)");

            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                var parts = splitNonAlnum.Split(n);
                foreach (var p in parts)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var matches = splitCamel.Matches(p);
                    if (matches != null && matches.Count > 0)
                    {
                        foreach (Match m in matches)
                        {
                            var tok = m.Value;
                            if (string.IsNullOrWhiteSpace(tok)) continue;
                            tokenFreq.TryGetValue(tok, out var c); tokenFreq[tok] = c + 1;
                        }
                    }
                    else
                    {
                        tokenFreq.TryGetValue(p, out var c); tokenFreq[p] = c + 1;
                    }
                }
            }

            var topTokens = tokenFreq.OrderByDescending(kv => kv.Value).Take(60).ToList();
            Debug.Log($"[VariantDetectDiag] Top tokens (count {topTokens.Count}): {string.Join(", ", topTokens.Select(kv => kv.Key + ":" + kv.Value))}");

            // Plausibility check sample
            int sampleCount = Math.Min(30, names.Count);
            for (int i = 0; i < sampleCount; i++)
            {
                var s = names[i];
                bool hasAlpha = Regex.IsMatch(s, @"[A-Za-z]");
                bool looksCoord = Regex.IsMatch(s, @"\(\s*-?\d+(\.\d+)?\s*,\s*-?\d+(\.\d+)?\s*\)");
                bool mostlyNonAlpha = Regex.IsMatch(s, @"^[^A-Za-z]+$");
                bool plausible = hasAlpha && !looksCoord && !mostlyNonAlpha && s.Length >= 2 && s.Length <= 200;
                Debug.Log($"[VariantDetectDiag] Plausible? {plausible,-5} | hasAlpha={hasAlpha} looksCoord={looksCoord} mostlyNonAlpha={mostlyNonAlpha} | '{s}'");
            }

            return (normal, destroyed, confidenceEnum.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[VariantDetectDiag] exception: " + ex);
            return (null, null, "Unknown");
        }
    }
}