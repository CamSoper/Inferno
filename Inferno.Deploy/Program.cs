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
    var privateKey = File.Exists(expandedKeyPath)
        ? Output.Create(File.ReadAllText(expandedKeyPath))
        : Output.Create("");

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

    // Step 1: Publish all projects locally
    var publishOutputs = new Dictionary<string, LocalCommand>();
    foreach (var svc in services)
    {
        var project = projectMap[svc];
        publishOutputs[svc] = new LocalCommand($"publish-{svc}", new Pulumi.Command.Local.CommandArgs
        {
            Create = $"dotnet publish ../{project} -c Release -o ../publish/{svc}",
            Triggers = new[]
            {
                // Re-publish when this stack is updated
                DateTime.UtcNow.ToString("o"),
            },
        });
    }

    // Step 2: Stop services before deploying
    var stopServices = new RemoteCommand("stop-services", new Pulumi.Command.Remote.CommandArgs
    {
        Connection = conn,
        Create = "sudo systemctl stop inferno-api inferno-mqtt inferno-cli 2>/dev/null || true",
        Triggers = new[] { DateTime.UtcNow.ToString("o") },
    }, new CustomResourceOptions
    {
        DependsOn = publishOutputs.Values.Cast<Resource>().ToList(),
    });

    // Step 3: Copy published artifacts to the Pi
    var copyOps = new Dictionary<string, CopyToRemote>();
    foreach (var svc in services)
    {
        var svcName = svc; // capture for closure
        copyOps[svc] = new CopyToRemote($"copy-{svc}", new CopyToRemoteArgs
        {
            Connection = conn,
            Source = publishOutputs[svcName].Stdout.Apply(_ => (AssetOrArchive)new FileArchive($"../publish/{svcName}")),
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
WorkingDirectory={1}/{2}
ExecStart={1}/{2}/{3}
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
