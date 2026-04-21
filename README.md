# NO WinWing Bridge

A **BepInEx mod for Nuclear Option** that reads in-game flight telemetry and sends it to **WinWing vibration hardware**.

This mod is for **WinWing stick vibration**, **not full force feedback**.

---

## What It Does

NO WinWing Bridge reads aircraft telemetry from **Nuclear Option** and converts it into vibration effects for supported WinWing hardware.

Current effect sources include:

- **AoA buffet**
- **G-force buffet**
- **Touchdown bumps**
- **Speedbrake rumble**
- **Weapon fire / ammo use pulses**

---

## How It Works

The mod runs inside **Nuclear Option** through **BepInEx**.

It reads flight data from the player aircraft, such as:

- angle of attack
- G load
- landing impact
- airbrake state
- weapon or ammo changes

It then converts that data into simple vibration strength values and sends them to the **WinWing listener over localhost UDP**.

### Telemetry flow

**Nuclear Option telemetry → vibration effect → WinWing output**

This means the mod does **not** try to create real force feedback.  
It turns aircraft telemetry into rumble effects your WinWing hardware can use.

---

## Requirements

You will need:

- **Nuclear Option**
- **BepInEx 5**
- **WinWing SimApp Pro**
- a **WinWing device with vibration support**

---

## Important

> **SimApp Pro should be running while using this mod.**

The mod sends telemetry to the WinWing listener, and **SimApp Pro** is what actually drives the hardware vibration.

If SimApp Pro is not running, the mod may still send data, but your stick will not vibrate.

---

## Install

Copy the DLL to:

```text
<Nuclear Option>\BepInEx\plugins\
```

Then start the game normally.

---

## Config

After first launch, the config file will be created here:

```text
<Nuclear Option>\BepInEx\config\com.ngamingpc.nowinwingbridge.cfg
```

---

## Tuning

If vibration feels too strong in normal flight, raise these values slightly:

- `AoABuffetStartDeg`
- `GBuffetStart`

### Example softer settings

```ini
AoABuffetStartDeg = 11.5
AoABuffetFullDeg = 18.5
GBuffetStart = 4.0
GBuffetFull = 7.5
```

These values make buffet start a little later than stock, which helps reduce vibration during lighter maneuvering and normal 1G flight.

---

## Credits

Inspired by:

- **NOFFB**
- **IL2WinWing**

---

## Disclaimer

This is an **unofficial community mod**.  
Use at your own risk.
