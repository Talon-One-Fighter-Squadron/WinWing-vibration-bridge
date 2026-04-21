using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace NOWinWingBridge;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("NuclearOption.exe")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.ngamingpc.nowinwingbridge";
    public const string PluginName = "NO WinWing Bridge";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log = null!;

    private ConfigEntry<string> _targetIp = null!;
    private ConfigEntry<int> _targetPort = null!;
    private ConfigEntry<float> _sendInterval = null!;
    private ConfigEntry<bool> _debug = null!;
    private ConfigEntry<float> _aoaBuffetStartDeg = null!;
    private ConfigEntry<float> _aoaBuffetFullDeg = null!;
    private ConfigEntry<float> _gBuffetStart = null!;
    private ConfigEntry<float> _gBuffetFull = null!;
    private ConfigEntry<float> _payloadThreshold = null!;

    private readonly Dictionary<int, int> _ammoSnapshot = new();
    private readonly Dictionary<int, float> _firePulseUntil = new();

    private UdpClient? _udp;
    private IPEndPoint? _endpoint;
    private bool _startedMission;
    private float _sendTimer;
    private float _lastAoADeg;
    private float _lastTelemetryTime;
    private float _smoothedSpeedbrake;

    private static readonly BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo? AirbrakeOpenAmountField = typeof(Airbrake).GetField("openAmount", AnyInstance);

    private const string NetReady = "{\"func\": \"net\", \"msg\": \"ready\"}";
    private const string MissionReady = "{\"func\": \"mission\", \"msg\": \"ready\"}";
    private const string MissionStart = "{\"func\": \"mission\", \"msg\": \"start\"}";
    private const string MissionStop = "{\"func\": \"mission\", \"msg\": \"stop\"}";
    private const string ModuleMessage = "{\"func\": \"mod\", \"msg\": \"TF-51D\"}";

    private sealed class Args
    {
        public float angleOfAttack { get; set; }
        public float rateOfAngleOfAttack { get; set; }
        public float trueAirSpeed { get; set; }
        public float gearValue { get; set; }
        public bool isGearDown { get; set; }
        public bool isGearTouchGround { get; set; }
        public int cannonShellsCount { get; set; } = 1000;
        public bool isFireCannonShells { get; set; }
        public float speedbrakesValue { get; set; }
        public float verticalVelocity { get; set; }
        public float accelerationX { get; set; }
        public float accelerationY { get; set; }
        public float accelerationZ { get; set; }
        public bool hasPayload { get; set; }
        public bool hasNoPayload { get; set; } = true;
        public List<object> payloadStations { get; set; } = new();
    }

    private sealed class TelemetryMessage
    {
        public string func { get; } = "addCommon";
        public Args args { get; set; } = new();
    }

    private void Awake()
    {
        Log = Logger;

        _targetIp = Config.Bind("Network", "TargetIp", "127.0.0.1", "SimApp Pro listener IP.");
        _targetPort = Config.Bind("Network", "TargetPort", 16536, "SimApp Pro listener UDP port.");
        _sendInterval = Config.Bind("Telemetry", "SendIntervalSeconds", 0.025f, "How often to send vibration telemetry.");
        _debug = Config.Bind("Debug", "VerboseLogging", false, "Log sampled telemetry.");
        _aoaBuffetStartDeg = Config.Bind("Tuning", "AoABuffetStartDeg", 10f, "AoA where buffet starts contributing.");
        _aoaBuffetFullDeg = Config.Bind("Tuning", "AoABuffetFullDeg", 18f, "AoA where buffet reaches full contribution.");
        _gBuffetStart = Config.Bind("Tuning", "GBuffetStart", 3.5f, "G where buffet starts contributing.");
        _gBuffetFull = Config.Bind("Tuning", "GBuffetFull", 7.5f, "G where buffet reaches full contribution.");
        _payloadThreshold = Config.Bind("Tuning", "PayloadAmmoThreshold", 0.98f, "Ammo level below this means payload has been spent.");

        ReconnectUdp();
        Log.LogInfo($"{PluginName} loaded.");
    }

    private void OnDestroy()
    {
        SendRaw(MissionStop);
        _udp?.Dispose();
        _udp = null;
    }

    private void ReconnectUdp()
    {
        _udp?.Dispose();
        _endpoint = new IPEndPoint(IPAddress.Parse(_targetIp.Value), _targetPort.Value);
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Connect(_endpoint);
    }

    private void Update()
    {
        _sendTimer += Time.unscaledDeltaTime;
        if (_sendTimer < Mathf.Max(0.01f, _sendInterval.Value))
            return;

        _sendTimer = 0f;

        if (!MissionManager.IsRunning)
        {
            if (_startedMission)
            {
                SendRaw(MissionStop);
                _startedMission = false;
                _ammoSnapshot.Clear();
                _firePulseUntil.Clear();
            }
            return;
        }

        GameManager.GetLocalAircraft(out var aircraft);
        if (aircraft == null)
            return;

        if (!_startedMission)
        {
            StartMissionHandshake();
            _startedMission = true;
            PrimeAmmoSnapshot(aircraft);
        }

        var telemetry = BuildTelemetry(aircraft);
        SendTelemetry(telemetry);
    }

    private void StartMissionHandshake()
    {
        SendRaw(MissionStop);
        SendRaw(NetReady);
        SendRaw(MissionReady);
        SendRaw(MissionStart);
        SendRaw(ModuleMessage);
        if (_debug.Value)
            Log.LogInfo("WinWing mission handshake sent.");
    }

    private TelemetryMessage BuildTelemetry(Aircraft aircraft)
    {
        var msg = new TelemetryMessage();
        var args = msg.args;

        var cockpitRb = aircraft.CockpitRB();
        var velocity = cockpitRb != null ? cockpitRb.velocity : aircraft.rb.velocity;
        var localVelocity = aircraft.transform.InverseTransformDirection(velocity);
        var localAccel = aircraft.transform.InverseTransformDirection(aircraft.accel);
        var aoaDeg = CalculateAoADeg(localVelocity);

        float now = Time.time;
        float dt = Mathf.Max(0.0001f, now - _lastTelemetryTime);
        _lastTelemetryTime = now;

        args.angleOfAttack = aoaDeg;
        args.rateOfAngleOfAttack = Mathf.Abs(aoaDeg - _lastAoADeg) / dt;
        _lastAoADeg = aoaDeg;

        args.trueAirSpeed = aircraft.speed;
        args.gearValue = GetGearValue(aircraft);
        args.isGearDown = aircraft.gearDeployed || aircraft.gearState == LandingGear.GearState.LockedExtended;
        args.isGearTouchGround = aircraft.radarAlt <= 0.25f;

        int cannonShells = UpdateAmmoAndFirePulses(aircraft, now, out bool firingPulse, out bool payloadSpent);
        args.cannonShellsCount = cannonShells;
        args.isFireCannonShells = firingPulse;

        float brakeValue = GetAirbrakeValue(aircraft);
        float buffetAoA = Mathf.InverseLerp(_aoaBuffetStartDeg.Value, _aoaBuffetFullDeg.Value, Mathf.Abs(aoaDeg));
        float buffetG = Mathf.InverseLerp(_gBuffetStart.Value, _gBuffetFull.Value, Mathf.Abs(aircraft.gForce));
        float buffetAccel = Mathf.Clamp01(localAccel.magnitude / 18f);
        float buffet = Mathf.Clamp01(Mathf.Max(buffetAoA, buffetG * 0.7f, buffetAccel * 0.35f));
        _smoothedSpeedbrake = Mathf.Lerp(_smoothedSpeedbrake, Mathf.Clamp01(brakeValue + buffet), 0.5f);
        args.speedbrakesValue = _smoothedSpeedbrake;

        args.verticalVelocity = velocity.y;
        args.accelerationX = localAccel.x;
        args.accelerationY = localAccel.y;
        args.accelerationZ = localAccel.z;

        float ammoLevel = aircraft.GetAmmoLevel();
        args.hasPayload = ammoLevel > 0.01f && !payloadSpent;
        args.hasNoPayload = !args.hasPayload;
        args.payloadStations = new List<object>();

        if (_debug.Value)
        {
            Log.LogInfo($"AoA={args.angleOfAttack:F2} dAoA={args.rateOfAngleOfAttack:F2} TAS={args.trueAirSpeed:F1} Gear={args.gearValue:F2} Fire={args.isFireCannonShells} Brake={args.speedbrakesValue:F2} Acc=({args.accelerationX:F2},{args.accelerationY:F2},{args.accelerationZ:F2})");
        }

        return msg;
    }

    private static float CalculateAoADeg(Vector3 localVelocity)
    {
        var planar = new Vector2(localVelocity.z, localVelocity.y);
        if (planar.sqrMagnitude < 0.0001f)
            return 0f;
        return Mathf.Atan2(planar.y, Mathf.Max(0.001f, planar.x)) * Mathf.Rad2Deg;
    }

    private static float GetGearValue(Aircraft aircraft)
    {
        return aircraft.gearState switch
        {
            LandingGear.GearState.LockedExtended => 1f,
            LandingGear.GearState.LockedRetracted => 0f,
            LandingGear.GearState.Extending => 0.5f,
            LandingGear.GearState.Retracting => 0.5f,
            _ => aircraft.gearDeployed ? 1f : 0f,
        };
    }

    private static float GetAirbrakeValue(Aircraft aircraft)
    {
        try
        {
            var brakes = aircraft.GetComponentsInChildren<Airbrake>(true);
            if (brakes != null && brakes.Length > 0 && AirbrakeOpenAmountField != null)
            {
                float max = 0f;
                foreach (var brake in brakes)
                {
                    if (AirbrakeOpenAmountField.GetValue(brake) is float openAmount)
                        max = Mathf.Max(max, Mathf.Clamp01(openAmount));
                }
                return max;
            }
        }
        catch
        {
            // fall through to control-based estimate
        }

        var inputs = aircraft.GetInputs();
        return inputs.throttle <= 0.001f ? 1f : 0f;
    }

    private void PrimeAmmoSnapshot(Aircraft aircraft)
    {
        _ammoSnapshot.Clear();
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            _ammoSnapshot[i] = aircraft.weaponStations[i]?.Ammo ?? 0;
        }
    }

    private int UpdateAmmoAndFirePulses(Aircraft aircraft, float now, out bool firingPulse, out bool payloadSpent)
    {
        firingPulse = false;
        payloadSpent = aircraft.GetAmmoLevel() < _payloadThreshold.Value;

        int totalAmmo = 0;

        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            var station = aircraft.weaponStations[i];
            if (station == null)
                continue;

            totalAmmo += Mathf.Max(0, station.Ammo);

            _ammoSnapshot.TryGetValue(i, out int previousAmmo);
            if (station.Ammo < previousAmmo)
            {
                _firePulseUntil[i] = now + 0.08f;
            }

            _ammoSnapshot[i] = station.Ammo;
        }

        foreach (var kv in _firePulseUntil.ToList())
        {
            if (kv.Value >= now)
            {
                firingPulse = true;
            }
            else
            {
                _firePulseUntil.Remove(kv.Key);
            }
        }

        return totalAmmo;
    }

    private void SendTelemetry(TelemetryMessage telemetry)
    {
        string json = BuildAddCommonJson(telemetry.args);
        SendRaw(json);
    }

    private void SendRaw(string payload)
    {
        try
        {
            if (_udp == null)
                ReconnectUdp();

            byte[] bytes = Encoding.ASCII.GetBytes(payload);
            _udp!.Send(bytes, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed sending WinWing payload: {ex.Message}");
        }
    }

    private static string BuildAddCommonJson(Args a)
    {
        string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string B(bool v) => v ? "true" : "false";
        return "{" +
               "\"func\":\"addCommon\"," +
               "\"args\":{" +
               "\"angleOfAttack\":" + F(a.angleOfAttack) + "," +
               "\"rateOfAngleOfAttack\":" + F(a.rateOfAngleOfAttack) + "," +
               "\"trueAirSpeed\":" + F(a.trueAirSpeed) + "," +
               "\"gearValue\":" + F(a.gearValue) + "," +
               "\"isGearDown\":" + B(a.isGearDown) + "," +
               "\"isGearTouchGround\":" + B(a.isGearTouchGround) + "," +
               "\"cannonShellsCount\":" + a.cannonShellsCount + "," +
               "\"isFireCannonShells\":" + B(a.isFireCannonShells) + "," +
               "\"speedbrakesValue\":" + F(a.speedbrakesValue) + "," +
               "\"verticalVelocity\":" + F(a.verticalVelocity) + "," +
               "\"accelerationX\":" + F(a.accelerationX) + "," +
               "\"accelerationY\":" + F(a.accelerationY) + "," +
               "\"accelerationZ\":" + F(a.accelerationZ) + "," +
               "\"hasPayload\":" + B(a.hasPayload) + "," +
               "\"hasNoPayload\":" + B(a.hasNoPayload) + "," +
               "\"payloadStations\":[]" +
               "}}";
    }

}
