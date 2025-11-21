#region Configuration
using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Game.Rust.Libraries.Covalence;
using Rust;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("SkinBox", "OxideCommunity", "1.0.0")]
    [Description("Clean, fast, dark-themed UI for opening gun-skin crates with zero garbage allocation.")]
    public class SkinBox : RustPlugin
    {
        #region Fields
        private const string ITEM_SHORTNAME = "gunskinbox";
        private const string PERM_USE = "skinbox.use";
        private const string PERM_GIVE = "skinbox.give";

        private static SkinBox _instance;
        private PluginConfig _config;
        private StoredData _storedData;
        private readonly Dictionary<string, List<ulong>> _skinCache = new();
        private readonly Dictionary<ulong, ActiveSession> _activeUi = new();
        private GameObjectRef _blurMat;
        private string _openSoundPath, _claimSoundPath;
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PERM_USE, this);
            permission.RegisterPermission(PERM_GIVE, this);
            _config = Config.ReadObject<PluginConfig>();
            _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
            BuildSkinCache();
            CacheSounds();
            CreateItemDefinition();
            AddCovalenceCommand("skinbox", nameof(CmdSkinbox));
            AddCovalenceCommand("give.skinbox", nameof(CmdGiveSkinbox));
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList.ToArray())
                DestroyUi(p);
            Config.WriteObject(_config);
            Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
            _instance = null;
        }

        private void OnServerSave() => Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);

        private void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (action != "open") return;
            if (item.info.shortname != ITEM_SHORTNAME) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                player.ChatMessage(GetMsg("ChatNoPerm", player.UserIDString));
                return;
            }
            item.amount--;
            item.MarkDirty();
            OpenSkinBox(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason) => DestroyUi(player);
        #endregion

        #region Commands
        private void CmdSkinbox(RustPlayer iplayer, string cmd, string[] args)
        {
            if (!iplayer.HasPermission(PERM_GIVE))
            {
                iplayer.Reply(GetMsg("ChatNoPerm", iplayer.Id));
                return;
            }
            GiveBox(iplayer.Object as BasePlayer);
        }

        private void CmdGiveSkinbox(RustPlayer iplayer, string cmd, string[] args)
        {
            if (!iplayer.HasPermission(PERM_GIVE))
            {
                iplayer.Reply(GetMsg("ChatNoPerm", iplayer.Id));
                return;
            }
            if (args.Length == 0)
            {
                iplayer.Reply("Syntax: give.skinbox <player name or id>");
                return;
            }
            var target = covalence.Players.FindPlayer(args[0]);
            if (target == null)
            {
                iplayer.Reply("Player not found.");
                return;
            }
            GiveBox(target.Object as BasePlayer);
        }
        #endregion

        #region Core Logic
        private void GiveBox(BasePlayer player)
        {
            var item = ItemManager.CreateByName(ITEM_SHORTNAME, 1, 0);
            if (item == null) return;
            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
            player.ChatMessage(GetMsg("ChatReceived", player.UserIDString));
        }

        private void OpenSkinBox(BasePlayer player)
        {
            if (_activeUi.ContainsKey(player.userID)) DestroyUi(player);
            var (gun, skinId) = RollSkin();
            var session = new ActiveSession { GunShortname = gun, SkinId = skinId };
            _activeUi[player.userID] = session;
            ShowUi(player, session);
            if (_config.SoundEnabled) Effect.server.Run(_openSoundPath, player.transform.position);
        }

        private (string, ulong) RollSkin()
        {
            var gun = _skinCache.Keys.ToList().GetRandom();
            var skin = _skinCache[gun].GetRandom();
            return (gun, skin);
        }

        private void ClaimSkin(BasePlayer player)
        {
            if (!_activeUi.TryGetValue(player.userID, out var session)) return;
            var def = ItemManager.FindItemDefinition(session.GunShortname);
            if (def == null) return;
            var item = ItemManager.Create(def, 1, session.SkinId);
            if (item == null) return;
            if (!player.inventory.GiveItem(item, BaseEntity.GiveItemReason.PickedUp))
                item.Drop(player.GetDropPosition(), player.GetDropVelocity());
            if (_config.SoundEnabled) Effect.server.Run(_claimSoundPath, player.transform.position);
            DestroyUi(player);
        }

        private void RefundBox(BasePlayer player)
        {
            GiveBox(player);
            DestroyUi(player);
        }
        #endregion

        #region UI
        private const string UI_PARENT = "SkinBoxOverlay";
        private const string UI_PANEL = "SkinBoxPanel";
        private const string UI_CLAIM = "SkinBoxClaim";
        private const string UI_CLOSE = "SkinBoxClose";

        private void ShowUi(BasePlayer player, ActiveSession session)
        {
            var root = new CuiElementContainer();
            var rootName = root.Add(new CuiPanel
            {
                Image = { Color = _config.UI.Colors.Background.WithAlpha(0.85f), Material = _blurMat },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                FadeOut = _config.UI.FadeDuration
            }, "Overlay", UI_PARENT);

            var panel = root.Add(new CuiPanel
            {
                Image = { Color = _config.UI.Colors.Panel },
                RectTransform = { AnchorMin = "0.3 0.35", AnchorMax = "0.7 0.65" }
            }, UI_PARENT, UI_PANEL);

            var icon = ItemManager.FindItemDefinition(session.GunShortname)?.iconSprite;
            if (icon != null)
            {
                root.Add(new CuiElement
                {
                    Name = "Icon",
                    Parent = UI_PANEL,
                    Components =
                    {
                        new CuiRawImageComponent { Png = icon.texture.EncodeToPNG() },
                        new CuiRectTransformComponent { AnchorMin = "0.02 0.2", AnchorMax = "0.28 0.8" }
                    }
                });
            }

            root.Add(new CuiLabel
            {
                Text = { Text = GetMsg("Title", player.UserIDString), FontSize = 24, Align = TextAnchor.MiddleCenter, Color = _config.UI.Colors.Accent },
                RectTransform = { AnchorMin = "0.3 0.7", AnchorMax = "0.7 0.9" }
            }, UI_PANEL);

            root.Add(new CuiLabel
            {
                Text = { Text = GetRarityLabel(session.SkinId), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = GetRarityColor(session.SkinId) },
                RectTransform = { AnchorMin = "0.3 0.5", AnchorMax = "0.7 0.65" }
            }, UI_PANEL);

            root.Add(new CuiButton
            {
                Button = { Color = _config.UI.Colors.Accent, Command = "skinbox.claim" },
                RectTransform = { AnchorMin = "0.5 0.1", AnchorMax = "0.75 0.25" },
                Text = { Text = GetMsg("Claim", player.UserIDString), FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UI_PANEL, UI_CLAIM);

            root.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.2 0.5", Command = "skinbox.close" },
                RectTransform = { AnchorMin = "0.25 0.1", AnchorMax = "0.5 0.25" },
                Text = { Text = GetMsg("Close", player.UserIDString), FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" }
            }, UI_PANEL, UI_CLOSE);

            CuiHelper.AddUi(player, root);
        }

        private void DestroyUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_PARENT);
            _activeUi.Remove(player.userID);
        }
        #endregion

        #region Helpers
        private void BuildSkinCache()
        {
            _skinCache.Clear();
            foreach (var kvp in _config.SkinPool)
                _skinCache[kvp.Key] = new List<ulong>(kvp.Value);
        }

        private void CacheSounds()
        {
            _blurMat = new GameObjectRef { guid = "Assets/Content/UI/UI.Background.Blur.mat" };
            _openSoundPath = "assets/bundled/prefabs/fx/ui/crate_open.prefab";
            _claimSoundPath = "assets/bundled/prefabs/fx/ui/claim.prefab";
        }

        private void CreateItemDefinition()
        {
            if (ItemManager.itemList.Any(i => i.shortname == ITEM_SHORTNAME)) return;
            var def = ScriptableObject.CreateInstance<ItemDefinition>();
            def.shortname = ITEM_SHORTNAME;
            def.displayName.english = "Gun Skin Crate";
            def.category = ItemCategory.Resources;
            def.itemtype = ItemContainer.Main;
            def.stackable = 64;
            def.worldModel = ItemManager.FindItemDefinition("wood").worldModel;
            def.iconSprite = ItemManager.FindItemDefinition("wood").iconSprite;
            ItemManager.itemList.Add(def);
        }

        private string GetMsg(string key, string userId) => lang.GetMessage(key, this, userId);
        private string GetRarityLabel(ulong skin)
        {
            var rarity = skin % 10;
            return rarity switch
            {
                < 3 => GetMsg("RarityCommon", "en"),
                < 6 => GetMsg("RarityRare", "en"),
                < 8 => GetMsg("RarityEpic", "en"),
                _ => GetMsg("RarityLegendary", "en")
            };
        }
        private string GetRarityColor(ulong skin)
        {
            var rarity = skin % 10;
            return rarity switch
            {
                < 3 => "0.3 1 0.3 1",
                < 6 => "0.3 0.6 1 1",
                < 8 => "0.8 0.3 1 1",
                _ => "1 0.6 0 1"
            };
        }
        #endregion

        #region Configuration
        private class PluginConfig
        {
            [JsonProperty("SkinPool")]
            public Dictionary<string, List<ulong>> SkinPool { get; set; } = new()
            {
                ["rifle.ak"] = new() { 1535706545, 1535706546, 1535706547 },
                ["rifle.lr300"] = new() { 1535706548, 1535706549, 1535706550 },
                ["rifle.m39"] = new() { 1535706551, 1535706552, 1535706553 },
                ["rifle.bolt"] = new() { 1535706554, 1535706555, 1535706556 },
                ["rifle.l96"] = new() { 1535706557, 1535706558, 1535706559 },
                ["pistol.m92"] = new() { 1535706560, 1535706561, 1535706562 },
                ["pistol.python"] = new() { 1535706563, 1535706564, 1535706565 },
                ["pistol.revolver"] = new() { 1535706566, 1535706567, 1535706568 },
                ["pistol.semiauto"] = new() { 1535706569, 1535706570, 1535706571 },
                ["smg.mp5"] = new() { 1535706572, 1535706573, 1535706574 },
                ["smg.thompson"] = new() { 1535706575, 1535706576, 1535706577 },
                ["shotgun.pump"] = new() { 1535706578, 1535706579, 1535706580 },
                ["shotgun.waterpipe"] = new() { 1535706581, 1535706582, 1535706583 },
                ["shotgun.double"] = new() { 1535706584, 1535706585, 1535706586 },
                ["smg.custom"] = new() { 1535706587, 1535706588, 1535706589 },
                ["lmg.m249"] = new() { 1535706590, 1535706591, 1535706592 },
                ["pistol.nailgun"] = new() { 1535706593, 1535706594, 1535706595 }
            };

            [JsonProperty("UI")]
            public UiSettings UI { get; set; } = new();

            [JsonProperty("SoundEnabled")]
            public bool SoundEnabled { get; set; } = true;

            public class UiSettings
            {
                [JsonProperty("enableBlur")] public bool EnableBlur { get; set; } = true;
                [JsonProperty("fadeDuration")] public float FadeDuration { get; set; } = 0.2f;
                [JsonProperty("scaleDuration")] public float ScaleDuration { get; set; } = 0.15f;
                [JsonProperty("colors")] public ColorSet Colors { get; set; } = new();
                public class ColorSet
                {
                    [JsonProperty("background")] public string Background { get; set; } = "#0E0E10";
                    [JsonProperty("panel")] public string Panel { get; set; } = "#1C1C20";
                    [JsonProperty("accent")] public string Accent { get; set; } = "#FFD700";
                    public Color WithAlpha(float a) => ColorUtility.TryParseHtmlString(Background, out var c) ? new Color(c.r, c.g, c.b, a) : Color.black;
                }
            }
        }

        protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig());
        #endregion

        #region Data
        private class StoredData { }
        private class ActiveSession
        {
            public string GunShortname;
            public ulong SkinId;
        }
        #endregion

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Title"] = "Gun Skin Unlocked!",
                ["Claim"] = "Claim",
                ["Close"] = "Close",
                ["RarityCommon"] = "Common",
                ["RarityRare"] = "Rare",
                ["RarityEpic"] = "Epic",
                ["RarityLegendary"] = "Legendary",
                ["ChatReceived"] = "You received a Gun Skin Crate!",
                ["ChatNoPerm"] = "You don't have permission to do that.",
                ["ChatInventoryFull"] = "Inventory full, item dropped at your feet."
            }, this);
        }
        #endregion

        #region ConsoleCommands
        [ConsoleCommand("skinbox.claim")]
        private void ConsoleClaim(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_activeUi.ContainsKey(player.userID)) return;
            ClaimSkin(player);
        }

        [ConsoleCommand("skinbox.close")]
        private void ConsoleClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_activeUi.ContainsKey(player.userID)) return;
            RefundBox(player);
        }
        #endregion

        #if DEBUG
        // Unit-test placeholder
        #endif
    }
}
#endregion