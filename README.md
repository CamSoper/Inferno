# Inferno
Wood pellet smoker powered by .NET Core and Raspberry Pi 3.  

* See the [photo album](https://1drv.ms/u/s!Ag9fVAifJI6dsrwlhf-iGDwD4qkaxw?e=BbMc6f)!
* See it [featured on an episode of On .NET](https://www.youtube.com/watch?v=4kJGRuXZ4kg)! This is a short overview and discussion.
* See me [demo the smoker on the stream I co-host, The DotNet Docs](https://www.twitch.tv/videos/581536242)! This is a more in-depth review of some of the code along with a live demo.

## Inferno.Api

This is the core functionality. It exposes all functionality as a web API. I run it on http://localhost:5000 and control it via a variety of tools. The PID algorithm, fire minder, and display logic all reside here.

## Inferno.Bot

A Skype bot for controlling the smoker. Designed for WAF. Built with Microsoft Bot Framework and hosted on Azure.

## Inferno.Cli

A command-line interface for the API.

## Inferno.Common

Class library shared between projects.

## Inferno.RelayListener

Service that makes an outgoing connection to an Azure Relay Hybrid Connection and relays requests from the Bot to the API from the cloud.

## Inferno.TemperatureLogger

A tool I made for gathering telemetry. It polls the local instance of Inferno.Api and outputs temperature readings in a CSV format. I run it as a background task, piping the output to a file for later analysis in Excel.

## Hardware

The grill itself is a Traeger Junior Elite 20. The custom hardware is comprised of a Raspberry Pi 3, some relays for controlling the 110V circuits, a 20x4 LCD display, a MCP3008 analog to digital converter.
 
![Raspberry Pi and components](Hardware/Images/Inferno_bb.png)
