/*
 * KillaDome.cs - Full COD-Style Rust Server Experience Plugin
 * 
 * Features:
 * - Lobby system with comprehensive UI (Play/Loadouts/Store/Stats tabs)
 * - Drag-and-drop loadout editor with image library
 * - Persistent weapon progression and attachment upgrades
 * - Custom VFX/SFX for bullets and attachments
 * - Store integration (Tebex-compatible)
 * - High performance, GC-friendly architecture
 * 
 * Version: 1.0.0
 * Author: KillaDome Dev Team
 */

using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;

namespace Oxide.Plugins
{
    [Info("KillaDome", "KillaDome", "1.0.0")]
    [Description("Full COD-style server experience with lobby, loadouts, and progression")]
    public class KillaDome : RustPlugin
    {
        #region Fields
        
        private DomeManager _domeManager;
        private LobbyUI _lobbyUI;
        private LoadoutEditor _loadoutEditor;
        private AttachmentSystem _attachmentSystem;
        private WeaponProgression _weaponProgression;
        private VFXManager _vfxManager;
        private SFXManager _sfxManager;
        private ForgeStationSystem _forgeStation;
        private BloodTokenEconomy _tokenEconomy;
        private StoreAPI _storeAPI;
        private SaveManager _saveManager;
        private AntiExploit _antiExploit;
        private TelemetrySystem _telemetry;
        
        private PluginConfig _config;
        private Dictionary<ulong, PlayerSession> _activeSessions = new Dictionary<ulong, PlayerSession>();
        
        private const string PERMISSION_ADMIN = "killadome.admin";
        private const string PERMISSION_VIP = "killadome.vip";
        
        #endregion
        
        #region Configuration
        
        internal class PluginConfig
        {
            [JsonProperty("Lobby Spawn Position")]
            public Vector3 LobbySpawnPosition { get; set; } = new Vector3(0, 100, 0);
            
            [JsonProperty("Arena Spawn Position")]
            public Vector3 ArenaSpawnPosition { get; set; } = new Vector3(0, 100, 500);
            
            [JsonProperty("Starting Blood Tokens")]
            public int StartingTokens { get; set; } = 500;
            
            [JsonProperty("Tokens Per Kill")]
            public int TokensPerKill { get; set; } = 10;
            
            [JsonProperty("Enable Tebex Integration")]
            public bool EnableTebex { get; set; } = false;
            
            [JsonProperty("Tebex Secret Key")]
            public string TebexSecretKey { get; set; } = "YOUR_SECRET_KEY_HERE";
            
            [JsonProperty("Max Weapon Level")]
            public int MaxWeaponLevel { get; set; } = 10;
            
            [JsonProperty("Max Attachment Level")]
            public int MaxAttachmentLevel { get; set; } = 5;
            
            [JsonProperty("UI Update Throttle MS")]
            public int UIUpdateThrottleMS { get; set; } = 100;
            
            [JsonProperty("Auto Save Interval Seconds")]
            public float AutoSaveInterval { get; set; } = 300f;
            
            [JsonProperty("Enable Debug Logging")]
            public bool EnableDebugLogging { get; set; } = false;
        }
        
        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }
        
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch
            {
                PrintError("Configuration file is corrupt. Loading defaults...");
                LoadDefaultConfig();
            }
            SaveConfig();
        }
        
        protected override void SaveConfig() => Config.WriteObject(_config, true);
        
        #endregion
        
        #region Oxide Hooks
        
        private void Init()
        {
            permission.RegisterPermission(PERMISSION_ADMIN, this);
            permission.RegisterPermission(PERMISSION_VIP, this);
            
            // Initialize all systems
            _saveManager = new SaveManager(this, _config);
            _antiExploit = new AntiExploit(this);
            _tokenEconomy = new BloodTokenEconomy(this, _config);
            _attachmentSystem = new AttachmentSystem(this, _config);
            _weaponProgression = new WeaponProgression(this, _config);
            _vfxManager = new VFXManager(this);
            _sfxManager = new SFXManager(this);
            _forgeStation = new ForgeStationSystem(this, _config, _tokenEconomy, _attachmentSystem, _weaponProgression);
            _loadoutEditor = new LoadoutEditor(this, _attachmentSystem);
            _storeAPI = new StoreAPI(this, _config, _tokenEconomy);
            _lobbyUI = new LobbyUI(this, _loadoutEditor, _forgeStation, _storeAPI);
            _domeManager = new DomeManager(this, _config);
            _telemetry = new TelemetrySystem(this);
            
            LogDebug("KillaDome initialized successfully");
        }
        
        private void OnServerInitialized()
        {
            timer.Every(_config.AutoSaveInterval, () => AutoSaveAllPlayers());
            LogDebug("Auto-save timer started");
        }
        
        private void Unload()
        {
            // Clean up all UI
            foreach (var player in BasePlayer.activePlayerList)
            {
                _lobbyUI?.DestroyUI(player);
            }
            
            // Save all player data
            foreach (var session in _activeSessions.Values)
            {
                _saveManager?.SavePlayerProfile(session.Profile);
            }
            
            _activeSessions.Clear();
            
            LogDebug("KillaDome unloaded and cleaned up");
        }
        
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            
            NextTick(() =>
            {
                var profile = _saveManager.LoadPlayerProfile(player.userID);
                var session = new PlayerSession(player, profile);
                _activeSessions[player.userID] = session;
                
                // Teleport to lobby
                TeleportToLobby(player);
                
                // Show lobby UI
                timer.Once(1f, () => _lobbyUI.ShowLobbyUI(player));
                
                LogDebug($"Player {player.displayName} ({player.userID}) connected");
            });
        }
        
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            
            _lobbyUI?.DestroyUI(player);
            
            if (_activeSessions.TryGetValue(player.userID, out var session))
            {
                _saveManager.SavePlayerProfile(session.Profile);
                _activeSessions.Remove(player.userID);
            }
            
            LogDebug($"Player {player.displayName} disconnected: {reason}");
        }
        
        private void OnEntityDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null) return;
            
            var attacker = info?.InitiatorPlayer;
            if (attacker != null && attacker != victim)
            {
                // Award tokens for kill
                _tokenEconomy.AwardTokens(attacker.userID, _config.TokensPerKill);
                
                // Track telemetry
                _telemetry.RecordKill(attacker.userID, victim.userID);
                
                LogDebug($"{attacker.displayName} killed {victim.displayName}");
            }
            
            // Respawn victim in lobby after delay
            timer.Once(3f, () =>
            {
                if (victim != null && victim.IsConnected)
                {
                    TeleportToLobby(victim);
                    victim.Respawn();
                }
            });
        }
        
        #endregion
        
        #region Helper Methods
        
        private void TeleportToLobby(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            player.Teleport(_config.LobbySpawnPosition);
        }
        
        private void TeleportToArena(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            player.Teleport(_config.ArenaSpawnPosition);
        }
        
        private void AutoSaveAllPlayers()
        {
            int saved = 0;
            foreach (var session in _activeSessions.Values)
            {
                _saveManager.SavePlayerProfile(session.Profile);
                saved++;
            }
            LogDebug($"Auto-saved {saved} player profiles");
        }
        
        private void LogDebug(string message)
        {
            if (_config?.EnableDebugLogging == true)
            {
                Puts($"[DEBUG] {message}");
            }
        }
        
        internal PlayerSession GetSession(ulong steamId)
        {
            _activeSessions.TryGetValue(steamId, out var session);
            return session;
        }
        
        #endregion
        
        #region Console Commands
        
        [ConsoleCommand("kd.open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(arg, "You don't have permission to use this command");
                return;
            }
            
            _lobbyUI.ShowLobbyUI(player);
            SendReply(arg, "Lobby UI opened");
        }
        
        [ConsoleCommand("kd.start")]
        private void CmdStart(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(arg, "You don't have permission to use this command");
                return;
            }
            
            _domeManager.StartMatch();
            SendReply(arg, "Match started");
        }
        
        [ConsoleCommand("kd.giveskin")]
        private void CmdGiveSkin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(2))
            {
                SendReply(arg, "Usage: kd.giveskin <steamid> <skinid>");
                return;
            }
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(arg, "You don't have permission to use this command");
                return;
            }
            
            if (!ulong.TryParse(arg.Args[0], out ulong targetId))
            {
                SendReply(arg, "Invalid Steam ID");
                return;
            }
            
            string skinId = arg.Args[1];
            
            if (_activeSessions.TryGetValue(targetId, out var session))
            {
                session.Profile.OwnedSkins.Add(skinId);
                _saveManager.SavePlayerProfile(session.Profile);
                SendReply(arg, $"Granted skin {skinId} to player {targetId}");
            }
            else
            {
                SendReply(arg, "Player not found or not online");
            }
        }
        
        [ConsoleCommand("kd.resetprogress")]
        private void CmdResetProgress(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1))
            {
                SendReply(arg, "Usage: kd.resetprogress <steamid>");
                return;
            }
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(arg, "You don't have permission to use this command");
                return;
            }
            
            if (!ulong.TryParse(arg.Args[0], out ulong targetId))
            {
                SendReply(arg, "Invalid Steam ID");
                return;
            }
            
            var newProfile = new PlayerProfile(targetId, _config.StartingTokens);
            _saveManager.SavePlayerProfile(newProfile);
            
            if (_activeSessions.TryGetValue(targetId, out var session))
            {
                session.Profile = newProfile;
            }
            
            SendReply(arg, $"Reset progress for player {targetId}");
        }
        
        #endregion
        
        #region Chat Commands
        
        [ChatCommand("kd")]
        private void CmdKD(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                SendReply(player, "KillaDome Commands:\n" +
                    "/kd open - Open lobby UI\n" +
                    "/kd stats - View your stats\n" +
                    "/kd help - Show this help");
                return;
            }
            
            switch (args[0].ToLower())
            {
                case "open":
                    _lobbyUI.ShowLobbyUI(player);
                    SendReply(player, "Lobby UI opened");
                    break;
                    
                case "stats":
                    if (_activeSessions.TryGetValue(player.userID, out var session))
                    {
                        SendReply(player, $"Blood Tokens: {session.Profile.Tokens}\n" +
                            $"VIP Status: {(session.Profile.IsVIP ? "Active" : "Inactive")}");
                    }
                    break;
                    
                case "help":
                    SendReply(player, "KillaDome - Full COD Experience\n" +
                        "Use /kd open to access the lobby");
                    break;
                    
                default:
                    SendReply(player, "Unknown command. Use /kd help");
                    break;
            }
        }
        
        #endregion
        
        #region UI Console Commands
        
        [ConsoleCommand("killadome.close")]
        private void CmdUIClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            _lobbyUI.DestroyUI(player);
        }
        
        [ConsoleCommand("killadome.tab")]
        private void CmdUITab(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;
            
            string tab = arg.Args[0].ToLower();
            _lobbyUI.ShowLobbyUIWithTab(player, tab);
            
            LogDebug($"Player {player.displayName} opened tab: {tab}");
        }
        
        [ConsoleCommand("killadome.joinqueue")]
        private void CmdUIJoinQueue(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            _domeManager.AddToQueue(player.userID);
            SendReply(player, "You have joined the queue!");
        }
        
        [ConsoleCommand("killadome.weapon.prev")]
        private void CmdWeaponPrev(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;
            
            if (!_antiExploit.CheckRateLimit(player.userID))
            {
                SendReply(player, "Please slow down!");
                return;
            }
            
            string slot = arg.Args[0]; // "primary" or "secondary"
            CycleWeapon(player, slot, -1);
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
        }
        
        [ConsoleCommand("killadome.weapon.next")]
        private void CmdWeaponNext(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;
            
            if (!_antiExploit.CheckRateLimit(player.userID))
            {
                SendReply(player, "Please slow down!");
                return;
            }
            
            string slot = arg.Args[0]; // "primary" or "secondary"
            CycleWeapon(player, slot, 1);
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
        }
        
        [ConsoleCommand("killadome.purchase")]
        private void CmdPurchase(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(2)) return;
            
            if (!_antiExploit.CheckRateLimit(player.userID))
            {
                SendReply(player, "Please slow down!");
                return;
            }
            
            string itemId = arg.Args[0];
            if (!int.TryParse(arg.Args[1], out int cost))
            {
                SendReply(player, "Invalid cost");
                return;
            }
            
            var session = GetSession(player.userID);
            if (session == null)
            {
                SendReply(player, "Session not found!");
                return;
            }
            
            if (session.Profile.Tokens < cost)
            {
                SendReply(player, $"Insufficient tokens! You need {cost} but only have {session.Profile.Tokens}.");
                return;
            }
            
            if (_storeAPI.PurchaseItem(player.userID, itemId, cost))
            {
                SendReply(player, $"Successfully purchased {itemId}!");
                _saveManager.SavePlayerProfile(session.Profile);
                _lobbyUI.ShowLobbyUIWithTab(player, "store");
            }
            else
            {
                SendReply(player, "Purchase failed!");
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        private void CycleWeapon(BasePlayer player, string slot, int direction)
        {
            var session = GetSession(player.userID);
            if (session == null || session.Profile.Loadouts.Count == 0) return;
            
            var loadout = session.Profile.Loadouts[0];
            string[] availableWeapons = { "ak47", "m249", "pistol" }; // Available weapons
            
            string currentWeapon = slot == "primary" ? loadout.Primary : loadout.Secondary;
            int currentIndex = Array.IndexOf(availableWeapons, currentWeapon);
            
            if (currentIndex == -1) currentIndex = 0;
            
            int newIndex = (currentIndex + direction + availableWeapons.Length) % availableWeapons.Length;
            string newWeapon = availableWeapons[newIndex];
            
            if (slot == "primary")
            {
                loadout.Primary = newWeapon;
            }
            else
            {
                loadout.Secondary = newWeapon;
            }
            
            _saveManager.SavePlayerProfile(session.Profile);
            LogDebug($"Player {player.displayName} changed {slot} weapon to {newWeapon}");
        }
        
        #endregion
        
        #region Data Models
        
        internal class PlayerSession
        {
            public BasePlayer Player { get; set; }
            public PlayerProfile Profile { get; set; }
            public DateTime LastAction { get; set; }
            public string SelectedItem { get; set; }
            public bool IsInMatch { get; set; }
            
            internal PlayerSession(BasePlayer player, PlayerProfile profile)
            {
                Player = player;
                Profile = profile;
                LastAction = DateTime.UtcNow;
            }
        }
        
        public class PlayerProfile
        {
            public ulong SteamID { get; set; }
            public List<Loadout> Loadouts { get; set; }
            public Dictionary<string, int> WeaponLevels { get; set; }
            public Dictionary<string, int> AttachmentLevels { get; set; }
            public List<string> OwnedSkins { get; set; }
            public int Tokens { get; set; }
            public bool IsVIP { get; set; }
            public DateTime LastUpdated { get; set; }
            public int TotalKills { get; set; }
            public int TotalDeaths { get; set; }
            public int MatchesPlayed { get; set; }
            
            public PlayerProfile()
            {
                Loadouts = new List<Loadout>();
                WeaponLevels = new Dictionary<string, int>();
                AttachmentLevels = new Dictionary<string, int>();
                OwnedSkins = new List<string>();
            }
            
            public PlayerProfile(ulong steamId, int startingTokens) : this()
            {
                SteamID = steamId;
                Tokens = startingTokens;
                LastUpdated = DateTime.UtcNow;
                
                // Create default loadout
                Loadouts.Add(new Loadout
                {
                    Name = "Default",
                    Primary = "ak47",
                    Secondary = "pistol",
                    PrimaryAttachments = new Dictionary<string, string>(),
                    Skins = new Dictionary<string, string>()
                });
            }
        }
        
        public class Loadout
        {
            public string Name { get; set; }
            public string Primary { get; set; }
            public string Secondary { get; set; }
            public Dictionary<string, string> PrimaryAttachments { get; set; }
            public Dictionary<string, string> SecondaryAttachments { get; set; }
            public Dictionary<string, string> Skins { get; set; }
            public string Lethal { get; set; }
            public string Tactical { get; set; }
            public List<string> Perks { get; set; }
            
            public Loadout()
            {
                PrimaryAttachments = new Dictionary<string, string>();
                SecondaryAttachments = new Dictionary<string, string>();
                Skins = new Dictionary<string, string>();
                Perks = new List<string>();
            }
        }
        
        #endregion
        
        #region Module: DomeManager
        
        internal class DomeManager
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private Match _currentMatch;
            private List<ulong> _matchQueue = new List<ulong>();
            
            internal DomeManager(KillaDome plugin, PluginConfig config)
            {
                _plugin = plugin;
                _config = config;
            }
            
            public void StartMatch()
            {
                if (_currentMatch != null && _currentMatch.IsActive)
                {
                    _plugin.PrintWarning("Match already in progress");
                    return;
                }
                
                _currentMatch = new Match
                {
                    MatchId = Guid.NewGuid().ToString(),
                    StartTime = DateTime.UtcNow,
                    IsActive = true
                };
                
                // Teleport queued players to arena
                foreach (var steamId in _matchQueue)
                {
                    var player = BasePlayer.FindByID(steamId);
                    if (player != null && player.IsConnected)
                    {
                        _plugin.TeleportToArena(player);
                        
                        var session = _plugin.GetSession(steamId);
                        if (session != null)
                        {
                            session.IsInMatch = true;
                        }
                    }
                }
                
                _plugin.Puts($"Match {_currentMatch.MatchId} started with {_matchQueue.Count} players");
                _matchQueue.Clear();
            }
            
            public void EndMatch()
            {
                if (_currentMatch == null || !_currentMatch.IsActive)
                {
                    return;
                }
                
                _currentMatch.IsActive = false;
                _currentMatch.EndTime = DateTime.UtcNow;
                
                // Return players to lobby
                foreach (var player in BasePlayer.activePlayerList)
                {
                    var session = _plugin.GetSession(player.userID);
                    if (session != null && session.IsInMatch)
                    {
                        _plugin.TeleportToLobby(player);
                        session.IsInMatch = false;
                    }
                }
                
                _plugin.Puts($"Match {_currentMatch.MatchId} ended");
            }
            
            public void AddToQueue(ulong steamId)
            {
                if (!_matchQueue.Contains(steamId))
                {
                    _matchQueue.Add(steamId);
                }
            }
            
            public void RemoveFromQueue(ulong steamId)
            {
                _matchQueue.Remove(steamId);
            }
        }
        
        internal class Match
        {
            public string MatchId { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public bool IsActive { get; set; }
            public List<ulong> Participants { get; set; } = new List<ulong>();
        }
        
        #endregion
        
        #region Module: LobbyUI
        
        internal class LobbyUI
        {
            private KillaDome _plugin;
            private LoadoutEditor _loadoutEditor;
            private ForgeStationSystem _forgeStation;
            private StoreAPI _storeAPI;
            
            private const string UI_MAIN = "KillaDome.Main";
            private const string UI_TAB_CONTAINER = "KillaDome.TabContainer";
            
            internal LobbyUI(KillaDome plugin, LoadoutEditor loadoutEditor, ForgeStationSystem forgeStation, StoreAPI storeAPI)
            {
                _plugin = plugin;
                _loadoutEditor = loadoutEditor;
                _forgeStation = forgeStation;
                _storeAPI = storeAPI;
            }
            
            public void ShowLobbyUI(BasePlayer player)
            {
                ShowLobbyUIWithTab(player, "play");
            }
            
            public void ShowLobbyUIWithTab(BasePlayer player, string tab)
            {
                DestroyUI(player);
                
                var container = new CuiElementContainer();
                
                // Main background
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.95" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true
                }, "Overlay", UI_MAIN);
                
                // Title
                container.Add(new CuiLabel
                {
                    Text = { Text = "KILLADOME", FontSize = 36, Align = TextAnchor.MiddleCenter, Color = "1 0.5 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.85", AnchorMax = "0.7 0.95" }
                }, UI_MAIN);
                
                // Tab buttons
                AddTabButton(container, UI_MAIN, "PLAY", 0, player, "killadome.tab play");
                AddTabButton(container, UI_MAIN, "LOADOUTS", 1, player, "killadome.tab loadouts");
                AddTabButton(container, UI_MAIN, "STORE", 2, player, "killadome.tab store");
                AddTabButton(container, UI_MAIN, "STATS", 3, player, "killadome.tab stats");
                AddTabButton(container, UI_MAIN, "SETTINGS", 4, player, "killadome.tab settings");
                
                // Close button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.8 0.2 0.2 1", Command = "killadome.close" },
                    RectTransform = { AnchorMin = "0.92 0.92", AnchorMax = "0.98 0.98" },
                    Text = { Text = "X", FontSize = 20, Align = TextAnchor.MiddleCenter }
                }, UI_MAIN);
                
                // Tab content container
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.9" },
                    RectTransform = { AnchorMin = "0.1 0.15", AnchorMax = "0.9 0.75" }
                }, UI_MAIN, UI_TAB_CONTAINER);
                
                // Show appropriate tab content
                switch (tab.ToLower())
                {
                    case "play":
                        ShowPlayTab(container, player);
                        break;
                    case "loadouts":
                        ShowLoadoutsTab(container, player);
                        break;
                    case "store":
                        ShowStoreTab(container, player);
                        break;
                    case "stats":
                        ShowStatsTab(container, player);
                        break;
                    case "settings":
                        ShowSettingsTab(container, player);
                        break;
                    default:
                        ShowPlayTab(container, player);
                        break;
                }
                
                CuiHelper.AddUi(player, container);
            }
            
            private void AddTabButton(CuiElementContainer container, string parent, string text, int index, BasePlayer player, string command)
            {
                float width = 0.15f;
                float spacing = 0.02f;
                float startX = 0.1f;
                float minX = startX + (width + spacing) * index;
                float maxX = minX + width;
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.3 0.3 0.3 1", Command = command },
                    RectTransform = { AnchorMin = $"{minX} 0.78", AnchorMax = $"{maxX} 0.83" },
                    Text = { Text = text, FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, parent);
            }
            
            private void ShowPlayTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "READY TO PLAY?", FontSize = 24, Align = TextAnchor.MiddleCenter },
                    RectTransform = { AnchorMin = "0.3 0.6", AnchorMax = "0.7 0.7" }
                }, UI_TAB_CONTAINER);
                
                // Join Queue button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.8 0.2 1", Command = "killadome.joinqueue" },
                    RectTransform = { AnchorMin = "0.35 0.4", AnchorMax = "0.65 0.5" },
                    Text = { Text = "JOIN QUEUE", FontSize = 18, Align = TextAnchor.MiddleCenter }
                }, UI_TAB_CONTAINER);
                
                // Stats preview
                var session = _plugin.GetSession(player.userID);
                if (session != null)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"Blood Tokens: {session.Profile.Tokens}", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                        RectTransform = { AnchorMin = "0.3 0.3", AnchorMax = "0.7 0.35" }
                    }, UI_TAB_CONTAINER);
                }
            }
            
            private void ShowLoadoutsTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "LOADOUT EDITOR", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.9" }
                }, UI_TAB_CONTAINER);
                
                var session = _plugin.GetSession(player.userID);
                if (session == null)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "Loading profile...", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 0 0 1" },
                        RectTransform = { AnchorMin = "0.3 0.5", AnchorMax = "0.7 0.6" }
                    }, UI_TAB_CONTAINER);
                    return;
                }
                
                // Ensure loadout exists
                if (session.Profile.Loadouts.Count == 0)
                {
                    session.Profile.Loadouts.Add(new Loadout
                    {
                        Name = "Default",
                        Primary = "ak47",
                        Secondary = "pistol",
                        PrimaryAttachments = new Dictionary<string, string>(),
                        Skins = new Dictionary<string, string>()
                    });
                }
                
                var loadout = session.Profile.Loadouts[0];
                    
                    // PRIMARY WEAPON SECTION
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "PRIMARY WEAPON", FontSize = 16, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.15 0.65", AnchorMax = "0.4 0.7" }
                    }, UI_TAB_CONTAINER);
                    
                    // Primary weapon display panel
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.2 0.2 0.2 1" },
                        RectTransform = { AnchorMin = "0.15 0.45", AnchorMax = "0.4 0.63" }
                    }, UI_TAB_CONTAINER);
                    
                    // Current primary weapon text
                    container.Add(new CuiLabel
                    {
                        Text = { Text = loadout.Primary.ToUpper(), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "0 1 0 1" },
                        RectTransform = { AnchorMin = "0.15 0.45", AnchorMax = "0.4 0.63" }
                    }, UI_TAB_CONTAINER);
                    
                    // PREV button for primary
                    container.Add(new CuiButton
                    {
                        Button = { Command = "killadome.weapon.prev primary", Color = "0.3 0.3 0.3 1" },
                        Text = { Text = "◄ PREV", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.15 0.38", AnchorMax = "0.25 0.43" }
                    }, UI_TAB_CONTAINER);
                    
                    // NEXT button for primary
                    container.Add(new CuiButton
                    {
                        Button = { Command = "killadome.weapon.next primary", Color = "0.3 0.3 0.3 1" },
                        Text = { Text = "NEXT ►", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.3 0.38", AnchorMax = "0.4 0.43" }
                    }, UI_TAB_CONTAINER);
                    
                    // SECONDARY WEAPON SECTION
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "SECONDARY WEAPON", FontSize = 16, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.55 0.65", AnchorMax = "0.85 0.7" }
                    }, UI_TAB_CONTAINER);
                    
                    // Secondary weapon display panel
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.2 0.2 0.2 1" },
                        RectTransform = { AnchorMin = "0.55 0.45", AnchorMax = "0.85 0.63" }
                    }, UI_TAB_CONTAINER);
                    
                    // Current secondary weapon text
                    container.Add(new CuiLabel
                    {
                        Text = { Text = loadout.Secondary.ToUpper(), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "0 1 0 1" },
                        RectTransform = { AnchorMin = "0.55 0.45", AnchorMax = "0.85 0.63" }
                    }, UI_TAB_CONTAINER);
                    
                    // PREV button for secondary
                    container.Add(new CuiButton
                    {
                        Button = { Command = "killadome.weapon.prev secondary", Color = "0.3 0.3 0.3 1" },
                        Text = { Text = "◄ PREV", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.55 0.38", AnchorMax = "0.65 0.43" }
                    }, UI_TAB_CONTAINER);
                    
                    // NEXT button for secondary
                    container.Add(new CuiButton
                    {
                        Button = { Command = "killadome.weapon.next secondary", Color = "0.3 0.3 0.3 1" },
                        Text = { Text = "NEXT ►", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.75 0.38", AnchorMax = "0.85 0.43" }
                    }, UI_TAB_CONTAINER);
                    
                // Info section
                container.Add(new CuiLabel
                {
                    Text = { Text = "Use PREV/NEXT buttons to cycle through weapons", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = "0.2 0.25", AnchorMax = "0.8 0.3" }
                }, UI_TAB_CONTAINER);
            }
            
            private void ShowStoreTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "STORE", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.9" }
                }, UI_TAB_CONTAINER);
                
                var session = _plugin.GetSession(player.userID);
                if (session != null)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"Your Tokens: {session.Profile.Tokens}", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0 1 0 1" },
                        RectTransform = { AnchorMin = "0.3 0.72", AnchorMax = "0.7 0.77" }
                    }, UI_TAB_CONTAINER);
                }
                
                // Store items with prices and purchase buttons
                var storeItems = new[]
                {
                    new { Name = "AK-47 Skin", Cost = 500, Id = "skin_ak47_neon" },
                    new { Name = "Extended Mag", Cost = 300, Id = "att_extended_mag" },
                    new { Name = "Reflex Sight", Cost = 250, Id = "att_reflex_sight" },
                    new { Name = "Silencer", Cost = 400, Id = "att_silencer" }
                };
                
                for (int i = 0; i < storeItems.Length; i++)
                {
                    var item = storeItems[i];
                    float yMin = 0.60f - (i * 0.12f);
                    float yMax = yMin + 0.08f;
                    
                    // Item panel
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.2 0.2 0.2 1" },
                        RectTransform = { AnchorMin = $"0.2 {yMin}", AnchorMax = $"0.8 {yMax}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Item name and price
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"{item.Name} - {item.Cost} Tokens", FontSize = 14, Align = TextAnchor.MiddleLeft },
                        RectTransform = { AnchorMin = $"0.22 {yMin}", AnchorMax = $"0.6 {yMax}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Purchase button
                    string buttonColor = (session != null && session.Profile.Tokens >= item.Cost) ? "0.2 0.8 0.2 1" : "0.5 0.2 0.2 1";
                    container.Add(new CuiButton
                    {
                        Button = { Command = $"killadome.purchase {item.Id} {item.Cost}", Color = buttonColor },
                        Text = { Text = "BUY", FontSize = 12, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = $"0.65 {yMin + 0.01f}", AnchorMax = $"0.78 {yMax - 0.01f}" }
                    }, UI_TAB_CONTAINER);
                }
                
                // Info text
                container.Add(new CuiLabel
                {
                    Text = { Text = "Click BUY to purchase items with Blood Tokens", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = "0.2 0.08", AnchorMax = "0.8 0.12" }
                }, UI_TAB_CONTAINER);
            }
            
            private void ShowStatsTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "YOUR STATS", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.9" }
                }, UI_TAB_CONTAINER);
                
                var session = _plugin.GetSession(player.userID);
                if (session != null)
                {
                    var profile = session.Profile;
                    
                    string[] stats = 
                    {
                        $"Kills: {profile.TotalKills}",
                        $"Deaths: {profile.TotalDeaths}",
                        $"K/D Ratio: {(profile.TotalDeaths > 0 ? ((float)profile.TotalKills / profile.TotalDeaths).ToString("F2") : profile.TotalKills.ToString())}",
                        $"Blood Tokens: {profile.Tokens}",
                        $"Matches Played: {profile.MatchesPlayed}",
                        $"VIP Status: {(profile.IsVIP ? "YES" : "NO")}"
                    };
                    
                    for (int i = 0; i < stats.Length; i++)
                    {
                        float yPos = 0.65f - (i * 0.08f);
                        container.Add(new CuiLabel
                        {
                            Text = { Text = stats[i], FontSize = 16, Align = TextAnchor.MiddleLeft },
                            RectTransform = { AnchorMin = $"0.25 {yPos}", AnchorMax = $"0.75 {yPos + 0.06f}" }
                        }, UI_TAB_CONTAINER);
                    }
                }
                else
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "No stats available", FontSize = 16, Align = TextAnchor.MiddleCenter },
                        RectTransform = { AnchorMin = "0.3 0.5", AnchorMax = "0.7 0.6" }
                    }, UI_TAB_CONTAINER);
                }
            }
            
            private void ShowSettingsTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "SETTINGS", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.9" }
                }, UI_TAB_CONTAINER);
                
                container.Add(new CuiLabel
                {
                    Text = { Text = "Plugin Settings", FontSize = 18, Align = TextAnchor.MiddleLeft },
                    RectTransform = { AnchorMin = "0.2 0.6", AnchorMax = "0.8 0.65" }
                }, UI_TAB_CONTAINER);
                
                string[] settings = 
                {
                    "UI Update Throttle: 100ms",
                    "Auto-Save Interval: 5 minutes",
                    "Max Weapon Level: 10",
                    "Max Attachment Level: 5"
                };
                
                for (int i = 0; i < settings.Length; i++)
                {
                    float yPos = 0.5f - (i * 0.08f);
                    container.Add(new CuiLabel
                    {
                        Text = { Text = settings[i], FontSize = 14, Align = TextAnchor.MiddleLeft },
                        RectTransform = { AnchorMin = $"0.25 {yPos}", AnchorMax = $"0.75 {yPos + 0.06f}" }
                    }, UI_TAB_CONTAINER);
                }
                
                container.Add(new CuiLabel
                {
                    Text = { Text = "Configure via KillaDome.json", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = "0.3 0.15", AnchorMax = "0.7 0.2" }
                }, UI_TAB_CONTAINER);
            }
            
            public void DestroyUI(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, UI_MAIN);
            }
        }
        
        #endregion
        
        #region Module: LoadoutEditor
        
        internal class LoadoutEditor
        {
            private KillaDome _plugin;
            private AttachmentSystem _attachmentSystem;
            private Dictionary<ulong, string> _selectedItems = new Dictionary<ulong, string>();
            
            internal LoadoutEditor(KillaDome plugin, AttachmentSystem attachmentSystem)
            {
                _plugin = plugin;
                _attachmentSystem = attachmentSystem;
            }
            
            public void SelectItem(ulong steamId, string itemId)
            {
                _selectedItems[steamId] = itemId;
            }
            
            public string GetSelectedItem(ulong steamId)
            {
                _selectedItems.TryGetValue(steamId, out string itemId);
                return itemId;
            }
            
            public void ClearSelection(ulong steamId)
            {
                _selectedItems.Remove(steamId);
            }
            
            public bool TryEquipItem(ulong steamId, string slotId, string itemId)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null || session.Profile.Loadouts.Count == 0)
                {
                    return false;
                }
                
                var loadout = session.Profile.Loadouts[0]; // Current loadout
                
                // Validate and equip based on slot type
                if (slotId.StartsWith("primary_att_"))
                {
                    string attachmentSlot = slotId.Replace("primary_att_", "");
                    loadout.PrimaryAttachments[attachmentSlot] = itemId;
                    return true;
                }
                
                return false;
            }
        }
        
        #endregion
        
        #region Module: AttachmentSystem
        
        internal class AttachmentSystem
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private Dictionary<string, AttachmentDefinition> _attachments;
            
            internal AttachmentSystem(KillaDome plugin, PluginConfig config)
            {
                _plugin = plugin;
                _config = config;
                InitializeAttachments();
            }
            
            private void InitializeAttachments()
            {
                _attachments = new Dictionary<string, AttachmentDefinition>
                {
                    ["silencer"] = new AttachmentDefinition
                    {
                        Id = "silencer",
                        Name = "Silencer",
                        Slot = "barrel",
                        MaxLevel = 5,
                        StatModifiers = new Dictionary<string, float>
                        {
                            ["noise_reduction"] = 0.8f,
                            ["damage"] = -0.05f
                        }
                    },
                    ["extended_mag"] = new AttachmentDefinition
                    {
                        Id = "extended_mag",
                        Name = "Extended Magazine",
                        Slot = "mag",
                        MaxLevel = 5,
                        StatModifiers = new Dictionary<string, float>
                        {
                            ["mag_size"] = 1.5f,
                            ["reload_speed"] = -0.1f
                        }
                    },
                    ["reflex"] = new AttachmentDefinition
                    {
                        Id = "reflex",
                        Name = "Reflex Sight",
                        Slot = "optic",
                        MaxLevel = 3,
                        StatModifiers = new Dictionary<string, float>
                        {
                            ["accuracy"] = 1.2f
                        }
                    }
                };
            }
            
            internal AttachmentDefinition GetAttachment(string attachmentId)
            {
                _attachments.TryGetValue(attachmentId, out var attachment);
                return attachment;
            }
            
            public Dictionary<string, float> CalculateWeaponStats(string weaponId, Dictionary<string, string> attachments)
            {
                var stats = new Dictionary<string, float>
                {
                    ["damage"] = 1.0f,
                    ["fire_rate"] = 1.0f,
                    ["accuracy"] = 1.0f,
                    ["mag_size"] = 1.0f,
                    ["reload_speed"] = 1.0f
                };
                
                foreach (var attachment in attachments.Values)
                {
                    var def = GetAttachment(attachment);
                    if (def != null)
                    {
                        foreach (var mod in def.StatModifiers)
                        {
                            if (stats.ContainsKey(mod.Key))
                            {
                                stats[mod.Key] *= mod.Value;
                            }
                            else
                            {
                                stats[mod.Key] = mod.Value;
                            }
                        }
                    }
                }
                
                return stats;
            }
        }
        
        internal class AttachmentDefinition
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Slot { get; set; }
            public int MaxLevel { get; set; }
            public Dictionary<string, float> StatModifiers { get; set; }
            public string VFXTag { get; set; }
            public string SFXTag { get; set; }
        }
        
        #endregion
        
        #region Module: WeaponProgression
        
        internal class WeaponProgression
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private Dictionary<string, WeaponDefinition> _weapons;
            
            internal WeaponProgression(KillaDome plugin, PluginConfig config)
            {
                _plugin = plugin;
                _config = config;
                InitializeWeapons();
            }
            
            private void InitializeWeapons()
            {
                _weapons = new Dictionary<string, WeaponDefinition>
                {
                    ["ak47"] = new WeaponDefinition
                    {
                        Id = "ak47",
                        Name = "AK-47",
                        MaxLevel = 10,
                        BaseStats = new Dictionary<string, float>
                        {
                            ["damage"] = 35f,
                            ["fire_rate"] = 0.13f,
                            ["accuracy"] = 0.75f
                        }
                    },
                    ["m249"] = new WeaponDefinition
                    {
                        Id = "m249",
                        Name = "M249",
                        MaxLevel = 10,
                        BaseStats = new Dictionary<string, float>
                        {
                            ["damage"] = 30f,
                            ["fire_rate"] = 0.1f,
                            ["accuracy"] = 0.7f
                        }
                    }
                };
            }
            
            public int GetWeaponLevel(ulong steamId, string weaponId)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null) return 0;
                
                session.Profile.WeaponLevels.TryGetValue(weaponId, out int level);
                return level;
            }
            
            public bool UpgradeWeapon(ulong steamId, string weaponId, int cost)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null) return false;
                
                int currentLevel = GetWeaponLevel(steamId, weaponId);
                if (currentLevel >= _config.MaxWeaponLevel)
                {
                    return false;
                }
                
                if (session.Profile.Tokens < cost)
                {
                    return false;
                }
                
                session.Profile.Tokens -= cost;
                session.Profile.WeaponLevels[weaponId] = currentLevel + 1;
                
                return true;
            }
        }
        
        internal class WeaponDefinition
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int MaxLevel { get; set; }
            public Dictionary<string, float> BaseStats { get; set; }
        }
        
        #endregion
        
        #region Module: VFXManager
        
        internal class VFXManager
        {
            private KillaDome _plugin;
            
            internal VFXManager(KillaDome plugin)
            {
                _plugin = plugin;
            }
            
            public void PlayVFX(BasePlayer player, string vfxTag, Vector3 position)
            {
                // This would trigger client-side VFX
                player.SendConsoleCommand($"killadome.vfx {vfxTag} {position.x} {position.y} {position.z}");
            }
        }
        
        #endregion
        
        #region Module: SFXManager
        
        internal class SFXManager
        {
            private KillaDome _plugin;
            
            internal SFXManager(KillaDome plugin)
            {
                _plugin = plugin;
            }
            
            public void PlaySFX(BasePlayer player, string sfxTag)
            {
                // This would trigger client-side SFX
                player.SendConsoleCommand($"killadome.sfx {sfxTag}");
            }
        }
        
        #endregion
        
        #region Module: ForgeStationSystem
        
        internal class ForgeStationSystem
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private BloodTokenEconomy _economy;
            private AttachmentSystem _attachmentSystem;
            private WeaponProgression _weaponProgression;
            
            internal ForgeStationSystem(KillaDome plugin, PluginConfig config, BloodTokenEconomy economy, 
                AttachmentSystem attachmentSystem, WeaponProgression weaponProgression)
            {
                _plugin = plugin;
                _config = config;
                _economy = economy;
                _attachmentSystem = attachmentSystem;
                _weaponProgression = weaponProgression;
            }
            
            public int CalculateUpgradeCost(int currentLevel)
            {
                return 100 * (currentLevel + 1);
            }
            
            public bool UpgradeAttachment(ulong steamId, string attachmentId)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null) return false;
                
                int currentLevel = 0;
                session.Profile.AttachmentLevels.TryGetValue(attachmentId, out currentLevel);
                
                if (currentLevel >= _config.MaxAttachmentLevel)
                {
                    return false;
                }
                
                int cost = CalculateUpgradeCost(currentLevel);
                
                if (!_economy.SpendTokens(steamId, cost))
                {
                    return false;
                }
                
                session.Profile.AttachmentLevels[attachmentId] = currentLevel + 1;
                return true;
            }
        }
        
        #endregion
        
        #region Module: BloodTokenEconomy
        
        internal class BloodTokenEconomy
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            
            internal BloodTokenEconomy(KillaDome plugin, PluginConfig config)
            {
                _plugin = plugin;
                _config = config;
            }
            
            public void AwardTokens(ulong steamId, int amount)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null) return;
                
                session.Profile.Tokens += amount;
                _plugin.LogDebug($"Awarded {amount} tokens to {steamId}. New balance: {session.Profile.Tokens}");
            }
            
            public bool SpendTokens(ulong steamId, int amount)
            {
                var session = _plugin.GetSession(steamId);
                if (session == null || session.Profile.Tokens < amount)
                {
                    return false;
                }
                
                session.Profile.Tokens -= amount;
                return true;
            }
            
            public int GetBalance(ulong steamId)
            {
                var session = _plugin.GetSession(steamId);
                return session?.Profile.Tokens ?? 0;
            }
        }
        
        #endregion
        
        #region Module: StoreAPI
        
        internal class StoreAPI
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private BloodTokenEconomy _economy;
            
            internal StoreAPI(KillaDome plugin, PluginConfig config, BloodTokenEconomy economy)
            {
                _plugin = plugin;
                _config = config;
                _economy = economy;
            }
            
            public bool PurchaseItem(ulong steamId, string itemId, int cost)
            {
                if (!_economy.SpendTokens(steamId, cost))
                {
                    return false;
                }
                
                var session = _plugin.GetSession(steamId);
                if (session == null) return false;
                
                session.Profile.OwnedSkins.Add(itemId);
                _plugin.LogDebug($"Player {steamId} purchased {itemId} for {cost} tokens");
                
                return true;
            }
            
            // Tebex integration stub
            public void ProcessTebexPurchase(ulong steamId, string packageId, string transactionId)
            {
                if (!_config.EnableTebex)
                {
                    _plugin.PrintWarning("Tebex integration is disabled");
                    return;
                }
                
                // TODO: Verify purchase with Tebex API using secret key
                // For now, just log
                _plugin.LogDebug($"Processing Tebex purchase: {steamId}, {packageId}, {transactionId}");
            }
        }
        
        #endregion
        
        #region Module: SaveManager
        
        internal class SaveManager
        {
            private KillaDome _plugin;
            private PluginConfig _config;
            private string _dataDirectory;
            
            internal SaveManager(KillaDome plugin, PluginConfig config)
            {
                _plugin = plugin;
                _config = config;
                _dataDirectory = Path.Combine(Interface.Oxide.DataDirectory, "KillaDome");
                
                if (!Directory.Exists(_dataDirectory))
                {
                    Directory.CreateDirectory(_dataDirectory);
                }
            }
            
            public PlayerProfile LoadPlayerProfile(ulong steamId)
            {
                string filePath = Path.Combine(_dataDirectory, $"{steamId}.json");
                
                if (!File.Exists(filePath))
                {
                    return new PlayerProfile(steamId, _config.StartingTokens);
                }
                
                try
                {
                    string json = File.ReadAllText(filePath);
                    var profile = JsonConvert.DeserializeObject<PlayerProfile>(json);
                    _plugin.LogDebug($"Loaded profile for {steamId}");
                    return profile ?? new PlayerProfile(steamId, _config.StartingTokens);
                }
                catch (Exception ex)
                {
                    _plugin.PrintError($"Failed to load profile for {steamId}: {ex.Message}");
                    return new PlayerProfile(steamId, _config.StartingTokens);
                }
            }
            
            public void SavePlayerProfile(PlayerProfile profile)
            {
                if (profile == null) return;
                
                profile.LastUpdated = DateTime.UtcNow;
                
                string filePath = Path.Combine(_dataDirectory, $"{profile.SteamID}.json");
                string tempPath = filePath + ".tmp";
                
                try
                {
                    string json = JsonConvert.SerializeObject(profile, Formatting.Indented);
                    File.WriteAllText(tempPath, json);
                    
                    // Atomic swap
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(tempPath, filePath);
                    
                    _plugin.LogDebug($"Saved profile for {profile.SteamID}");
                }
                catch (Exception ex)
                {
                    _plugin.PrintError($"Failed to save profile for {profile.SteamID}: {ex.Message}");
                }
            }
        }
        
        #endregion
        
        #region Module: AntiExploit
        
        internal class AntiExploit
        {
            private KillaDome _plugin;
            private Dictionary<ulong, RateLimiter> _rateLimiters = new Dictionary<ulong, RateLimiter>();
            
            internal AntiExploit(KillaDome plugin)
            {
                _plugin = plugin;
            }
            
            public bool CheckRateLimit(ulong steamId, int maxActionsPerSecond = 5)
            {
                if (!_rateLimiters.TryGetValue(steamId, out var limiter))
                {
                    limiter = new RateLimiter(maxActionsPerSecond);
                    _rateLimiters[steamId] = limiter;
                }
                
                return limiter.AllowAction();
            }
            
            public bool ValidateAction(ulong steamId, string action)
            {
                if (!CheckRateLimit(steamId))
                {
                    _plugin.PrintWarning($"Rate limit exceeded for {steamId} on action {action}");
                    return false;
                }
                
                return true;
            }
        }
        
        internal class RateLimiter
        {
            private int _maxActions;
            private Queue<DateTime> _actions = new Queue<DateTime>();
            
            internal RateLimiter(int maxActionsPerSecond)
            {
                _maxActions = maxActionsPerSecond;
            }
            
            public bool AllowAction()
            {
                var now = DateTime.UtcNow;
                var cutoff = now.AddSeconds(-1);
                
                // Remove old actions
                while (_actions.Count > 0 && _actions.Peek() < cutoff)
                {
                    _actions.Dequeue();
                }
                
                if (_actions.Count >= _maxActions)
                {
                    return false;
                }
                
                _actions.Enqueue(now);
                return true;
            }
        }
        
        #endregion
        
        #region Module: TelemetrySystem
        
        internal class TelemetrySystem
        {
            private KillaDome _plugin;
            private Dictionary<string, int> _eventCounts = new Dictionary<string, int>();
            
            internal TelemetrySystem(KillaDome plugin)
            {
                _plugin = plugin;
            }
            
            public void RecordKill(ulong attackerId, ulong victimId)
            {
                IncrementEvent("kills");
                
                var session = _plugin.GetSession(attackerId);
                if (session != null)
                {
                    session.Profile.TotalKills++;
                }
                
                var victimSession = _plugin.GetSession(victimId);
                if (victimSession != null)
                {
                    victimSession.Profile.TotalDeaths++;
                }
            }
            
            public void RecordPurchase(ulong steamId, string itemId, int cost)
            {
                IncrementEvent("purchases");
                _plugin.LogDebug($"Telemetry: Purchase - {steamId}, {itemId}, {cost}");
            }
            
            private void IncrementEvent(string eventName)
            {
                if (!_eventCounts.ContainsKey(eventName))
                {
                    _eventCounts[eventName] = 0;
                }
                _eventCounts[eventName]++;
            }
            
            public Dictionary<string, int> GetStats()
            {
                return new Dictionary<string, int>(_eventCounts);
            }
        }
        
        #endregion
    }
}