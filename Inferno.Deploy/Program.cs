// HOST PREREQUISITE: the deploy user (pi) must have passwordless sudo, because
// setup-services / restart-* run `sudo` over a non-interactive SSH session (no
// TTY to type a password into). On a fresh Pi, set it up once:
//
//   echo 'pi ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/010_pi-nopasswd
//   sudo chmod 440 /etc/sudoers.d/010_pi-nopasswd
//
// Without it, setup-services fails with "sudo: a password is required".
//
// The deploy also enables the I2C + SPI interfaces (see enable-interfaces), but
// a fresh Pi needs ONE reboot after the first deploy for the /dev nodes to
// appear before the API can talk to the hardware.
using System.Security.Cryptography;
using Pulumi;
using Pulumi.Command.Remote;
using Pulumi.Command.Remote.Inputs;
using Pulumi.Command.Local;
using LocalCommand = Pulumi.Command.Local.Command;
using RemoteCommand = Pulumi.Command.Remote.Command;

return await Deployment.RunAsync(() =>
{
    var config = new Config();
    var piHost = config.Get("piHost") ?? "inferno";
    var piUser = config.Get("piUser") ?? "pi";
    var remotePath = config.Get("remotePath") ?? "~/inferno";
    var privateKeyPath = config.Get("privateKeyPath") ?? "~/.ssh/id_rsa";
    var mqttBrokerAddress = config.Get("mqttBrokerAddress") ?? "localhost";
    var mqttUsername = config.Get("mqttUsername") ?? "";
    var mqttPassword = config.Get("mqttPassword") ?? "";
    // Bump this (config set deployToken <anything-new>) to force a full
    // reprovision when the host is rebuilt under the same name.
    var deployToken = config.Get("deployToken") ?? "";

    // Identity of the deploy target. Pulumi's command resources can't observe
    // remote drift -- they only know whether the command ran once. So we make
    // every remote operation depend on this value: change the host/user (a new
    // Pi) or bump deployToken (a rebuilt Pi) and all provisioning re-runs,
    // converging the target instead of silently no-op'ing.
    var targetId = $"{piUser}@{piHost}|{deployToken}";

    var expandedKeyPath = privateKeyPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    if (!File.Exists(expandedKeyPath))
    {
        Pulumi.Log.Warn($"SSH private key not found at '{expandedKeyPath}'. Deployment will fail unless the key is provided.");
    }
    var privateKey = Output.Create(File.Exists(expandedKeyPath) ? File.ReadAllText(expandedKeyPath) : "");

    var conn = new ConnectionArgs
    {
        Host = piHost,
        User = piUser,
        PrivateKey = privateKey,
    };

    // Everything we publish + copy to the Pi. cli is the interactive client --
    // its binary lands on the Pi so it can be run by hand, but it is NOT run as
    // a service (as a daemon it would just print usage and exit in a restart loop).
    var services = new[] { "api", "mqtt", "cli" };
    // Subset that runs as long-lived systemd services.
    var serviceUnits = new[] { "api", "mqtt" };
    var projectMap = new Dictionary<string, string>
    {
        ["api"] = "Inferno.Api",
        ["mqtt"] = "Inferno.Mqtt",
        ["cli"] = "Inferno.Cli",
    };

    // Each service depends on its own project + Common
    var serviceDeps = new Dictionary<string, string[]>
    {
        ["api"] = ["Inferno.Api", "Inferno.Common"],
        ["mqtt"] = ["Inferno.Mqtt", "Inferno.Common"],
        ["cli"] = ["Inferno.Cli", "Inferno.Common"],
    };

    // Ensure publish directories exist so FileArchive doesn't fail during preview
    foreach (var svc in services)
    {
        Directory.CreateDirectory(Path.Combine("..", "publish", svc));
    }

    // Step 0a: Let sshd accept the env vars the Pulumi command provider passes
    // (PULUMI_COMMAND_STDOUT/STDERR). Without this, every remote command logs an
    // "Unable to set 'PULUMI_COMMAND_STDERR'" warning. AcceptEnv is additive, so
    // a drop-in is safe even though the base config already lists one of them.
    var configureSshEnv = new RemoteCommand("configure-ssh-env", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create =
            "echo 'AcceptEnv PULUMI_COMMAND_STDOUT PULUMI_COMMAND_STDERR' | " +
            "sudo tee /etc/ssh/sshd_config.d/50-pulumi-acceptenv.conf > /dev/null && " +
            "sudo systemctl reload ssh && echo 'sshd AcceptEnv configured'",
        Triggers = new[] { targetId },
    });

    // Step 0: Ensure .NET 8 ASP.NET Core runtime is installed on the Pi.
    // The runtime-only install lands in ~/.dotnet (not on PATH and no SDK), so
    // probe that path directly -- checking `command -v dotnet` would always miss
    // and re-trigger the installer, and `dotnet --version` needs an SDK.
    var installDotnet = new RemoteCommand("install-dotnet", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create =
            "DOTNET=$HOME/.dotnet/dotnet; " +
            "if ! \"$DOTNET\" --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 8\\.'; then " +
            "curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --runtime aspnetcore; fi && " +
            "echo \"dotnet runtimes: $(\"$DOTNET\" --list-runtimes 2>/dev/null | tr '\\n' ';' || echo 'not found')\"",
        // Re-run when targeting a new/rebuilt host.
        Triggers = new[] { targetId },
    }, new CustomResourceOptions { DependsOn = { configureSshEnv } });

    // Step 0b: Enable the I2C + SPI kernel interfaces the API needs (MCP3008 ADC
    // over SPI, LCD over I2C). Idempotent; raspi-config get_* returns 0 when
    // enabled. A fresh Pi ships with these off and needs ONE reboot after first
    // enable for /dev/i2c-1 and /dev/spidev0.0 to appear -- the command flags that.
    var enableInterfaces = new RemoteCommand("enable-interfaces", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create =
            "sudo raspi-config nonint do_i2c 0 && sudo raspi-config nonint do_spi 0 && " +
            "if [ ! -e /dev/i2c-1 ] || [ ! -e /dev/spidev0.0 ]; then " +
            "echo 'i2c/spi enabled -- REBOOT REQUIRED for device nodes to appear'; " +
            "else echo 'i2c/spi enabled and device nodes present'; fi",
        Triggers = new[] { targetId },
    }, new CustomResourceOptions { DependsOn = { configureSshEnv } });

    // Resolve ~ to absolute path (CopyToRemote and systemd don't expand ~)
    var absoluteRemotePath = remotePath.Replace("~", $"/home/{piUser}");

    // Step 1: Publish each project independently, triggered by its own source hash
    var publishOps = new Dictionary<string, LocalCommand>();
    foreach (var svc in services)
    {
        var hash = SourceHash.Compute("..", serviceDeps[svc]);
        publishOps[svc] = new LocalCommand($"publish-{svc}", new Pulumi.Command.Local.CommandArgs
        {
            Create = $"dotnet publish ../{projectMap[svc]} -c Release -o ../publish/{svc}",
            Triggers = new[] { hash },
        });
    }

    // Step 2: Copy published artifacts to the Pi (Pulumi diffs the file archive)
    var copyOps = new Dictionary<string, CopyToRemote>();
    foreach (var svc in services)
    {
        copyOps[svc] = new CopyToRemote($"copy-{svc}", new CopyToRemoteArgs
        {
            Connection = conn,
            Source = new FileArchive($"../publish/{svc}"),
            RemotePath = absoluteRemotePath,
            // CopyToRemote re-copies when the archive changes; also re-copy when
            // the target host changes so a rebuilt Pi gets the artifacts back.
            Triggers = new object[] { targetId },
        }, new CustomResourceOptions
        {
            DependsOn = { publishOps[svc], installDotnet, enableInterfaces },
        });
    }

    string BuildServiceUnit(string name, string workDir, string dllPath, string user, string? afterService = null, string extraEnv = "")
    {
        var after = afterService != null ? $"network.target {afterService}" : "network.target";
        return
            "[Unit]\n" +
            $"Description=Inferno {name} Service\n" +
            $"After={after}\n" +
            "\n" +
            "[Service]\n" +
            $"Environment=DOTNET_ROOT=/home/{user}/.dotnet\n" +
            $"Environment=PATH=/home/{user}/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\n" +
            extraEnv +
            $"WorkingDirectory={workDir}\n" +
            $"ExecStart=/home/{user}/.dotnet/dotnet {dllPath}\n" +
            "Restart=always\n" +
            "RestartSec=5\n" +
            $"User={user}\n" +
            "\n" +
            "[Install]\n" +
            "WantedBy=multi-user.target\n";
    }

    // Step 3: Install/update systemd unit files (runs once, updates when unit content changes)
    var unitFiles = new Dictionary<string, string>();
    foreach (var svc in serviceUnits)
    {
        var project = projectMap[svc];
        var workDir = $"{absoluteRemotePath}/{svc}";
        var dllPath = $"{workDir}/{project}.dll";

        string? afterService = null;
        var extraEnv = "";
        if (svc == "mqtt")
        {
            afterService = "inferno-api.service";
            extraEnv =
                $"Environment=MQTT_BROKER_ADDRESS={mqttBrokerAddress}\n" +
                $"Environment=MQTT_USERNAME={mqttUsername}\n" +
                $"Environment=MQTT_PASSWORD={mqttPassword}\n";
        }

        unitFiles[svc] = BuildServiceUnit(svc.ToUpper(), workDir, dllPath, piUser, afterService, extraEnv);
    }

    var installUnits = new List<string>();
    foreach (var svc in serviceUnits)
    {
        var escapedUnit = unitFiles[svc].Replace("'", "'\\''");
        installUnits.Add($"echo '{escapedUnit}' | sudo tee /etc/systemd/system/inferno-{svc}.service > /dev/null");
    }
    installUnits.Add("sudo systemctl daemon-reload");

    var setupServices = new RemoteCommand("setup-services", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create = string.Join(" && ", installUnits),
        // Re-run when unit file content changes or the target host changes.
        Triggers = unitFiles.Values.Append(targetId).ToArray(),
    }, new CustomResourceOptions { DependsOn = { configureSshEnv } });

    // Step 4: Restart each service independently when its files change
    foreach (var svc in serviceUnits)
    {
        new RemoteCommand($"restart-{svc}", new Pulumi.Command.Remote.CommandArgs
        {
            Connection = conn,
            // enable (boot persistence) + restart (start now / pick up new files).
            Create = $"sudo systemctl enable inferno-{svc} && sudo systemctl restart inferno-{svc}",
            // Restart when the copied artifacts change or the target host changes.
            Triggers = new Input<object>[]
            {
                copyOps[svc].Id.Apply(id => (object)id),
                targetId,
            },
        }, new CustomResourceOptions
        {
            DependsOn = { copyOps[svc], setupServices },
        });
    }

    return new Dictionary<string, object?>
    {
        ["piHost"] = piHost,
        ["remotePath"] = remotePath,
        ["services"] = services,
    };
});

static class SourceHash
{
    public static string Compute(string rootDir, string[] projectDirs)
    {
        var files = projectDirs
            .SelectMany(proj => Directory.EnumerateFiles(
                Path.Combine(rootDir, proj), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj"))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .OrderBy(f => f);

        using var sha = SHA256.Create();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
