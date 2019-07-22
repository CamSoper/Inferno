# Inferno
Wood pellet smoker powered by .NET Core and Raspberry Pi 3.

## Inferno.Api

This is the core functionality. It exposes all functionality as a web API. I run it on http://localhost:5000 and, until I write UI, control the smoker by connecting to the Pi via SSH and issuing cURL commands.

## Inferno.Cli

A command-line interface to use over SSH in lieu of cURL.

## Inferno.TemperatureLogger

A tool I made for gathering telemetry. It polls the local instance of Inferno.Api and outputs temperature readings in a CSV format. I run it as a background task, piping the output to a file for later analysis in Excel.

## Hardware
![Raspberry Pi and components](Hardware/Images/Inferno_bb.png)
