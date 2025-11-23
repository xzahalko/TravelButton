using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class TravelButtonVariantUpdater
{
    // New API expected by compat code: accepts a list of variant IDs (order-preserving) and a concrete lastKnownVariant id.
    // Performs an in-memory update of TravelButton.Cities runtime entry and attempts a graceful fallback to any legacy API if needed.
    public static void UpdateCityVariantDataWithVariants(string sceneName, List<string> variants, string lastKnownVariant)
    {
        try
        {
            TBLog.Info($"TravelButtonVariantUpdater: Attempting to update variant data for scene='{sceneName}' variants=[{(variants != null ? string.Join(",", variants) : "")}] lastKnown='{lastKnownVariant}'");

            bool updatedRuntime = false;

            try
            {
                var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                if (citiesEnum != null)
                {
                    foreach (var c in citiesEnum)
                    {
                        if (c == null) continue;
                        try
                        {
                            var t = c.GetType();
                            // read sceneName (try property then field)
                            string cityScene = null;
                            try { cityScene = t.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                            if (string.IsNullOrEmpty(cityScene))
                            {
                                try { cityScene = t.GetField("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                            }

                            if (string.IsNullOrEmpty(cityScene) || !string.Equals(cityScene, sceneName, StringComparison.OrdinalIgnoreCase)) continue;

                            // Found matching runtime city object -> update fields/properties
                            // 1) legacy fields variantNormalName / variantDestroyedName
                            try
                            {
                                var setNormal = t.GetProperty("variantNormalName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                var setDestroyed = t.GetProperty("variantDestroyedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                var fieldNormal = t.GetField("variantNormalName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                var fieldDestroyed = t.GetField("variantDestroyedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

                                if (variants != null && variants.Count > 0)
                                {
                                    if (setNormal != null) setNormal.SetValue(c, variants.Count >= 1 ? variants[0] : null);
                                    else if (fieldNormal != null) fieldNormal.SetValue(c, variants.Count >= 1 ? variants[0] : null);
                                }
                                if (variants != null && variants.Count > 1)
                                {
                                    if (setDestroyed != null) setDestroyed.SetValue(c, variants.Count >= 2 ? variants[1] : null);
                                    else if (fieldDestroyed != null) fieldDestroyed.SetValue(c, variants.Count >= 2 ? variants[1] : null);
                                }
                            }
                            catch (Exception exSetLegacy)
                            {
                                TBLog.Warn("TravelButtonVariantUpdater: failed to set legacy variantNormalName/variantDestroyedName: " + exSetLegacy);
                            }

                            // 2) set lastKnownVariant (property or field)
                            try
                            {
                                var lastProp = t.GetProperty("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                var lastField = t.GetField("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                if (lastProp != null) lastProp.SetValue(c, lastKnownVariant);
                                else if (lastField != null) lastField.SetValue(c, lastKnownVariant);
                            }
                            catch (Exception exSetLast)
                            {
                                TBLog.Warn("TravelButtonVariantUpdater: failed to set lastKnownVariant on runtime city entry: " + exSetLast);
                            }

                            // 3) if runtime city object supports a 'variants' property/field, try to set it (List<string> or string[])
                            try
                            {
                                var variantsProp = t.GetProperty("variants", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                var variantsField = t.GetField("variants", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

                                if (variantsProp != null)
                                {
                                    var targetType = variantsProp.PropertyType;
                                    object toAssign = ConvertVariantsToType(variants, targetType);
                                    if (toAssign != null) variantsProp.SetValue(c, toAssign);
                                }
                                else if (variantsField != null)
                                {
                                    var targetType = variantsField.FieldType;
                                    object toAssign = ConvertVariantsToType(variants, targetType);
                                    if (toAssign != null) variantsField.SetValue(c, toAssign);
                                }
                            }
                            catch (Exception exSetVariants)
                            {
                                TBLog.Warn("TravelButtonVariantUpdater: failed to set runtime 'variants' on city entry: " + exSetVariants);
                            }

                            updatedRuntime = true;
                            TBLog.Info($"TravelButtonVariantUpdater: updated runtime city entry for scene '{sceneName}'.");
                            
                            // Persist the updated city to JSON using atomic write
                            try
                            {
                                // Get the city object as TravelButton.City if possible
                                if (c is TravelButton.City city)
                                {
                                    TravelButton.AppendOrUpdateCityInJsonAndSave(city);
                                    TBLog.Info($"TravelButtonVariantUpdater: persisted updated variant data for '{city.name}' to JSON.");
                                }
                                else
                                {
                                    TBLog.Warn("TravelButtonVariantUpdater: city object is not of type TravelButton.City; cannot persist to JSON directly.");
                                }
                            }
                            catch (Exception exPersist)
                            {
                                TBLog.Warn($"TravelButtonVariantUpdater: failed to persist variant update to JSON: {exPersist}");
                            }
                            
                            break; // updated the matching entry; stop
                        }
                        catch (Exception exPerCity)
                        {
                            TBLog.Warn("TravelButtonVariantUpdater: error updating a runtime city entry: " + exPerCity);
                        }
                    }
                }
            }
            catch (Exception exEnum)
            {
                TBLog.Warn("TravelButtonVariantUpdater: error enumerating TravelButton.Cities: " + exEnum);
            }

            // If we didn't find a runtime entry to update, attempt to call the legacy UpdateCityVariantData if it's available
            if (!updatedRuntime)
            {
                try
                {
                    var tbvuType = typeof(TravelButtonVariantUpdater);
                    var miLegacy = tbvuType.GetMethod("UpdateCityVariantData", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    if (miLegacy != null)
                    {
                        // Map variants -> normal/destroyed for legacy call
                        string normal = (variants != null && variants.Count >= 1) ? variants[0] : null;
                        string destroyed = (variants != null && variants.Count >= 2) ? variants[1] : null;

                        try
                        {
                            miLegacy.Invoke(null, new object[] { sceneName, normal, destroyed, lastKnownVariant });
                            TBLog.Info("TravelButtonVariantUpdater: called legacy UpdateCityVariantData as fallback.");
                        }
                        catch (TargetParameterCountException)
                        {
                            // try ignore lastKnown if signature is different
                            try { miLegacy.Invoke(null, new object[] { sceneName, normal, destroyed }); TBLog.Info("TravelButtonVariantUpdater: called legacy UpdateCityVariantData (3-arg) fallback."); } catch { TBLog.Warn("TravelButtonVariantUpdater: legacy UpdateCityVariantData invocation failed."); }
                        }
                    }
                    else
                    {
                        TBLog.Info("TravelButtonVariantUpdater: no legacy UpdateCityVariantData method found; runtime update was the primary path.");
                    }
                }
                catch (Exception exLegacy)
                {
                    TBLog.Warn("TravelButtonVariantUpdater: legacy update attempt threw: " + exLegacy);
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TravelButtonVariantUpdater: unexpected error: " + ex);
        }
    }

    // Convert a List<string> to a target type if possible (string[], List<string>, IEnumerable<string>, etc.)
    static object ConvertVariantsToType(List<string> variants, Type targetType)
    {
        if (variants == null) return null;
        if (targetType == null) return null;

        if (targetType.IsAssignableFrom(typeof(List<string>))) return new List<string>(variants);
        if (targetType.IsArray && targetType.GetElementType() == typeof(string)) return variants.ToArray();
        if (targetType.IsAssignableFrom(typeof(string[]))) return variants.ToArray();

        // Try to construct targetType from IEnumerable<string> if it has a ctor(IEnumerable<string>)
        try
        {
            var ctor = targetType.GetConstructor(new[] { typeof(IEnumerable<string>) });
            if (ctor != null) return ctor.Invoke(new object[] { variants });
        }
        catch { }

        // Last resort: if it's object, return List<string>
        if (targetType == typeof(object)) return new List<string>(variants);

        return null;
    }

    public static void UpdateCityVariantData(string sceneName, string normalName, string destroyedName, string lastKnownVariant)
    {
        try
        {
            if (string.IsNullOrEmpty(sceneName)) return;

            try { TBLog.Info($"TravelButtonVariantUpdater: Attempting to update variant data for scene='{sceneName}' normal='{normalName}' destroyed='{destroyedName}' lastKnown='{lastKnownVariant}'"); } catch { }

            var citiesObj = TravelButton.Cities as IEnumerable;
            if (citiesObj == null)
            {
                try { TBLog.Warn("TravelButtonVariantUpdater: TravelButton.Cities runtime collection is null or not enumerable."); } catch { }
                return;
            }

            bool updated = false;
            foreach (var c in citiesObj)
            {
                if (c == null) continue;
                var t = c.GetType();

                string scenePropVal = null;
                var sceneProp = t.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                var sceneField = t.GetField("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (sceneProp != null)
                {
                    try { scenePropVal = sceneProp.GetValue(c) as string; } catch { }
                }
                else if (sceneField != null)
                {
                    try { scenePropVal = sceneField.GetValue(c) as string; } catch { }
                }

                if (string.IsNullOrEmpty(scenePropVal)) continue;

                if (string.Equals(scenePropVal, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    TrySetMemberString(t, c, "variantNormalName", normalName);
                    TrySetMemberString(t, c, "variantDestroyedName", destroyedName);
                    TrySetMemberString(t, c, "lastKnownVariant", lastKnownVariant);

                    updated = true;
                    try { TBLog.Info($"TravelButtonVariantUpdater: updated runtime city entry for scene '{sceneName}'."); } catch { }
                    break;
                }
            }

            if (updated)
            {
                // Persist the updated city to JSON using the new atomic write helper
                try
                {
                    // Find and persist the updated city
                    foreach (var c in citiesObj)
                    {
                        if (c == null) continue;
                        var t = c.GetType();
                        
                        string scenePropVal = null;
                        var sceneProp = t.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        var sceneField = t.GetField("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (sceneProp != null)
                        {
                            try { scenePropVal = sceneProp.GetValue(c) as string; } catch { }
                        }
                        else if (sceneField != null)
                        {
                            try { scenePropVal = sceneField.GetValue(c) as string; } catch { }
                        }
                        
                        if (string.IsNullOrEmpty(scenePropVal)) continue;
                        
                        if (string.Equals(scenePropVal, sceneName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (c is TravelButton.City city)
                            {
                                TravelButton.AppendOrUpdateCityInJsonAndSave(city);
                                TBLog.Info($"TravelButtonVariantUpdater: persisted updated variant data for '{city.name}' to JSON.");
                            }
                            break;
                        }
                    }
                }
                catch (Exception exPersist)
                {
                    TBLog.Warn($"TravelButtonVariantUpdater: failed to persist variant update to JSON: {exPersist}");
                }
                
                // Also call the legacy persist method for backward compatibility (best-effort)
                // Only call persist if there's a zero-argument overload to avoid TargetParameterCountException.
                try
                {
                    var persistMi = typeof(TravelButton).GetMethod("PersistCitiesToPluginFolder", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (persistMi != null)
                    {
                        var paramCount = persistMi.GetParameters()?.Length ?? 0;
                        if (paramCount == 0)
                        {
                            if (persistMi.IsStatic)
                            {
                                try { persistMi.Invoke(null, null); TBLog.Info("TravelButtonVariantUpdater: called PersistCitiesToPluginFolder (static)."); } catch (Exception ex) { TBLog.Warn("TravelButtonVariantUpdater: PersistCitiesToPluginFolder(static) threw: " + ex); }
                            }
                            else
                            {
                                try
                                {
                                    var instField = typeof(TravelButton).GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                    object inst = instField != null ? instField.GetValue(null) : null;
                                    if (inst != null) persistMi.Invoke(inst, null);
                                    else persistMi.Invoke(null, null); // last resort
                                    TBLog.Info("TravelButtonVariantUpdater: called PersistCitiesToPluginFolder (instance).");
                                }
                                catch (Exception ex) { TBLog.Warn("TravelButtonVariantUpdater: PersistCitiesToPluginFolder(instance) threw: " + ex); }
                            }
                        }
                        else
                        {
                            TBLog.Warn($"TravelButtonVariantUpdater: PersistCitiesToPluginFolder exists but expects {paramCount} parameters; skipping automatic persist to avoid invoking with wrong args.");
                        }
                    }
                    else
                    {
                        TBLog.Warn("TravelButtonVariantUpdater: PersistCitiesToPluginFolder method not found by reflection; skipping persist.");
                    }
                }
                catch (Exception exAll) { try { TBLog.Warn("TravelButtonVariantUpdater: error while trying to persist cities: " + exAll); } catch { } }
            }
            else
            {
                try { TBLog.Info("TravelButtonVariantUpdater: no matching runtime city entry found to update."); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("TravelButtonVariantUpdater: top-level exception: " + ex); } catch { }
        }
    }

    static void TrySetMemberString(Type objType, object objInstance, string memberName, string value)
    {
        if (string.IsNullOrEmpty(memberName)) return;
        try
        {
            var p = objType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite)
            {
                p.SetValue(objInstance, value);
                return;
            }
            var f = objType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null)
            {
                f.SetValue(objInstance, value);
                return;
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn($"TravelButtonVariantUpdater: failed to set member '{memberName}': {ex}"); } catch { }
        }
    }
}