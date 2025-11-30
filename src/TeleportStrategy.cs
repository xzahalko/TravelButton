using System;
using System.Collections;
using UnityEngine;

public interface ITeleportStrategy
{
    // Coroutine-based: invoke from StartCoroutine or host.StartCoroutine
    // The strategy must call resultCallback(true) if it moved the player, otherwise resultCallback(false).
    IEnumerator PlacePlayer(Vector3 target, Action<bool> resultCallback);
    string Name { get; }
}
