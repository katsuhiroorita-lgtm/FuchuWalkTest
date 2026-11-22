using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

/// <summary>Entry point for the server-only baked scene. No local player or cloud services.</summary>
[DefaultExecutionOrder(-10000)]
public sealed class FuchuLocalServer : MonoBehaviour
{
    public MeshFilter road;
    public Vector3 playableCenter;
    public float walkingSpeed = 6.5f;
    public float sprintSpeed = 9f;
    internal static bool Active { get; private set; }
    private int previousFrameRate;
    private bool previousBackground;
    private float previousTimeScale;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState() => Active = false;

    private void Awake()
    {
        Active = true;
        previousFrameRate = Application.targetFrameRate;
        previousBackground = Application.runInBackground;
        previousTimeScale = Time.timeScale;
        Application.runInBackground = true;
        Application.targetFrameRate = 30;
        Time.timeScale = 1;
    }
    private IEnumerator Start()
    {
        // Existing road actor spawners finish Start before their transforms are collected.
        yield return null;
        try
        {
            string address = Argument("-fuchu-bind", "127.0.0.1");
            string portText = Argument("-fuchu-port", "7777");
            if (!IsIPv4(address) || !ushort.TryParse(portText, out ushort port) || port == 0)
                throw new ArgumentException("-fuchu-bindにはIPv4、-fuchu-portには1～65535を指定してください。");
            var context = new FuchuOnlineSimulation(transform, road, playableCenter, walkingSpeed, sprintSpeed);
            var connection = gameObject.AddComponent<FuchuOnlineConnection>();
            connection.StartDedicated(context, address, port);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (Application.isBatchMode) Application.Quit(1);
            else enabled = false;
        }
    }
    private void OnDestroy()
    {
        if (Application.isEditor)
        {
            Application.targetFrameRate = previousFrameRate;
            Application.runInBackground = previousBackground;
            Time.timeScale = previousTimeScale;
        }
        Active = false;
    }
    internal static bool IsIPv4(string value) => IPAddress.TryParse(value, out IPAddress address) && address.AddressFamily == AddressFamily.InterNetwork;
    private static string Argument(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 == args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException(name + " の値がありません。");
                return args[i + 1];
            }
        return fallback;
    }
}
