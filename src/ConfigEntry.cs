/// <summary>
/// Simple configurable wrapper to keep compatibility with existing code.
/// Provides a generic container for configuration values.
/// </summary>
/// <typeparam name="T">The type of the configuration value</typeparam>
public class ConfigEntry<T>
{
    /// <summary>
    /// Gets or sets the configuration value.
    /// </summary>
    public T Value;

    /// <summary>
    /// Initializes a new instance of the ConfigEntry class with the specified value.
    /// </summary>
    /// <param name="v">The initial value</param>
    public ConfigEntry(T v) { Value = v; }

}
