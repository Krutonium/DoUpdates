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
                Console.WriteLine("Didn't find deploy.json in your ~/NixOS/ directory. Please edit the new one.");
                Config c = new Config();
                c.Remotes = new List<Remote>();
                c.Remotes.Add(new Remote()
                {
                    Name = "remotehost",
                    IP = "10.1",
                    SwitchOrBoot = "switch"
                });
                c.updateFlake = true;
                File.WriteAllText(configPath, JsonConvert.SerializeObject(c, Formatting.Indented));
                Environment.Exit(1);
            }
            var config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
            
            //Check that the remote IPs is valid and online
            //`nix store ping --store ssh://10.3` to check that the remote is online
            //Kill the process after 2 seconds
            foreach (var remote in config.Remotes)
            {
                if (remote.Deploy == false || remote.IP == "null")
                {
                    continue;
                }
                var ping = new Process();
                ping.StartInfo.FileName = "nix";
                ping.StartInfo.Arguments = $"store ping --store ssh://{remote.IP}";
                ping.StartInfo.UseShellExecute = false;
                ping.StartInfo.RedirectStandardOutput = true;
                ping.StartInfo.RedirectStandardError = true;
                ping.Start();
                ping.WaitForExit(2000);
                if (ping.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to ping {remote.IP}.");
                    Console.WriteLine(ping.StandardError.ReadToEnd());
                    Environment.Exit(1);
                }
                Console.WriteLine($"Successfully pinged {remote.IP}.");
                //Add it to the list of hosts that are online
                OnlineHosts.Add(remote.Name, remote.IP);
            }
            


            if (!File.Exists(FlakePath))
            {
                Console.WriteLine("Didn't find flake.nix in your ~/NixOS/ directory. Please run this from a NixOS machine.");
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
                    Console.WriteLine($"Online:  {name}");
                }
                else
                {
                    //Check if the device is listed in the config
                    if (config.Remotes.Find(x => x.Name == name) != null)
                    {
                        Console.WriteLine($"Offline: {name}");
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"Not Listed in Config: {name}");
                    }
                   
                }
            }
            //Update Flake
            if (config.updateFlake)
            {
                Console.WriteLine("Updating flake...");
                var update = new Process();
                update.StartInfo.FileName = "nupdate";
                update.StartInfo.WorkingDirectory = localPath;
                update.StartInfo.UseShellExecute = false;
                update.Start();
                update.WaitForExit();
                if (update.ExitCode != 0)
                {
                    Console.WriteLine("Failed to update flake.");
                    Console.WriteLine(update.StandardError.ReadToEnd());
                    Environment.Exit(1);
                }
                Console.WriteLine("Successfully updated flake.");
            }
            
            
            //Build each one to a folder with a name based on the config name, but only if it's online
            //nix build -L .#nixosConfigurations.remotehost.config.system.build.toplevel
            foreach (var name in configNames)
            {
                if (!OnlineHosts.ContainsKey(name))
                {
                    Console.WriteLine($"Skipping {name} because it's offline or not configured.");
                    continue;
                }
                // Get the IP from the config
                string ip = config.Remotes.Find(x => x.Name == name).IP;
                
                Console.WriteLine($"Building {name}...");
                var build = new Process();
                build.StartInfo.FileName = "nix";
                build.StartInfo.WorkingDirectory = localPath;
                build.StartInfo.Arguments = $"build -L .#nixosConfigurations.{name}.config.system.build.toplevel -o {name}";
                build.StartInfo.UseShellExecute = false;
                build.Start();
                build.WaitForExit();
                if (build.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to build {name}.");
                    Console.WriteLine(build.StandardError.ReadToEnd());
                    Environment.Exit(1);
                }
                Console.WriteLine($"Successfully built {name}.");
                //At this point, we have a folder with the name of the config
                //We need to copy it to the remote machine
                
                //nix copy --to root@remotehost ./result
                
                var copy = new Process();
                copy.StartInfo.FileName = "nix";
                copy.StartInfo.Arguments = $"copy --to {ip} .#nixosConfigurations.{name}.config.system.build.toplevel";
                copy.StartInfo.WorkingDirectory = localPath;
                copy.StartInfo.UseShellExecute = false;
                copy.Start();
                
                copy.WaitForExit();
                if (copy.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to copy {name}.");
                    Console.WriteLine(copy.StandardError.ReadToEnd());
                    Environment.Exit(1);
                }
                Console.WriteLine($"Successfully copied {name}.");
                //Remove local build
                File.Delete($"./{name}");
                //ssh into the remote machine as root and run `nswitch or nboot`
                
                var ssh = new Process();
                ssh.StartInfo.FileName = "ssh";
                if(config.Remotes.Find(x => x.Name == name).SwitchOrBoot == "switch")
                {
                    ssh.StartInfo.Arguments = $"root@{ip} nswitch";
                }
                else
                {
                    ssh.StartInfo.Arguments = $"root@{ip} nboot";
                }
                ssh.StartInfo.Arguments = $"root@{ip} ";
                ssh.StartInfo.UseShellExecute = false;
                ssh.Start();
                ssh.WaitForExit();
                if (ssh.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to switch to {name}.");
                    Console.WriteLine(ssh.StandardError.ReadToEnd());
                    Environment.Exit(1);
                }
                Console.WriteLine($"Successfully switched on {name}. Deploy Complete");
            }

            Console.WriteLine("Deploy Complete");
        }
    }

    internal class Config
    {
        //Settings for each remote machine; Name as it is seen in the flake + IP
        public List<Remote> Remotes { get; set; }
        public bool updateFlake { get; set; }
    }
    internal class Remote
    {
        public string Name { get; set; }
        public string IP { get; set; }
        public string SwitchOrBoot { get; set; }
        public bool Deploy { get; set; } = false;
    }
}