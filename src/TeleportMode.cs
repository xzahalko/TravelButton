public enum TeleportMode
{
    Auto,   // choose the refactored/new strategy by default, fallback enabled
    New,    // always prefer new SafePlacePlayerCoroutine (current branch)
    Old     // always prefer restored AttemptTeleportToPositionSafe (cb10ef3 behavior)
}
