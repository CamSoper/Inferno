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

    var services = new[] { "api", "mqtt", "cli" };
    var projectMap = new Dictionary<string, string>
    {
        ["api"] = "Inferno.Api",
        ["mqtt"] = "Inferno.Mqtt",
        ["cli"] = "Inferno.Cli",
    };

    // Ensure publish directories exist so FileArchive doesn't fail during preview
    foreach (var svc in services)
    {
        Directory.CreateDirectory(Path.Combine("..", "publish", svc));
    }

    // Step 0: Ensure .NET 10 runtime is installed on the Pi
    var installDotnet = new RemoteCommand("install-dotnet", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create = "if ! command -v dotnet &> /dev/null || ! dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 10.'; then curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --runtime aspnetcore; fi && echo \"dotnet: $(dotnet --version 2>/dev/null || echo 'not found')\"",
    });

    // Step 1: Publish all projects locally (sequential to avoid shared-project file locking)
    var publishCommands = services.Select(svc =>
        $"dotnet publish ../{projectMap[svc]} -c Release -o ../publish/{svc}");
    var publishAll = new LocalCommand("publish-all", new Pulumi.Command.Local.CommandArgs
    {
        Create = string.Join(" && ", publishCommands),
        Triggers = new[]
        {
            // Re-publish when this stack is updated
            DateTime.UtcNow.ToString("o"),
        },
    });

    // Step 2: Stop services before deploying
    var stopServices = new RemoteCommand("stop-services", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create = "sudo systemctl stop inferno-api inferno-mqtt inferno-cli 2>/dev/null || true",
        Triggers = new[] { DateTime.UtcNow.ToString("o") },
    }, new CustomResourceOptions
    {
        DependsOn = { publishAll, installDotnet },
    });

    // Step 3: Copy published artifacts to the Pi
    var copyOps = new Dictionary<string, CopyToRemote>();
    foreach (var svc in services)
    {
        copyOps[svc] = new CopyToRemote($"copy-{svc}", new CopyToRemoteArgs
        {
            Connection = conn,
            Source = new FileArchive($"../publish/{svc}"),
            RemotePath = $"{remotePath}/{svc}",
        }, new CustomResourceOptions
        {
            DependsOn = { stopServices },
        });
    }

    // Step 4: Ensure systemd services exist and start them
    var serviceTemplate = @"
[Unit]
Description=Inferno {0} Service
After=network.target

[Service]
Environment=DOTNET_ROOT=/home/{4}/.dotnet
Environment=PATH=/home/{4}/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
WorkingDirectory={1}/{2}
ExecStart=/home/{4}/.dotnet/dotnet {1}/{2}/{3}.dll
Restart=always
RestartSec=5
User={4}

[Install]
WantedBy=multi-user.target
";

    var setupCommands = new List<string>();
    foreach (var svc in services)
    {
        var project = projectMap[svc];
        var unitFile = string.Format(serviceTemplate, svc.ToUpper(), remotePath, svc, project, piUser);
        var escapedUnit = unitFile.Replace("'", "'\\''");
        setupCommands.Add($"echo '{escapedUnit}' | sudo tee /etc/systemd/system/inferno-{svc}.service > /dev/null");
    }
    setupCommands.Add("sudo systemctl daemon-reload");
    foreach (var svc in services)
    {
        setupCommands.Add($"sudo systemctl enable --now inferno-{svc}");
    }

    var startServices = new RemoteCommand("start-services", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create = string.Join(" && ", setupCommands),
        Triggers = new[] { DateTime.UtcNow.ToString("o") },
    }, new CustomResourceOptions
    {
        DependsOn = copyOps.Values.Cast<Resource>().ToList(),
    });

    return new Dictionary<string, object?>
    {
        ["piHost"] = piHost,
        ["remotePath"] = remotePath,
        ["services"] = services,
    };
});
