# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Inferno is a smart wood pellet smoker controller for a Traeger Junior Elite 20, running on a Raspberry Pi 3 with .NET. It manages fire control (auger, blower, igniter) via GPIO relays, reads temperatures via RTD sensors through an MCP3008 ADC over SPI, and displays status on a 20x4 I2C LCD.

## Build & Deploy

```bash
# Build entire solution
dotnet build Inferno.sln

# Build individual project
dotnet build Inferno.Api/Inferno.Api.csproj

# Publish and deploy to Pi (from Windows)
publish-all.bat          # Publishes CLI → Mqtt → Api, deploys via scp to pi@inferno
```

Deployment to the Pi is managed via Pulumi (see `Inferno.Deploy`). The Pi hostname is `inferno`.

```bash
# Run tests
dotnet test Inferno.Tests
```

## Architecture

Six projects in `Inferno.sln`:

- **Inferno.Api** — ASP.NET Core Web API (port 5000/5001). Core controller logic, PID algorithm, fire management, hardware device abstractions. This is the main application that runs on the Pi.
- **Inferno.Cli** — Command-line client for controlling the smoker remotely via HTTP.
- **Inferno.Common** — Shared models (`SmokerMode`, `SmokerStatus`, `Temps`), interfaces (`ISmoker`), and `SmokerProxy` HTTP client.
- **Inferno.Mqtt** — MQTT bridge service that exposes smoker state to Home Assistant. Subscribes/publishes on `inferno/{topic}/{command|state}` topics.
- **Inferno.Deploy** — Pulumi infrastructure-as-code project for deploying to the Pi.
- **Inferno.Tests** — xUnit test project covering PID, RTD, FireMinder, PreheatMonitor, and extensions.

### Core Control Flow (Inferno.Api)

`Program.cs` sets up GPIO, SPI, I2C hardware and DI. The `ISmoker` singleton (`Smoker.cs`) runs an async `ModeLoop()` state machine with modes: **Ready, Smoke, Hold, Sear, Shutdown, Error**.

- **Smoker.cs** — Main state machine. Controls auger feed cycles, blower, igniter. In Hold mode, uses PID output to modulate auger duty cycle.
- **FireMinder.cs** — Background fire health monitor with auto-reignition and timeout detection.
- **PreheatMonitor.cs** — Stability-based preheat detection using a rolling temperature window with latch behavior.
- **SmokerPid.cs** — PID controller (PB=60, Ti=180, Td=45) with integral windup protection. Returns duty cycle for 10-second hold cycles.
- **DisplayUpdater.cs** — Refreshes the LCD every second with mode, temps, and hardware status.

### Hardware Layer (Inferno.Api/Devices/)

- **RelayDevice.cs** — Abstract GPIO relay base (active-low). Inherited by Auger, Blower, Igniter.
- **RtdArray.cs** — Reads grill (ch0) and probe (ch1) temps from MCP3008 ADC. Maintains 100-sample rolling average. Converts ADC → resistance → temperature via Callendar-Van Dusen equation.
- **Display.cs** — 20x4 LCD via Pcf8574 I2C expander at address 0x27.

### API Endpoints

All under `/api/`: `status` (GET), `mode` (GET/POST), `temps` (GET), `setpoint` (GET/POST, 180-400°F), `pvalue` (GET/POST, 0-5).

### P-Value System

The P-value (0-5) is a Traeger-style pellet feed rate that controls auger on/off timing in Smoke mode. Higher values = less fuel. In Hold mode, the PID controller overrides this.

## Key Dependencies

- `IoT.Device.Bindings` — .NET IoT library for Pcf8574, Lcd2004, Mcp3008, GPIO
- `MQTTnet.Extensions.ManagedClient` — MQTT client for Home Assistant bridge
- `System.Text.Json` — JSON serialization in Common project

## Hardware Pin Assignments

- GPIO 22: Auger, GPIO 21: Blower, GPIO 23: Igniter
- SPI 0,0 @ 1MHz: MCP3008 ADC
- I2C 0x27: LCD expander
