using System;
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
    public static bool TryDeductPlayerCurrency(int amount)
    {
        try
        {
            TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: trying to deduct {amount} silver.");

            var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMonoBehaviours)
            {
                if (mb == null) continue;
                var t = mb.GetType();

                // Try common methods first
                string[] methodNames = new string[] { "RemoveMoney", "SpendMoney", "RemoveSilver", "SpendSilver", "RemoveCurrency", "TakeMoney", "UseMoney" };
                foreach (var mn in methodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(int) }, null);
                        if (mi != null)
                        {
                            try
                            {
                                var res = mi.Invoke(mb, new object[] { amount });
                                TravelButtonPlugin.LogInfo($"TryDeductPlayerCurrency: called {t.FullName}.{mn}({amount}) -> {res}");
                                return true;
                            }
                            catch (TargetInvocationException tie)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: {t.FullName}.{mn} threw: {tie.InnerException?.Message ?? tie.Message}");
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: invoking {t.FullName}.{mn} failed: {ex.Message}");
                            }
                        }
                    }
                    catch { /* ignore reflect lookup problems */ }
                }

                // Try fields
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
                        {
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
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }

                // Try properties
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = pi.Name.ToLowerInvariant();
                        if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency")) && pi.CanRead && pi.CanWrite)
                        {
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
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryDeductPlayerCurrency: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }

            TravelButtonPlugin.LogWarning("TryDeductPlayerCurrency: could not find an inventory/money field or method automatically. Travel aborted.");
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
    public static bool TryRefundPlayerCurrency(int amount)
    {
        try
        {
            TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: trying to refund {amount} silver.");

            var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMonoBehaviours)
            {
                if (mb == null) continue;
                var t = mb.GetType();

                // Try methods that add money
                string[] addMethodNames = new string[] { "AddMoney", "GrantMoney", "GiveMoney", "AddSilver", "GiveSilver", "GrantSilver", "AddCoins" };
                foreach (var mn in addMethodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, new Type[] { typeof(int) }, null);
                        if (mi != null)
                        {
                            try
                            {
                                mi.Invoke(mb, new object[] { amount });
                                TravelButtonPlugin.LogInfo($"TryRefundPlayerCurrency: called {t.FullName}.{mn}({amount})");
                                return true;
                            }
                            catch (Exception ex)
                            {
                                TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: calling {t.FullName}.{mn} threw: {ex}");
                            }
                        }
                    }
                    catch { /* ignore lookup failures */ }
                }

                // Try fields/properties to increment
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coins") || name.Contains("currency"))
                        {
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
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }

                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var name = pi.Name.ToLowerInvariant();
                        if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coins") || name.Contains("currency")) && pi.CanRead && pi.CanWrite)
                        {
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
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"TryRefundPlayerCurrency: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }

            TravelButtonPlugin.LogWarning("TryRefundPlayerCurrency: could not find a place to refund the currency automatically.");
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
            const int silverItemID = 6100110;
            // First, check if the player has enough silver.
            long playerSilver = DetectPlayerCurrencyOrMinusOne();
            if (playerSilver == -1)
            {
                TravelButtonPlugin.LogWarning("AttemptDeductSilverDirect: Could not determine player's silver amount.");
                return false;
            }
            if (playerSilver < amount)
            {
                TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Player does not have enough silver ({playerSilver} < {amount}).");
                return false;
            }
            if (justSimulate)
            {
                TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Simulation successful. Player has enough silver ({playerSilver} >= {amount}).");
                return true;
                TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Attempting to deduct {amount} silver.");
                try
                {
                    TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Simulating deduction of {amount} silver.");
                    try
                    {
                        // Try to remove the silver.
                        inventory.RemoveItem(silverItemID, amount);
                        // If successful, immediately add it back.
                        inventory.AddItem(silverItemID, amount);
                        TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Simulation successful. Player has enough silver.");
                        return true;
                    }
                    catch (Exception)
                    {
                        // If RemoveItem fails, it means the player doesn't have enough.
                        TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Simulation failed. Player does not have enough silver.");
                        return false;
                    }
                }
                catch (Exception)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Simulation failed. Player does not have enough silver.");
                    return false;
                }
            }
            else // This is the actual deduction
            {
                TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Attempting to deduct {amount} silver.");
                try
                {
                    inventory.RemoveItem(silverItemID, amount);
                    TravelButtonPlugin.LogInfo($"AttemptDeductSilverDirect: Successfully deducted {amount} silver.");
                    return true;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptDeductSilverDirect: Failed to deduct silver. Player may not have enough. Exception: {ex.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError($"AttemptDeductSilverDirect: An exception occurred: {ex}");
            return false;
        }
    }
}