using System;
using System.Linq;
using System.Reflection;
using VibelightValley.Core;
using UnityEngine;

namespace VibelightValley.Vr;

/// <summary>
/// Finds the local player's character transform. DDLV has a singleton (static Instance) exposing a
/// LocalPlayerAvatar property; which class it is does not matter, so it is discovered by reflection over the
/// game's interop assembly once, then the transform is cached and refreshed when it goes stale.
/// </summary>
public sealed class PlayerLocator
{
    private PropertyInfo _instanceProp, _avatarProp;
    private bool _searched;
    private Transform _player;
    private float _nextRefresh;

    public bool TryGetPosition(out Vector3 position)
    {
        position = default;
        if (_player == null || Time.realtimeSinceStartup >= _nextRefresh)
        {
            _nextRefresh = Time.realtimeSinceStartup + 1f;
            Refresh();
        }
        if (_player == null) return false;
        position = _player.position;
        return true;
    }

    private void Refresh()
    {
        try
        {
            if (!_searched) Search();
            if (_avatarProp == null) return;
            object singleton = _instanceProp.GetValue(null);
            if (singleton == null) { _player = null; return; }
            var avatar = _avatarProp.GetValue(singleton) as Component;
            var t = avatar != null ? avatar.transform : null;
            if (t != null && _player == null) Log.Info($"PlayerLocator: local player is '{t.name}' at {t.position}");
            _player = t;
        }
        catch (Exception ex)
        {
            Log.Limited("playerlocator", 5, $"PlayerLocator: {ex.GetType().Name}: {ex.Message}", 'W');
            _player = null;
        }
    }

    private void Search()
    {
        _searched = true;
        var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game");
        if (game == null) { Log.Warn("PlayerLocator: game assembly 'Game' not loaded"); return; }
        foreach (var type in SafeTypes(game))
        {
            var avatar = type.GetProperty("LocalPlayerAvatar", BindingFlags.Public | BindingFlags.Instance);
            var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (avatar == null || instance == null || !typeof(Component).IsAssignableFrom(avatar.PropertyType)) continue;
            _avatarProp = avatar;
            _instanceProp = instance;
            Log.Info($"PlayerLocator: using {type.FullName}.Instance.LocalPlayerAvatar ({avatar.PropertyType.Name})");
            return;
        }
        Log.Warn("PlayerLocator: no LocalPlayerAvatar accessor found - character-follow mode unavailable");
    }

    private static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
    }
}
