using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Centralized, conservative currency helpers extracted from TravelButtonUI.
/// - DetectPlayerCurrencyOrMinusOne(): best-effort read of player's currency (returns -1 if unknown)
/// - TryDeductPlayerCurrency(amount): best-effort deduction using common method/field/property names
/// - TryRefundPlayerCurrency(amount): best-effort refund using common method/field/property names
///
/// This helper preserves the original reflection-based heuristics used in the project so behavior remains compatible.
/// Keep it small and conservative — it only centralizes the duplicated logic and logs via TravelButtonMod.
/// </summary>
public static class CurrencyHelpers
{
    // Helper that attempts to notify / refresh UI and game systems after currency change.
    private static void TryRefreshCurrencyDisplay(string currencyKeyword = "silver")
    {
        try
        {
            currencyKeyword = (currencyKeyword ?? "silver").Trim().ToLowerInvariant();

            // Candidate method/property names that likely update UI/state
            string[] refreshMethodCandidates = new string[]
            {
            "OnCurrencyChanged", "OnMoneyChanged", "OnSilverChanged", "Refresh", "RefreshUI", "UpdateUI",
            "RefreshInventory", "OnInventoryChanged", "Rebuild", "ForceUpdate", "Sync", "UpdateMoneyDisplay",
            "NotifyCurrencyChanged", "UpdateCurrency", "UpdateHud", "RefreshHud"
            };

            // 1) Try to find a CentralGatherable-like singleton and invoke refresh on it if available
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });

            // Prefer types named CentralGatherable or any type containing "central" + currencyKeyword
            Type centralType = allTypes.FirstOrDefault(tt => tt.Name.Equals("CentralGatherable", StringComparison.InvariantCultureIgnoreCase))
                                ?? allTypes.FirstOrDefault(tt => tt.Name.ToLowerInvariant().Contains("central") && tt.Name.ToLowerInvariant().Contains(currencyKeyword));

            if (centralType != null)
            {
                // Try to get a singleton Instance field/property
                object centralInstance = null;
                var instProp = centralType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instProp != null) centralInstance = instProp.GetValue(null);
                else
                {
                    var instField = centralType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (instField != null) centralInstance = instField.GetValue(null);
                }

                if (centralInstance != null)
                {
                    foreach (var mName in refreshMethodCandidates)
                    {
                        try
                        {
                            var mi = centralType.GetMethod(mName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                            if (mi != null)
                            {
                                mi.Invoke(centralInstance, null);
                                TBLog.Info($"TryRefreshCurrencyDisplay: invoked {centralType.FullName}.{mName}()");
                                return;
                            }
                        }
                        catch (TargetInvocationException tie)
                        {
                            TBLog.Warn($"TryRefreshCurrencyDisplay: {centralType.FullName}.{mName} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }
                        catch { }
                    }

                    // Try broadcasting a common GameObject message if component has a GameObject property
                    try
                    {
                        var goProp = centralType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                        var go = goProp?.GetValue(centralInstance) as UnityEngine.GameObject;
                        if (go != null)
                        {
                            foreach (var mName in new string[] { "OnCurrencyChanged", "OnMoneyChanged", "OnSilverChanged", "Refresh" })
                            {
                                try { go.BroadcastMessage(mName, SendMessageOptions.DontRequireReceiver); }
                                catch { }
                            }
                            TBLog.Info("TryRefreshCurrencyDisplay: BroadcastMessage attempted on central instance gameObject.");
                            return;
                        }
                    }
                    catch { }
                }
            }

            // 2) Try player inventory / player character objects (common names)
            try
            {
                // Try CharacterManager.Instance?.GetFirstLocalCharacter() if available via reflection
                var cmType = allTypes.FirstOrDefault(tt => tt.Name == "CharacterManager");
                object charManagerInstance = null;
                if (cmType != null)
                {
                    var cmInstProp = cmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (cmInstProp != null) charManagerInstance = cmInstProp.GetValue(null);
                }

                if (charManagerInstance != null)
                {
                    // Try to get the local character object and its inventory/gameObject
                    var getFirstLocal = cmType.GetMethod("GetFirstLocalCharacter", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (getFirstLocal != null)
                    {
                        var player = getFirstLocal.Invoke(charManagerInstance, null);
                        if (player != null)
                        {
                            // invoke refresh methods on player
                            var pType = player.GetType();
                            foreach (var mName in refreshMethodCandidates)
                            {
                                try
                                {
                                    var mi = pType.GetMethod(mName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                                    if (mi != null)
                                    {
                                        mi.Invoke(player, null);
                                        TBLog.Info($"TryRefreshCurrencyDisplay: invoked player.{mName}()");
                                        return;
                                    }
                                }
                                catch { }
                            }

                            // Try player.gameObject.BroadcastMessage(...)
                            try
                            {
                                var goProp = pType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                                var go = goProp?.GetValue(player) as UnityEngine.GameObject;
                                if (go != null)
                                {
                                    foreach (var mName in new string[] { "OnCurrencyChanged", "OnMoneyChanged", "OnInventoryChanged", "Refresh" })
                                    {
                                        try { go.BroadcastMessage(mName, SendMessageOptions.DontRequireReceiver); }
                                        catch { }
                                    }
                                    TBLog.Info("TryRefreshCurrencyDisplay: BroadcastMessage attempted on player gameObject.");
                                    return;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { /* ignore player refresh attempts failing */ }

            // 3) Broad attempt: call candidate refresh methods on all MonoBehaviours found (best-effort)
            try
            {
                var allMBs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in allMBs)
                {
                    if (mb == null) continue;
                    var mt = mb.GetType();
                    foreach (var mName in refreshMethodCandidates)
                    {
                        try
                        {
                            var mi = mt.GetMethod(mName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                            if (mi != null)
                            {
                                mi.Invoke(mb, null);
                                TBLog.Info($"TryRefreshCurrencyDisplay: invoked {mt.FullName}.{mName}()");
                                return;
                            }
                        }
                        catch { /*ignore per-component invocation failures*/ }
                    }
                }
            }
            catch { /* final fallback failure ignored */ }

            TBLog.Info("TryRefreshCurrencyDisplay: no explicit refresh method found; refresh attempt complete (no-op).");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryRefreshCurrencyDisplay exception: " + ex);
        }
    }

    /// <summary>
    /// Attempts to locate and return player's currency amount. Returns -1 if it couldn't be detected.
    /// Uses common property names, getter methods, fields and properties heuristics.
    /// </summary>
    public static long DetectPlayerCurrencyOrMinusOne()
    {
        try
        {
            var player = CharacterManager.Instance?.GetFirstLocalCharacter();
            if (player == null)
            {
                TBLog.Warn("DetectPlayerCurrencyOrMinusOne: Could not find the local player character.");
                return -1;
            }
            // Instead of scanning all MonoBehaviours, only scan components on the player character.
            var playerComponents = player.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in playerComponents)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                // Try common property names first (readable)
                string[] propNames = new string[] { "Silver", "Money", "Gold", "Coins", "Currency", "CurrentMoney", "SilverAmount", "MoneyAmount", "ContainedSilver" };
                foreach (var pn in propNames)
                {
                    try
                    {
                        var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pi != null && pi.CanRead)
                        {
                            var val = pi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                    }
                    catch { /* ignore property access errors */ }
                }
                // Try common zero-arg methods like GetMoney(), GetSilver()
                string[] methodNames = new string[] { "GetMoney", "GetSilver", "GetCoins", "GetCurrency" };
                foreach (var mn in methodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (mi != null && mi.GetParameters().Length == 0)
                        {
                            var res = mi.Invoke(mb, null);
                            if (res is int) return (int)res;
                            if (res is long) return (long)res;
                            if (res is float) return (long)((float)res);
                            if (res is double) return (long)((double)res);
                        }
                    }
                    catch { /* ignore method invocation errors */ }
                }
                // Try fields with heuristic names
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency") || name.Contains("contained"))
                        {
                            var val = fi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                    }
                    catch { /* ignore field access */ }
                }
                // Try properties by heuristic names (generic scan)
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = pi.Name.ToLowerInvariant();
                        if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency") || name.Contains("contained")) && pi.CanRead)
                        {
                            var val = pi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                    }
                    catch { /* ignore property access */ }
                }
            }
            TBLog.Warn("CurrencyHelpers: could not detect a currency field/property on the player character.");
            return -1;
        }
        catch (Exception ex)
        {
            TBLog.Warn("CurrencyHelpers.DetectPlayerCurrencyOrMinusOne exception: " + ex);
            return -1;
        }
    }

    /// <summary>
    /// Attempts to deduct currency from player. Returns true if deduction was performed successfully.
    /// Uses common method names (RemoveMoney, SpendMoney, RemoveSilver, etc.) or direct field/property mutation.
    /// If it finds a candidate and determines funds are insufficient it returns false.
    ///
    /// NOTE: This version is more conservative than the previous one — it verifies effects when method return type is non-boolean.
    /// </summary>
    // Replace the existing TryDeductPlayerCurrency method with this improved implementation.
    public static bool TryDeductPlayerCurrency(int amount, string currencyKeyword = "silver")
    {
        if (amount < 0)
        {
            TBLog.Warn($"TryDeductPlayerCurrency: cannot process a negative amount: {amount}");
            return false;
        }
        if (amount == 0) return true;

        currencyKeyword = (currencyKeyword ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(currencyKeyword)) currencyKeyword = "silver";

        try
        {
            TBLog.Info($"TryDeductPlayerCurrency: trying to deduct {amount} {currencyKeyword}.");

            var player = CharacterManager.Instance?.GetFirstLocalCharacter();
            if (player == null)
            {
                TBLog.Warn("TryDeductPlayerCurrency: could not find local player.");
                return false;
            }

            // Gather candidate inventory-like objects: player.Inventory plus all child components whose
            // type name suggests an inventory/characterinventory/bag/wallet.
            var candidates = new List<object>();
            try
            {
                var inv = player.Inventory;
                if (inv != null) candidates.Add(inv);

                var comps = player.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (var c in comps)
                {
                    var tname = c.GetType().Name.ToLowerInvariant();
                    if (tname.Contains("inventory") || tname.Contains("characterinventory") || tname.Contains("bag") || tname.Contains("wallet") || tname.Contains("pouch"))
                    {
                        if (!candidates.Contains(c)) candidates.Add(c);
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryDeductPlayerCurrency: collecting inventory candidates failed: " + ex.Message);
            }

            long before = DetectPlayerCurrencyOrMinusOne();

            // Try each candidate with the same reflection-based strategies your original code used,
            // but applied across all candidates until one succeeds.
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                var invType = candidate.GetType();

                // 1) Try RemoveItem(int id, int/long qty) style methods
                try
                {
                    const int silverItemID = 6100110;
                    var removeMi = invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                     null, new Type[] { typeof(int), typeof(int) }, null)
                                   ?? invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                        null, new Type[] { typeof(int), typeof(long) }, null)
                                   ?? invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                            .FirstOrDefault(m => m.Name.Equals("RemoveItem", StringComparison.InvariantCultureIgnoreCase)
                                                                 && m.GetParameters().Length >= 2
                                                                 && (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long))
                                                                 && (m.GetParameters()[1].ParameterType == typeof(int) || m.GetParameters()[1].ParameterType == typeof(long)));

                    if (removeMi != null)
                    {
                        object res = null;
                        try
                        {
                            var pType = removeMi.GetParameters()[1].ParameterType;
                            var argQty = pType == typeof(long) ? (object)(long)amount : (object)amount;
                            var args = new object[] { silverItemID, argQty };
                            var ps = removeMi.GetParameters();
                            if (ps.Length > 2)
                            {
                                var fullArgs = new object[ps.Length];
                                fullArgs[0] = silverItemID;
                                fullArgs[1] = argQty;
                                for (int i = 2; i < ps.Length; i++)
                                    fullArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                                args = fullArgs;
                            }
                            res = removeMi.Invoke(candidate, args);
                            TBLog.Info($"TryDeductPlayerCurrency: called {invType.FullName}.{removeMi.Name}({silverItemID},{amount}) -> {res}");
                        }
                        catch (TargetInvocationException tie)
                        {
                            TBLog.Warn($"TryDeductPlayerCurrency: {invType.FullName}.{removeMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }

                        // If method returns bool, accept true only if it returned true AND currency decreased
                        if (removeMi.ReturnType == typeof(bool))
                        {
                            if (res is bool ok && ok)
                            {
                                long after = DetectPlayerCurrencyOrMinusOne();
                                if (before != -1 && after != -1 && after == before - amount)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                // If currency reading is unreliable, accept boolean true as success (best-effort)
                                if (!(before != -1 && after != -1)) { TryRefreshCurrencyDisplay(currencyKeyword); return true; }
                            }
                            else
                            {
                                // returned false => try next candidate
                            }
                        }
                        else
                        {
                            // Non-boolean return: verify by reading currency
                            long after = DetectPlayerCurrencyOrMinusOne();
                            if (before != -1 && after != -1 && after == before - amount)
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryDeductPlayerCurrency: RemoveItem attempt on {candidate.GetType().FullName} failed: {ex.Message}");
                }

                // 2) Try AddItem with negative quantity (some inventories accept negative adds)
                try
                {
                    var addMi = invType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                  null, new Type[] { typeof(int), typeof(int) }, null)
                                ?? invType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                      null, new Type[] { typeof(int), typeof(long) }, null)
                                ?? invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                        .FirstOrDefault(m => m.Name.Equals("AddItem", StringComparison.InvariantCultureIgnoreCase)
                                                             && m.GetParameters().Length >= 2
                                                             && (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long))
                                                             && (m.GetParameters()[1].ParameterType == typeof(int) || m.GetParameters()[1].ParameterType == typeof(long)));
                    if (addMi != null)
                    {
                        object res = null;
                        try
                        {
                            var pType = addMi.GetParameters()[1].ParameterType;
                            var argQty = pType == typeof(long) ? (object)(long)-amount : (object)-amount;
                            var args = new object[] { 6100110, argQty };
                            var ps = addMi.GetParameters();
                            if (ps.Length > 2)
                            {
                                var fullArgs = new object[ps.Length];
                                fullArgs[0] = 6100110;
                                fullArgs[1] = argQty;
                                for (int i = 2; i < ps.Length; i++)
                                    fullArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                                args = fullArgs;
                            }
                            res = addMi.Invoke(candidate, args);
                            TBLog.Info($"TryDeductPlayerCurrency: called {invType.FullName}.{addMi.Name}(6100110,{-amount}) -> {res}");
                        }
                        catch (TargetInvocationException tie)
                        {
                            TBLog.Warn($"TryDeductPlayerCurrency: {invType.FullName}.{addMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }

                        if (addMi.ReturnType == typeof(bool))
                        {
                            if (res is bool ok && ok)
                            {
                                long after = DetectPlayerCurrencyOrMinusOne();
                                if (before != -1 && after != -1 && after == before - amount)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                if (!(before != -1 && after != -1)) { TryRefreshCurrencyDisplay(currencyKeyword); return true; }
                            }
                        }
                        else
                        {
                            long after = DetectPlayerCurrencyOrMinusOne();
                            if (before != -1 && after != -1 && after == before - amount)
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryDeductPlayerCurrency: AddItem attempt on {candidate.GetType().FullName} failed: {ex.Message}");
                }

                // 3) Try generic named methods that mention currencyKeyword and an action (remove/spend/consume)
                try
                {
                    var nameLowerCandidates = new[] { "remove", "subtract", "sub", "spend", "consume", "decrease", "deduct" };
                    var invMi = invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                      .FirstOrDefault(m =>
                                      {
                                          var n = m.Name.ToLowerInvariant();
                                          bool nameMatchesCurrency = n.Contains(currencyKeyword);
                                          bool nameMatchesAction = nameLowerCandidates.Any(k => n.Contains(k));
                                          bool hasSingleNumericParam = m.GetParameters().Length == 1 &&
                                              (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long));
                                          return (nameMatchesCurrency && nameMatchesAction && hasSingleNumericParam) ||
                                                 (nameMatchesAction && hasSingleNumericParam && m.Name.ToLowerInvariant().Contains("silver"));
                                      });

                    if (invMi != null)
                    {
                        var pType = invMi.GetParameters()[0].ParameterType;
                        var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                        object res = null;
                        try
                        {
                            res = invMi.Invoke(candidate, new object[] { arg });
                            TBLog.Info($"TryDeductPlayerCurrency: called {invType.FullName}.{invMi.Name}({amount}) -> {res}");
                        }
                        catch (TargetInvocationException tie)
                        {
                            TBLog.Warn($"TryDeductPlayerCurrency: {invType.FullName}.{invMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }

                        if (invMi.ReturnType == typeof(bool))
                        {
                            if (res is bool ok && ok)
                            {
                                long after = DetectPlayerCurrencyOrMinusOne();
                                if (before != -1 && after != -1 && after == before - amount)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                if (!(before != -1 && after != -1)) { TryRefreshCurrencyDisplay(currencyKeyword); return true; }
                            }
                        }
                        else
                        {
                            long after = DetectPlayerCurrencyOrMinusOne();
                            if (before != -1 && after != -1 && after == before - amount)
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryDeductPlayerCurrency: generic method attempt on {candidate.GetType().FullName} failed: {ex}");
                }

                // 4) Generic numeric field/property decrement fallbacks on this candidate
                try
                {
                    foreach (var fi in invType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try
                        {
                            var name = fi.Name.ToLowerInvariant();
                            if (!name.Contains(currencyKeyword) && !name.Contains("contained") && !name.Contains("silver") && !name.Contains("amount")) continue;

                            if (fi.FieldType == typeof(int))
                            {
                                int cur = (int)fi.GetValue(candidate);
                                if (cur < amount) { TBLog.Warn($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{fi.Name} (int). Current {cur}, requested {amount}."); continue; }
                                fi.SetValue(candidate, cur - amount);
                                TBLog.Info($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                long after = DetectPlayerCurrencyOrMinusOne();
                                if (before != -1 && after != -1 && after == before - amount)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                // If field change is local and detection unreliable, still accept as success
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                            else if (fi.FieldType == typeof(long))
                            {
                                long cur = (long)fi.GetValue(candidate);
                                if (cur < amount) { TBLog.Warn($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{fi.Name} (long). Current {cur}, requested {amount}."); continue; }
                                fi.SetValue(candidate, cur - amount);
                                TBLog.Info($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn($"TryDeductPlayerCurrency: inventory field access {invType.FullName}.{fi.Name} threw: {ex}");
                        }
                    }

                    foreach (var pi in invType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try
                        {
                            var name = pi.Name.ToLowerInvariant();
                            if (!name.Contains(currencyKeyword) && !name.Contains("contained") && !name.Contains("silver") && !name.Contains("amount")) continue;
                            if (!pi.CanRead || !pi.CanWrite) continue;

                            if (pi.PropertyType == typeof(int))
                            {
                                int cur = (int)pi.GetValue(candidate);
                                if (cur < amount) { TBLog.Warn($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{pi.Name} (int). Current {cur}, requested {amount}."); continue; }
                                pi.SetValue(candidate, cur - amount);
                                TBLog.Info($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                            else if (pi.PropertyType == typeof(long))
                            {
                                long cur = (long)pi.GetValue(candidate);
                                if (cur < amount) { TBLog.Warn($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{pi.Name} (long). Current {cur}, requested {amount}."); continue; }
                                pi.SetValue(candidate, cur - amount);
                                TBLog.Info($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn($"TryDeductPlayerCurrency: inventory property access {invType.FullName}.{pi.Name} threw: {ex}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryDeductPlayerCurrency: generic field/property fallback on {invType.FullName} failed: {ex}");
                }
            } // end foreach candidate

            // If we get here, no candidate successfully deducted currency
            TBLog.Warn($"TryDeductPlayerCurrency: could not find a place to deduct the currency containing '{currencyKeyword}'.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryDeductPlayerCurrency exception: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Attempts to refund currency to the player. Returns true if a refund action was performed.
    /// Mirrors the deduction heuristics with additive actions and verifies changes for non-boolean returns.
    /// </summary>
    public static bool TryRefundPlayerCurrency(int amount, string currencyKeyword = "silver")
    {
        if (amount < 0)
        {
            TBLog.Warn($"TryRefundPlayerCurrency: cannot process a negative amount: {amount}");
            return false;
        }
        if (amount == 0)
        {
            return true;
        }

        currencyKeyword = (currencyKeyword ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(currencyKeyword))
        {
            currencyKeyword = "silver";
        }

        try
        {
            TBLog.Info($"TryRefundPlayerCurrency: trying to refund {amount} {currencyKeyword}.");

            // 2) Try direct player / inventory manipulation (preferred fallback)
            try
            {
                var player = CharacterManager.Instance?.GetFirstLocalCharacter();
                if (player != null)
                {
                    var inventory = player.Inventory;
                    if (inventory != null)
                    {
                        long before = DetectPlayerCurrencyOrMinusOne();

                        // If currencyKeyword == "silver" try adding silver item back via Inventory.AddItem(itemId, qty) if available
                        if (currencyKeyword == "silver")
                        {
                            const int silverItemID = 6100110;
                            try
                            {
                                var invType = inventory.GetType();
                                var addMi = invType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                              null, new Type[] { typeof(int), typeof(int) }, null)
                                            ?? invType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                                  null, new Type[] { typeof(int), typeof(long) }, null)
                                            ?? invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                                    .FirstOrDefault(m => m.Name.Equals("AddItem", StringComparison.InvariantCultureIgnoreCase)
                                                                         && m.GetParameters().Length >= 2
                                                                         && (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long))
                                                                         && (m.GetParameters()[1].ParameterType == typeof(int) || m.GetParameters()[1].ParameterType == typeof(long)));

                                if (addMi != null)
                                {
                                    object res = null;
                                    try
                                    {
                                        var paramType = addMi.GetParameters()[1].ParameterType;
                                        var argQty = paramType == typeof(long) ? (object)(long)amount : (object)amount;
                                        var args = new object[] { silverItemID, argQty };
                                        var ps = addMi.GetParameters();
                                        if (ps.Length > 2)
                                        {
                                            var fullArgs = new object[ps.Length];
                                            fullArgs[0] = silverItemID;
                                            fullArgs[1] = argQty;
                                            for (int i = 2; i < ps.Length; i++)
                                            {
                                                fullArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                                            }
                                            args = fullArgs;
                                        }

                                        res = addMi.Invoke(inventory, args);
                                        TBLog.Info($"TryRefundPlayerCurrency: called Inventory.{addMi.Name}({silverItemID},{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TBLog.Warn($"TryRefundPlayerCurrency: Inventory.{addMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                    }

                                    if (addMi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool ok && ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TBLog.Warn("TryRefundPlayerCurrency: Inventory.AddItem returned false.");
                                        return false;
                                    }
                                    else
                                    {
                                        long after = DetectPlayerCurrencyOrMinusOne();
                                        if (before != -1 && after != -1)
                                        {
                                            if (after == before + amount)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            TBLog.Warn($"TryRefundPlayerCurrency: AddItem did not change currency as expected (before={before}, after={after}). Will try alternative fallbacks.");
                                        }
                                        else
                                        {
                                            TBLog.Info("TryRefundPlayerCurrency: AddItem returned non-bool and currency read is unreliable; will attempt fallbacks.");
                                        }
                                    }
                                }
                                else
                                {
                                    // fallback: look for inventory methods that contain currencyKeyword and are additive
                                    var invMi = invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                                      .FirstOrDefault(m => m.Name.ToLowerInvariant().Contains("add") && m.Name.ToLowerInvariant().Contains(currencyKeyword)
                                                                           && m.GetParameters().Length == 1
                                                                           && (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long)));
                                    if (invMi != null)
                                    {
                                        var pType = invMi.GetParameters()[0].ParameterType;
                                        var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                        object res = null;
                                        try
                                        {
                                            res = invMi.Invoke(inventory, new object[] { arg });
                                            TBLog.Info($"TryRefundPlayerCurrency: called {invType.FullName}.{invMi.Name}({amount}) -> {res}");
                                        }
                                        catch (TargetInvocationException tie)
                                        {
                                            TBLog.Warn($"TryRefundPlayerCurrency: inventory method {invMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                        }

                                        if (invMi.ReturnType == typeof(bool))
                                        {
                                            if (res is bool ok && ok)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            TBLog.Warn($"TryRefundPlayerCurrency: {invType.FullName}.{invMi.Name} returned false.");
                                            return false;
                                        }
                                        else
                                        {
                                            long after = DetectPlayerCurrencyOrMinusOne();
                                            if (before != -1 && after != -1)
                                            {
                                                if (after == before + amount)
                                                {
                                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                                    return true;
                                                }
                                                TBLog.Warn($"TryRefundPlayerCurrency: {invMi.Name} did not change currency as expected (before={before}, after={after}).");
                                            }
                                            else
                                            {
                                                TBLog.Info($"TryRefundPlayerCurrency: {invMi.Name} returned non-bool and currency read is unreliable; trying other fallbacks.");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TBLog.Warn($"TryRefundPlayerCurrency: inventory silver-path failed: {ex}");
                            }
                        }

                        // Generic fallback: increment numeric field/property on inventory that contains currencyKeyword
                        try
                        {
                            var invType = inventory.GetType();

                            foreach (var fi in invType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var name = fi.Name.ToLowerInvariant();
                                    if (!name.Contains(currencyKeyword) && !name.Contains("contained")) continue;

                                    if (fi.FieldType == typeof(int))
                                    {
                                        int cur = (int)fi.GetValue(inventory);
                                        fi.SetValue(inventory, cur + amount);
                                        TBLog.Info($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{fi.Name} (int). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (fi.FieldType == typeof(long))
                                    {
                                        long cur = (long)fi.GetValue(inventory);
                                        fi.SetValue(inventory, cur + amount);
                                        TBLog.Info($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{fi.Name} (long). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TBLog.Warn($"TryRefundPlayerCurrency: inventory field access {invType.FullName}.{fi.Name} threw: {ex}");
                                }
                            }

                            foreach (var pi in invType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var name = pi.Name.ToLowerInvariant();
                                    if (!name.Contains(currencyKeyword) && !name.Contains("contained")) continue;
                                    if (!pi.CanRead || !pi.CanWrite) continue;

                                    if (pi.PropertyType == typeof(int))
                                    {
                                        int cur = (int)pi.GetValue(inventory);
                                        pi.SetValue(inventory, cur + amount);
                                        TBLog.Info($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{pi.Name} (int). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (pi.PropertyType == typeof(long))
                                    {
                                        long cur = (long)pi.GetValue(inventory);
                                        pi.SetValue(inventory, cur + amount);
                                        TBLog.Info($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{pi.Name} (long). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TBLog.Warn($"TryRefundPlayerCurrency: inventory property access {invType.FullName}.{pi.Name} threw: {ex}");
                                }
                            }

                            long finalAfter = DetectPlayerCurrencyOrMinusOne();
                            if (before != -1 && finalAfter != -1 && finalAfter >= before + amount)
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn($"TryRefundPlayerCurrency: generic inventory fallback failed: {ex}");
                        }
                    }
                    else
                    {
                        TBLog.Warn("TryRefundPlayerCurrency: player inventory is null.");
                    }
                }
                else
                {
                    TBLog.Warn("TryRefundPlayerCurrency: could not find local player via CharacterManager.Instance.GetFirstLocalCharacter().");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TryRefundPlayerCurrency: player/inventory attempt failed: {ex}");
            }

            TBLog.Warn($"TryRefundPlayerCurrency: could not find a place to refund the currency containing '{currencyKeyword}'.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryRefundPlayerCurrency exception: " + ex);
            return false;
        }
    }

    // Replace or update the AttemptDeductSilverDirect method to perform an actual deduction (no simulated remove+refund).
    // Return true when currency was actually deducted, false otherwise.
    private static bool AttemptDeductSilverDirect(int amount)
    {
        try
        {
            TBLog.Info($"AttemptDeductSilverDirect: attempting to deduct {amount} silver (real).");

            // Try real deduction using the robust reflection-based helper
            bool ok = TryDeductPlayerCurrency(amount, "silver");
            if (ok)
            {
                TBLog.Info($"AttemptDeductSilverDirect: real deduction succeeded for {amount} silver.");
                return true;
            }

            TBLog.Warn($"AttemptDeductSilverDirect: real deduction failed for {amount} silver.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"AttemptDeductSilverDirect exception: {ex}");
            return false;
        }
    }

    // Unified AttemptDeductSilverDirect that supports both "simulate" and "real" modes.
    // Keep this public with the optional parameter to preserve existing callsites.
    public static bool AttemptDeductSilverDirect(int amount, bool justSimulate = false)
    {
        try
        {
            if (amount < 0)
            {
                TBLog.Warn($"AttemptDeductSilverDirect: invalid negative amount {amount}");
                return false;
            }
            if (amount == 0) return true;

            if (justSimulate)
            {
                TBLog.Info($"AttemptDeductSilverDirect: Simulating deduction of {amount} silver by attempting to remove and refund.");

                // Simulation: attempt a removal, verify it decreased total, then refund immediately.
                // We try real removal, but always refund so game state is unchanged.
                long before = DetectPlayerCurrencyOrMinusOne();
                bool removed = TryDeductPlayerCurrency(amount, "silver");
                long afterRemove = DetectPlayerCurrencyOrMinusOne();

                if (!removed && before != -1 && afterRemove != -1 && afterRemove == before - amount)
                {
                    // In some cases TryDeductPlayerCurrency may have used direct field manipulation or methods
                    // and returned false; treat observed change as removed.
                    removed = true;
                }

                if (!removed)
                {
                    TBLog.Info("AttemptDeductSilverDirect: Simulation remove failed; no refundable change observed.");
                    return false; // simulation failed
                }

                // Refund the same amount so we leave the player's state unchanged.
                bool refunded = TryRefundPlayerCurrency(amount, "silver");
                if (!refunded)
                {
                    TBLog.Warn("AttemptDeductSilverDirect: Simulation refund failed after successful simulated remove; this is unexpected.");
                    // Try best-effort to restore by reloading or logging, but return false to be safe.
                    return false;
                }

                TBLog.Info("AttemptDeductSilverDirect: Simulation successful (remove + refund verified).");
                return true;
            }
            else
            {
                TBLog.Info($"AttemptDeductSilverDirect: attempting to deduct {amount} silver (real).");
                bool ok = TryDeductPlayerCurrency(amount, "silver");
                if (ok)
                {
                    TBLog.Info($"AttemptDeductSilverDirect: real deduction succeeded for {amount} silver.");
                    return true;
                }

                TBLog.Warn($"AttemptDeductSilverDirect: real deduction failed for {amount} silver.");
                return false;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"AttemptDeductSilverDirect exception: {ex}");
            return false;
        }
    }

    // Best-effort currency amount detection used to show early "not enough resources"
    public static long GetPlayerCurrencyAmountOrMinusOne()
    {
        try
        {
            var allMono = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMono)
            {
                var t = mb.GetType();
                string[] propNames = new string[] { "Silver", "Money", "Gold", "Coins", "Currency", "CurrentMoney", "SilverAmount", "MoneyAmount" };
                foreach (var pn in propNames)
                {
                    var pi = t.GetProperty(pn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (pi != null && pi.CanRead)
                    {
                        try
                        {
                            var val = pi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                        catch { }
                    }
                }

                string[] methodNames = new string[] { "GetMoney", "GetSilver", "GetCoins", "GetCurrency" };
                foreach (var mn in methodNames)
                {
                    var mi = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (mi != null && mi.GetParameters().Length == 0)
                    {
                        try
                        {
                            var res = mi.Invoke(mb, null);
                            if (res is int) return (int)res;
                            if (res is long) return (long)res;
                            if (res is float) return (long)((float)res);
                            if (res is double) return (long)((double)res);
                        }
                        catch { }
                    }
                }

                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var name = fi.Name.ToLower();
                    if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
                    {
                        try
                        {
                            var val = fi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                        catch { }
                    }
                }
            }

            TBLog.Warn("GetPlayerCurrencyAmountOrMinusOne: could not detect a currency field/property automatically.");
            return -1;
        }
        catch (Exception ex)
        {
            TBLog.Warn("GetPlayerCurrencyAmountOrMinusOne exception: " + ex);
            return -1;
        }
    }

    /// <summary>
    /// Check whether the player can be charged 'price' silver.
    /// Does NOT perform any teleportation.
    /// Preferred (non-invasive) check: uses CurrencyHelpers.AttemptDeductSilverDirect(price, true) if available
    /// which simulates the deduction. If that simulation throws or is unavailable we fall back to a
    /// real deduct+refund attempt as a best-effort check.
    ///
    /// Returns true when a deduction is possible (either simulation succeeded, or real deduct succeeded
    /// and was refunded). Returns false when the player cannot be charged or when an unrecoverable
    /// error occurs. All exceptional conditions are caught and logged. If a real deduction is performed
    /// it is immediately refunded (best-effort).
    /// </summary>
    public static bool CheckChargePossibleAndRefund(int price)
    {
        if (price <= 0)
        {
            // No cost => trivially affordable
            return true;
        }

        try
        {
            // Preferred: simulate deduction if helper supports it.
            // (Existing code used AttemptDeductSilverDirect(price, false) to perform a real deduction,
            // so we call with simulate=true to only test affordability.)
            bool canSimulate = CurrencyHelpers.AttemptDeductSilverDirect(price, true);
            if (canSimulate)
            {
                TBLog.Info($"CheckChargePossibleAndRefund: simulation indicates player can pay {price} silver (no changes made).");
                return true;
            }

            TBLog.Info($"CheckChargePossibleAndRefund: simulation indicates player cannot pay {price} silver.");
            return false;
        }
        catch (Exception exSim)
        {
            // Simulation failed (method might throw or behave unexpectedly). Fall back to a real deduct+refund.
            TBLog.Warn("CheckChargePossibleAndRefund: simulation attempt threw, falling back to real deduct+refund. Exception: " + exSim);

            try
            {
                bool deducted = false;
                try
                {
                    // Perform a real deduction
                    deducted = CurrencyHelpers.AttemptDeductSilverDirect(price, false);
                }
                catch (Exception exDeduct)
                {
                    TBLog.Warn("CheckChargePossibleAndRefund: real deduction attempt threw: " + exDeduct);
                    deducted = false;
                }

                if (!deducted)
                {
                    TBLog.Info($"CheckChargePossibleAndRefund: real deduction failed -> player cannot pay {price} silver.");
                    return false;
                }

                // We successfully deducted. Now refund immediately (best-effort).
                try
                {
                    CurrencyHelpers.TryRefundPlayerCurrency(price);
                    TBLog.Info($"CheckChargePossibleAndRefund: deducted {price} silver and refunded successfully (probe).");
                }
                catch (Exception exRefund)
                {
                    TBLog.Warn("CheckChargePossibleAndRefund: refund after probe deduction failed: " + exRefund);
                    // Even if refund failed, return true because deduction succeeded (but state may be corrupt).
                    // Caller should be aware of the logged warning.
                }

                return true;
            }
            catch (Exception exFallback)
            {
                TBLog.Warn("CheckChargePossibleAndRefund: unexpected exception during fallback deduct/refund: " + exFallback);
                // Best-effort: attempt to refund in case some partial operation occurred
                try { CurrencyHelpers.TryRefundPlayerCurrency(price); } catch { }
                return false;
            }
        }
    }

}
