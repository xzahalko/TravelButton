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
                string[] propNames = new string[] { "Silver", "Money", "Gold", "Coins", "Currency", "CurrentMoney", "SilverAmount", "MoneyAmount" };
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
                        if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
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
                        if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency")) && pi.CanRead)
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

            // 1) Prefer authoritative CentralGatherable singleton if it exists (common for silver)
            try
            {
                var centralType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(tt => tt.Name.Equals("CentralGatherable", StringComparison.InvariantCultureIgnoreCase)
                                           || (tt.Name.ToLowerInvariant().Contains("central") && tt.Name.ToLowerInvariant().Contains(currencyKeyword)));

                if (centralType != null)
                {
                    object centralInstance = null;
                    try
                    {
                        var instProp = centralType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        if (instProp != null) centralInstance = instProp.GetValue(null);
                        else
                        {
                            var instField = centralType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                            if (instField != null) centralInstance = instField.GetValue(null);
                        }
                    }
                    catch { /* ignore */ }

                    if (centralInstance != null)
                    {
                        // Try direct known subtractive method names on central instance
                        string[] preferMethods = new string[] { "RemoveSilver", "SpendSilver", "RemoveMoney", "SpendMoney", "RemoveCurrency", "TakeMoney", "DebitMoney", "DeductSilver" };
                        foreach (var mn in preferMethods)
                        {
                            try
                            {
                                var mi = centralType.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase,
                                                               null, new Type[] { typeof(int) }, null)
                                         ?? centralType.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase,
                                                                   null, new Type[] { typeof(long) }, null);
                                if (mi != null)
                                {
                                    var paramType = mi.GetParameters()[0].ParameterType;
                                    var arg = paramType == typeof(long) ? (object)(long)amount : (object)amount;
                                    object res = null;
                                    try
                                    {
                                        res = mi.Invoke(centralInstance, new object[] { arg });
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {centralType.FullName}.{mn}({amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {centralType.FullName}.{mn} threw: {tie.InnerException?.Message ?? tie.Message}");
                                        continue;
                                    }

                                    // Interpret result: if method returns bool use it, otherwise treat as success (no exception)
                                    if (mi.ReturnType == typeof(bool))
                                    {
                                        bool ok = res != null && (bool)res;
                                        if (ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {centralType.FullName}.{mn} returned false.");
                                            return false;
                                        }
                                    }
                                    else
                                    {
                                        // method returned something else (int remaining or void) — consider success
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: reflection call {centralType.FullName}.{mn} failed: {ex.Message}");
                            }
                        }

                        // If no preferred method found, try methods that contain the currencyKeyword and look subtractive
                        foreach (var mi in centralType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            try
                            {
                                var mname = mi.Name.ToLowerInvariant();
                                if (!mname.Contains(currencyKeyword)) continue;
                                var pars = mi.GetParameters();
                                if (pars.Length != 1) continue;
                                var pType = pars[0].ParameterType;
                                if (pType != typeof(int) && pType != typeof(long)) continue;

                                if (mname.Contains("remove") || mname.Contains("spend") || mname.Contains("take") || mname.Contains("use") ||
                                    mname.Contains("deduct") || mname.Contains("debit") || mname.Contains("decrease") || mname.Contains("consume"))
                                {
                                    var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                    object res = null;
                                    try
                                    {
                                        res = mi.Invoke(centralInstance, new object[] { arg });
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {centralType.FullName}.{mi.Name}({amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {centralType.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                        continue;
                                    }

                                    if (mi.ReturnType == typeof(bool))
                                    {
                                        bool ok = res != null && (bool)res;
                                        if (ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {centralType.FullName}.{mi.Name} returned false.");
                                            return false;
                                        }
                                    }
                                    else
                                    {
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: central method enumeration failure: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: central handler attempt failed: {ex.Message}");
            }

            // 2) Try to address the player and player's inventory directly (preferred fallback)
            try
            {
                var player = CharacterManager.Instance?.GetFirstLocalCharacter();
                if (player != null)
                {
                    var inventory = player.Inventory;
                    if (inventory != null)
                    {
                        // If currencyKeyword == "silver" try known silver item removal first (inventory.RemoveItem(itemId, amount))
                        if (currencyKeyword == "silver")
                        {
                            const int silverItemID = 6100110;
                            try
                            {
                                var invType = inventory.GetType();
                                // look for RemoveItem(int id, int qty) or similar
                                var removeMi = invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                                 null, new Type[] { typeof(int), typeof(int) }, null)
                                               ?? invType.GetMethod("RemoveItem", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                                                                     null, new Type[] { typeof(int), typeof(long) }, null);

                                if (removeMi != null)
                                {
                                    var paramType = removeMi.GetParameters()[1].ParameterType;
                                    var argQty = paramType == typeof(long) ? (object)(long)amount : (object)amount;
                                    object res = null;
                                    try
                                    {
                                        res = removeMi.Invoke(inventory, new object[] { silverItemID, argQty });
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called Inventory.RemoveItem({silverItemID},{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: Inventory.RemoveItem threw: {tie.InnerException?.Message ?? tie.Message}");
                                        // Fall through to other inventory handling
                                    }

                                    // If method returns bool treat it as success flag
                                    if (removeMi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool b && b)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: Inventory.RemoveItem returned false (not enough items?).");
                                            return false;
                                        }
                                    }
                                    else
                                    {
                                        // assume success if no exception
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }
                                else
                                {
                                    // No RemoveItem found - try RemoveMoney/RemoveSilver on inventory itself
                                    var invMi = invType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                                      .FirstOrDefault(m => m.Name.ToLowerInvariant().Contains("remove") && m.GetParameters().Length == 1
                                                                           && (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long))
                                                                           && m.Name.ToLowerInvariant().Contains(currencyKeyword));
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
                                            if (res is bool b && b)
                                            {
                                                TryRefreshCurrencyDisplay(currencyKeyword);
                                                return true;
                                            }
                                            else
                                            {
                                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {invType.FullName}.{invMi.Name} returned false.");
                                                return false;
                                            }
                                        }
                                        else
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory silver-path failed: {ex}");
                            }
                        }

                        // Generic fallback: try to find a numeric field/property on the player's inventory that contains the currencyKeyword
                        try
                        {
                            var invType = inventory.GetType();

                            // Fields
                            foreach (var fi in invType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var name = fi.Name.ToLowerInvariant();
                                    if (!name.Contains(currencyKeyword)) continue;

                                    if (fi.FieldType == typeof(int))
                                    {
                                        int cur = (int)fi.GetValue(inventory);
                                        if (cur >= amount)
                                        {
                                            fi.SetValue(inventory, cur - amount);
                                            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {invType.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {invType.FullName}.{fi.Name} ({cur} < {amount}).");
                                            return false;
                                        }
                                    }
                                    if (fi.FieldType == typeof(long))
                                    {
                                        long cur = (long)fi.GetValue(inventory);
                                        if (cur >= amount)
                                        {
                                            fi.SetValue(inventory, cur - amount);
                                            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {invType.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {invType.FullName}.{fi.Name} ({cur} < {amount}).");
                                            return false;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory field access {invType.FullName}.{fi.Name} threw: {ex}");
                                }
                            }

                            // Properties
                            foreach (var pi in invType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var name = pi.Name.ToLowerInvariant();
                                    if (!name.Contains(currencyKeyword)) continue;
                                    if (!pi.CanRead || !pi.CanWrite) continue;

                                    if (pi.PropertyType == typeof(int))
                                    {
                                        int cur = (int)pi.GetValue(inventory);
                                        if (cur >= amount)
                                        {
                                            pi.SetValue(inventory, cur - amount);
                                            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {invType.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {invType.FullName}.{pi.Name} ({cur} < {amount}).");
                                            return false;
                                        }
                                    }
                                    if (pi.PropertyType == typeof(long))
                                    {
                                        long cur = (long)pi.GetValue(inventory);
                                        if (cur >= amount)
                                        {
                                            pi.SetValue(inventory, cur - amount);
                                            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {invType.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        else
                                        {
                                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {invType.FullName}.{pi.Name} ({cur} < {amount}).");
                                            return false;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: inventory property access {invType.FullName}.{pi.Name} threw: {ex}");
                                }
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

            // 3) Last resort: attempt broader scan (kept minimal to avoid hitting non-authoritative components)
            try
            {
                var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in allMonoBehaviours)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();

                    // Try a single subtractive method that contains the currencyKeyword
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var mname = mi.Name.ToLowerInvariant();
                            if (!mname.Contains(currencyKeyword)) continue;
                            if (!(mname.Contains("remove") || mname.Contains("spend") || mname.Contains("take") || mname.Contains("use") || mname.Contains("deduct") || mname.Contains("debit") || mname.Contains("consume"))) continue;

                            var pars = mi.GetParameters();
                            if (pars.Length != 1) continue;
                            var pType = pars[0].ParameterType;
                            if (pType != typeof(int) && pType != typeof(long)) continue;

                            var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                            object res = null;
                            try
                            {
                                res = mi.Invoke(mb, new object[] { arg });
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {t.FullName}.{mi.Name}({amount}) -> {res}");
                            }
                            catch (TargetInvocationException tie)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                continue;
                            }

                            if (mi.ReturnType == typeof(bool))
                            {
                                if (res is bool b && b)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                else
                                {
                                    TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mi.Name} returned false.");
                                    return false;
                                }
                            }
                            else
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch { /* ignore per-method issues */ }
                    }
                }
            }
            catch { /* ignore broad fallback failures */ }

            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: could not find an authoritative inventory/money field, property, or method containing '{currencyKeyword}'. Travel aborted.");
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
    /// Mirrors the deduction heuristics with additive actions.
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

            // 1) Prefer authoritative CentralGatherable-like singleton if available
            try
            {
                var centralType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(tt => tt.Name.Equals("CentralGatherable", StringComparison.InvariantCultureIgnoreCase)
                                           || (tt.Name.ToLowerInvariant().Contains("central") && tt.Name.ToLowerInvariant().Contains(currencyKeyword)));

                if (centralType != null)
                {
                    object centralInstance = null;
                    try
                    {
                        var instProp = centralType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        if (instProp != null) centralInstance = instProp.GetValue(null);
                        else
                        {
                            var instField = centralType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                            if (instField != null) centralInstance = instField.GetValue(null);
                        }
                    }
                    catch { /* ignore */ }

                    if (centralInstance != null)
                    {
                        // try preferred additive method names first
                        string[] preferMethods = new string[] { "AddSilver", "GiveSilver", "GrantSilver", "AddMoney", "GrantMoney", "GiveMoney", "AddCoins", "CreditMoney", "IncreaseSilver" };
                        foreach (var mn in preferMethods)
                        {
                            try
                            {
                                var mi = centralType.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase,
                                                               null, new Type[] { typeof(int) }, null)
                                         ?? centralType.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase,
                                                                   null, new Type[] { typeof(long) }, null);
                                if (mi == null) continue;

                                var paramType = mi.GetParameters()[0].ParameterType;
                                var arg = paramType == typeof(long) ? (object)(long)amount : (object)amount;

                                object res = null;
                                try
                                {
                                    res = mi.Invoke(centralInstance, new object[] { arg });
                                    TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {centralType.FullName}.{mn}({amount}) -> {res}");
                                }
                                catch (TargetInvocationException tie)
                                {
                                    TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {centralType.FullName}.{mn} threw: {tie.InnerException?.Message ?? tie.Message}");
                                    continue;
                                }

                                // Interpret return value (if bool return -> success flag, otherwise treat as success)
                                if (mi.ReturnType == typeof(bool))
                                {
                                    if (res is bool ok && ok)
                                    {
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                    TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {centralType.FullName}.{mn} returned false.");
                                    return false;
                                }
                                else
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: reflection call {centralType.FullName}.{mn} failed: {ex.Message}");
                            }
                        }

                        // fallback: try any method on central that contains the currency keyword and looks additive
                        foreach (var mi in centralType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            try
                            {
                                var mname = mi.Name.ToLowerInvariant();
                                if (!mname.Contains(currencyKeyword)) continue;
                                var pars = mi.GetParameters();
                                if (pars.Length != 1) continue;
                                var pType = pars[0].ParameterType;
                                if (pType != typeof(int) && pType != typeof(long)) continue;

                                if (mname.Contains("add") || mname.Contains("grant") || mname.Contains("give") || mname.Contains("increase") || mname.Contains("credit") || mname.Contains("award"))
                                {
                                    var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                    object res = null;
                                    try
                                    {
                                        res = mi.Invoke(centralInstance, new object[] { arg });
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {centralType.FullName}.{mi.Name}({amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {centralType.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                        continue;
                                    }

                                    if (mi.ReturnType == typeof(bool))
                                    {
                                        if (res is bool ok && ok)
                                        {
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {centralType.FullName}.{mi.Name} returned false.");
                                        return false;
                                    }
                                    else
                                    {
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
                                    }
                                }

                                // setter-like methods: compute current + amount using getter/property then call setter
                                if (mname.StartsWith("set") || mname.Contains("set") || mname.Contains("count"))
                                {
                                    Func<long?> tryGetCurrent = () =>
                                    {
                                        // common getter candidates
                                        string[] getterCandidates = new string[]
                                        {
                                        "Get" + mi.Name.Substring(3),
                                        "Get" + currencyKeyword,
                                        currencyKeyword + "count",
                                        "Get" + currencyKeyword + "count",
                                        "Get" + char.ToUpper(currencyKeyword[0]) + currencyKeyword.Substring(1)
                                        };

                                        foreach (var gc in getterCandidates)
                                        {
                                            try
                                            {
                                                var gm = centralType.GetMethod(gc, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                                                if (gm != null && (gm.ReturnType == typeof(int) || gm.ReturnType == typeof(long)))
                                                {
                                                    var v = gm.Invoke(centralInstance, null);
                                                    return v == null ? (long?)null : Convert.ToInt64(v);
                                                }
                                            }
                                            catch { }
                                        }

                                        foreach (var pi in centralType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                        {
                                            try
                                            {
                                                var pname = pi.Name.ToLowerInvariant();
                                                if (!pname.Contains(currencyKeyword)) continue;
                                                if (!pi.CanRead) continue;
                                                if (pi.PropertyType == typeof(int) || pi.PropertyType == typeof(long))
                                                {
                                                    var v = pi.GetValue(centralInstance);
                                                    return v == null ? (long?)null : Convert.ToInt64(v);
                                                }
                                            }
                                            catch { }
                                        }

                                        return (long?)null;
                                    };

                                    var cur = tryGetCurrent();
                                    if (cur.HasValue)
                                    {
                                        long newVal = cur.Value + amount;
                                        var paramType = mi.GetParameters()[0].ParameterType;
                                        var arg = paramType == typeof(long) ? (object)newVal : (object)(int)newVal;

                                        try
                                        {
                                            var res = mi.Invoke(centralInstance, new object[] { arg });
                                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {centralType.FullName}.{mi.Name}({arg}) -> setter-based refund (was {cur.Value}, now {newVal})");
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
                                        }
                                        catch (TargetInvocationException tie)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {centralType.FullName}.{mi.Name} threw when setting: {tie.InnerException?.Message ?? tie.Message}");
                                        }
                                        catch (Exception ex)
                                        {
                                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: calling setter {centralType.FullName}.{mi.Name} failed: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: skipped setter-like method {centralType.FullName}.{mi.Name} because no getter/property found.");
                                    }
                                }
                            }
                            catch { /* per-method ignore */ }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: central handler attempt failed: {ex.Message}");
            }

            // 2) Try direct player / inventory manipulation (preferred fallback)
            try
            {
                var player = CharacterManager.Instance?.GetFirstLocalCharacter();
                if (player != null)
                {
                    var inventory = player.Inventory;
                    if (inventory != null)
                    {
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
                                                                  null, new Type[] { typeof(int), typeof(long) }, null);
                                if (addMi != null)
                                {
                                    var paramType = addMi.GetParameters()[1].ParameterType;
                                    var argQty = paramType == typeof(long) ? (object)(long)amount : (object)amount;

                                    object res = null;
                                    try
                                    {
                                        res = addMi.Invoke(inventory, new object[] { silverItemID, argQty });
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called Inventory.AddItem({silverItemID},{amount}) -> {res}");
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: Inventory.AddItem threw: {tie.InnerException?.Message ?? tie.Message}");
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
                                        TryRefreshCurrencyDisplay(currencyKeyword);
                                        return true;
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
                                            TryRefreshCurrencyDisplay(currencyKeyword);
                                            return true;
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
                                    if (!name.Contains(currencyKeyword)) continue;

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
                                    if (!name.Contains(currencyKeyword)) continue;
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

            // 3) Last resort: light-weight broader scan for additive methods (minimal to avoid non-authoritative hits)
            try
            {
                var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in allMonoBehaviours)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();

                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var mname = mi.Name.ToLowerInvariant();
                            if (!mname.Contains(currencyKeyword)) continue;
                            if (!(mname.Contains("add") || mname.Contains("grant") || mname.Contains("give") || mname.Contains("increase") || mname.Contains("credit") || mname.Contains("award"))) continue;

                            var pars = mi.GetParameters();
                            if (pars.Length != 1) continue;
                            var pType = pars[0].ParameterType;
                            if (pType != typeof(int) && pType != typeof(long)) continue;
                            var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;

                            object res = null;
                            try
                            {
                                res = mi.Invoke(mb, new object[] { arg });
                                TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {t.FullName}.{mi.Name}({amount}) -> {res}");
                            }
                            catch (TargetInvocationException tie)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {t.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                                continue;
                            }

                            if (mi.ReturnType == typeof(bool))
                            {
                                if (res is bool b && b)
                                {
                                    TryRefreshCurrencyDisplay(currencyKeyword);
                                    return true;
                                }
                                else
                                {
                                    TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {t.FullName}.{mi.Name} returned false.");
                                    return false;
                                }
                            }
                            else
                            {
                                TryRefreshCurrencyDisplay(currencyKeyword);
                                return true;
                            }
                        }
                        catch { /* ignore per-method issues */ }
                    }
                }
            }
            catch { /* ignore broad fallback failures */ }

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

//            const int silverItemID = 9000010;

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
                try
                {
                    // Attempt to remove; if RemoveItem throws on insufficient, this will go to catch.
//                    inventory.RemoveItem(silverItemID, amount);
                    if (!TryDeductPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Simulation refund failed after RemoveItem. THIS IS SERIOUS.");
                        // At this point inventory has been mutated and refund failed — decide how to handle.
                        return false;
                    }

                    removed = true;
                    TravelButtonPlugin.LogInfo("AttemptDeductSilverDirect: Simulation remove succeeded - will attempt refund.");
                    // TryRefundPlayerCurrency should add the silver back. Ensure it returns success/false.
                    if (!TryRefundPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Simulation refund failed after RemoveItem. THIS IS SERIOUS.");
                        // At this point inventory has been mutated and refund failed — decide how to handle.
                        return false;
                    }
                    TravelButtonPlugin.LogInfo("AttemptDeductSilverDirect: Simulation successful (remove + refund).");
                    return true;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Simulation remove failed (player likely lacks silver). Exception: {ex.Message}");
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
//                    inventory.RemoveItem(silverItemID, amount);
                    if (!TryDeductPlayerCurrency(amount))
                    {
                        TravelButtonPlugin.LogError("AttemptDeductSilverDirect: Simulation refund failed after RemoveItem. THIS IS SERIOUS.");
                        // At this point inventory has been mutated and refund failed — decide how to handle.
                        return false;
                    }
                    TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Successfully deducted {amount} silver.");

//                    TryRefreshCurrencyDisplay("silver");

                    return true;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Failed to deduct silver. Exception: {ex.Message}");
                    return false;
                }
            }

//            TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Attempting to deduct {amount} silver.");
//            inventory.RemoveItem(silverItemID, amount);
            TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Successfully deducted {amount} silver.");
            return true;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: An exception occurred: {ex.Message}");
            return false;
        }
    }
}