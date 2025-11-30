using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class InventoryHelpers
{
    // Pokusí se pøidat položku do hráèova inventáøe reflexivnì.
    // Vrací true pokud volání vypadalo úspìšnì, false jinak.
    public static bool TryAddItemToPlayerInventory(int itemId, int amount = 1)
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("TryAddItemToPlayerInventory: could not locate any inventory instance/type.");
                return false;
            }

            var invInstance = invPair.instance;
            var invType = invPair.type;

            // 1) Try direct/add-by-id methods (preferred)
            string[] exactNames = new[] { "AddItemByID", "AddItemById", "GiveItemByID", "GiveItemById", "AddItemByItemID", "AddItemByItemId" };
            foreach (var name in exactNames)
            {
                var mi = invType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi == null) continue;
                var pr = mi.GetParameters();
                try
                {
                    if (pr.Length == 2 && IsIntegerParam(pr[0]) && IsIntegerParam(pr[1]))
                    {
                        mi.Invoke(invInstance, new object[] { itemId, amount });
                        TBLog.Info($"TryAddItemToPlayerInventory: invoked {name}({itemId},{amount})");
                        TryInvokeRefresh(invInstance);
                        return true;
                    }
                    if (pr.Length == 1 && IsIntegerParam(pr[0]))
                    {
                        for (int i = 0; i < amount; ++i) mi.Invoke(invInstance, new object[] { itemId });
                        TBLog.Info($"TryAddItemToPlayerInventory: invoked {name}({itemId}) x{amount}");
                        TryInvokeRefresh(invInstance);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryAddItemToPlayerInventory: invocation {name} failed: {ex.Message}");
                }
            }

            // 2) Prefer clear add-item methods with (int id, int amount)
            string[] addMethodNames = new[] { "AddItem", "Add", "GiveItem", "AddToInventory", "InsertItem", "AddItemStack", "AddItems" };
            foreach (var mn in addMethodNames)
            {
                var methods = invType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     .Where(m => string.Equals(m.Name, mn, StringComparison.OrdinalIgnoreCase));
                foreach (var mi in methods)
                {
                    var pr = mi.GetParameters();
                    try
                    {
                        if (pr.Length == 2 && IsIntegerParam(pr[0]) && IsIntegerParam(pr[1]))
                        {
                            mi.Invoke(invInstance, new object[] { itemId, amount });
                            TBLog.Info($"TryAddItemToPlayerInventory: invoked {mn}({itemId},{amount})");
                            TryInvokeRefresh(invInstance);
                            return true;
                        }
                        if (pr.Length == 1 && IsIntegerParam(pr[0]))
                        {
                            for (int i = 0; i < amount; ++i) mi.Invoke(invInstance, new object[] { itemId });
                            TBLog.Info($"TryAddItemToPlayerInventory: invoked {mn}({itemId}) x{amount}");
                            TryInvokeRefresh(invInstance);
                            return true;
                        }
                        if (pr.Length == 2 && pr[1].ParameterType == typeof(int) && pr[0].ParameterType.Name.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var itemObj = TryCreateItemObjectById(pr[0].ParameterType, itemId);
                            if (itemObj != null)
                            {
                                mi.Invoke(invInstance, new object[] { itemObj, amount });
                                TBLog.Info($"TryAddItemToPlayerInventory: invoked {mn}(Item,int) with created object -> {itemId} x{amount}");
                                TryInvokeRefresh(invInstance);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"TryAddItemToPlayerInventory: invocation of {mi.Name} failed: {ex.Message}");
                    }
                }
            }

            // 3) Conservative tolerant invocation (RESTRICTED) - only for methods that strongly indicate item-handling,
            //    and explicitly SKIP methods that appear to be currency-related.
            string[] moneyIndicators = new[] { "Money", "Coin", "Silver", "Currency", "Coins", "Gold", "Cash", "Wallet" };
            string[] itemPositiveIndicators = new[] { "Item", "Stack", "Entry", "Inventory", "AddItemBy", "AddItem" };

            var allAddLike = invType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     .Where(m => m.Name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0)
                                     .ToArray();

            foreach (var mi in allAddLike)
            {
                try
                {
                    // Skip obvious currency methods
                    if (moneyIndicators.Any(s => mi.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    // Only allow tolerant invocation for methods that likely operate on items
                    bool hasItemIndicator = itemPositiveIndicators.Any(s => mi.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!hasItemIndicator)
                        continue;

                    var prms = mi.GetParameters();
                    if (prms.Length >= 2 && IsIntegerParam(prms[0]) && IsIntegerParam(prms.Last()))
                    {
                        var args = new object[prms.Length];
                        args[0] = itemId;
                        args[prms.Length - 1] = amount;
                        for (int i = 1; i < prms.Length - 1; ++i)
                            args[i] = GetDefaultForType(prms[i].ParameterType);

                        mi.Invoke(invInstance, args);
                        TBLog.Info($"TryAddItemToPlayerInventory: invoked tolerant {mi.Name} on {invType.FullName} (best-effort).");
                        TryInvokeRefresh(invInstance);
                        return true;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    TBLog.Warn($"TryAddItemToPlayerInventory: tolerant method {mi.Name} threw: {tie.InnerException?.Message}");
                }
                catch { /* continue */ }
            }

            // 4) Fallback: attempt to append into known collections (conservative)
            string[] containerNames = new[] { "m_items", "items", "Items", "inventory", "m_contents", "ContainedItems", "ItemStacks", "m_itemStacks" };
            foreach (var cname in containerNames)
            {
                try
                {
                    var f = invType.GetField(cname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        var listObj = f.GetValue(invInstance);
                        if (TryAppendToCollection(listObj, itemId, amount))
                        {
                            TBLog.Info($"TryAddItemToPlayerInventory: appended to field '{cname}' on {invType.FullName}.");
                            TryInvokeRefresh(invInstance);
                            return true;
                        }
                    }

                    var p = invType.GetProperty(cname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanRead)
                    {
                        var listObj = p.GetValue(invInstance, null);
                        if (TryAppendToCollection(listObj, itemId, amount))
                        {
                            TBLog.Info($"TryAddItemToPlayerInventory: appended to property '{cname}' on {invType.FullName}.");
                            TryInvokeRefresh(invInstance);
                            return true;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            TBLog.Warn($"TryAddItemToPlayerInventory: could not find suitable Add method or collection for inventory type {invType.FullName}");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryAddItemToPlayerInventory: unexpected exception: " + ex);
            return false;
        }
    }

    public static bool SafeAddSilverToPlayer(int amount)
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("SafeAddSilverToPlayer: inventory instance not found.");
                return false;
            }

            var inv = invPair.instance;
            var t = invPair.type;

            // Prefer AddMoney(int)
            var addMoneyMethod = t.GetMethod("AddMoney", new Type[] { typeof(int) })
                              ?? t.GetMethod("AddMoney", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null);
            if (addMoneyMethod != null)
            {
                addMoneyMethod.Invoke(inv, new object[] { amount });
                TBLog.Info($"SafeAddSilverToPlayer: invoked AddMoney({amount}).");
                TryInvokeRefresh(inv);
                return true;
            }

            // Fallback to property/field ContainedSilver
            var prop = t.GetProperty("ContainedSilver", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanRead && prop.CanWrite)
            {
                object curObj = prop.GetValue(inv, null);
                long cur = Convert.ToInt64(curObj);
                long nv = cur + amount;
                if (prop.PropertyType == typeof(int)) prop.SetValue(inv, (int)nv, null);
                else prop.SetValue(inv, nv, null);
                TBLog.Info($"SafeAddSilverToPlayer: updated ContainedSilver from {cur} to {nv}");
                TryInvokeRefresh(inv);
                return true;
            }

            var field = t.GetField("ContainedSilver", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && (field.FieldType == typeof(int) || field.FieldType == typeof(long)))
            {
                object curObj = field.GetValue(inv);
                long cur = Convert.ToInt64(curObj);
                long nv = cur + amount;
                if (field.FieldType == typeof(int)) field.SetValue(inv, (int)nv);
                else field.SetValue(inv, nv);
                TBLog.Info($"SafeAddSilverToPlayer: updated ContainedSilver field from {cur} to {nv}");
                TryInvokeRefresh(inv);
                return true;
            }

            TBLog.Warn("SafeAddSilverToPlayer: no AddMoney or ContainedSilver found.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeAddSilverToPlayer: unexpected: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Safely add item(s) by ID using CharacterInventory.ReceiveItemReward(itemId, qty, tryToEquip).
    /// </summary>
    public static bool SafeAddItemByIdToPlayer(int itemId, int amount = 1)
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("SafeAddItemByIdToPlayer: inventory instance not found.");
                return false;
            }

            var inv = invPair.instance;
            var t = invPair.type;

            // Prefer ReceiveItemReward(int _itemID, int _quantity, bool _tryToEquip)
            var recv = t.GetMethod("ReceiveItemReward", new Type[] { typeof(int), typeof(int), typeof(bool) })
                    ?? t.GetMethod("ReceiveItemReward", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int), typeof(int), typeof(bool) }, null);
            if (recv != null)
            {
                try
                {
                    recv.Invoke(inv, new object[] { itemId, amount, false });
                    TBLog.Info($"SafeAddItemByIdToPlayer: invoked ReceiveItemReward({itemId},{amount},false)");
                    TryInvokeRefresh(inv);
                    return true;
                }
                catch (TargetInvocationException tie)
                {
                    TBLog.Warn("SafeAddItemByIdToPlayer: ReceiveItemReward threw: " + tie.InnerException?.Message);
                }
            }

            // Alternative: GenerateItem(Item itemToGenerate, int quantity, bool tryToEquip) exists: try to create Item and call GenerateItem
            var gen = t.GetMethod("GenerateItem", new Type[] { ReflectionUtils.SafeGetType("Item, Assembly-CSharp") ?? typeof(object), typeof(int), typeof(bool) });
            if (gen != null)
            {
                // try to create Item instance via ItemDatabase or Activator fallback
                var itemType = gen.GetParameters()[0].ParameterType;
                var itemObj = TryCreateItemObjectById(itemType, itemId) ?? ActivatorCreateWithId(itemType, itemId);
                if (itemObj != null)
                {
                    gen.Invoke(inv, new object[] { itemObj, amount, false });
                    TBLog.Info($"SafeAddItemByIdToPlayer: invoked GenerateItem with created Item for id {itemId} x{amount}");
                    TryInvokeRefresh(inv);
                    return true;
                }
            }

            TBLog.Warn($"SafeAddItemByIdToPlayer: could not find ReceiveItemReward/GenerateItem for inventory type {t.FullName}.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeAddItemByIdToPlayer: unexpected: " + ex);
            return false;
        }
    }

    public static bool GiveTravelRationToPlayer(int amount = 1) => AddItemByIdToPlayer(4100550, amount);
    public static bool GiveSilverToPlayer(int amount = 1) => AddSilverToPlayer(amount);

    public static bool AddSilverToPlayer(int amount)
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("AddSilverToPlayer: inventory instance not found.");
                return false;
            }

            var inv = invPair.instance;
            var t = invPair.type;

            string[] silverNames = new[] { "ContainedSilver", "containedSilver", "Silver", "silver", "Coins", "coins", "Currency", "ContainedCurrency" };
            foreach (var name in silverNames)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(long)))
                {
                    try
                    {
                        object current = f.GetValue(inv);
                        long cur = Convert.ToInt64(current);
                        long nv = cur + amount;
                        if (f.FieldType == typeof(int)) f.SetValue(inv, (int)nv);
                        else f.SetValue(inv, nv);
                        TBLog.Info($"AddSilverToPlayer: updated field {name} from {cur} to {nv}");
                        TryInvokeRefresh(inv);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"AddSilverToPlayer: failed to update field {name}: {ex.Message}");
                    }
                }

                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead && p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long)))
                {
                    try
                    {
                        object current = p.GetValue(inv, null);
                        long cur = Convert.ToInt64(current);
                        long nv = cur + amount;
                        if (p.PropertyType == typeof(int)) p.SetValue(inv, (int)nv, null);
                        else p.SetValue(inv, nv, null);
                        TBLog.Info($"AddSilverToPlayer: updated property {name} from {cur} to {nv}");
                        TryInvokeRefresh(inv);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"AddSilverToPlayer: failed to update property {name}: {ex.Message}");
                    }
                }
            }

            // Look for AddCurrency-like methods
            string[] silverMethodNames = new[] { "AddSilver", "AddCurrency", "AddCoins", "AddMoney", "ModifySilver", "ChangeSilver" };
            foreach (var mn in silverMethodNames)
            {
                var mi = t.GetMethod(mn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null)
                {
                    var parms = mi.GetParameters();
                    try
                    {
                        if (parms.Length == 1 && IsIntegerParam(parms[0]))
                        {
                            mi.Invoke(inv, new object[] { amount });
                            TBLog.Info($"AddSilverToPlayer: invoked {mn}({amount})");
                            TryInvokeRefresh(inv);
                            return true;
                        }
                        if (parms.Length == 2 && IsIntegerParam(parms[0]) && IsIntegerParam(parms[1]))
                        {
                            mi.Invoke(inv, new object[] { amount, 0 });
                            TBLog.Info($"AddSilverToPlayer: invoked {mn}({amount},0)");
                            TryInvokeRefresh(inv);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"AddSilverToPlayer: invocation of {mn} failed: {ex.Message}");
                    }
                }
            }

            TBLog.Warn("AddSilverToPlayer: no currency field/method found - skipping unsafe fallback.");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("AddSilverToPlayer: unexpected: " + ex);
            return false;
        }
    }

    // Safer AddItemById (preferred path for adding items like rations)
    public static bool AddItemByIdToPlayer(int itemId, int amount = 1)
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("AddItemByIdToPlayer: inventory instance not found.");
                return false;
            }

            var inv = invPair.instance;
            var t = invPair.type;

            // prefer explicit methods or generic add-methods (attempted by TryAddItemToPlayerInventory already)
            // Reuse that logic by invoking TryAddItemToPlayerInventory which now has safer tolerant invocation
            return TryAddItemToPlayerInventory(itemId, amount);
        }
        catch (Exception ex)
        {
            TBLog.Warn("AddItemByIdToPlayer: unexpected: " + ex);
            return false;
        }
    }

    // Diagnostics: dump inventory api
    public static void DumpFoundInventoryApi()
    {
        try
        {
            var invPair = FindInventoryInstance();
            if (invPair.instance == null || invPair.type == null)
            {
                TBLog.Warn("DumpFoundInventoryApi: inventory instance not found.");
                return;
            }

            var inv = invPair.instance;
            var t = invPair.type;
            TBLog.Info($"DumpFoundInventoryApi: found inventory instance of type {t.FullName}");

            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TBLog.Info($"  Fields ({fields.Length}):");
            foreach (var f in fields)
            {
                object val = null;
                try { val = f.GetValue(inv); } catch { val = "(err)"; }
                TBLog.Info($"    {f.FieldType.Name} {f.Name} = {FormatValueShort(val)}");
            }

            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TBLog.Info($"  Properties ({props.Length}):");
            foreach (var p in props)
            {
                object val = null;
                try { if (p.CanRead) val = p.GetValue(inv, null); } catch { val = "(err)"; }
                TBLog.Info($"    {p.PropertyType.Name} {p.Name} (canRead={p.CanRead}, canWrite={p.CanWrite}) = {FormatValueShort(val)}");
            }

            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           .OrderBy(m => m.Name).ToArray();
            TBLog.Info($"  Methods ({methods.Length}):");
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                string parmDesc = string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name));
                TBLog.Info($"    {m.ReturnType.Name} {m.Name}({parmDesc})");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpFoundInventoryApi: unexpected: " + ex);
        }
    }

    private static (object instance, Type type) FindInventoryInstance()
    {
        try
        {
            var playerRoot = TeleportHelpers.FindPlayerRoot();
            if (playerRoot == null) return (null, null);

            string[] candidateTypeNames = new[]
            {
                "CharacterInventory", "CharacterBackpack", "Inventory", "PlayerInventory", "ItemInventory", "InventoryManager", "CharacterItemInventory"
            };

            foreach (var tn in candidateTypeNames)
            {
                var t = ReflectionUtils.SafeGetType(tn + ", Assembly-CSharp") ?? ReflectionUtils.SafeGetType(tn);
                if (t == null) continue;

                try
                {
                    var comp = playerRoot.GetComponent(t);
                    if (comp != null) return (comp, t);
                }
                catch { }

                try
                {
                    var getCompInChildren = typeof(GameObject).GetMethod("GetComponentInChildren", new Type[] { typeof(Type), typeof(bool) });
                    if (getCompInChildren != null)
                    {
                        var comp = getCompInChildren.Invoke(playerRoot, new object[] { t, true });
                        if (comp != null) return (comp, t);
                    }
                }
                catch { }

                try
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0) return (objs[0], t);
                }
                catch { }
            }
        }
        catch { }
        return (null, null);
    }

    private static object GetDefaultForType(Type t)
    {
        if (!t.IsValueType) return null;
        return Activator.CreateInstance(t);
    }

    private static void TryInvokeRefresh(object invInstance)
    {
        if (invInstance == null) return;
        var t = invInstance.GetType();
        string[] refreshNames = new[] { "Refresh", "OnInventoryChanged", "UpdateUI", "Sync", "Rebuild" };
        foreach (var rn in refreshNames)
        {
            try
            {
                var m = t.GetMethod(rn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    m.Invoke(invInstance, null);
                    TBLog.Info($"TryInvokeRefresh: invoked {rn} on {t.FullName}");
                    return;
                }
            }
            catch { }
        }
    }

    private static object ActivatorCreateWithId(Type elemType, int itemId)
    {
        try
        {
            var ctor1 = elemType.GetConstructor(new Type[] { typeof(int) });
            if (ctor1 != null) return ctor1.Invoke(new object[] { itemId });

            var ctor2 = elemType.GetConstructor(new Type[] { typeof(int), typeof(int) });
            if (ctor2 != null) return ctor2.Invoke(new object[] { itemId, 1 });

            var inst = Activator.CreateInstance(elemType);
            var fid = elemType.GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? elemType.GetField("itemId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? elemType.GetField("ItemID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fid != null)
            {
                fid.SetValue(inst, itemId);
                return inst;
            }
            var pid = elemType.GetProperty("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? elemType.GetProperty("itemId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? elemType.GetProperty("ItemID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pid != null && pid.CanWrite)
            {
                pid.SetValue(inst, itemId, null);
                return inst;
            }
        }
        catch { }
        return null;
    }

    private static bool TryAppendToCollection(object collectionObj, int itemId, int amount)
    {
        if (collectionObj == null) return false;
        var t = collectionObj.GetType();

        if (typeof(System.Collections.IList).IsAssignableFrom(t))
        {
            var list = (System.Collections.IList)collectionObj;
            Type elemType = null;
            try { elemType = t.IsGenericType ? t.GetGenericArguments()[0] : null; } catch { elemType = null; }

            if (elemType == typeof(int) || elemType == typeof(uint) || elemType == typeof(short))
            {
                for (int i = 0; i < amount; ++i) list.Add(itemId);
                return true;
            }

            if (elemType != null)
            {
                for (int i = 0; i < amount; ++i)
                {
                    var itemObj = TryCreateItemObjectById(elemType, itemId) ?? ActivatorCreateWithId(elemType, itemId);
                    if (itemObj == null) return false;
                    list.Add(itemObj);
                }
                return true;
            }

            for (int i = 0; i < amount; ++i) list.Add(itemId);
            return true;
        }
        return false;
    }

    // Shortcut pro Travel Ration
    /// <summary>Convenience: give the Travel Ration item id 4100550</summary>
    private static bool IsIntegerParam(ParameterInfo p)
    {
        var tt = p.ParameterType;
        return tt == typeof(int) || tt == typeof(uint) || tt == typeof(short) || tt == typeof(long);
    }

    // Pokusí se vytvoøit instanci "Item" typu pro API, pokud existuje ItemDatabase.CreateItem(int id) nebo podobné.
    private static object TryCreateItemObjectById(Type itemType, int itemId)
    {
        try
        {
            string[] dbTypeNames = new[] { "ItemDatabase", "ItemManager", "ItemFactory", "ItemPool" };
            foreach (var tn in dbTypeNames)
            {
                var dbType = ReflectionUtils.SafeGetType(tn + ", Assembly-CSharp") ?? ReflectionUtils.SafeGetType(tn);
                if (dbType == null) continue;

                var createStatic = dbType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null)
                                  ?? dbType.GetMethod("GetItem", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null)
                                  ?? dbType.GetMethod("CreateItem", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null);
                if (createStatic != null)
                {
                    var obj = createStatic.Invoke(null, new object[] { itemId });
                    if (obj != null && itemType.IsInstanceOfType(obj)) return obj;
                    if (obj != null && itemType.IsAssignableFrom(obj.GetType())) return obj;
                }

                var instances = UnityEngine.Object.FindObjectsOfType(dbType);
                if (instances != null && instances.Length > 0)
                {
                    foreach (var inst in instances)
                    {
                        var instMethod = dbType.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null)
                                     ?? dbType.GetMethod("GetItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null);
                        if (instMethod != null)
                        {
                            var obj = instMethod.Invoke(inst, new object[] { itemId });
                            if (obj != null && itemType.IsInstanceOfType(obj)) return obj;
                            if (obj != null && itemType.IsAssignableFrom(obj.GetType())) return obj;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string FormatValueShort(object v)
    {
        if (v == null) return "null";
        if (v is string) return "\"" + v + "\"";
        if (v is Array arr) return $"Array[{arr.Length}]";
        try
        {
            var s = v.ToString();
            if (s.Length > 120) s = s.Substring(0, 120) + "...";
            return s;
        }
        catch { return "(val)"; }
    }
}
