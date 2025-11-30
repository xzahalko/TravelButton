using System;

/// <summary>
/// City representation consumed by UI code
/// </summary>
[Serializable]
public class City
{
    public string name;
    // coords array [x,y,z] or null
    public float[] coords;
    // optional name of a GameObject to find at runtime
    public string targetGameObjectName;
    // optional per-city price; null means use global
    public int? price;
    // whether city is explicitly enabled in config (default false)
    public bool enabled;

    public bool visited;
    
    public string desc;

    public string sceneName;
    
    // New fields for multi-variant support
    public string[] variants;
    public string lastKnownVariant;

    public City(string name)
    {
        this.name = name;
        this.coords = null;
        this.targetGameObjectName = null;
        this.price = null;
        this.enabled = false;
        bool visited = false; 
        this.sceneName = null;
        this.variants = null;
        this.lastKnownVariant = null;
    }

    // Compatibility properties expected by older code:
    // property 'visited' (lowercase) ? maps to VisitedTracker if available
    public bool setVisited
    {
        get
        {
            try { return VisitedTracker.HasVisited(this.name); }
            catch { return false; }
        }
        set
        {
            try
            {
                if (value) VisitedTracker.MarkVisited(this.name);
            }
            catch { }
        }
    }

    // compatibility method name used previously in code: isCityEnabled()
    public bool isCityEnabled()
    {
        return TravelButton.IsCityEnabled(this.name);
    }
}
