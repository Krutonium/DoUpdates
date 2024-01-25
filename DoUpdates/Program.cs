using System;
using System.Diagnostics;
using System.Net;
using Newtonsoft.Json;

namespace DoUpdates
{
    internal class Program
    {
        public static readonly string FlakePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NixOS", "flake.nix");
        public static readonly string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NixOS", "deploy.json");
        public static readonly string localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NixOS");
        public static Dictionary<string, string> OnlineHosts = new Dictionary<string, string>();
        static void Main(string[] args)
        {
            //Read the config file after checking that it exists
            if (!File.Exists(configPath))
            {
                LogOutput("Didn't find deploy.json in your ~/NixOS/ directory. Please edit the new one.", LogType.Error);
                Config c = new Config();
                c.Remotes = new List<Remote>();
                c.Remotes.Add(new Remote()
                {
                    Name = "remotehost",
                    Ip = "10.1",
                    SwitchOrBoot = "switch"
                });
                c.UpdateFlake = true;
                File.WriteAllText(configPath, JsonConvert.SerializeObject(c, Formatting.Indented));
                Environment.Exit(1);
            }
            var config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
            
            //Check that the remote IPs is valid and online
            //`nix store ping --store ssh://10.3` to check that the remote is online
            //Kill the process after 2 seconds
            foreach (var remote in config.Remotes)
            {
                if (remote.Deploy == false || remote.Ip == "null")
                {
                    continue;
                }
                var ping = new Process();
                ping.StartInfo.FileName = "nix";
                ping.StartInfo.Arguments = $"store ping --store ssh://{remote.Ip}";
                ping.StartInfo.UseShellExecute = false;
                ping.StartInfo.RedirectStandardOutput = true;
                ping.StartInfo.RedirectStandardError = true;
                ping.Start();
                ping.WaitForExit(2000);
                if (ping.ExitCode != 0)
                {
                    LogOutput($"Failed to ping {remote.Ip}.", LogType.Warning);
                    LogOutput(ping.StandardError.ReadToEnd(), LogType.Warning);
                }
                LogOutput($"Successfully pinged {remote.Ip}.", LogType.Info);
                //Add it to the list of hosts that are online
                OnlineHosts.Add(remote.Name, remote.Ip);
            }
            
            if (!File.Exists(FlakePath))
            {
                LogOutput("Didn't find flake.nix in your ~/NixOS/ directory. Please run this from a NixOS machine.", LogType.Error);
                Environment.Exit(1);
            }
            var flake = File.ReadAllLines(FlakePath);
            var configNames = new List<string>();
            foreach (var line in flake)
            {
                if (line.Contains("nixosConfigurations."))
                {
                    var name = line.Split(".")[1].Split("=")[0].Trim();
                    configNames.Add(name);
                }
            }
            
            foreach (var name in configNames)
            {
                if (OnlineHosts.ContainsKey(name))
                {
                    LogOutput($"Online:  {name}", LogType.Info);
                }
                else
                {
                    //Check if the device is listed in the config
                    if (config.Remotes.Find(x => x.Name == name) != null)
                    {
                        LogOutput($"Offline: {name}", LogType.Info);
                        continue;
                    }
                    else
                    {
                        LogOutput($"Not Listed in Config: {name}", LogType.Info);
                    }
                   
                }
            }
            //Update Flake if older than 12 hours
            if (config.UpdateFlake && File.GetLastWriteTime(FlakePath) < DateTime.Now.AddHours(-12))
            {
                LogOutput("Updating flake...", LogType.Info);
                var update = new Process();
                update.StartInfo.FileName = "nupdate";
                update.StartInfo.WorkingDirectory = localPath;
                update.StartInfo.UseShellExecute = false;
                update.Start();
                update.WaitForExit();
                if (update.ExitCode != 0)
                {
                    LogOutput("Failed to update flake.", LogType.Error);
                    LogOutput(update.StandardError.ReadToEnd(), LogType.Error);
                    Environment.Exit(1);
                }
                Console.WriteLine("Successfully updated flake.");
            }
            
            
            //Build each one to a folder with a name based on the config name, but only if it's online
            //nixos-rebuild switch --flake .#uGamingPC --target-host root@ip

            foreach (var name in configNames)
            {
                if (!OnlineHosts.ContainsKey(name))
                {
                    //Device isn't online or available
                    LogOutput("Skipping {name} as it is not available.", LogType.Error);
                    continue;
                }

                Remote device = config.Remotes.Find(x => x.Name == name);
                LogOutput($"Building {device.Name}", LogType.Info);
                Console.Title = $"Building {device.Name}";
                var builder = new ProcessStartInfo();
                builder.FileName = "nixos-rebuild";
                builder.WorkingDirectory = localPath;
                builder.Arguments = $"{device.SwitchOrBoot} --flake {localPath}#{device.Name} --target-host {config.Username}@{device.Ip}";
                builder.UseShellExecute = false;
                Process buildRunner = new Process();
                buildRunner.StartInfo = builder;
                buildRunner.Start();
                buildRunner.WaitForExit();
                if (buildRunner.ExitCode != 0)
                {
                    LogOutput($"Build or Deploy Failure on {device.Name}", LogType.Error);
                }
            }   
            LogOutput("Deploy Complete", LogType.Info);
        }

        public static void LogOutput(string Output, LogType logType)
        {
            string toWrite = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoUpdates.log");
            switch (logType)
            {
                case LogType.Error:
                {
                    string toLog = $"{DateTime.Now} |  ERROR  | {Output}";
                    Console.WriteLine(toLog, ConsoleColor.Red);
                    File.AppendAllText(toWrite, toLog + Environment.NewLine);
                    break;
                }
                case LogType.Warning:
                {
                    string toLog = $"{DateTime.Now} | WARNING | {Output}";
                    Console.WriteLine(toLog, ConsoleColor.Magenta);
                    File.AppendAllText(toWrite, toLog + Environment.NewLine);
                    break;
                }
                case LogType.Info:
                {
                    string toLog = $"{DateTime.Now} |  INFO   | {Output}";
                    Console.WriteLine(toLog, ConsoleColor.Gray);
                    File.AppendAllText(toWrite, toLog + Environment.NewLine);
                    break;
                }
            }
            Console.ResetColor();
        }

        public enum LogType
        {
            Info,
            Warning,
            Error
        }
    }

    internal class Config
    {
        //Settings for each remote machine; Name as it is seen in the flake + IP
        public List<Remote> Remotes { get; set; } = new List<Remote>();
        public bool UpdateFlake { get; set; } = true;
        public string Username { get; set; } = "root";
    }

    internal class Remote
    {
        public string Name { get; set; } = "Default";
        public string Ip { get; set; } = "255.255.255.255";
        public string SwitchOrBoot { get; set; } = "switch";
        public bool Deploy { get; set; } = false;
    }
}