using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

/*
    1.1.8:
    Added API - bool ResetRules(string key)
    Added flag SamSitesIgnorePlayers
    Fixed issues where players would not be detected while on mounts, allowing certain flags and rules to be bypassed

    1.1.7:
    Fixed NoTurretDamagePlayer
    TurretsIgnorePlayers now excludes turrets using instruments
    TruePVE now negates blocked damage entirely
        Use CanEntityTakeDamage(BaseEntity entity, HitInfo info) to bypass this
    Incompatibility with DynamicPVP is still being looked at

    1.1.5:
    Fix for TurretsIgnorePlayers still ignoring npcs
    Incompatibility with DynamicPVP is still being looked at

    1.1.4:
    Added MiniCopterIsImmuneToCollision flag
    Added null checks to ClockUpdate to prevent NRE, however this does not solve the issue
    Changed the behavior of TurretsIgnorePlayers to only ignore players, and not npcs. Use TurretsIgnoreScientist to have turrets ignore all npcs
    Increased the prune layer by 15 meters
    Increased the minimum distance from monuments by 15 meters

    1.1.3:
    CanEntityBeTargeted hook now returns null if true
    CanEntityTrapTrigger hook now returns null if true
    Possible fix for TimerLoop ClockUpdate.NullReferenceException @Gbutome
    TurretsIgnorePlayers now checks for an excluded zone @ThePitereq

    1.1.2:    
    Added hook CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
    Added hook CanEntityBeTargeted(BasePlayer player, BaseEntity target)
    Evaluate rules when minicoper attacks building
    Added flag TurretsIgnoreScientist @Credits to RFC1920
    Added flag TrapsIgnoreScientist

    1.1.1:
    Fixed CanBeTargeted performance issue
    Fixed HasEmptyMapping.InvalidOperationException

    Credits to RFC1920 for the following:
        Update some old crufty logging calls (nivex callout)
        Add new flag, NoTurretDamageScientist. If added to ruleset, turrets will not kill scientist NPCs. Prior to this with the new turrets and turret checks, they weren't killable. To continue with that behavior, set the flag.
        Add /tpve_enable and tpve.enable to toggle enabling/disabling the plugin
        Add flag NoHeliDamageQuarry
        Fix CheckTurretInitiator function so that it only finds weapons in AutoTurrets 
        Add new ruleset flag, NoTurretDamagePlayer. Perhaps a better way will come soon.
        Ensure that valid rulesets don't get deleted
        Add bailout if incoming damage type is decay. Added first pass fix for AddOrUpdateMapping() clearing all but the default ruleset. Probably more work to be done.
*/

namespace Oxide.Plugins
{
    [Info("TruePVE", "RFC1920", "1.1.8")]
    [Description("Improvement of the default Rust PVE behavior")]
    // Thanks to the original author, ignignokt84.
    class TruePVE : RustPlugin
    {
        #region Variables
        static TruePVE Instance;

        // config/data container
        TruePVEData data = new TruePVEData();

        // ZoneManager plugin
        [PluginReference]
        Plugin ZoneManager;

        // LiteZone plugin (private)
        [PluginReference]
        Plugin LiteZones;

        // usage information string with formatting
        public string usageString;
        // valid commands
        enum Command { def, sched, trace, usage, enable };
        // valid configuration options
        public enum Option
        {
            handleDamage, // (true) enable TruePVE damage handling hooks
            useZones      // (true) use ZoneManager/LiteZones for zone-specific damage behavior (requires modification of ZoneManager.cs)
        };
        // default values array
        bool[] defaults = {
            true, // handleDamage
            true  // useZones
        };

        // flags for RuleSets
        [Flags]
        enum RuleFlags
        {
            None = 0,
            SuicideBlocked = 1,
            AuthorizedDamage = 1 << 1,
            NoHeliDamage = 1 << 2,
            HeliDamageLocked = 1 << 3,
            NoHeliDamagePlayer = 1 << 4,
            HumanNPCDamage = 1 << 5,
            LockedBoxesImmortal = 1 << 6,
            LockedDoorsImmortal = 1 << 7,
            AdminsHurtSleepers = 1 << 8,
            ProtectedSleepers = 1 << 9,
            TrapsIgnorePlayers = 1 << 10,
            TurretsIgnorePlayers = 1 << 11,
            CupboardOwnership = 1 << 12,
            SelfDamage = 1 << 13,
            TwigDamage = 1 << 14,
            NoTurretDamagePlayer = 1 << 15,
            NoHeliDamageQuarry = 1 << 16,
            NoTurretDamageScientist = 1 << 17,
            TurretsIgnoreScientist = 1 << 18,
            TrapsIgnoreScientist = 1 << 19,
            MiniCopterIsImmuneToCollision = 1 << 20,
            //HeliCanDamageTwig = 1 << 21,
            SamSitesIgnorePlayers = 1 << 22
        }

        // timer to check for schedule updates
        Timer scheduleUpdateTimer;
        // current ruleset
        RuleSet currentRuleSet;
        // current broadcast message
        string currentBroadcastMessage;
        // internal useZones flag
        bool useZones = false;
        // constant "any" string for rules
        const string Any = "any";
        // constant "allzones" string for mappings
        const string AllZones = "allzones";
        // flag to prevent certain things from happening before server initialized
        bool serverInitialized = false;
        // permission for mapping command
        string PermCanMap = "truepve.canmap";

        // trace flag
        bool trace = false;
        // tracefile name
        string traceFile = "ruletrace";
        // auto-disable trace after 300s (5m)
        float traceTimeout = 300f;
        // trace timeout timer
        Timer traceTimer;
        bool tpveEnabled = true;
        #endregion

        #region Lang
        // load default messages to Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Prefix", "<color=#FFA500>[ TruePVE ]</color>" },
                {"Enable", "TruePVE enable set to {0}" },

                {"Header_Usage", "---- TruePVE usage ----"},
                {"Cmd_Usage_def", "Loads default configuration and data"},
                {"Cmd_Usage_sched", "Enable or disable the schedule" },
                {"Cmd_Usage_prod", "Show the prefab name and type of the entity being looked at"},
                {"Cmd_Usage_map", "Create/remove a mapping entry" },
                {"Cmd_Usage_trace", "Toggle tracing on/off" },

                {"Warning_PveMode", "Server is set to PVE mode!  TruePVE is designed for PVP mode, and may cause unexpected behavior in PVE mode."},
                {"Warning_OldConfig", "Old config detected - moving to {0}" },
                {"Warning_NoRuleSet", "No RuleSet found for \"{0}\"" },
                {"Warning_DuplicateRuleSet", "Multiple RuleSets found for \"{0}\"" },

                {"Error_InvalidCommand", "Invalid command" },
                {"Error_InvalidParameter", "Invalid parameter: {0}"},
                {"Error_InvalidParamForCmd", "Invalid parameters for command \"{0}\""},
                {"Error_InvalidMapping", "Invalid mapping: {0} => {1}; Target must be a valid RuleSet or \"exclude\"" },
                {"Error_NoMappingToDelete", "Cannot delete mapping: \"{0}\" does not exist" },
                {"Error_NoPermission", "Cannot execute command: No permission"},
                {"Error_NoSuicide", "You are not allowed to commit suicide"},
                {"Error_NoEntityFound", "No entity found"},

                {"Notify_AvailOptions", "Available Options: {0}"},
                {"Notify_DefConfigLoad", "Loaded default configuration"},
                {"Notify_DefDataLoad", "Loaded default mapping data"},
                {"Notify_ProdResult", "Prod results: type={0}, prefab={1}"},
                {"Notify_SchedSetEnabled", "Schedule enabled" },
                {"Notify_SchedSetDisabled", "Schedule disabled" },
                {"Notify_InvalidSchedule", "Schedule is not valid" },
                {"Notify_MappingCreated", "Mapping created for \"{0}\" => \"{1}\"" },
                {"Notify_MappingUpdated", "Mapping for \"{0}\" changed from \"{1}\" to \"{2}\"" },
                {"Notify_MappingDeleted", "Mapping for \"{0}\" => \"{1}\" deleted" },
                {"Notify_TraceToggle", "Trace mode toggled {0}" },

                {"Format_EnableColor", "#00FFFF"}, // cyan
                {"Format_EnableSize", "12"},
                {"Format_NotifyColor", "#00FFFF"}, // cyan
                {"Format_NotifySize", "12"},
                {"Format_HeaderColor", "#FFA500"}, // orange
                {"Format_HeaderSize", "14"},
                {"Format_ErrorColor", "#FF0000"}, // red
                {"Format_ErrorSize", "12"},
            }, this);
        }

        // get message from Lang
        string GetMessage(string key, string userId = null) => lang.GetMessage(key, this, userId);
        #endregion

        #region Loading/Unloading
        // load things
        void Loaded()
        {
            Instance = this;
            LoadDefaultMessages();
            string baseCommand = "tpve";
            // register console commands automagically
            foreach (Command command in Enum.GetValues(typeof(Command)))
                cmd.AddConsoleCommand((baseCommand + "." + command.ToString()), this, "CommandDelegator");
            // register chat commands
            cmd.AddChatCommand(baseCommand + "_prod", this, "HandleProd");
            cmd.AddChatCommand(baseCommand + "_enable", this, "EnableToggle");
            cmd.AddChatCommand(baseCommand, this, "ChatCommandDelegator");
            // build usage string for console (without sizing)
            usageString = WrapColor("orange", GetMessage("Header_Usage")) + "\n" +
                          WrapColor("cyan", $"{baseCommand}.{Command.def.ToString()}") + $" - {GetMessage("Cmd_Usage_def")}{Environment.NewLine}" +
                          WrapColor("cyan", $"{baseCommand}.{Command.trace.ToString()}") + $" - {GetMessage("Cmd_Usage_trace")}{Environment.NewLine}" +
                          WrapColor("cyan", $"{baseCommand}.{Command.sched.ToString()} [enable|disable]") + $" - {GetMessage("Cmd_Usage_sched")}{Environment.NewLine}" +
                          WrapColor("cyan", $"/{baseCommand}_prod") + $" - {GetMessage("Cmd_Usage_prod")}{Environment.NewLine}" +
                          WrapColor("cyan", $"/{baseCommand} map") + $" - {GetMessage("Cmd_Usage_map")}";
            permission.RegisterPermission(PermCanMap, this);
        }

        // on unloaded
        void Unload()
        {
            if (scheduleUpdateTimer != null)
                scheduleUpdateTimer.Destroy();
            Instance = null;
        }

        // plugin loaded
        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "ZoneManager")
                ZoneManager = plugin;
            if (plugin.Name == "LiteZones")
                LiteZones = plugin;
            if (!serverInitialized) return;
            if (ZoneManager != null || LiteZones != null)
                useZones = data.config[Option.useZones];
        }

        // plugin unloaded
        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == "ZoneManager")
                ZoneManager = null;
            if (plugin.Name == "LiteZones")
                LiteZones = null;
            if (!serverInitialized) return;
            if (ZoneManager == null && LiteZones == null)
                useZones = false;
            traceTimer?.Destroy();
        }

        // server initialized
        void OnServerInitialized()
        {
            // check for server pve setting
            if (ConVar.Server.pve)
                WarnPve();
            // load configuration
            LoadConfiguration();
            data.Init();
            currentRuleSet = data.GetDefaultRuleSet();
            if (currentRuleSet == null)
                PrintWarning(GetMessage("Warning_NoRuleSet"), data.defaultRuleSet);
            useZones = data.config[Option.useZones] && (LiteZones != null || ZoneManager != null);
            if (useZones && data.mappings.Count == 1 && data.mappings.First().Key.Equals(data.defaultRuleSet))
                useZones = false;
            if (data.schedule.enabled)
                TimerLoop(true);
            serverInitialized = true;
        }
        #endregion

        #region Command Handling
        // delegation method for console commands
        void CommandDelegator(ConsoleSystem.Arg arg)
        {
            // return if user doesn't have access to run console command
            if (!HasAccess(arg)) return;

            string cmd = arg.cmd.Name;
            if (!Enum.IsDefined(typeof(Command), cmd))
            {
                // shouldn't hit this
                SendMessage(arg, "Error_InvalidParameter");
            }
            else
            {
                switch ((Command)Enum.Parse(typeof(Command), cmd))
                {
                    case Command.def:
                        HandleDef(arg);
                        return;
                    case Command.sched:
                        HandleScheduleSet(arg);
                        return;
                    case Command.trace:
                        trace = !trace;
                        SendMessage(arg, "Notify_TraceToggle", new object[] { trace ? "on" : "off" });
                        if (trace)
                            traceTimer = timer.In(traceTimeout, () => trace = false);
                        else
                            traceTimer?.Destroy();
                        return;
                    case Command.enable:
                        tpveEnabled = !tpveEnabled;
                        SendMessage(arg, "Enable", new object[] { tpveEnabled.ToString() });
                        return;
                    case Command.usage:
                        ShowUsage(arg);
                        return;
                }
                SendMessage(arg, "Error_InvalidParamForCmd", new object[] { cmd });
            }
            ShowUsage(arg);
        }

        void EnableToggle(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
            {
                SendMessage(player, "Error_NoPermission");
            }

            tpveEnabled = !tpveEnabled;
            SendMessage(player, "Enable", new object[] { tpveEnabled.ToString() });
        }

        // handle setting defaults
        void HandleDef(ConsoleSystem.Arg arg)
        {
            LoadDefaultConfiguration();
            SendMessage(arg, "Notify_DefConfigLoad");
            LoadDefaultData();
            SendMessage(arg, "Notify_DefDataLoad");

            SaveData();
        }

        // handle prod command (raycast to determine what player is looking at)
        void HandleProd(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                SendMessage(player, "Error_NoPermission");

            object entity;
            if (!GetRaycastTarget(player, out entity) || entity == null)
            {
                SendReply(player, WrapSize(12, WrapColor("red", GetMessage("Error_NoEntityFound", player.UserIDString))));
                return;
            }
            SendMessage(player, "Notify_ProdResult", new object[] { entity.GetType(), (entity as BaseEntity).ShortPrefabName });
        }

        // delegation method for chat commands
        void ChatCommandDelegator(BasePlayer player, string command, string[] args)
        {
            if (!hasPermission(player, PermCanMap))
            {
                SendMessage(player, "Error_NoPermission");
                return;
            }

            // assume args[0] is the command (beyond /tpve)
            if (args != null && args.Length > 0)
                command = args[0];
            // shift arguments
            if (args != null)
            {
                if (args.Length > 1)
                    args = args.Skip(1).ToArray();
                else
                    args = new string[] { };
            }

            string message = "";
            object[] opts = new object[] { };

            if (command == null || command != "map")
            {
                message = "Error_InvalidCommand";
            }
            else if (args == null || args.Length == 0)
            {
                message = "Error_InvalidParamForCmd";
                opts = new object[] { command };
            }
            else
            {
                // args[0] should be mapping name
                // args[1] if exists should be target ruleset or "exclude"
                // if args[1] is empty, delete mapping
                string from = args[0];
                string to = null;
                if (args.Length == 2)
                    to = args[1];

                if (to != null && !data.ruleSets.Select(r => r.name).Contains(to) && to != "exclude")
                {
                    // target ruleset must exist, or be "exclude"
                    message = "Error_InvalidMapping";
                    opts = new object[] { from, to };
                }
                else
                {
                    bool dirty = false;
                    if (to != null)
                    {
                        dirty = true;
                        if (data.HasMapping(from))
                        {
                            // update existing mapping
                            string old = data.mappings[from];
                            data.mappings[from] = to;
                            message = "Notify_MappingUpdated";
                            opts = new object[] { from, old, to };
                        }
                        else
                        {
                            // add new mapping
                            data.mappings.Add(from, to);
                            message = "Notify_MappingCreated";
                            opts = new object[] { from, to };
                        }
                    }
                    else
                    {
                        if (data.HasMapping(from))
                        {
                            dirty = true;
                            // remove mapping
                            string old = data.mappings[from];
                            data.mappings.Remove(from);
                            message = "Notify_MappingDeleted";
                            opts = new object[] { from, old };
                        }
                        else
                        {
                            message = "Error_NoMappingToDelete";
                            opts = new object[] { from };
                        }
                    }

                    if (dirty)
                        // save changes to config file
                        SaveData();
                }
            }
            SendMessage(player, message, opts);
        }

        // handles schedule enable/disable
        void HandleScheduleSet(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Args == null || arg.Args.Length == 0)
            {
                SendMessage(arg, "Error_InvalidParamForCmd");
                return;
            }
            string message = "";
            if (!data.schedule.valid)
            {
                message = "Notify_InvalidSchedule";
            }
            else if (arg.Args[0] == "enable")
            {
                if (data.schedule.enabled) return;
                data.schedule.enabled = true;
                TimerLoop();
                message = "Notify_SchedSetEnabled";
            }
            else if (arg.Args[0] == "disable")
            {
                if (!data.schedule.enabled) return;
                data.schedule.enabled = false;
                if (scheduleUpdateTimer != null)
                    scheduleUpdateTimer.Destroy();
                message = "Notify_SchedSetDisabled";
            }
            object[] opts = new object[] { };
            if (message == "")
            {
                message = "Error_InvalidParameter";
                opts = new object[] { arg.Args[0] };
            }
            SendMessage(arg, message, opts);
        }
        #endregion

        #region Configuration/Data
        // load config
        void LoadConfiguration()
        {
            CheckVersion();
            Config.Settings.NullValueHandling = NullValueHandling.Include;
            bool dirty = false;
            try
            {
                data = Config.ReadObject<TruePVEData>() ?? null;
            }
            catch (Exception)
            {
                data = new TruePVEData();
            }
            if (data == null || data.schedule == null)
                LoadDefaultConfig();

            dirty |= CheckConfig();
            dirty |= CheckData();
            // check config version, update version to current version
            if (data.configVersion == null || !data.configVersion.Equals(Version.ToString()))
            {
                data.configVersion = Version.ToString();
                dirty |= true;
            }
            if (dirty)
                SaveData();
        }

        // save data
        void SaveData() => Config.WriteObject(data);

        // verify/update configuration
        bool CheckConfig()
        {
            bool dirty = false;
            foreach (Option option in Enum.GetValues(typeof(Option)))
                if (!data.config.ContainsKey(option))
                {
                    data.config[option] = defaults[(int)option];
                    dirty = true;
                }
            return dirty;
        }

        // check rulesets and groups
        bool CheckData()
        {
            bool dirty = false;
            if ((data.ruleSets == null || data.ruleSets.Count == 0) ||
                (data.groups == null || data.groups.Count == 0))
                dirty = LoadDefaultData();
            if (data.schedule == null)
            {
                data.schedule = new Schedule();
                dirty = true;
            }
            dirty |= CheckMappings();
            return dirty;
        }

        // rebuild mappings
        bool CheckMappings()
        {
            bool dirty = false;
            foreach (RuleSet rs in data.ruleSets)
            {
                if (!data.mappings.ContainsValue(rs.name))
                {
                    data.mappings[rs.name] = rs.name;
                    dirty = true;
                }
            }
            return dirty;
        }

        // default config creation
        protected override void LoadDefaultConfig()
        {
            data = new TruePVEData();
            data.configVersion = Version.ToString();
            LoadDefaultConfiguration();
            LoadDefaultData();
            SaveData();
        }

        void CheckVersion()
        {
            if (Config["configVersion"] == null) return;
            Version config = new Version(Config["configVersion"].ToString());
            if (config < new Version("0.7.0"))
            {
                string fname = Config.Filename.Replace(".json", ".old.json");
                Config.Save(fname);
                PrintWarning(string.Format(GetMessage("Warning_OldConfig"), fname));
                Config.Clear();
            }
        }

        // populates default configuration entries
        bool LoadDefaultConfiguration()
        {
            foreach (Option option in Enum.GetValues(typeof(Option)))
                data.config[option] = defaults[(int)option];
            return true;
        }

        // load default data to mappings, rulesets, and groups
        bool LoadDefaultData()
        {
            data.mappings.Clear();
            data.ruleSets.Clear();
            data.groups.Clear();
            data.schedule = new Schedule();
            data.defaultRuleSet = "default";

            // build groups first
            EntityGroup dispenser = new EntityGroup("dispensers");
            dispenser.Add(typeof(BaseCorpse).Name);
            dispenser.Add(typeof(HelicopterDebris).Name);
            data.groups.Add(dispenser);

            EntityGroup players = new EntityGroup("players");
            players.Add(typeof(BasePlayer).Name);
            data.groups.Add(players);

            EntityGroup traps = new EntityGroup("traps");
            traps.Add(typeof(AutoTurret).Name);
            traps.Add(typeof(BearTrap).Name);
            traps.Add(typeof(FlameTurret).Name);
            traps.Add(typeof(Landmine).Name);
            traps.Add(typeof(GunTrap).Name);
            traps.Add(typeof(ReactiveTarget).Name); // include targets with traps, since behavior is the same
            traps.Add("spikes.floor");
            data.groups.Add(traps);

            EntityGroup barricades = new EntityGroup("barricades");
            barricades.Add(typeof(Barricade).Name);
            data.groups.Add(barricades);

            EntityGroup highwalls = new EntityGroup("highwalls");
            highwalls.Add("wall.external.high.stone");
            highwalls.Add("wall.external.high.wood");
            highwalls.Add("gates.external.high.wood");
            highwalls.Add("gates.external.high.wood");
            data.groups.Add(highwalls);

            EntityGroup heli = new EntityGroup("heli");
            heli.Add(typeof(BaseHelicopter).Name);
            data.groups.Add(heli);

            EntityGroup npcs = new EntityGroup("npcs");
            npcs.Add(typeof(NPCPlayerApex).Name);
            npcs.Add(typeof(BradleyAPC).Name);
            data.groups.Add(npcs);

            EntityGroup fire = new EntityGroup("fire"); ;
            fire.Add(typeof(FireBall).Name);
            data.groups.Add(fire);

            EntityGroup resources = new EntityGroup("resources");
            resources.Add(typeof(ResourceEntity).Name);
            resources.Add(typeof(TreeEntity).Name);
            resources.Add(typeof(OreResourceEntity).Name);
            data.groups.Add(resources);

            // create default ruleset
            RuleSet defaultRuleSet = new RuleSet(data.defaultRuleSet);
            defaultRuleSet.flags = RuleFlags.HumanNPCDamage | RuleFlags.LockedBoxesImmortal | RuleFlags.LockedDoorsImmortal;

            // create rules and add to ruleset
            defaultRuleSet.AddRule("anything can hurt " + dispenser.name); // anything hurts dispensers
            defaultRuleSet.AddRule("anything can hurt " + players.name); // anything hurts players
            defaultRuleSet.AddRule(players.name + " cannot hurt " + players.name); // players cannot hurt other players
            defaultRuleSet.AddRule("anything can hurt " + traps.name); // anything hurts traps
            defaultRuleSet.AddRule(traps.name + " cannot hurt " + players.name); // traps cannot hurt players
            defaultRuleSet.AddRule(players.name + " can hurt " + barricades.name); // players can hurt barricades
            defaultRuleSet.AddRule(barricades.name + " cannot hurt " + players.name); // barricades cannot hurt players
            defaultRuleSet.AddRule(highwalls.name + " cannot hurt " + players.name); // highwalls cannot hurt players
            defaultRuleSet.AddRule("anything can hurt " + heli.name); // anything can hurt heli
            defaultRuleSet.AddRule("anything can hurt " + npcs.name); // anything can hurt npcs
            defaultRuleSet.AddRule(fire.name + " cannot hurt " + players.name); // fire cannot hurt players
            defaultRuleSet.AddRule("anything can hurt " + resources.name); // anything can hurt resources (gather)

            data.ruleSets.Add(defaultRuleSet); // add ruleset to rulesets list

            data.mappings[data.defaultRuleSet] = data.defaultRuleSet; // create mapping for ruleset

            return true;
        }

        bool ResetRules(string key)
        {
            if (!serverInitialized) return false;
            if (string.IsNullOrEmpty(key)) return false;
            string old = data.defaultRuleSet;

            data.defaultRuleSet = key;
            currentRuleSet = data.GetDefaultRuleSet();
            if (currentRuleSet == null)
            {
                data.defaultRuleSet = old;
                currentRuleSet = data.GetDefaultRuleSet();
                return false;
            }

            return true;
        }
        #endregion

        #region Hooks/Handler Procedures
        void OnPlayerConnected(BasePlayer player)
        {
            if (data.schedule.enabled && data.schedule.broadcast && currentBroadcastMessage != null)
                SendReply(player, GetMessage("Prefix") + currentBroadcastMessage);
        }

        // handle damage - if another mod must override TruePVE damages or take priority,
        // set handleDamage to false and reference HandleDamage from the other mod(s)
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            //if(entity == null || hitInfo == null || hitInfo.HitEntity == null) return null;
            if (hitInfo.damageTypes.Has(Rust.DamageType.Decay)) return null;
            if (!tpveEnabled) return null;
            if (!data.config[Option.handleDamage]) return null;
            //if (data.AllowKillingSleepers && entity is BasePlayer && entity.ToPlayer().IsSleeping()) return null;
            var handleDamage = HandleDamage(entity, hitInfo);
            if (handleDamage is bool)
            {
                hitInfo.damageTypes = new DamageTypeList();
            }
            return handleDamage;
        }

        // handle damage
        object HandleDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (!AllowDamage(entity, hitInfo))
                return true;
            return null;
        }

        // determines if an entity is "allowed" to take damage
        bool AllowDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null) return true;

            object extCanTakeDamage = Interface.CallHook("CanEntityTakeDamage", new object[] { entity, hitInfo });
            if (extCanTakeDamage != null && extCanTakeDamage is bool)
            {
                return (bool)extCanTakeDamage;
            }

            // if default global is not enabled, return true (allow all damage)
            if (currentRuleSet == null || currentRuleSet.IsEmpty() || !currentRuleSet.enabled)
                return true;

            // allow decay
            if (hitInfo.damageTypes.Get(DamageType.Decay) > 0)
                return true;

            // allow NPCs to take damage
            if (entity is BaseNpc)
                return true;

            // allow damage to door barricades and covers
            if (entity is Barricade && (entity.ShortPrefabName.Contains("door_barricade") || entity.ShortPrefabName.Contains("cover")))
                return true;

            // if entity is a barrel, trash can, or giftbox, allow damage (exclude water barrels)
            if (entity.ShortPrefabName.Contains("barrel") ||
               entity.ShortPrefabName.Equals("loot_trash") ||
               entity.ShortPrefabName.Equals("giftbox_loot"))
            {
                if (!entity.ShortPrefabName.Equals("waterbarrel"))
                {
                    return true;
                }
            }

            if (trace)
            {
                // Sometimes the initiator is not the attacker (turrets)
                try
                {
                    Trace("======================" + Environment.NewLine +
                      "==  STARTING TRACE  ==" + Environment.NewLine +
                      "==  " + DateTime.Now.ToString("HH:mm:ss.fffff") + "  ==" + Environment.NewLine +
                      "======================");
                    Trace($"From: {hitInfo.Initiator.GetType().Name}, {hitInfo.Initiator.ShortPrefabName}", 1);
                    Trace($"To: {entity.GetType().Name}, {entity.ShortPrefabName}", 1);
                }
                catch
                {
                    Trace("======================" + Environment.NewLine +
                      "==  STARTING TRACE  ==" + Environment.NewLine +
                      "==  " + DateTime.Now.ToString("HH:mm:ss.fffff") + "  ==" + Environment.NewLine +
                      "======================");
                    Trace($"From: {hitInfo.WeaponPrefab.GetType().Name}, {hitInfo.WeaponPrefab.ShortPrefabName}", 1);
                    Trace($"To: {entity.GetType().Name}, {entity.ShortPrefabName}", 1);
                }
            }

            // get entity and initiator locations (zones)
            List<string> entityLocations = GetLocationKeys(entity);
            List<string> initiatorLocations = GetLocationKeys(hitInfo.Initiator);
            // check for exclusion zones (zones with no rules mapped)
            if (CheckExclusion(entityLocations, initiatorLocations)) return true;

            if (trace) Trace("No exclusion found - looking up RuleSet...", 1);
            // process location rules
            RuleSet ruleSet = GetRuleSet(entityLocations, initiatorLocations);
            if (trace) Trace($"Using RuleSet \"{ruleSet.name}\"", 1);

            if (entity is MiniCopter && hitInfo.Initiator == entity && ruleSet.HasFlag(RuleFlags.MiniCopterIsImmuneToCollision))
            {
                if (trace) Trace("Minicopter had a collision; block and return", 1);
                return false;
            }

            // handle suicide
            if (hitInfo.damageTypes.Get(DamageType.Suicide) > 0)
            {
                if (trace) Trace($"DamageType is suicide; blocked? { (ruleSet.HasFlag(RuleFlags.SuicideBlocked) ? "true; block and return" : "false; continue processing") }", 1);
                if (ruleSet.HasFlag(RuleFlags.SuicideBlocked))
                {
                    SendMessage(entity as BasePlayer, "Error_NoSuicide");
                    return false;
                }
                return true;
            }

            // allow anything to hurt itself
            if (ruleSet.HasFlag(RuleFlags.SelfDamage) && (hitInfo.Initiator == entity))
                return true;

            // Check storage containers and doors for locks
            if ((entity is StorageContainer && ruleSet.HasFlag(RuleFlags.LockedBoxesImmortal)) ||
               (entity is Door && ruleSet.HasFlag(RuleFlags.LockedDoorsImmortal)))
            {
                // check for lock
                object hurt = CheckLock(ruleSet, entity, hitInfo);
                if (trace) Trace($"Door/StorageContainer detected with immortal flag; lock check results: { (hurt == null ? "null (no lock or unlocked); continue checks" : (bool)hurt ? "allow and return" : "block and return") }", 1);
                if (hurt != null)
                    return (bool)hurt;
            }

            // check heli and turret
            object heli = CheckHeliInitiator(ruleSet, hitInfo);
            if (heli != null)
            {
                /*if (entity is BuildingBlock && ruleSet.HasFlag(RuleFlags.HeliCanDamageTwig))
                {
                    var block = entity as BuildingBlock;

                    if (trace) Trace($"Initiator is heli, and target is twig; flag check results: { (ruleSet.HasFlag(RuleFlags.HeliCanDamageTwig) ? "flag set; allow and return" : "flag not set; block and return") }", 1);
                    return block.grade == BuildingGrade.Enum.Twigs;
                }*/
                if (entity is BasePlayer)
                {
                    if (trace) Trace($"Initiator is heli, and target is player; flag check results: { (ruleSet.HasFlag(RuleFlags.NoHeliDamagePlayer) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoHeliDamagePlayer);
                }
                if (entity is MiningQuarry)
                {
                    if (trace) Trace($"Initiator is heli, and target is quarry; flag check results: { (ruleSet.HasFlag(RuleFlags.NoHeliDamageQuarry) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoHeliDamageQuarry);
                }
                if (trace) Trace($"Initiator is heli, target is non-player; results: { ((bool)heli ? "allow and return" : "block and return") }", 1);
                return (bool)heli;
            }
            object turret = CheckTurretInitiator(ruleSet, hitInfo);
            if (turret != null)
            {
                if (entity is Scientist)
                {
                    if (trace) Trace($"Initiator is turret, and target is scientist; flag check results: { (ruleSet.HasFlag(RuleFlags.NoTurretDamageScientist) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoTurretDamageScientist);
                }
                if (entity is BasePlayer)
                {
                    if (trace) Trace($"Initiator is turret, and target is player; flag check results: { (ruleSet.HasFlag(RuleFlags.NoTurretDamagePlayer) ? "flag set; block and return" : "flag not set; allow and return") }", 1);
                    return !ruleSet.HasFlag(RuleFlags.NoTurretDamagePlayer);
                }
                if (trace) Trace($"Initiator is turret, target is non-player; results: { ((bool)turret ? "allow and return" : "block and return") }", 1);
                return (bool)turret;
            }
            // after heli and turret check, return true if initiator is null
            if (hitInfo.Initiator == null)
            {
                if (trace) Trace("Initiator empty; allow and return", 1);
                return true;
            }
            if (hitInfo.Initiator is MiniCopter && entity is BuildingBlock)
            {
                if (trace) Trace("Initiator is minicopter, target is building; evaluate and return", 1);
                return EvaluateRules(entity, hitInfo, ruleSet);
            }
            // check for sleeper protection - return false if sleeper protection is on (true)
            if (ruleSet.HasFlag(RuleFlags.ProtectedSleepers) && hitInfo.Initiator is BaseNpc && entity is BasePlayer && (entity as BasePlayer).IsSleeping())
            {
                if (trace) Trace("Target is sleeping player, with ProtectedSleepers flag set; block and return", 1);
                return false;
            }

            // allow NPC damage to other entities if sleeper protection is off
            if (hitInfo.Initiator is BaseNpc)
            {
                if (trace) Trace("Initiator is NPC animal; allow and return", 1);
                return true;
            }

            var player = GetPlayerFromHitInfo(hitInfo);
            // ignore checks if authorized damage enabled (except for players)
            if (ruleSet.HasFlag(RuleFlags.AuthorizedDamage) && !(entity is BasePlayer) && player.IsValid() && CheckAuthorized(entity, player, ruleSet))
            {
                if (entity is SamSite)
                {
                    if (trace) Trace("Target is SamSite; evaluate and return", 1);
                    return EvaluateRules(entity, hitInfo, ruleSet);
                }
                if (trace) Trace("Initiator is player with authorization over non-player target; allow and return", 1);
                return true;
            }

            // allow sleeper damage by admins if configured
            if (ruleSet.HasFlag(RuleFlags.AdminsHurtSleepers) && entity is BasePlayer && player.IsValid())
                if ((entity as BasePlayer).IsSleeping() && IsAdmin(player))
                {
                    if (trace) Trace("Initiator is admin player and target is sleeping player, with AdminsHurtSleepers flag set; allow and return", 1);
                    return true;
                }

            // allow Human NPC damage if configured
            if (ruleSet.HasFlag(RuleFlags.HumanNPCDamage) && entity is BasePlayer && player.IsValid())
                if (IsHumanNPC(entity as BasePlayer) || IsHumanNPC(player))
                {
                    if (trace) Trace("Initiator or target is HumanNPC, with HumanNPCDamage flag set; allow and return", 1);
                    return true;
                }

            if (trace) Trace("No match in pre-checks; evaluating RuleSet rules...", 1);
            return EvaluateRules(entity, hitInfo, ruleSet);
        }

        // process rules to determine whether to allow damage
        bool EvaluateRules(BaseEntity entity, BaseEntity attacker, RuleSet ruleSet)
        {
            List<string> e0Groups = data.ResolveEntityGroups(attacker);
            List<string> e1Groups = data.ResolveEntityGroups(entity);
            if (trace)
            {
                Trace($"Initator EntityGroup matches: { (e0Groups.Count == 0 ? "none" : string.Join(", ", e0Groups.ToArray())) }", 2);
                Trace($"Target EntityGroup matches: { (e1Groups.Count == 0 ? "none" : string.Join(", ", e1Groups.ToArray())) }", 2);
            }
            return ruleSet.Evaluate(e0Groups, e1Groups);
        }
        bool EvaluateRules(BaseEntity entity, HitInfo hitInfo, RuleSet ruleSet)
        {
            return EvaluateRules(entity, hitInfo.Initiator, ruleSet);
        }

        // checks for a lock
        object CheckLock(RuleSet ruleSet, BaseEntity entity, HitInfo hitInfo)
        {
            // exclude deployed items in storage container lock check (since they can't have locks)
            if (entity.ShortPrefabName.Equals("lantern.deployed") ||
               entity.ShortPrefabName.Equals("ceilinglight.deployed") ||
               entity.ShortPrefabName.Equals("furnace.large") ||
               entity.ShortPrefabName.Equals("campfire") ||
               entity.ShortPrefabName.Equals("furnace") ||
               entity.ShortPrefabName.Equals("refinery_small_deployed") ||
               entity.ShortPrefabName.Equals("waterbarrel") ||
               entity.ShortPrefabName.Equals("jackolantern.angry") ||
               entity.ShortPrefabName.Equals("jackolantern.happy") ||
               entity.ShortPrefabName.Equals("repairbench_deployed") ||
               entity.ShortPrefabName.Equals("researchtable_deployed") ||
               entity.ShortPrefabName.Contains("shutter"))
                return null;

            // if unlocked damage allowed - check for lock
            BaseLock alock = entity.GetSlot(BaseEntity.Slot.Lock) as BaseLock; // get lock
            if (alock == null) return null; // no lock, return null

            if (alock.IsLocked()) // is locked, cancel damage except heli
            {
                // if heliDamageLocked option is false or heliDamage is false, all damage is cancelled
                if (!ruleSet.HasFlag(RuleFlags.HeliDamageLocked) || ruleSet.HasFlag(RuleFlags.NoHeliDamage)) return false;
                object heli = CheckHeliInitiator(ruleSet, hitInfo);
                if (heli != null)
                    return (bool)heli;
                return false;
            }
            return null;
        }

        // check for heli
        object CheckHeliInitiator(RuleSet ruleSet, HitInfo hitInfo)
        {
            // Check for heli initiator
            if (hitInfo.Initiator is BaseHelicopter ||
               (hitInfo.Initiator != null && (
                   hitInfo.Initiator.ShortPrefabName.Equals("oilfireballsmall") ||
                   hitInfo.Initiator.ShortPrefabName.Equals("napalm"))))
                return !ruleSet.HasFlag(RuleFlags.NoHeliDamage);
            else if (hitInfo.WeaponPrefab != null) // prevent null spam
            {
                if (hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli") ||
                   hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli_napalm"))
                    return !ruleSet.HasFlag(RuleFlags.NoHeliDamage);
            }
            return null;
        }

        object CheckTurretInitiator(RuleSet ruleSet, HitInfo hitInfo)
        {
            // Check for turret initiator
            if (hitInfo.WeaponPrefab != null)
            {
                try
                {
                    var turret = hitInfo.WeaponPrefab.GetComponentInParent<AutoTurret>();

                    if (turret != null)
                    {
                        if (trace) Trace("Found WeaponPrefab in a turret", 2);
                        return !ruleSet.HasFlag(RuleFlags.NoTurretDamagePlayer);
                    }
                }
                catch { }
            }

            if (hitInfo.Weapon != null)
            {
                try
                {
                    var turret = hitInfo.Weapon.GetComponentInParent<AutoTurret>();

                    if (turret != null)
                    {
                        if (trace) Trace("Found Weapon in a turret", 2);
                        return !ruleSet.HasFlag(RuleFlags.NoTurretDamagePlayer);
                    }
                }
                catch { }
            }

            if (hitInfo.Initiator is AutoTurret)
            {
                if (trace) Trace("Initiator is a turret", 2);
                return !ruleSet.HasFlag(RuleFlags.NoTurretDamagePlayer);
            }

            return null;
        }

        // checks if the player is authorized to damage the entity
        bool CheckAuthorized(BaseEntity entity, BasePlayer player, RuleSet ruleSet)
        {
            // Allow twig damage by anyone if ruleset flag is set
            if (ruleSet.HasFlag(RuleFlags.TwigDamage))
            {
                try
                {
                    var block = entity as BuildingBlock;
                    if (block.grade == BuildingGrade.Enum.Twigs)
                    {
                        if (trace) Trace("Allowing twig destruction...");
                        return true;
                    }
                }
                catch { }
            }

            // check if the player is the owner of the entity
            if ((!ruleSet.HasFlag(RuleFlags.CupboardOwnership) && player.userID == entity.OwnerID) || entity.OwnerID == 0L)
                return true; // player is the owner or the owner is undefined, allow damage/looting

            // block if building blocked
            if (player.IsBuildingBlocked(entity.transform.position, entity.transform.rotation, entity.bounds))
                return false;

            // if not CupboardOwnership, check for build authorization
            if (!ruleSet.HasFlag(RuleFlags.CupboardOwnership))
                return player.IsBuildingAuthed(entity.transform.position, entity.transform.rotation, entity.bounds);

            // else, allow damage
            return true;
        }

        // handle player attacking an entity - specifically, checks resource dispensers
        // to determine whether to prevent gathering, based on rules
        object OnPlayerAttack(BasePlayer attacker, HitInfo hitInfo)
        {
            if (attacker == null || hitInfo == null || hitInfo.HitEntity == null) return null;
            if (hitInfo?.HitEntity is ResourceEntity)
            {
                if (!AllowDamage(hitInfo.HitEntity, hitInfo))
                    return false;
            }
            return null;
        }

        private BasePlayer GetPlayerFromHitInfo(HitInfo hitInfo)
        {
            var player = hitInfo?.Initiator as BasePlayer;

            if (!player.IsValid() && hitInfo?.Initiator is BaseMountable)
            {
                player = GetMountedPlayer(hitInfo.Initiator as BaseMountable);
            }

            return player;
        }

        private BasePlayer GetMountedPlayer(BaseMountable m)
        {
            if (m.GetMounted())
            {
                return m.GetMounted();
            }

            if (m is BaseVehicle)
            {
                var vehicle = m as BaseVehicle;

                foreach (var point in vehicle.mountPoints)
                {
                    if (point.mountable.IsValid() && point.mountable.GetMounted())
                    {
                        return point.mountable.GetMounted();
                    }
                }
            }

            return null;
        }

        object OnSamSiteTarget(SamSite samSite, BaseMountable m)
        {
            var player = GetMountedPlayer(m);

            if (player.IsValid())
            {
                object extCanEntityBeTargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { player, samSite });

                if (extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
                {
                    return null;
                }

                RuleSet ruleSet = GetRuleSet(player, samSite);

                if (ruleSet.HasFlag(RuleFlags.SamSitesIgnorePlayers))
                {
                    var entityLocations = GetLocationKeys(m);
                    var initiatorLocations = GetLocationKeys(samSite);
                    // check for exclusion zones (zones with no rules mapped)
                    if (CheckExclusion(entityLocations, initiatorLocations)) return null;
                    if (CheckExclusion(player, samSite)) return null;
                    return true;
                }
            }

            return null;
        }

        object CanBeTargeted(BaseMountable m, MonoBehaviour turret)
        {
            var player = GetMountedPlayer(m);

            return CanBeTargeted(player, turret);
        }

        // check if entity can be targeted
        object CanBeTargeted(BasePlayer target, MonoBehaviour turret)
        {
            //Puts($"CanBeTargeted called for {target.name}", 2);
            if (!serverInitialized || target == null || turret == null) return null;
            if (turret as HelicopterTurret)
            {
                //if (target.InSafeZone() && !target.IsHostile()) return false;
                return null;
            }
            object extCanEntityBeTargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { target, turret as BaseEntity });
            if (extCanEntityBeTargeted != null && extCanEntityBeTargeted is bool && (bool)extCanEntityBeTargeted)
            {
                return null;
            }
            RuleSet ruleSet = GetRuleSet(target, turret as BaseCombatEntity);
            if (target.IsNpc && ruleSet.HasFlag(RuleFlags.TurretsIgnoreScientist))
                return false;
            if (!target.IsNpc && ruleSet.HasFlag(RuleFlags.TurretsIgnorePlayers))
            {
                var weapon = (turret as AutoTurret)?.GetAttachedWeapon()?.GetItem();
                if (weapon != null && weapon.info.shortname.StartsWith("fun.")) return null;
                var entityLocations = GetLocationKeys(target);
                var initiatorLocations = GetLocationKeys(turret as BaseEntity);
                // check for exclusion zones (zones with no rules mapped)
                if (CheckExclusion(entityLocations, initiatorLocations)) return null;
                return false;
            }
            return null;
        }

        // ignore players stepping on traps if configured
        object OnTrapTrigger(BaseTrap trap, GameObject go)
        {
            BasePlayer player = go.GetComponent<BasePlayer>();
            if (player == null || trap == null) return null;
            object extCanEntityTrapTrigger = Interface.CallHook("CanEntityTrapTrigger", new object[] { trap, player });
            if (extCanEntityTrapTrigger != null && extCanEntityTrapTrigger is bool && (bool)extCanEntityTrapTrigger)
            {
                return null;
            }
            RuleSet ruleSet = GetRuleSet(trap, player);
            if (player.IsNpc && ruleSet.HasFlag(RuleFlags.TrapsIgnoreScientist))
                return false;
            if (ruleSet.HasFlag(RuleFlags.TrapsIgnorePlayers))
                return false;
            return null;
        }

        // Check exclusion for entities
        bool CheckExclusion(BaseEntity target, BaseEntity attacker)
        {
            if (!serverInitialized || !target || !attacker) return false;
            // check for exclusions in entity groups

            //var targetGroup = data.groups.FirstOrDefault(g => g.exclusions.Contains(target.ShortPrefabName, CompareOptions.OrdinalIgnoreCase));
            var attackerGroup = data.groups.FirstOrDefault(g => g.exclusions.Contains(attacker.ShortPrefabName, CompareOptions.OrdinalIgnoreCase));

            return attackerGroup != null;
        }

        RuleSet GetRuleSet(List<string> e0Locations, List<string> e1Locations)
        {
            RuleSet ruleSet = currentRuleSet;
            if (e0Locations?.Count > 0 && e1Locations?.Count > 0)
            {
                if (trace) Trace($"Beginning RuleSet lookup for [{ (e0Locations.Count == 0 ? "empty" : string.Join(", ", e0Locations.ToArray())) }] and [{ (e1Locations.Count == 0 ? "empty" : string.Join(", ", e1Locations.ToArray())) }]", 2);
                List<string> locations = GetSharedLocations(e0Locations, e1Locations);
                if (trace) Trace($"Shared locations: { (locations.Count == 0 ? "none" : string.Join(", ", locations.ToArray())) }", 3);
                if (locations != null && locations.Count > 0)
                {
                    List<string> names = locations.Select(s => data.mappings[s]).ToList();
                    List<RuleSet> sets = data.ruleSets.Where(r => names.Contains(r.name)).ToList();
                    if (trace) Trace($"Found {names.Count} location names, with {sets.Count} mapped RuleSets", 3);
                    if (sets.Count == 0 && data.mappings.ContainsKey(AllZones) && data.ruleSets.Any(r => r.name == data.mappings[AllZones]))
                    {
                        sets.Add(data.ruleSets.FirstOrDefault(r => r.name == data.mappings[AllZones]));
                        if (trace) Trace($"Found allzones mapped RuleSet", 3);
                    }

                    if (sets.Count > 1)
                    {
                        if (trace) Trace($"WARNING: Found multiple RuleSets: {string.Join(", ", sets.Select(s => s.name).ToArray())}", 3);
                        PrintWarning(GetMessage("Warning_MultipleRuleSets"), string.Join(", ", sets.Select(s => s.name).ToArray()));
                    }

                    ruleSet = sets.FirstOrDefault();
                    if (trace && ruleSet != null) Trace($"Found RuleSet: {ruleSet.name}", 3);
                }
            }
            if (ruleSet == null)
            {
                ruleSet = currentRuleSet;
                if (trace) Trace($"No RuleSet found; assigned current global RuleSet: {ruleSet.name}", 3);
            }
            return ruleSet;
        }

        RuleSet GetRuleSet(BaseEntity e0, BaseEntity e1)
        {
            //if(!serverInitialized) return List<string>;
            List<string> e0Locations = GetLocationKeys(e0);
            List<string> e1Locations = GetLocationKeys(e1);

            return GetRuleSet(e0Locations, e1Locations);
        }

        // get locations shared between the two passed location lists
        List<string> GetSharedLocations(List<string> e0Locations, List<string> e1Locations)
        {
            //if(!serverInitialized) return List<string>;
            return e0Locations.Intersect(e1Locations).Where(s => data.HasMapping(s)).ToList();
        }

        // Check exclusion for given entity locations
        bool CheckExclusion(List<string> e0Locations, List<string> e1Locations)
        {
            if (!serverInitialized) return false;
            if (e0Locations == null || e1Locations == null)
            {
                if (trace) Trace("No shared locations (empty location) - no exclusions", 3);
                return false;
            }
            if (trace) Trace($"Checking exclusions between [{ (e0Locations.Count == 0 ? "empty" : string.Join(", ", e0Locations.ToArray())) }] and [{ (e1Locations.Count == 0 ? "empty" : string.Join(", ", e1Locations.ToArray())) }]", 2);
            List<string> locations = GetSharedLocations(e0Locations, e1Locations);
            if (trace) Trace($"Shared locations: {(locations.Count == 0 ? "none" : string.Join(", ", locations.ToArray()))}", 3);
            if (locations != null && locations.Count > 0)
                foreach (string loc in locations)
                    if (data.HasEmptyMapping(loc))
                    {
                        if (trace) Trace($"Found exclusion mapping for location: {loc}", 3);
                        return true;
                    }
            if (trace) Trace("No shared locations, or no matching exclusion mapping - no exclusions)", 3);
            return false;
        }

        // add or update a mapping
        bool AddOrUpdateMapping(string key, string ruleset)
        {
            LoadConfiguration(); // Added to help ensure that valid rulesets don't get deleted
            if (!serverInitialized) return false;
            if (string.IsNullOrEmpty(key))
                return false;
            if (ruleset == null || (!data.ruleSets.Select(r => r.name).Contains(ruleset) && ruleset != "exclude"))
                return false;

            if (data.HasMapping(key))
                // update existing mapping
                data.mappings[key] = ruleset;
            else
                // add new mapping
                data.mappings.Add(key, ruleset);
            SaveData();

            return true;
        }

        // remove a mapping
        bool RemoveMapping(string key)
        {
            if (!serverInitialized) return false;
            if (string.IsNullOrEmpty(key))
                return false;
            if (data.HasMapping(key))
            {
                data.mappings.Remove(key);
                SaveData();
                return true;
            }
            return false;
        }
        #endregion

        #region Messaging
        // send message to player (chat)
        void SendMessage(BasePlayer player, string key, object[] options = null) => SendReply(player, BuildMessage(player, key, options));

        // send message to player (console)
        void SendMessage(ConsoleSystem.Arg arg, string key, object[] options = null) => SendReply(arg, BuildMessage(null, key, options));

        // build message string
        string BuildMessage(BasePlayer player, string key, object[] options = null)
        {
            string message = player == null ? GetMessage(key) : GetMessage(key, player.UserIDString);
            if (options != null && options.Length > 0)
                message = string.Format(message, options);
            string type = key.Split('_')[0];
            if (player != null)
            {
                string size = GetMessage("Format_" + type + "Size");
                string color = GetMessage("Format_" + type + "Color");
                return WrapSize(size, WrapColor(color, message));
            }
            else
            {
                string color = GetMessage("Format_" + type + "Color");
                return WrapColor(color, message);
            }
        }

        // prints the value of an Option
        private void PrintValue(ConsoleSystem.Arg arg, Option opt)
        {
            SendReply(arg, WrapSize(GetMessage("Format_NotifySize"), WrapColor(GetMessage("Format_NotifyColor"), opt + ": ") + data.config[opt]));
        }

        // wrap string in <size> tag, handles parsing size string to integer
        string WrapSize(string size, string input)
        {
            int i = 0;
            if (int.TryParse(size, out i))
                return WrapSize(i, input);
            return input;
        }

        // wrap a string in a <size> tag with the passed size
        string WrapSize(int size, string input)
        {
            if (input == null || input.Equals(""))
                return input;
            return "<size=" + size + ">" + input + "</size>";
        }

        // wrap a string in a <color> tag with the passed color
        string WrapColor(string color, string input)
        {
            if (input == null || input.Equals("") || color == null || color.Equals(""))
                return input;
            return "<color=" + color + ">" + input + "</color>";
        }

        // show usage information
        void ShowUsage(ConsoleSystem.Arg arg) => SendReply(arg, usageString);

        // warn that the server is set to PVE mdoe
        void WarnPve() => PrintWarning(GetMessage("Warning_PveMode"));
        #endregion

        #region Helper Procedures
        // is admin
        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (player?.net?.connection == null) return true;
            return player.net.connection.authLevel > 0;
        }

        // check if player has permission or is an admin
        private bool hasPermission(BasePlayer player, string permname)
        {
            return IsAdmin(player) || permission.UserHasPermission(player.UserIDString, permname);
        }

        // is player a HumanNPC
        private bool IsHumanNPC(BasePlayer player)
        {
            return player.userID < 76560000000000000L && player.userID > 0L && !player.IsDestroyed;
        }

        // get location keys from ZoneManager (zone IDs) or LiteZones (zone names)
        private List<string> GetLocationKeys(BaseEntity entity)
        {
            if (!useZones || entity == null) return null;
            List<string> locations = new List<string>();
            string zname = null;
            if (ZoneManager != null)
            {
                List<string> zmloc = new List<string>();
                if (ZoneManager.Version >= new VersionNumber(3, 0, 1))
                {
                    if (entity is BasePlayer)
                    {
                        // BasePlayer fix from chadomat
                        string[] zmlocplr = (string[])ZoneManager.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                        foreach (string s in zmlocplr)
                        {
                            zmloc.Add(s);
                        }
                    }
                    else if (entity.IsValid())
                    {
                        string[] zmlocent = (string[])ZoneManager.Call("GetEntityZoneIDs", new object[] { entity });
                        foreach (string s in zmlocent)
                        {
                            zmloc.Add(s);
                        }
                    }
                }
                else if (ZoneManager.Version < new VersionNumber(3, 0, 0))
                {
                    if (entity is BasePlayer)
                    {
                        string[] zmlocplr = (string[])ZoneManager.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                        foreach (string s in zmlocplr)
                        {
                            zmloc.Add(s);
                        }
                    }
                    else if (entity.IsValid())
                    {
                        zmloc = (List<string>)ZoneManager.Call("GetEntityZones", new object[] { entity });
                    }
                }
                else // Skip ZM version 3.0.0
                {
                    zmloc = null;
                }

                if (zmloc != null && zmloc.Count > 0)
                {
                    // Add names into list of ID numbers
                    foreach (string s in zmloc)
                    {
                        locations.Add(s);
                        zname = (string)ZoneManager.Call("GetZoneName", s);
                        if (zname != null) locations.Add(zname);
                        if (trace) base.Puts($"Found zone {zname}: {s}");
                    }
                }
            }
            if (LiteZones != null)
            {
                List<string> lzloc = (List<string>)LiteZones?.Call("GetEntityZones", new object[] { entity });
                if (lzloc != null && lzloc.Count > 0)
                {
                    locations.AddRange(lzloc);
                }
            }
            if (locations == null || locations.Count == 0) return null;
            return locations;
        }

        // check user access
        bool HasAccess(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                if (arg.Connection.authLevel < 1)
                {
                    SendMessage(arg, "Error_NoPermission");
                    return false;
                }
            }
            return true;
        }

        // handle raycast from player (for prodding)
        bool GetRaycastTarget(BasePlayer player, out object closestEntity)
        {
            closestEntity = false;

            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
            {
                closestEntity = hit.GetEntity();
                return true;
            }
            return false;
        }

        // loop to update current ruleset
        void TimerLoop(bool firstRun = false)
        {
            string ruleSetName;
            data.schedule.ClockUpdate(out ruleSetName, out currentBroadcastMessage);
            if (currentRuleSet.name != ruleSetName || firstRun)
            {
                currentRuleSet = data.ruleSets.FirstOrDefault(r => r.name == ruleSetName);
                if (currentRuleSet == null)
                    currentRuleSet = new RuleSet(ruleSetName); // create empty ruleset to hold name
                if (data.schedule.broadcast && currentBroadcastMessage != null)
                {
                    Server.Broadcast(currentBroadcastMessage, GetMessage("Prefix"));
                    base.Puts(GetMessage("Prefix") + " Schedule Broadcast: " + currentBroadcastMessage);
                }
            }

            if (data.schedule.enabled)
                scheduleUpdateTimer = timer.Once(data.schedule.useRealtime ? 30f : 3f, () => TimerLoop());
        }

        internal void Trace(string message, int indentation = 0) => LogToFile(traceFile, "".PadLeft(indentation, ' ') + message, this);
        #endregion

        #region Subclasses
        // configuration and data storage container
        class TruePVEData
        {
            [JsonProperty(PropertyName = "Config Version")]
            public string configVersion = null;
            [JsonProperty(PropertyName = "Default RuleSet")]
            public string defaultRuleSet = "default";
            [JsonProperty(PropertyName = "Configuration Options")]
            public Dictionary<Option, bool> config = new Dictionary<Option, bool>();
            [JsonProperty(PropertyName = "Mappings")]
            public Dictionary<string, string> mappings = new Dictionary<string, string>();
            [JsonProperty(PropertyName = "Schedule")]
            public Schedule schedule = new Schedule();
            [JsonProperty(PropertyName = "RuleSets")]
            public List<RuleSet> ruleSets = new List<RuleSet>();
            [JsonProperty(PropertyName = "Entity Groups")]
            public List<EntityGroup> groups { get; set; } = new List<EntityGroup>();
            //[JsonProperty(PropertyName = "Allow Killing Sleepers")]
            //public bool AllowKillingSleepers;

            Dictionary<uint, List<string>> groupCache = new Dictionary<uint, List<string>>();

            public void Init()
            {
                schedule.Init();
                foreach (RuleSet rs in ruleSets)
                    rs.Build();
                ruleSets.Remove(null);
            }

            public List<string> ResolveEntityGroups(BaseEntity entity)
            {
                if (!entity.IsValid()) return null;
                List<string> groupList;
                if (!groupCache.TryGetValue(entity.net.ID, out groupList))
                {
                    groupList = groups.Where(g => g.Contains(entity)).Select(g => g.name).ToList();
                    groupCache[entity.net.ID] = groupList;
                }
                return groupList;
            }

            public bool HasMapping(string key)
            {
                return mappings.ContainsKey(key) || mappings.ContainsKey(AllZones);
            }

            public bool HasEmptyMapping(string key)
            {
                if (mappings.ContainsKey(AllZones) && mappings[AllZones].Equals("exclude")) return true; // exlude all zones
                if (!mappings.ContainsKey(key)) return false;
                if (mappings[key].Equals("exclude")) return true;
                RuleSet r = ruleSets.FirstOrDefault(rs => rs.name.Equals(mappings[key]));
                if (r == null) return true;
                return r.IsEmpty();
            }

            public RuleSet GetDefaultRuleSet()
            {
                try
                {
                    return ruleSets.Single(r => r.name == defaultRuleSet);
                }
                catch (Exception)
                {
                    Interface.Oxide.LogWarning($"Warning - duplicate ruleset found for default RuleSet: '{defaultRuleSet}'");
                    return ruleSets.FirstOrDefault(r => r.name == defaultRuleSet);
                }
            }
        }

        class RuleSet
        {
            public string name;
            public bool enabled = true;
            public bool defaultAllowDamage = false;
            [JsonConverter(typeof(StringEnumConverter))]
            public RuleFlags flags = RuleFlags.None;
            public HashSet<string> rules = new HashSet<string>();
            HashSet<Rule> parsedRules = new HashSet<Rule>();

            public RuleSet() { }
            public RuleSet(string name) { this.name = name; }

            // evaluate the passed lists of entity groups against rules
            public bool Evaluate(List<string> eg1, List<string> eg2)
            {
                if (Instance.trace) Instance.Trace("Evaluating Rules...", 3);
                if (parsedRules == null || parsedRules.Count == 0)
                {
                    if (Instance.trace) Instance.Trace($"No rules found; returning default value: {defaultAllowDamage}", 4);
                    return defaultAllowDamage;
                }
                bool? res;
                if (Instance.trace) Instance.Trace("Checking direct initiator->target rules...", 4);
                // check all direct links
                if (eg1 != null && eg1.Count > 0 && eg2 != null && eg2.Count > 0)
                    foreach (string s1 in eg1)
                        foreach (string s2 in eg2)
                            if ((res = Evaluate(s1, s2)).HasValue) return res.Value;

                if (Instance.trace) Instance.Trace("No direct match rules found; continuing...", 4);
                if (eg1 != null && eg1.Count > 0)
                    // check group -> any
                    foreach (string s1 in eg1)
                        if ((res = Evaluate(s1, Any)).HasValue) return res.Value;

                if (Instance.trace) Instance.Trace("No matching initiator->any rules found; continuing...", 4);
                if (eg2 != null && eg2.Count > 0)
                    // check any -> group
                    foreach (string s2 in eg2)
                        if ((res = Evaluate(Any, s2)).HasValue) return res.Value;

                if (Instance.trace) Instance.Trace($"No matching any->target rules found; returning default value: {defaultAllowDamage}", 4);
                return defaultAllowDamage;
            }

            // evaluate two entity groups against rules
            public bool? Evaluate(string eg1, string eg2)
            {
                if (eg1 == null || eg2 == null || parsedRules == null || parsedRules.Count == 0) return null;
                if (Instance.trace) Instance.Trace($"Evaluating \"{eg1}->{eg2}\"...", 5);
                Rule rule = parsedRules.FirstOrDefault(r => r.valid && r.key.Equals(eg1 + "->" + eg2));
                if (rule != null)
                {
                    if (Instance.trace) Instance.Trace($"Match found; allow damage? {rule.hurt}", 6);
                    return rule.hurt;
                }
                if (Instance.trace) Instance.Trace($"No match found", 6);
                return null;
            }

            // build rule strings to rules
            public void Build()
            {
                foreach (string ruleText in rules)
                    parsedRules.Add(new Rule(ruleText));
                parsedRules.Remove(null);
                ValidateRules();
            }

            public void ValidateRules()
            {
                foreach (Rule rule in parsedRules)
                    if (!rule.valid)
                        Interface.Oxide.LogWarning($"Warning - invalid rule: {rule.ruleText}");
            }

            // add a rule
            public void AddRule(string ruleText)
            {
                rules.Add(ruleText);
                parsedRules.Add(new Rule(ruleText));
            }

            public bool HasAnyFlag(RuleFlags flags) { return (this.flags | flags) != RuleFlags.None; }
            public bool HasFlag(RuleFlags flag) { return (flags & flag) == flag; }
            public bool IsEmpty() { return (rules == null || rules.Count == 0) && flags == RuleFlags.None; }
        }

        class Rule
        {
            public string ruleText;
            [JsonIgnore]
            public string key;
            [JsonIgnore]
            public bool hurt;
            [JsonIgnore]
            public bool valid;

            public Rule() { }
            public Rule(string ruleText)
            {
                this.ruleText = ruleText;
                valid = RuleTranslator.Translate(this);
            }

            public override int GetHashCode() { return key.GetHashCode(); }

            public override bool Equals(object obj)
            {
                if (obj == null) return false;
                if (obj == this) return true;
                if (obj is Rule)
                    return key.Equals((obj as Rule).key);
                return false;
            }
        }

        // helper class to translate rule text to rules
        class RuleTranslator
        {
            static readonly Regex regex = new Regex(@"\s+");
            static readonly List<string> synonyms = new List<string>() { "anything", "nothing", "all", "any", "none", "everything" };
            public static bool Translate(Rule rule)
            {
                if (rule.ruleText == null || rule.ruleText.Equals("")) return false;
                string str = rule.ruleText;
                string[] splitStr = regex.Split(str);
                // first and last words should be ruleset names
                string rs0 = splitStr[0];
                string rs1 = splitStr[splitStr.Length - 1];
                string[] mid = splitStr.Skip(1).Take(splitStr.Length - 2).ToArray();
                if (mid == null || mid.Length == 0) return false;

                bool canHurt = true;
                foreach (string s in mid)
                    if (s.Equals("cannot") || s.Equals("can't"))
                        canHurt = false;

                // rs0 and rs1 shouldn't ever be "nothing" simultaneously
                if (rs0.Equals("nothing") || rs1.Equals("nothing") || rs0.Equals("none") || rs1.Equals("none")) canHurt = !canHurt;

                if (synonyms.Contains(rs0)) rs0 = Any;
                if (synonyms.Contains(rs1)) rs1 = Any;

                rule.key = rs0 + "->" + rs1;
                rule.hurt = canHurt;
                return true;
            }
        }

        // container for mapping entities
        class EntityGroup
        {
            public string name;
            public string members
            {
                get
                {
                    if (memberList == null || memberList.Count == 0) return "";
                    return string.Join(", ", memberList.ToArray());
                }
                set
                {
                    if (value == null || value.Equals("")) return;
                    memberList = value.Split(',').Select(s => s.Trim()).ToList();
                }
            }
            List<string> memberList = new List<string>();

            public string exclusions
            {
                get
                {
                    if (exclusionList == null || exclusionList.Count == 0) return "";
                    return string.Join(", ", exclusionList.ToArray());
                }
                set
                {
                    if (value == null || value.Equals("")) return;
                    exclusionList = value.Split(',').Select(s => s.Trim()).ToList();
                }
            }
            List<string> exclusionList = new List<string>();

            public EntityGroup() { }
            public EntityGroup(string name) { this.name = name; }
            public void Add(string prefabOrType)
            {
                memberList.Add(prefabOrType);
            }
            public bool Contains(BaseEntity entity)
            {
                if (entity == null) return false;
                return (memberList.Contains(entity.GetType().Name) || memberList.Contains(entity.ShortPrefabName)) &&
                      !(exclusionList.Contains(entity.GetType().Name) || exclusionList.Contains(entity.ShortPrefabName));
            }
        }

        // scheduler
        class Schedule
        {
            public bool enabled = false;
            public bool useRealtime = false;
            public bool broadcast = false;
            public List<string> entries = new List<string>();
            List<ScheduleEntry> parsedEntries = new List<ScheduleEntry>();
            [JsonIgnore]
            public bool valid = false;

            public void Init()
            {
                foreach (string str in entries)
                    parsedEntries.Add(new ScheduleEntry(str));
                // schedule not valid if entries are empty, there are less than 2 entries, or there are less than 2 rulesets defined
                if (parsedEntries == null || parsedEntries.Count == 0 || parsedEntries.Count(e => e.valid) < 2 || parsedEntries.Select(e => e.ruleSet).Distinct().Count() < 2)
                    enabled = false;
                else
                    valid = true;
            }

            // returns delta between current time and next schedule entry
            public void ClockUpdate(out string currentRuleSet, out string message)
            {
                TimeSpan time = useRealtime ? new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0).Add(DateTime.Now.TimeOfDay) : TOD_Sky.Instance.Cycle.DateTime.TimeOfDay;
                try
                {

                    ScheduleEntry se = null;
                    // get the most recent schedule entry
                    if (parsedEntries.Where(t => !t.isDaily).Count() > 0)
                        se = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.time <= time && ((useRealtime && !t.isDaily) || !useRealtime)).Max(t => t.time));
                    // if realtime, check for daily
                    if (useRealtime)
                    {
                        ScheduleEntry daily = null;
                        try
                        {
                            daily = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.time <= DateTime.Now.TimeOfDay && t.isDaily).Max(t => t.time));

                        }
                        catch (Exception)
                        { // no daily entries
                        }
                        if (daily != null && se == null)
                            se = daily;
                        if (daily != null && daily.time.Add(new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0)) > se.time)
                            se = daily;
                    }
                    currentRuleSet = se.ruleSet;
                    message = se.message;
                }
                catch (Exception)
                {
                    ScheduleEntry se = null;
                    // if time is earlier than all schedule entries, use max time
                    if (parsedEntries.Where(t => !t.isDaily).Count() > 0)
                        se = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && ((useRealtime && !t.isDaily) || !useRealtime)).Max(t => t.time));
                    if (useRealtime)
                    {
                        ScheduleEntry daily = null;
                        try
                        {
                            daily = parsedEntries.FirstOrDefault(e => e.time == parsedEntries.Where(t => t.valid && t.isDaily).Max(t => t.time));

                        }
                        catch (Exception)
                        { // no daily entries
                        }
                        if (daily != null && se == null)
                            se = daily;
                        if (daily != null && daily.time.Add(new TimeSpan((int)DateTime.Now.DayOfWeek, 0, 0, 0)) > se.time)
                            se = daily;
                    }
                    currentRuleSet = se?.ruleSet;
                    message = se?.message;
                }
            }
        }

        // helper class to translate schedule text to schedule entries
        class ScheduleTranslator
        {
            static readonly Regex regex = new Regex(@"\s+");
            public static bool Translate(ScheduleEntry entry)
            {
                if (entry.scheduleText == null || entry.scheduleText.Equals("")) return false;
                string str = entry.scheduleText;
                string[] splitStr = regex.Split(str, 3); // split into 3 parts
                // first word should be a timespan
                string ts = splitStr[0];
                // second word should be a ruleset name
                string rs = splitStr[1];
                // remaining should be message
                string message = splitStr.Length > 2 ? splitStr[2] : null;

                try
                {
                    if (ts.StartsWith("*."))
                    {
                        entry.isDaily = true;
                        ts = ts.Substring(2);
                    }
                    entry.time = TimeSpan.Parse(ts);
                    entry.ruleSet = rs;
                    entry.message = message;
                    return true;
                }
                catch (Exception)
                { }

                return false;
            }
        }

        class ScheduleEntry
        {
            public string ruleSet;
            public string message;
            public string scheduleText;
            public bool valid;
            public TimeSpan time;
            [JsonIgnore]
            public bool isDaily = false;

            public ScheduleEntry() { }
            public ScheduleEntry(string scheduleText)
            {
                this.scheduleText = scheduleText;
                valid = ScheduleTranslator.Translate(this);
            }
        }
        #endregion
    }
}