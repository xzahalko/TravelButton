using System;
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
                                TravelButtonPlugin.LogInfo($"TryRefreshCurrencyDisplay: invoked {centralType.FullName}.{mName}()");
                                return;
                            }
                        }
                        catch (TargetInvocationException tie)
                        {
                            TravelButtonPlugin.LogWarning($"TryRefreshCurrencyDisplay: {centralType.FullName}.{mName} threw: {tie.InnerException?.Message ?? tie.Message}");
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
                            TravelButtonPlugin.LogInfo("TryRefreshCurrencyDisplay: BroadcastMessage attempted on central instance gameObject.");
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
                                        TravelButtonPlugin.LogInfo($"TryRefreshCurrencyDisplay: invoked player.{mName}()");
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
                                    TravelButtonPlugin.LogInfo("TryRefreshCurrencyDisplay: BroadcastMessage attempted on player gameObject.");
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
                                TravelButtonPlugin.LogInfo($"TryRefreshCurrencyDisplay: invoked {mt.FullName}.{mName}()");
                                return;
                            }
                        }
                        catch { /*ignore per-component invocation failures*/ }
                    }
                }
            }
            catch { /* final fallback failure ignored */ }

            TravelButtonPlugin.LogInfo("TryRefreshCurrencyDisplay: no explicit refresh method found; refresh attempt complete (no-op).");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryRefreshCurrencyDisplay exception: " + ex);
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
                TravelButtonPlugin.LogWarning("DetectPlayerCurrencyOrMinusOne: Could not find the local player character.");
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
            TravelButtonPlugin.LogWarning("CurrencyHelpers: could not detect a currency field/property on the player character.");
            return -1;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("CurrencyHelpers.DetectPlayerCurrencyOrMinusOne exception: " + ex);
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
    public static bool TryDeductPlayerCurrency(int amount, string currencyKeyword = "silver")
    {
        if (amount < 0)
        {
            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: cannot process a negative amount: {amount}");
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
            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: trying to deduct {amount} {currencyKeyword}.");

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

                        // If currencyKeyword == "silver" try removing silver item via Inventory.RemoveItem(itemId, qty) if available
                        if (currencyKeyword == "silver")
                        {
                            const int silverItemID = 6100110;
                            try
                            {
                                var invType = inventory.GetType();

                                // 1) Prefer explicit RemoveItem(itemId, qty)
                                var removeMi = invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                                 null, new Type[] { typeof(int), typeof(int) }, null)
                                               ?? invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                                    null, new Type[] { typeof(int), typeof(long) }, null)
                                               // some games use signatures with more parameters (e.g. removeItem(id, qty, out, flags)), try simpler fallback
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
                                        // For methods with >=2 params we only pass the first two; additional params may be optional/defaulted.
                                        var args = new object[] { silverItemID, argQty };
                                        // If more params exist, fill with defaults/nulls
                                        var ps = removeMi.GetParameters();
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

                                        res = removeMi.Invoke(inventory, args);
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called Inventory.{removeMi.Name}({silverItemID},{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: Inventory.{removeMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                    }

                                    // If method returns bool, trust it.
                                    if (removeMi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool ok && ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: Inventory.RemoveItem returned false.");
                                        return false;
                                    }
                                    else
                                    {
                                        // Non-boolean return: verify effect by re-reading currency when possible.
                                        long after = DetectPlayerCurrencyOrMinusOne();
                                        if (before != -1 && after != -1)
                                        {
                                            if (after == before - amount)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: RemoveItem did not change currency as expected (before={before}, after={after}). Will try alternative fallbacks.");
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogInfo("TryDeductPlayerCurrency: RemoveItem returned non-bool and currency read is unreliable; will attempt fallbacks.");
                                        }
                                        // fall through to fallback attempts below
                                    }
                                }

                                // 2) If explicit RemoveItem not found or verification failed, see if AddItem exists and supports negative amounts
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
                                        var argQty = paramType == typeof(long) ? (object)(long)-amount : (object)-amount;
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
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called Inventory.{addMi.Name}({silverItemID},-{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: Inventory.{addMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                    }

                                    if (addMi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool ok && ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: Inventory.AddItem returned false when called with negative quantity.");
                                        return false;
                                    }
                                    else
                                    {
                                        long after = DetectPlayerCurrencyOrMinusOne();
                                        if (before != -1 && after != -1)
                                        {
                                            if (after == before - amount)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: AddItem(negative) did not change currency as expected (before={before}, after={after}). Will try alternative fallbacks.");
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogInfo("TryDeductPlayerCurrency: AddItem(negative) returned non-bool and currency read is unreliable; will attempt fallbacks.");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory silver-path failed: {ex}");
                            }
                        }

                        // Generic fallback: look for inventory methods that are subtractive or consume currency
                        try
                        {
                            var invType = inventory.GetType();
                            var nameLowerCandidates = new[] { "remove", "subtract", "sub", "spend", "consume", "decrease", "deduct" };

                            var invMi = invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                              .FirstOrDefault(m =>
                                              {
                                                  var n = m.Name.ToLowerInvariant();
                                                  bool nameMatchesCurrency = n.Contains(currencyKeyword);
                                                  bool nameMatchesAction = nameLowerCandidates.Any(k => n.Contains(k));
                                                  bool hasSingleNumericParam = m.GetParameters().Length == 1 &&
                                                      (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long));
                                                  return nameMatchesCurrency && nameMatchesAction && hasSingleNumericParam;
                                              });

                            if (invMi != null)
                            {
                                var pType = invMi.GetParameters()[0].ParameterType;
                                var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                object res = null;
                                try
                                {
                                    res = invMi.Invoke(inventory, new object[] { arg });
                                    TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {invType.FullName}.{invMi.Name}({amount}) -> {res}");
                                }
                                catch (TargetInvocationException tie)
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory method {invMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                }

                                if (invMi.ReturnType == typeof(bool))
                                {
                                    if (res is bool ok && ok)
                                    {
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {invType.FullName}.{invMi.Name} returned false.");
                                    return false;
                                }
                                else
                                {
                                    long after = DetectPlayerCurrencyOrMinusOne();
                                    if (before != -1 && after != -1)
                                    {
                                        if (after == before - amount)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {invMi.Name} did not change currency as expected (before={before}, after={after}).");
                                    }
                                    else
                                    {
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: {invMi.Name} returned non-bool and currency read is unreliable; trying other fallbacks.");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory method fallback failed: {ex}");
                        }

                        // Generic fallback: decrement numeric field/property on inventory that contains currencyKeyword
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
                                        if (cur < amount)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{fi.Name} (int). Current {cur}, requested {amount}.");
                                            return false;
                                        }
                                        fi.SetValue(inventory, cur - amount);
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (fi.FieldType == typeof(long))
                                    {
                                        long cur = (long)fi.GetValue(inventory);
                                        if (cur < amount)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{fi.Name} (long). Current {cur}, requested {amount}.");
                                            return false;
                                        }
                                        fi.SetValue(inventory, cur - amount);
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory field access {invType.FullName}.{fi.Name} threw: {ex}");
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
                                        if (cur < amount)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{pi.Name} (int). Current {cur}, requested {amount}.");
                                            return false;
                                        }
                                        pi.SetValue(inventory, cur - amount);
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (pi.PropertyType == typeof(long))
                                    {
                                        long cur = (long)pi.GetValue(inventory);
                                        if (cur < amount)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: insufficient {currencyKeyword} in {invType.FullName}.{pi.Name} (long). Current {cur}, requested {amount}.");
                                            return false;
                                        }
                                        pi.SetValue(inventory, cur - amount);
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: subtracted {amount} from {invType.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory property access {invType.FullName}.{pi.Name} threw: {ex}");
                                }
                            }

                            // If we reached here and we had a reliable 'before' read but couldn't change anything, fail.
                            long finalAfter = DetectPlayerCurrencyOrMinusOne();
                            if (before != -1 && finalAfter != -1 && finalAfter <= before - amount)
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: generic inventory fallback failed: {ex}");
                        }
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: player inventory is null.");
                    }
                }
                else
                {
                    TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: could not find local player via CharacterManager.Instance.GetFirstLocalCharacter().");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: player/inventory attempt failed: {ex}");
            }

            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: could not find a place to deduct the currency containing '{currencyKeyword}'.");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency exception: " + ex);
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
            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: cannot process a negative amount: {amount}");
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
            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: trying to refund {amount} {currencyKeyword}.");

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
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called Inventory.{addMi.Name}({silverItemID},{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: Inventory.{addMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                    }

                                    if (addMi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool ok && ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TravelButtonPlugin.LogWarning("TryRefundPlayerCurrency: Inventory.AddItem returned false.");
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
                                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: AddItem did not change currency as expected (before={before}, after={after}). Will try alternative fallbacks.");
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogInfo("TryRefundPlayerCurrency: AddItem returned non-bool and currency read is unreliable; will attempt fallbacks.");
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
                                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {invType.FullName}.{invMi.Name}({amount}) -> {res}");
                                        }
                                        catch (TargetInvocationException tie)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: inventory method {invMi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                        }

                                        if (invMi.ReturnType == typeof(bool))
                                        {
                                            if (res is bool ok && ok)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {invType.FullName}.{invMi.Name} returned false.");
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
                                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {invMi.Name} did not change currency as expected (before={before}, after={after}).");
                                            }
                                            else
                                            {
                                                TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: {invMi.Name} returned non-bool and currency read is unreliable; trying other fallbacks.");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: inventory silver-path failed: {ex}");
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
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{fi.Name} (int). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (fi.FieldType == typeof(long))
                                    {
                                        long cur = (long)fi.GetValue(inventory);
                                        fi.SetValue(inventory, cur + amount);
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{fi.Name} (long). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: inventory field access {invType.FullName}.{fi.Name} threw: {ex}");
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
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{pi.Name} (int). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    else if (pi.PropertyType == typeof(long))
                                    {
                                        long cur = (long)pi.GetValue(inventory);
                                        pi.SetValue(inventory, cur + amount);
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {invType.FullName}.{pi.Name} (long). New value {cur + amount}.");
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: inventory property access {invType.FullName}.{pi.Name} threw: {ex}");
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
                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: generic inventory fallback failed: {ex}");
                        }
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning("TryRefundPlayerCurrency: player inventory is null.");
                    }
                }
                else
                {
                    TravelButtonPlugin.LogWarning("TryRefundPlayerCurrency: could not find local player via CharacterManager.Instance.GetFirstLocalCharacter().");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: player/inventory attempt failed: {ex}");
            }

            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: could not find a place to refund the currency containing '{currencyKeyword}'.");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryRefundPlayerCurrency exception: " + ex);
            return false;
        }
    }

    public static bool AttemptDeductSilverDirect(int amount, bool justSimulate = false)
    {
        if (amount < 0)
        {
            TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Cannot process a negative amount: {amount}");
            return false;
        }
        if (amount == 0)
        {
            return true;
        }

        try
        {
            var player = CharacterManager.Instance?.GetFirstLocalCharacter();
            if (player == null)
            {
                TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Could not find the local player character.");
                return false;
            }

            var inventory = player.Inventory;
            if (inventory == null)
            {
                TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Player inventory is null.");
                return false;
            }

            // If you already have a reliable read (preferred), use it first.
            long playerSilver = DetectPlayerCurrencyOrMinusOne();
            if (playerSilver != -1 && playerSilver < amount)
            {
                TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Player does not have enough silver ({playerSilver} < {amount}).");
                return false;
            }

            if (justSimulate)
            {
                TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Simulating deduction of {amount} silver by attempting to remove and refund.");
                bool removed = false;
                long before = DetectPlayerCurrencyOrMinusOne();
                try
                {
                    // TryDeduct will now verify that currency actually decreased before returning true.
                    if (!TryDeductPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogInfo("AttemptDeductSilverDirect: Simulation remove reported failure (likely insufficient funds or verification failed).");
                        return false;
                    }

                    removed = true;
                    TravelButtonPlugin.LogInfo("AttemptDeductSilverDirect: Simulation remove succeeded - will attempt refund.");

                    // Verify currency decreased as expected before refunding (TryDeduct already does verification when possible).
                    long afterRemove = DetectPlayerCurrencyOrMinusOne();
                    if (before != -1 && afterRemove != -1 && afterRemove != before - amount)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: After simulated remove the currency did not match expected (before={before}, afterRemove={afterRemove}). Attempting to refund and failing simulation.");
                        TryRefundPlayerCurrency(amount);
                        return false;
                    }

                    // TryRefundPlayerCurrency should add the silver back. Ensure it returns success/false.
                    if (!TryRefundPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Simulation refund failed after RemoveItem. THIS IS SERIOUS.");
                        return false;
                    }

                    // Final verification: player currency should be back to before
                    long final = DetectPlayerCurrencyOrMinusOne();
                    if (before != -1 && final != -1 && final == before)
                    {
                        TravelButtonPlugin.LogInfo("AttemptDeductSilverDirect: Simulation successful (remove + refund verified).");
                        return true;
                    }

                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Simulation final verification failed (before={before}, final={final}).");
                    return false;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Simulation remove failed. Exception: {ex.Message}");
                    // If RemoveItem threw and removed == false, nothing to refund.
                    if (removed)
                    {
                        // Attempt to refund in case RemoveItem succeeded but something threw later.
                        try
                        {
                            TryRefundPlayerCurrency(amount);
                        }
                        catch (Exception refundEx)
                        {
                            TravelButtonPlugin.LogError($"AttemptDeductSilverDirect: Failed to refund after partial simulation remove: {refundEx}");
                        }
                    }
                    return false;
                }
            }
            else
            {
                TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Attempting to deduct {amount} silver.");
                try
                {
                    // The TryDeductPlayerCurrency now verifies actual effect when possible.
                    if (!TryDeductPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogError("AttemptDeductSilverDirect: TryDeductPlayerCurrency reported failure; deduction did not occur.");
                        return false;
                    }
                    TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Successfully deducted {amount} silver.");
                    return true;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Failed to deduct silver. Exception: {ex.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: An exception occurred: {ex.Message}");
            return false;
        }
    }
}