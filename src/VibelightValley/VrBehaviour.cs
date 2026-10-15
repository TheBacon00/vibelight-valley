using System;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using VibelightValley.Vr;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace VibelightValley;

/// <summary>
/// The mod's MonoBehaviour (registered into IL2CPP by BasePlugin.AddComponent). Unity calls drive everything:
/// Update -> diagnostics + VR controller, coroutine -> end-of-frame VR work, quit -> clean teardown.
/// </summary>
public class VrBehaviour : MonoBehaviour
{
    public VrBehaviour(IntPtr ptr) : base(ptr) { }

    private bool _started;
    private bool _quitting;
    private VrController _vr;
    private static Il2CppSystem.Action _quitDelegate; // kept alive: IL2CPP holds only a native reference

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
    }

    private void Start()
    {
        try
        {
            _quitDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
                Log.Info($"Application.quitting (frame {Time.frameCount})")));
            Application.add_quitting(_quitDelegate);
        }
        catch (Exception ex) { Log.Warn($"Could not hook Application.quitting: {ex.Message}"); }
    }

    private void Update()
    {
        Log.Frame = Time.frameCount;
        try
        {
            PipelineMonitor.NewFrame();
            if (!_started)
            {
                _started = true;
                RuntimeMonitor.Startup();
                StartVr();
                return;
            }
            RuntimeMonitor.Update();
            _vr?.Update();
        }
        catch (Exception ex) { Log.Limited("update", 20, $"VrBehaviour.Update: {ex}", 'E'); }
    }

    private void StartVr()
    {
        if (Settings.Mode.Value == VrMode.Diagnostics) return;
        try
        {
            _vr = new VrController(Settings.Mode.Value == VrMode.TestPattern);
            StartCoroutine(_vr.EndOfFrameLoop().WrapToIl2Cpp());
        }
        catch (Exception ex) { Log.Error("VrController init failed", ex); _vr = null; }
    }

    private void OnApplicationQuit()
    {
        _quitting = true;
        Log.Info("OnApplicationQuit");
        _vr?.TearDown();
        _vr = null;
    }

    private void OnDestroy()
    {
        if (!_quitting) Log.Warn("VrBehaviour destroyed while the game is running - VR stops");
    }
}
