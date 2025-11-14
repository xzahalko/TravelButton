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

            var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMonoBehaviours)
            {
                if (mb == null) continue;
                var t = mb.GetType();

                // 1) Try canonical subtractive methods first (preferred)
                string[] subtractMethodNames = new string[]
                {
                "RemoveMoney", "SpendMoney", "RemoveSilver", "SpendSilver", "RemoveCurrency",
                "TakeMoney", "UseMoney", "DebitMoney", "DecreaseSilver", "DeductSilver",
                "SubtractMoney", "ConsumeSilver"
                };
                foreach (var mn in subtractMethodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(int) }, null)
                              ?? t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(long) }, null);
                        if (mi != null)
                        {
                            try
                            {
                                var arg = mi.GetParameters()[0].ParameterType == typeof(long) ? (object)(long)amount : (object)amount;
                                var res = mi.Invoke(mb, new object[] { arg });
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {t.FullName}.{mn}({amount}) -> {res}");
                                return true;
                            }
                            catch (TargetInvocationException tie)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mn} threw: {tie.InnerException?.Message ?? tie.Message}");
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: calling {t.FullName}.{mn} failed: {ex.Message}");
                            }
                        }
                    }
                    catch { /* ignore reflection lookup failures */ }
                }

                // 2) Try methods that contain the currencyKeyword.
                //    Prefer methods that look subtractive (remove/spend/take/use/deduct/debit/decrease/consume).
                try
                {
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        string mname = mi.Name.ToLowerInvariant();
                        if (!mname.Contains(currencyKeyword)) continue;

                        var pars = mi.GetParameters();
                        if (pars.Length != 1) continue;

                        var pType = pars[0].ParameterType;
                        bool paramIsIntLike = pType == typeof(int) || pType == typeof(long);

                        if (!paramIsIntLike) continue;

                        try
                        {
                            // If method name suggests subtractive behaviour, call with amount.
                            if (mname.Contains("remove") || mname.Contains("spend") || mname.Contains("take") ||
                                mname.Contains("use") || mname.Contains("deduct") || mname.Contains("debit") ||
                                mname.Contains("consume") || mname.Contains("subtract") || mname.Contains("decrease"))
                            {
                                var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                var res = mi.Invoke(mb, new object[] { arg });
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {t.FullName}.{mi.Name}({amount}) -> {res}");
                                return true;
                            }

                            // If method looks like a setter (set*, *set*, *count*), treat as setter:
                            // find a getter/property to read current value and call setter with current-amount (if enough).
                            if (mname.StartsWith("set") || mname.Contains("set") || mname.Contains("count"))
                            {
                                Func<long?> tryGetCurrent = () =>
                                {
                                    // Try common getter names
                                    string[] getterCandidates = new string[]
                                    {
                                    "Get" + mi.Name.Substring(3), // SetSilverCount -> GetSilverCount
                                    "Get" + currencyKeyword,
                                    currencyKeyword + "count",
                                    "Get" + currencyKeyword + "count",
                                    "Get" + char.ToUpper(currencyKeyword[0]) + currencyKeyword.Substring(1)
                                    };

                                    foreach (var gc in getterCandidates)
                                    {
                                        try
                                        {
                                            var gm = t.GetMethod(gc, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                                            if (gm != null && (gm.ReturnType == typeof(int) || gm.ReturnType == typeof(long)))
                                            {
                                                var v = gm.Invoke(mb, null);
                                                return v == null ? (long?)null : Convert.ToInt64(v);
                                            }
                                        }
                                        catch { }
                                    }

                                    // Try properties that contain currencyKeyword and are readable
                                    foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                    {
                                        try
                                        {
                                            var pname = pi.Name.ToLowerInvariant();
                                            if (!pname.Contains(currencyKeyword)) continue;
                                            if (!pi.CanRead) continue;
                                            if (pi.PropertyType == typeof(int) || pi.PropertyType == typeof(long))
                                            {
                                                var v = pi.GetValue(mb);
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
                                    if (cur.Value < amount)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds according to getter/property for {t.FullName}.{mi.Name} ({cur.Value} < {amount}).");
                                        return false;
                                    }

                                    long newVal = cur.Value - amount;
                                    var arg = pType == typeof(long) ? (object)newVal : (object)(int)newVal;
                                    try
                                    {
                                        mi.Invoke(mb, new object[] { arg });
                                        TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {t.FullName}.{mi.Name}({arg}) -> setter-based deduct (was {cur.Value}, now {newVal})");
                                        return true;
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mi.Name} threw when setting: {tie.InnerException?.Message ?? tie.Message}");
                                    }
                                    catch (Exception ex)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: calling setter {t.FullName}.{mi.Name} failed: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    // No getter found; skip to avoid accidentally setting absolute small value.
                                    TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: skipped setter-like method {t.FullName}.{mi.Name} because no getter/property found to compute new value.");
                                    continue;
                                }
                            }

                            // Ambiguous methods are skipped to avoid incorrect semantics.
                        }
                        catch (TargetInvocationException tie)
                        {
                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: invoking {t.FullName}.{mi.Name} failed: {ex.Message}");
                        }
                    }
                }
                catch { /* ignore method enumeration issues */ }

                // 3) Try fields (first matching field only): subtract amount if possible
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (!name.Contains(currencyKeyword)) continue;

                        if (fi.FieldType == typeof(int))
                        {
                            int cur = (int)fi.GetValue(mb);
                            if (cur >= amount)
                            {
                                fi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {t.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (fi.FieldType == typeof(long))
                        {
                            long cur = (long)fi.GetValue(mb);
                            if (cur >= amount)
                            {
                                fi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {t.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }

                // 4) Try properties (first matching property only): subtract amount if possible
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = pi.Name.ToLowerInvariant();
                        if (!name.Contains(currencyKeyword)) continue;
                        if (!pi.CanRead || !pi.CanWrite) continue;

                        if (pi.PropertyType == typeof(int))
                        {
                            int cur = (int)pi.GetValue(mb);
                            if (cur >= amount)
                            {
                                pi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {t.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (pi.PropertyType == typeof(long))
                        {
                            long cur = (long)pi.GetValue(mb);
                            if (cur >= amount)
                            {
                                pi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: deducted {amount} from {t.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }

            TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: could not find an inventory/money field, property, or method containing '{currencyKeyword}'. Travel aborted.");
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

            var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMonoBehaviours)
            {
                if (mb == null) continue;
                var t = mb.GetType();

                // 1) Try canonical additive methods first (preferred)
                string[] addMethodNames = new string[] { "AddMoney", "GrantMoney", "GiveMoney", "AddSilver", "GiveSilver", "GrantSilver", "AddCoins", "AddCurrency", "CreditMoney", "IncreaseSilver" };
                foreach (var mn in addMethodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(int) }, null)
                              ?? t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(long) }, null);
                        if (mi != null)
                        {
                            try
                            {
                                mi.Invoke(mb, new object[] { amount });
                                TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {t.FullName}.{mn}({amount})");
                                return true;
                            }
                            catch (TargetInvocationException tie)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {t.FullName}.{mn} threw: {tie.InnerException?.Message ?? tie.Message}");
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: calling {t.FullName}.{mn} failed: {ex.Message}");
                            }
                        }
                    }
                    catch { /* ignore reflection lookup failures */ }
                }

                // 2) Try methods that contain the currencyKeyword.
                //    Prefer methods that look additive (add/grant/give/increase/inc/credit/award).
                try
                {
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        string mname = mi.Name.ToLowerInvariant();
                        if (!mname.Contains(currencyKeyword)) continue;

                        var pars = mi.GetParameters();
                        if (pars.Length != 1) continue;

                        var pType = pars[0].ParameterType;
                        bool paramIsIntLike = pType == typeof(int) || pType == typeof(long);

                        if (!paramIsIntLike) continue;

                        try
                        {
                            // If method name suggests additive behaviour, call with amount.
                            if (mname.Contains("add") || mname.Contains("grant") || mname.Contains("give") ||
                                mname.Contains("increase") || mname.Contains("inc") || mname.Contains("award") || mname.Contains("credit"))
                            {
                                var arg = pType == typeof(long) ? (object)(long)amount : (object)amount;
                                mi.Invoke(mb, new object[] { arg });
                                TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {t.FullName}.{mi.Name}({amount}) -> additive");
                                return true;
                            }

                            // If method looks like a setter (set*, *set*, *count*), treat as setter:
                            // find a getter/property to read current value and call setter with current+amount
                            if (mname.StartsWith("set") || mname.Contains("set") || mname.Contains("count"))
                            {
                                // Try to find a getter method (GetX) or a readable property for the same currency.
                                Func<long?> tryGetCurrent = () =>
                                {
                                    // Try common getter names
                                    string[] getterCandidates = new string[]
                                    {
                                    "Get" + mi.Name.Substring(3), // SetSilverCount -> GetSilverCount
                                    "Get" + currencyKeyword,
                                    currencyKeyword + "count",
                                    "Get" + currencyKeyword + "count",
                                    "Get" + char.ToUpper(currencyKeyword[0]) + currencyKeyword.Substring(1)
                                    };

                                    foreach (var gc in getterCandidates)
                                    {
                                        try
                                        {
                                            var gm = t.GetMethod(gc, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                                            if (gm != null && (gm.ReturnType == typeof(int) || gm.ReturnType == typeof(long)))
                                            {
                                                var v = gm.Invoke(mb, null);
                                                return v == null ? (long?)null : Convert.ToInt64(v);
                                            }
                                        }
                                        catch { }
                                    }

                                    // Try properties that contain currencyKeyword and are readable
                                    foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                    {
                                        try
                                        {
                                            var pname = pi.Name.ToLowerInvariant();
                                            if (!pname.Contains(currencyKeyword)) continue;
                                            if (!pi.CanRead) continue;
                                            if (pi.PropertyType == typeof(int) || pi.PropertyType == typeof(long))
                                            {
                                                var v = pi.GetValue(mb);
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
                                    var arg = pType == typeof(long) ? (object)newVal : (object)(int)newVal;
                                    try
                                    {
                                        mi.Invoke(mb, new object[] { arg });
                                        TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {t.FullName}.{mi.Name}({arg}) -> setter-based refund (was {cur.Value}, now {newVal})");
                                        return true;
                                    }
                                    catch (TargetInvocationException tie)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {t.FullName}.{mi.Name} threw when setting: {tie.InnerException?.Message ?? tie.Message}");
                                    }
                                    catch (Exception ex)
                                    {
                                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: calling setter {t.FullName}.{mi.Name} failed: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    // No getter found; skip this setter to avoid accidental setting to 'amount' (absolute).
                                    TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: skipped setter-like method {t.FullName}.{mi.Name} because no getter/property found to compute new value.");
                                    continue;
                                }
                            }

                            // For ambiguous methods that don't match the above, skip to avoid wrong semantics.
                        }
                        catch (TargetInvocationException tie)
                        {
                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: {t.FullName}.{mi.Name} threw: {tie.InnerException?.Message ?? tie.Message}");
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: invoking {t.FullName}.{mi.Name} failed: {ex.Message}");
                        }
                    }
                }
                catch { /* ignore method enumeration issues */ }

                // 3) Try fields (first matching field only): add amount
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (!name.Contains(currencyKeyword)) continue;

                        if (fi.FieldType == typeof(int))
                        {
                            int cur = (int)fi.GetValue(mb);
                            fi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {t.FullName}.{fi.Name} (int). New value {cur + amount}.");
                            return true;
                        }
                        else if (fi.FieldType == typeof(long))
                        {
                            long cur = (long)fi.GetValue(mb);
                            fi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {t.FullName}.{fi.Name} (long). New value {cur + amount}.");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }

                // 4) Try properties (first matching property only): add amount
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = pi.Name.ToLowerInvariant();
                        if (!name.Contains(currencyKeyword)) continue;
                        if (!pi.CanRead || !pi.CanWrite) continue;

                        if (pi.PropertyType == typeof(int))
                        {
                            int cur = (int)pi.GetValue(mb);
                            pi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {t.FullName}.{pi.Name} (int). New value {cur + amount}.");
                            return true;
                        }
                        else if (pi.PropertyType == typeof(long))
                        {
                            long cur = (long)pi.GetValue(mb);
                            pi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: added {amount} to {t.FullName}.{pi.Name} (long). New value {cur + amount}.");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
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

            TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Attempting to deduct {amount} silver.");
            inventory.RemoveItem(silverItemID, amount);
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