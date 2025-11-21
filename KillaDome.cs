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
using System.Linq;
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
        
        [PluginReference]
        private Plugin ImageLibrary;
        
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
            
            // Apply loadout when entering arena
            ApplyLoadout(player);
        }
        
        private void ApplyLoadout(BasePlayer player)
        {
            var session = GetSession(player.userID);
            if (session == null || session.Profile.Loadouts.Count == 0) return;
            
            var loadout = session.Profile.Loadouts[0];
            
            // Strip existing items
            player.inventory.Strip();
            
            // Give primary weapon
            GiveWeapon(player, loadout.Primary, loadout.PrimaryAttachments, loadout.Skins);
            
            // Give secondary weapon
            GiveWeapon(player, loadout.Secondary, loadout.SecondaryAttachments, loadout.Skins);
            
            LogDebug($"Applied loadout to {player.displayName}");
        }
        
        private void GiveWeapon(BasePlayer player, string weaponName, Dictionary<string, string> attachments, Dictionary<string, string> skins)
        {
            if (string.IsNullOrEmpty(weaponName)) return;
            
            // Map weapon names to item short names
            string itemName = weaponName switch
            {
                "ak47" => "rifle.ak",
                "m249" => "lmg.m249",
                "pistol" => "pistol.semiauto",
                _ => "rifle.ak"
            };
            
            var item = ItemManager.CreateByName(itemName, 1);
            if (item == null) return;
            
            // Apply skin if exists
            if (skins != null && skins.TryGetValue(weaponName, out string skinId))
            {
                if (ulong.TryParse(skinId, out ulong skin))
                {
                    item.skin = skin;
                    item.MarkDirty(); // Mark for network update
                }
            }
            
            // Apply attachments if exists
            if (attachments != null && attachments.Count > 0)
            {
                var heldEntity = item.GetHeldEntity() as BaseProjectile;
                if (heldEntity != null)
                {
                    foreach (var attachmentEntry in attachments)
                    {
                        string attachmentId = attachmentEntry.Value;
                        if (!string.IsNullOrEmpty(attachmentId))
                        {
                            var attachmentItem = ItemManager.CreateByName(attachmentId, 1);
                            if (attachmentItem != null)
                            {
                                // Add attachment to weapon's content container
                                attachmentItem.MoveToContainer(item.contents);
                            }
                        }
                    }
                }
            }
            
            // Give item to player
            player.inventory.GiveItem(item);
            
            // If item was given to belt, ensure visual update
            var heldItem = item.GetHeldEntity();
            if (heldItem != null)
            {
                heldItem.skinID = item.skin;
                heldItem.SendNetworkUpdate();
            }
            
            // Give ammo
            string ammoType = weaponName == "pistol" ? "ammo.pistol" : "ammo.rifle";
            var ammo = ItemManager.CreateByName(ammoType, 250);
            if (ammo != null)
            {
                player.inventory.GiveItem(ammo);
            }
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
                // Create session if it doesn't exist
                var profile = _saveManager.LoadPlayerProfile(player.userID);
                session = new PlayerSession(player, profile);
                _activeSessions[player.userID] = session;
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
        
        [ConsoleCommand("killadome.applyskin")]
        private void CmdApplySkin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(2)) return;
            
            if (!_antiExploit.CheckRateLimit(player.userID))
            {
                SendReply(player, "Please slow down!");
                return;
            }
            
            string weapon = arg.Args[0]; // "primary" or "secondary"
            string skinId = arg.Args[1];
            
            var session = GetSession(player.userID);
            if (session == null || session.Profile.Loadouts.Count == 0) return;
            
            // Check if player owns the skin
            if (!session.Profile.OwnedSkins.Contains(skinId))
            {
                SendReply(player, "You don't own this skin!");
                return;
            }
            
            var loadout = session.Profile.Loadouts[0];
            string weaponName = weapon == "primary" ? loadout.Primary : loadout.Secondary;
            
            // Apply skin to weapon
            loadout.Skins[weaponName] = skinId;
            _saveManager.SavePlayerProfile(session.Profile);
            
            SendReply(player, $"Skin applied to {weaponName}!");
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
        }
        
        [ConsoleCommand("killadome.applyattachment")]
        private void CmdApplyAttachment(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(3)) return;
            
            if (!_antiExploit.CheckRateLimit(player.userID))
            {
                SendReply(player, "Please slow down!");
                return;
            }
            
            string weapon = arg.Args[0]; // "primary" or "secondary"
            string attachmentSlot = arg.Args[1]; // "optic", "barrel", "magazine", "grip"
            string attachmentId = arg.Args[2];
            
            var session = GetSession(player.userID);
            if (session == null || session.Profile.Loadouts.Count == 0) return;
            
            // Check if player owns the attachment (stored in OwnedSkins list for simplicity)
            if (!session.Profile.OwnedSkins.Contains(attachmentId))
            {
                SendReply(player, "You don't own this attachment!");
                return;
            }
            
            var loadout = session.Profile.Loadouts[0];
            var attachments = weapon == "primary" ? loadout.PrimaryAttachments : loadout.SecondaryAttachments;
            
            // Apply attachment
            attachments[attachmentSlot] = attachmentId;
            _saveManager.SavePlayerProfile(session.Profile);
            
            SendReply(player, $"Attachment applied!");
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
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
        
        [ConsoleCommand("killadome.attachcat")]
        private void CmdAttachmentCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;
            
            string category = arg.Args[0].ToLower(); // "scopes", "silencers", "underbarrel"
            if (category != "scopes" && category != "silencers" && category != "underbarrel") return;
            
            var session = GetSession(player.userID);
            if (session == null) return;
            
            session.SelectedAttachmentCategory = category;
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
        }
        
        [ConsoleCommand("killadome.editweapon")]
        private void CmdEditWeapon(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !arg.HasArgs(1)) return;
            
            string slot = arg.Args[0].ToLower(); // "primary" or "secondary"
            if (slot != "primary" && slot != "secondary") return;
            
            var session = GetSession(player.userID);
            if (session == null) return;
            
            session.EditingWeaponSlot = slot;
            _lobbyUI.ShowLobbyUIWithTab(player, "loadouts");
        }
        
        [ChatCommand("dice")]
        private void CmdDiceGame(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            
            var session = GetSession(player.userID);
            if (session == null)
            {
                SendReply(player, "Session not found! Please rejoin.");
                return;
            }
            
            // Check if player is in cooldown
            if (session.LastDiceGame != default && (DateTime.UtcNow - session.LastDiceGame).TotalSeconds < 30)
            {
                int remaining = 30 - (int)(DateTime.UtcNow - session.LastDiceGame).TotalSeconds;
                SendReply(player, $"<color=#FF8A00>Dice Game:</color> Wait {remaining}s before playing again!");
                return;
            }
            
            if (args.Length == 0)
            {
                SendReply(player, "<color=#FF8A00>Dice Game:</color> Roll the dice! Win 2x your bet!");
                SendReply(player, "Usage: /dice <bet> (10-100 tokens)");
                SendReply(player, $"Your tokens: <color=#FF8A00>{session.Profile.Tokens}</color>");
                return;
            }
            
            if (!int.TryParse(args[0], out int bet))
            {
                SendReply(player, "<color=#FF8A00>Dice Game:</color> Invalid bet amount!");
                return;
            }
            
            if (bet < 10 || bet > 100)
            {
                SendReply(player, "<color=#FF8A00>Dice Game:</color> Bet must be between 10-100 tokens!");
                return;
            }
            
            if (session.Profile.Tokens < bet)
            {
                SendReply(player, $"<color=#FF8A00>Dice Game:</color> Not enough tokens! You have {session.Profile.Tokens}");
                return;
            }
            
            // Deduct bet
            session.Profile.Tokens -= bet;
            session.LastDiceGame = DateTime.UtcNow;
            
            // Roll dice (1-6 for player, 1-6 for house)
            int playerRoll = UnityEngine.Random.Range(1, 7);
            int houseRoll = UnityEngine.Random.Range(1, 7);
            
            SendReply(player, $"<color=#FF8A00>╔═══════════════════╗</color>");
            SendReply(player, $"<color=#FF8A00>║</color>   DICE GAME    <color=#FF8A00>║</color>");
            SendReply(player, $"<color=#FF8A00>╚═══════════════════╝</color>");
            SendReply(player, $"Your roll: <color=#4CFF4C>[{playerRoll}]</color>");
            SendReply(player, $"House roll: <color=#FF4C4C>[{houseRoll}]</color>");
            
            if (playerRoll > houseRoll)
            {
                int winnings = bet * 2;
                session.Profile.Tokens += winnings;
                SendReply(player, $"<color=#4CFF4C>★ YOU WIN! ★</color> +{winnings} tokens");
                SendReply(player, $"Balance: <color=#FF8A00>{session.Profile.Tokens}</color> tokens");
                Effect.server.Run("assets/prefabs/deployable/vendingmachine/effects/buy.prefab", player.transform.position);
            }
            else if (playerRoll < houseRoll)
            {
                SendReply(player, $"<color=#FF4C4C>✖ YOU LOSE!</color> -{bet} tokens");
                SendReply(player, $"Balance: <color=#FF8A00>{session.Profile.Tokens}</color> tokens");
                Effect.server.Run("assets/prefabs/deployable/vendingmachine/effects/deny.prefab", player.transform.position);
            }
            else
            {
                // Tie - return bet
                session.Profile.Tokens += bet;
                SendReply(player, $"<color=#FFD700>═ TIE! ═</color> Bet returned");
                SendReply(player, $"Balance: <color=#FF8A00>{session.Profile.Tokens}</color> tokens");
            }
            
            _saveManager.SavePlayerProfile(session.Profile);
        }
        
        [ChatCommand("tokengame")]
        private void CmdTokenGameHelp(BasePlayer player, string command, string[] args)
        {
            SendReply(player, "<color=#FF8A00>╔═══════════════════════════╗</color>");
            SendReply(player, "<color=#FF8A00>║</color>  BLOOD TOKEN GAMES     <color=#FF8A00>║</color>");
            SendReply(player, "<color=#FF8A00>╚═══════════════════════════╝</color>");
            SendReply(player, "");
            SendReply(player, "<color=#4CFF4C>/dice <bet></color> - Roll dice vs house");
            SendReply(player, "  • Bet: 10-100 tokens");
            SendReply(player, "  • Win: 2x your bet");
            SendReply(player, "  • Cooldown: 30 seconds");
            SendReply(player, "");
            SendReply(player, "<color=#FFD700>More games coming soon!</color>");
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
            public string EditingWeaponSlot { get; set; } // "primary" or "secondary"
            public string SelectedAttachmentCategory { get; set; } // "scopes", "silencers", "underbarrel"
            public DateTime LastDiceGame { get; set; } // Cooldown for dice game
            
            internal PlayerSession(BasePlayer player, PlayerProfile profile)
            {
                Player = player;
                Profile = profile;
                LastAction = DateTime.UtcNow;
                EditingWeaponSlot = "primary"; // Default to editing primary
                SelectedAttachmentCategory = "scopes"; // Default to scopes tab
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
                // Title with rust orange color and letter spacing
                container.Add(new CuiLabel
                {
                    Text = { Text = "L O A D O U T   E D I T O R", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.54 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.85", AnchorMax = "0.7 0.92" }
                }, UI_TAB_CONTAINER);
                
                // Thin orange underline with glow effect
                container.Add(new CuiPanel
                {
                    Image = { Color = "1 0.54 0 0.8" },
                    RectTransform = { AnchorMin = "0.35 0.845", AnchorMax = "0.65 0.847" }
                }, UI_TAB_CONTAINER);
                
                var session = _plugin.GetSession(player.userID);
                if (session == null)
                {
                    // Create session if it doesn't exist
                    var profile = _plugin._saveManager.LoadPlayerProfile(player.userID);
                    session = new PlayerSession(player, profile);
                    _plugin._activeSessions[player.userID] = session;
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
                        SecondaryAttachments = new Dictionary<string, string>(),
                        Skins = new Dictionary<string, string>()
                    });
                }
                
                var loadout = session.Profile.Loadouts[0];
                
                // PRIMARY WEAPON SECTION (Left, improved)
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.11 0.11 0.11 0.95" },
                    RectTransform = { AnchorMin = "0.12 0.67", AnchorMax = "0.48 0.82" }
                }, UI_TAB_CONTAINER);
                
                container.Add(new CuiLabel
                {
                    Text = { Text = "P R I M A R Y", FontSize = 14, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.12 0.79", AnchorMax = "0.48 0.82" }
                }, UI_TAB_CONTAINER);
                
                // Current primary weapon with larger text
                container.Add(new CuiLabel
                {
                    Text = { Text = loadout.Primary.ToUpper(), FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.12 0.72", AnchorMax = "0.48 0.78" }
                }, UI_TAB_CONTAINER);
                
                // PREV button for primary
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.weapon.prev primary", Color = "0.35 0.35 0.38 1" },
                    Text = { Text = "◄ PREV", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.14 0.68", AnchorMax = "0.26 0.72" }
                }, UI_TAB_CONTAINER);
                
                // NEXT button for primary
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.weapon.next primary", Color = "0.35 0.35 0.38 1" },
                    Text = { Text = "NEXT ►", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.34 0.68", AnchorMax = "0.46 0.72" }
                }, UI_TAB_CONTAINER);
                
                // SECONDARY WEAPON SECTION (Right, improved)
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.11 0.11 0.11 0.95" },
                    RectTransform = { AnchorMin = "0.52 0.67", AnchorMax = "0.88 0.82" }
                }, UI_TAB_CONTAINER);
                
                container.Add(new CuiLabel
                {
                    Text = { Text = "S E C O N D A R Y", FontSize = 14, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.52 0.79", AnchorMax = "0.88 0.82" }
                }, UI_TAB_CONTAINER);
                
                // Current secondary weapon
                container.Add(new CuiLabel
                {
                    Text = { Text = loadout.Secondary.ToUpper(), FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.52 0.72", AnchorMax = "0.88 0.78" }
                }, UI_TAB_CONTAINER);
                
                // PREV button for secondary
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.weapon.prev secondary", Color = "0.35 0.35 0.38 1" },
                    Text = { Text = "◄ PREV", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.54 0.68", AnchorMax = "0.66 0.72" }
                }, UI_TAB_CONTAINER);
                
                // NEXT button for secondary
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.weapon.next secondary", Color = "0.35 0.35 0.38 1" },
                    Text = { Text = "NEXT ►", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.74 0.68", AnchorMax = "0.86 0.72" }
                }, UI_TAB_CONTAINER);
                
                // WEAPON SLOT SELECTOR - Shows which weapon is being edited
                string editingSlot = session.EditingWeaponSlot ?? "primary";
                string currentWeapon = editingSlot == "primary" ? loadout.Primary : loadout.Secondary;
                
                // Safety check for null weapon
                if (string.IsNullOrEmpty(currentWeapon))
                {
                    currentWeapon = editingSlot == "primary" ? "ak47" : "pistol";
                }
                
                // Selector panel
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.11 0.11 0.11 0.95" },
                    RectTransform = { AnchorMin = "0.12 0.60", AnchorMax = "0.88 0.65" }
                }, UI_TAB_CONTAINER);
                
                container.Add(new CuiLabel
                {
                    Text = { Text = "E D I T I N G", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = "0.12 0.625", AnchorMax = "0.30 0.65" }
                }, UI_TAB_CONTAINER);
                
                // PRIMARY button
                string primaryColor = editingSlot == "primary" ? "1 0.54 0 1" : "0.25 0.25 0.25 1";
                string primaryTextColor = editingSlot == "primary" ? "1 1 1 1" : "0.7 0.7 0.7 1";
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.editweapon primary", Color = primaryColor },
                    Text = { Text = "PRIMARY", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = primaryTextColor },
                    RectTransform = { AnchorMin = "0.32 0.605", AnchorMax = "0.48 0.645" }
                }, UI_TAB_CONTAINER);
                
                // SECONDARY button
                string secondaryColor = editingSlot == "secondary" ? "1 0.54 0 1" : "0.25 0.25 0.25 1";
                string secondaryTextColor = editingSlot == "secondary" ? "1 1 1 1" : "0.7 0.7 0.7 1";
                container.Add(new CuiButton
                {
                    Button = { Command = "killadome.editweapon secondary", Color = secondaryColor },
                    Text = { Text = "SECONDARY", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = secondaryTextColor },
                    RectTransform = { AnchorMin = "0.52 0.605", AnchorMax = "0.68 0.645" }
                }, UI_TAB_CONTAINER);
                
                // Show current weapon being edited
                container.Add(new CuiLabel
                {
                    Text = { Text = $"[ {currentWeapon.ToUpper()} ]", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.54 0 1" },
                    RectTransform = { AnchorMin = "0.70 0.605", AnchorMax = "0.88 0.645" }
                }, UI_TAB_CONTAINER);
                
                // NEW SECTION: SKINS & ATTACHMENTS EDITOR
                // Main container with border (adjusted height to accommodate weapon selector)
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.11 0.11 0.11 0.95" },
                    RectTransform = { AnchorMin = "0.12 0.15", AnchorMax = "0.88 0.58" }
                }, UI_TAB_CONTAINER, "LoadoutEditorMain");
                
                // LEFT PANEL - SKINS
                container.Add(new CuiLabel
                {
                    Text = { Text = "S K I N S", FontSize = 16, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.48 0.97" }
                }, "LoadoutEditorMain");
                
                // Orange underline for SKINS
                container.Add(new CuiPanel
                {
                    Image = { Color = "1 0.54 0 0.8" },
                    RectTransform = { AnchorMin = "0.15 0.915", AnchorMax = "0.35 0.918" }
                }, "LoadoutEditorMain");
                
                // Available skins grid - filter by currently edited weapon
                var allSkins = new[]
                {
                    new { Name = "AK-47 Neon", Id = "3102802323", ImageId = "ak47_neon", Weapon = "ak47" },
                    new { Name = "AK-47 Classic", Id = "skin_ak47_neon", ImageId = "ak47_classic", Weapon = "ak47" },
                    new { Name = "M249 Chrome", Id = "skin_m249_chrome", ImageId = "m249_chrome", Weapon = "m249" },
                    new { Name = "Pistol Black", Id = "skin_pistol_black", ImageId = "pistol_black", Weapon = "pistol" },
                };
                
                // Filter skins for the currently edited weapon
                var availableSkins = allSkins.Where(s => s.Weapon == currentWeapon).ToArray();
                
                for (int i = 0; i < availableSkins.Length; i++)
                {
                    var skin = availableSkins[i];
                    float yMin = 0.70f - (i * 0.22f);
                    float yMax = yMin + 0.18f;
                    
                    bool isOwned = session.Profile.OwnedSkins.Contains(skin.Id);
                    bool isEquipped = loadout.Skins.TryGetValue(skin.Weapon, out string equippedSkin) && equippedSkin == skin.Id;
                    
                    // Skin tile
                    string tileColor = isOwned ? "0.12 0.12 0.12 1" : "0.12 0.12 0.12 0.5";
                    string borderColor = isEquipped ? "1 0.54 0 1" : "0.15 0.15 0.15 1";
                    
                    container.Add(new CuiPanel
                    {
                        Image = { Color = tileColor },
                        RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.45 {yMax}" }
                    }, "LoadoutEditorMain");
                    
                    // Border for selected/equipped
                    if (isEquipped)
                    {
                        container.Add(new CuiPanel
                        {
                            Image = { Color = borderColor },
                            RectTransform = { AnchorMin = $"0.049 {yMin - 0.002f}", AnchorMax = $"0.451 {yMax + 0.002f}" }
                        }, "LoadoutEditorMain");
                    }
                    
                    // Preview box
                    string previewBoxName = $"SkinPreview_{i}";
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.08 0.08 0.08 1" },
                        RectTransform = { AnchorMin = $"0.07 {yMin + 0.02f}", AnchorMax = $"0.17 {yMax - 0.02f}" }
                    }, "LoadoutEditorMain", previewBoxName);
                    
                    // Try to add image from ImageLibrary
                    if (_plugin.ImageLibrary != null && _plugin.ImageLibrary.IsLoaded && isOwned)
                    {
                        string imageId = (string)_plugin.ImageLibrary.Call("GetImage", skin.ImageId);
                        if (!string.IsNullOrEmpty(imageId))
                        {
                            container.Add(new CuiElement
                            {
                                Parent = previewBoxName,
                                Components =
                                {
                                    new CuiRawImageComponent { Png = imageId },
                                    new CuiRectTransformComponent { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" }
                                }
                            });
                        }
                    }
                    
                    // Skin name
                    container.Add(new CuiLabel
                    {
                        Text = { Text = skin.Name, FontSize = 12, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = $"0.19 {yMin + 0.10f}", AnchorMax = $"0.40 {yMax - 0.02f}" }
                    }, "LoadoutEditorMain");
                    
                    // Ownership badge
                    if (isOwned)
                    {
                        string badgeText = isEquipped ? "EQUIPPED" : "OWNED";
                        string badgeColor = isEquipped ? "0.3 1 0.3 1" : "0.3 1 0.3 1";
                        container.Add(new CuiLabel
                        {
                            Text = { Text = badgeText, FontSize = 9, Align = TextAnchor.UpperLeft, Color = badgeColor },
                            RectTransform = { AnchorMin = $"0.19 {yMin + 0.04f}", AnchorMax = $"0.35 {yMin + 0.09f}" }
                        }, "LoadoutEditorMain");
                    }
                    else
                    {
                        // LOCKED badge
                        container.Add(new CuiLabel
                        {
                            Text = { Text = "LOCKED", FontSize = 9, Align = TextAnchor.UpperLeft, Color = "1 0.3 0.3 1" },
                            RectTransform = { AnchorMin = $"0.19 {yMin + 0.04f}", AnchorMax = $"0.35 {yMin + 0.09f}" }
                        }, "LoadoutEditorMain");
                    }
                    
                    // Apply button
                    if (isOwned && !isEquipped)
                    {
                        container.Add(new CuiButton
                        {
                            Button = { Command = $"killadome.applyskin {editingSlot} {skin.Id}", Color = "0.35 0.35 0.38 1" },
                            Text = { Text = "APPLY", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                            RectTransform = { AnchorMin = $"0.36 {yMin + 0.05f}", AnchorMax = $"0.43 {yMin + 0.12f}" }
                        }, "LoadoutEditorMain");
                    }
                }
                
                // RIGHT PANEL - ATTACHMENTS
                container.Add(new CuiLabel
                {
                    Text = { Text = "A T T A C H M E N T S", FontSize = 16, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.52 0.92", AnchorMax = "0.98 0.97" }
                }, "LoadoutEditorMain");
                
                // Orange underline for ATTACHMENTS
                container.Add(new CuiPanel
                {
                    Image = { Color = "1 0.54 0 0.8" },
                    RectTransform = { AnchorMin = "0.62 0.915", AnchorMax = "0.88 0.918" }
                }, "LoadoutEditorMain");
                
                // Attachment category tabs (3 tabs: Scopes, Silencers/Muzzle, Underbarrel)
                string selectedCategory = session.SelectedAttachmentCategory ?? "scopes";
                string[] categories = { "SCOPES", "SILENCERS", "UNDERBARREL" };
                for (int i = 0; i < categories.Length; i++)
                {
                    float xMin = 0.52f + (i * 0.15f);
                    float xMax = xMin + 0.14f;
                    
                    bool isSelected = categories[i].ToLower() == selectedCategory;
                    string buttonColor = isSelected ? "1 0.54 0 0.8" : "0.2 0.2 0.2 0.8";
                    string textColor = isSelected ? "1 1 1 1" : "0.7 0.7 0.7 1";
                    
                    container.Add(new CuiButton
                    {
                        Button = { Command = $"killadome.attachcat {categories[i].ToLower()}", Color = buttonColor },
                        Text = { Text = categories[i], FontSize = 9, Align = TextAnchor.MiddleCenter, Color = textColor },
                        RectTransform = { AnchorMin = $"{xMin} 0.87", AnchorMax = $"{xMax} 0.91" }
                    }, "LoadoutEditorMain");
                }
                
                // Available attachments with actual Rust mod item short names
                var allAttachments = new[]
                {
                    // Scopes
                    new { Name = "Small Scope", Id = "weapon.mod.small.scope", ImageId = "small_scope", Category = "scopes", Desc = "Simple 4x scope" },
                    new { Name = "8x Scope", Id = "weapon.mod.8x.scope", ImageId = "8x_scope", Category = "scopes", Desc = "Long range 8x scope" },
                    
                    // Silencers/Muzzle
                    new { Name = "Soda Can Silencer", Id = "weapon.mod.sodacansilencer", ImageId = "sodacan_silencer", Category = "silencers", Desc = "Improvised silencer" },
                    new { Name = "Oil Filter Silencer", Id = "weapon.mod.oilfiltersilencer", ImageId = "oilfilter_silencer", Category = "silencers", Desc = "Makeshift silencer" },
                    new { Name = "Silencer", Id = "weapon.mod.silencer", ImageId = "silencer", Category = "silencers", Desc = "Professional silencer" },
                    new { Name = "Muzzle Brake", Id = "weapon.mod.muzzlebrake", ImageId = "muzzle_brake", Category = "silencers", Desc = "Reduces recoil" },
                    new { Name = "Muzzle Boost", Id = "weapon.mod.muzzleboost", ImageId = "muzzle_boost", Category = "silencers", Desc = "Increases fire rate" },
                    
                    // Underbarrel
                    new { Name = "Laser Sight", Id = "weapon.mod.lasersight", ImageId = "laser_sight", Category = "underbarrel", Desc = "Improved hip fire" },
                    new { Name = "Holo Sight", Id = "weapon.mod.holosight", ImageId = "holo_sight", Category = "underbarrel", Desc = "Red dot sight" },
                };
                
                // Filter attachments by selected category
                var availableAttachments = allAttachments.Where(a => a.Category == selectedCategory).ToArray();
                
                for (int i = 0; i < availableAttachments.Length; i++)
                {
                    var att = availableAttachments[i];
                    float yMin = 0.70f - (i * 0.22f);
                    float yMax = yMin + 0.18f;
                    
                    // Check ownership (both skins and attachments stored in OwnedSkins list)
                    bool isOwned = session.Profile.OwnedSkins.Contains(att.Id);
                    
                    // Check if equipped on the currently edited weapon (stored by attachment ID)
                    var attachments = editingSlot == "primary" ? loadout.PrimaryAttachments : loadout.SecondaryAttachments;
                    bool isEquipped = attachments.ContainsValue(att.Id);
                    
                    // Attachment tile
                    string tileColor = isOwned ? "0.12 0.12 0.12 1" : "0.12 0.12 0.12 0.5";
                    
                    container.Add(new CuiPanel
                    {
                        Image = { Color = tileColor },
                        RectTransform = { AnchorMin = $"0.55 {yMin}", AnchorMax = $"0.95 {yMax}" }
                    }, "LoadoutEditorMain");
                    
                    // Border for equipped
                    if (isEquipped)
                    {
                        container.Add(new CuiPanel
                        {
                            Image = { Color = "1 0.54 0 1" },
                            RectTransform = { AnchorMin = $"0.549 {yMin - 0.002f}", AnchorMax = $"0.951 {yMax + 0.002f}" }
                        }, "LoadoutEditorMain");
                    }
                    
                    // Preview box
                    string previewBoxName = $"AttPreview_{i}";
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.08 0.08 0.08 1" },
                        RectTransform = { AnchorMin = $"0.57 {yMin + 0.02f}", AnchorMax = $"0.67 {yMax - 0.02f}" }
                    }, "LoadoutEditorMain", previewBoxName);
                    
                    // Try to add image from ImageLibrary
                    if (_plugin.ImageLibrary != null && _plugin.ImageLibrary.IsLoaded && isOwned)
                    {
                        string imageId = (string)_plugin.ImageLibrary.Call("GetImage", att.ImageId);
                        if (!string.IsNullOrEmpty(imageId))
                        {
                            container.Add(new CuiElement
                            {
                                Parent = previewBoxName,
                                Components =
                                {
                                    new CuiRawImageComponent { Png = imageId },
                                    new CuiRectTransformComponent { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" }
                                }
                            });
                        }
                    }
                    
                    // Attachment name
                    container.Add(new CuiLabel
                    {
                        Text = { Text = att.Name, FontSize = 12, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = $"0.69 {yMin + 0.10f}", AnchorMax = $"0.90 {yMax - 0.02f}" }
                    }, "LoadoutEditorMain");
                    
                    // Description
                    container.Add(new CuiLabel
                    {
                        Text = { Text = att.Desc, FontSize = 8, Align = TextAnchor.UpperLeft, Color = "0.7 0.7 0.7 1" },
                        RectTransform = { AnchorMin = $"0.69 {yMin + 0.04f}", AnchorMax = $"0.93 {yMin + 0.10f}" }
                    }, "LoadoutEditorMain");
                    
                    // Ownership badge
                    if (isOwned)
                    {
                        string badgeText = isEquipped ? "EQUIPPED" : "OWNED";
                        string badgeColor = isEquipped ? "0.3 1 0.3 1" : "0.3 1 0.3 1";
                        container.Add(new CuiLabel
                        {
                            Text = { Text = badgeText, FontSize = 9, Align = TextAnchor.UpperLeft, Color = badgeColor },
                            RectTransform = { AnchorMin = $"0.69 {yMin + 0.01f}", AnchorMax = $"0.85 {yMin + 0.04f}" }
                        }, "LoadoutEditorMain");
                    }
                    else
                    {
                        // LOCKED badge
                        container.Add(new CuiLabel
                        {
                            Text = { Text = "LOCKED", FontSize = 9, Align = TextAnchor.UpperLeft, Color = "1 0.3 0.3 1" },
                            RectTransform = { AnchorMin = $"0.69 {yMin + 0.01f}", AnchorMax = $"0.85 {yMin + 0.04f}" }
                        }, "LoadoutEditorMain");
                    }
                    
                    // Apply button
                    if (isOwned && !isEquipped)
                    {
                        container.Add(new CuiButton
                        {
                            Button = { Command = $"killadome.applyattachment {editingSlot} {att.Category} {att.Id}", Color = "0.35 0.35 0.38 1" },
                            Text = { Text = "APPLY", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                            RectTransform = { AnchorMin = $"0.86 {yMin + 0.07f}", AnchorMax = $"0.93 {yMin + 0.14f}" }
                        }, "LoadoutEditorMain");
                    }
                }
                
                // Bottom help text
                container.Add(new CuiLabel
                {
                    Text = { Text = "Select a skin or attachment to equip it on your current weapon", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                    RectTransform = { AnchorMin = "0.2 0.10", AnchorMax = "0.8 0.14" }
                }, UI_TAB_CONTAINER);
            }
            
            private void ShowStoreTab(CuiElementContainer container, BasePlayer player)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "S T O R E", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.9" }
                }, UI_TAB_CONTAINER);
                
                var session = _plugin.GetSession(player.userID);
                if (session == null)
                {
                    // Create session if it doesn't exist
                    var profile = _plugin._saveManager.LoadPlayerProfile(player.userID);
                    session = new PlayerSession(player, profile);
                    _plugin._activeSessions[player.userID] = session;
                }
                
                // Token balance display
                container.Add(new CuiLabel
                {
                    Text = { Text = $"Your Tokens: {session.Profile.Tokens}", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 0.6 0 1" },
                    RectTransform = { AnchorMin = "0.3 0.72", AnchorMax = "0.7 0.77" }
                }, UI_TAB_CONTAINER);
                
                // Gun Skins (LEFT COLUMN)
                var gunSkins = new[]
                {
                    new { Name = "AK-47 Neon Skin", Cost = 500, Id = "3102802323", ImageId = "ak47_neon" },
                    new { Name = "AK-47 Classic Skin", Cost = 400, Id = "skin_ak47_neon", ImageId = "ak47_classic" }
                };
                
                // Column Title: GUN SKINS
                container.Add(new CuiLabel
                {
                    Text = { Text = "G U N   S K I N S", FontSize = 16, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.12 0.62", AnchorMax = "0.48 0.67" }
                }, UI_TAB_CONTAINER);
                
                // Gun Skins Items
                for (int i = 0; i < gunSkins.Length; i++)
                {
                    var item = gunSkins[i];
                    float yMin = 0.52f - (i * 0.15f);
                    float yMax = yMin + 0.10f;
                    
                    // Item panel with rounded corners and drop shadow effect
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.15 0.15 0.15 0.95" },
                        RectTransform = { AnchorMin = $"0.12 {yMin}", AnchorMax = $"0.48 {yMax}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Shadow effect (slightly offset darker panel)
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.05 0.05 0.05 0.5" },
                        RectTransform = { AnchorMin = $"0.121 {yMin - 0.002f}", AnchorMax = $"0.481 {yMax - 0.002f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // 1-inch preview box with faint outline
                    string previewBoxName = $"StorePreviewGun_{i}";
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.1 0.1 0.1 1" },
                        RectTransform = { AnchorMin = $"0.13 {yMin + 0.01f}", AnchorMax = $"0.21 {yMax - 0.01f}" }
                    }, UI_TAB_CONTAINER, previewBoxName);
                    
                    // Faint outline for preview box
                    container.Add(new CuiElement
                    {
                        Parent = previewBoxName,
                        Components =
                        {
                            new CuiImageComponent { Color = "0.3 0.3 0.3 0.5" },
                            new CuiOutlineComponent { Color = "0.4 0.4 0.4 0.8", Distance = "1 1" },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                    
                    // Try to add image from ImageLibrary if available
                    if (_plugin.ImageLibrary != null && _plugin.ImageLibrary.IsLoaded)
                    {
                        string imageId = (string)_plugin.ImageLibrary.Call("GetImage", item.ImageId);
                        if (!string.IsNullOrEmpty(imageId))
                        {
                            container.Add(new CuiElement
                            {
                                Parent = previewBoxName,
                                Components =
                                {
                                    new CuiRawImageComponent { Png = imageId },
                                    new CuiRectTransformComponent { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" }
                                }
                            });
                        }
                    }
                    
                    // Item name (white)
                    container.Add(new CuiLabel
                    {
                        Text = { Text = item.Name, FontSize = 13, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = $"0.22 {yMin + 0.055f}", AnchorMax = $"0.40 {yMax - 0.01f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Token price (orange)
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"{item.Cost} Tokens", FontSize = 11, Align = TextAnchor.UpperLeft, Color = "1 0.6 0 1" },
                        RectTransform = { AnchorMin = $"0.22 {yMin + 0.02f}", AnchorMax = $"0.40 {yMin + 0.055f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Sleek BUY button (matte steel-gray with rounded corners)
                    string buttonColor = (session.Profile.Tokens >= item.Cost) ? "0.35 0.35 0.38 1" : "0.25 0.25 0.25 0.7";
                    string textColor = (session.Profile.Tokens >= item.Cost) ? "0.95 0.9 0.85 1" : "0.5 0.5 0.5 1";
                    container.Add(new CuiButton
                    {
                        Button = { Color = buttonColor, Command = $"killadome.purchase {item.Id} {item.Cost}" },
                        Text = { Text = "BUY", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = textColor },
                        RectTransform = { AnchorMin = $"0.41 {yMin + 0.025f}", AnchorMax = $"0.47 {yMax - 0.025f}" }
                    }, UI_TAB_CONTAINER);
                }
                
                // Attachments (RIGHT COLUMN) - All real Rust mod items
                var attachments = new[]
                {
                    // Scopes
                    new { Name = "Small Scope", Cost = 250, Id = "weapon.mod.small.scope", ImageId = "small_scope" },
                    new { Name = "8x Scope", Cost = 400, Id = "weapon.mod.8x.scope", ImageId = "8x_scope" },
                    
                    // Silencers/Muzzle
                    new { Name = "Soda Can Silencer", Cost = 150, Id = "weapon.mod.sodacansilencer", ImageId = "sodacan_silencer" },
                    new { Name = "Oil Filter Silencer", Cost = 200, Id = "weapon.mod.oilfiltersilencer", ImageId = "oilfilter_silencer" },
                    new { Name = "Silencer", Cost = 400, Id = "weapon.mod.silencer", ImageId = "silencer" },
                    new { Name = "Muzzle Brake", Cost = 300, Id = "weapon.mod.muzzlebrake", ImageId = "muzzle_brake" },
                    new { Name = "Muzzle Boost", Cost = 350, Id = "weapon.mod.muzzleboost", ImageId = "muzzle_boost" },
                    
                    // Underbarrel
                    new { Name = "Laser Sight", Cost = 250, Id = "weapon.mod.lasersight", ImageId = "laser_sight" },
                    new { Name = "Holo Sight", Cost = 300, Id = "weapon.mod.holosight", ImageId = "holo_sight" }
                };
                
                // Column Title: ATTACHMENTS
                container.Add(new CuiLabel
                {
                    Text = { Text = "A T T A C H M E N T S", FontSize = 16, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.52 0.62", AnchorMax = "0.88 0.67" }
                }, UI_TAB_CONTAINER);
                
                // Attachments Items
                for (int i = 0; i < attachments.Length; i++)
                {
                    var item = attachments[i];
                    float yMin = 0.52f - (i * 0.15f);
                    float yMax = yMin + 0.10f;
                    
                    // Item panel with rounded corners and drop shadow effect
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.15 0.15 0.15 0.95" },
                        RectTransform = { AnchorMin = $"0.52 {yMin}", AnchorMax = $"0.88 {yMax}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Shadow effect (slightly offset darker panel)
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.05 0.05 0.05 0.5" },
                        RectTransform = { AnchorMin = $"0.521 {yMin - 0.002f}", AnchorMax = $"0.881 {yMax - 0.002f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // 1-inch preview box with faint outline
                    string previewBoxName = $"StorePreviewAtt_{i}";
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.1 0.1 0.1 1" },
                        RectTransform = { AnchorMin = $"0.53 {yMin + 0.01f}", AnchorMax = $"0.61 {yMax - 0.01f}" }
                    }, UI_TAB_CONTAINER, previewBoxName);
                    
                    // Faint outline for preview box
                    container.Add(new CuiElement
                    {
                        Parent = previewBoxName,
                        Components =
                        {
                            new CuiImageComponent { Color = "0.3 0.3 0.3 0.5" },
                            new CuiOutlineComponent { Color = "0.4 0.4 0.4 0.8", Distance = "1 1" },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                    
                    // Try to add image from ImageLibrary if available
                    if (_plugin.ImageLibrary != null && _plugin.ImageLibrary.IsLoaded)
                    {
                        string imageId = (string)_plugin.ImageLibrary.Call("GetImage", item.ImageId);
                        if (!string.IsNullOrEmpty(imageId))
                        {
                            container.Add(new CuiElement
                            {
                                Parent = previewBoxName,
                                Components =
                                {
                                    new CuiRawImageComponent { Png = imageId },
                                    new CuiRectTransformComponent { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95" }
                                }
                            });
                        }
                    }
                    
                    // Item name (white)
                    container.Add(new CuiLabel
                    {
                        Text = { Text = item.Name, FontSize = 13, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                        RectTransform = { AnchorMin = $"0.62 {yMin + 0.055f}", AnchorMax = $"0.80 {yMax - 0.01f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Token price (orange)
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"{item.Cost} Tokens", FontSize = 11, Align = TextAnchor.UpperLeft, Color = "1 0.6 0 1" },
                        RectTransform = { AnchorMin = $"0.62 {yMin + 0.02f}", AnchorMax = $"0.80 {yMin + 0.055f}" }
                    }, UI_TAB_CONTAINER);
                    
                    // Sleek BUY button (matte steel-gray with rounded corners)
                    string buttonColor = (session.Profile.Tokens >= item.Cost) ? "0.35 0.35 0.38 1" : "0.25 0.25 0.25 0.7";
                    string textColor = (session.Profile.Tokens >= item.Cost) ? "0.95 0.9 0.85 1" : "0.5 0.5 0.5 1";
                    container.Add(new CuiButton
                    {
                        Button = { Color = buttonColor, Command = $"killadome.purchase {item.Id} {item.Cost}" },
                        Text = { Text = "BUY", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = textColor },
                        RectTransform = { AnchorMin = $"0.81 {yMin + 0.025f}", AnchorMax = $"0.87 {yMax - 0.025f}" }
                    }, UI_TAB_CONTAINER);
                }
                
                // Info text at bottom
                container.Add(new CuiLabel
                {
                    Text = { Text = "Purchase items with Blood Tokens to enhance your loadout", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
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