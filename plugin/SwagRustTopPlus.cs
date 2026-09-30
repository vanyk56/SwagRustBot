using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SwagRustTop+", "SwagRust", "1.0.0")]
    [Description("Статистика /top, таблица лидеров, Steam-Discord привязка и API для бота SwagRust")]
    public class SwagRustTopPlus : RustPlugin
    {
        const string PermUse = "swagrusttop.use";
        const string Ui = "SwagRustTopPlus.UI";
        ConfigData cfg;
        Dictionary<ulong, PlayerStats> players = new Dictionary<ulong, PlayerStats>();
        LinkData links = new LinkData();
        HttpListener listener;
        Thread listenerThread;
        System.Random random = new System.Random();
        readonly Dictionary<ulong, string> avatarUrls = new Dictionary<ulong, string>();
        readonly HashSet<ulong> loadingAvatars = new HashSet<ulong>();

        class ConfigData
        {
            public string Brand = "SwagRust";
            public string Accent = "0.85 0.60 0.55 1";
            public string ApiHost = "*";
            public int ApiPort = 28110;
            public string ApiSecret = "CHANGE-THIS-SECRET";
            public bool EnableApi = true;
            public bool EnablePush = true;
            public string PushUrl = "https://ВАШ-БОТ.up.railway.app/ingest";
            public int PushIntervalSeconds = 30;
            public string ConnectAddress = "157.85.87.131:28061";
            public bool ResetStatsOnNewSave = false;
            public int LinkCodeMinutes = 10;
            public float KillScore = 2f, DeathScore = -0.5f, FarmScorePerThousand = 0.2f, RaidScore = 0.5f;
        }
        class PlayerStats
        {
            public ulong SteamId;
            public string Name = "Unknown";
            public int Kills, Deaths, CratesOpened, BarrelsDestroyed, AnimalsKilled, NpcKilled;
            public long PlaytimeSeconds;
            public Dictionary<string, long> Farm = new Dictionary<string, long>();
            public Dictionary<string, long> Raids = new Dictionary<string, long>();
            public Dictionary<string, long> Farming = new Dictionary<string, long>();
            public Dictionary<string, int> WeaponKills = new Dictionary<string, int>();
            [JsonIgnore] public long SessionStarted;
            public long TotalFarm { get { return Farm.Values.Sum(); } }
            public long TotalRaids { get { return Raids.Values.Sum(); } }
            public double KD { get { return Deaths == 0 ? Kills : (double)Kills / Deaths; } }
        }
        class LinkCode { public ulong SteamId; public long ExpiresAt; }
        class PushClaim { public string code; public string discordId; }
        class PushResponse { public List<PushClaim> claims = new List<PushClaim>(); }
        class LinkData
        {
            public Dictionary<string, LinkCode> Codes = new Dictionary<string, LinkCode>();
            public Dictionary<string, ulong> DiscordToSteam = new Dictionary<string, ulong>();
        }

        protected override void LoadDefaultConfig() { cfg = new ConfigData(); SaveConfig(); }
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { cfg = Config.ReadObject<ConfigData>(); if (cfg == null) throw new Exception(); if (cfg.ApiHost == "127.0.0.1" || cfg.ApiHost == "localhost") { cfg.ApiHost = "*"; PrintWarning("ApiHost автоматически изменён на * для доступа Railway"); } }
            catch { PrintWarning("Повреждённый конфиг заменён стандартным"); LoadDefaultConfig(); }
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(cfg, true);

        void Init()
        {
            permission.RegisterPermission(PermUse, this);
            players = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerStats>>("SwagRustTopPlus/players") ?? new Dictionary<ulong, PlayerStats>();
            players = players.Where(x => x.Key.IsSteamId()).ToDictionary(x => x.Key, x => x.Value);
            links = Interface.Oxide.DataFileSystem.ReadObject<LinkData>("SwagRustTopPlus/links") ?? new LinkData();
        }
        void OnServerInitialized()
        {
            foreach (var p in BasePlayer.activePlayerList) StartSession(p);
            timer.Every(60f, TickOnline);
            timer.Every(300f, SaveData);
            if (cfg.EnableApi) StartApi();
            if (cfg.EnablePush && !string.IsNullOrEmpty(cfg.PushUrl) && cfg.PushUrl != "https://ВАШ-БОТ.up.railway.app/ingest") { timer.Once(3f, PushSnapshot); timer.Every(Math.Max(15, cfg.PushIntervalSeconds), PushSnapshot); }
        }
        void Unload()
        {
            TickOnline(); SaveData(); StopApi();
            foreach (var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, Ui);
        }
        void OnNewSave(string filename) { if (cfg.ResetStatsOnNewSave) { players.Clear(); SaveData(); } }
        void OnPlayerConnected(BasePlayer p) => StartSession(p);
        void OnPlayerDisconnected(BasePlayer p, string reason) { AddSession(p.userID); SaveData(); }
        void StartSession(BasePlayer p) { var s = Get(p.userID, p.displayName); s.Name = p.displayName; s.SessionStarted = Now(); }
        void TickOnline() { foreach (var p in BasePlayer.activePlayerList) AddSession(p.userID); }
        void AddSession(ulong id) { var s = Get(id); if (s.SessionStarted == 0) s.SessionStarted = Now(); var now = Now(); s.PlaytimeSeconds += Math.Max(0, now - s.SessionStarted); s.SessionStarted = now; }
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        PlayerStats Get(ulong id, string name = null)
        {
            PlayerStats s; if (!players.TryGetValue(id, out s)) players[id] = s = new PlayerStats { SteamId = id, Name = name ?? id.ToString() };
            if (!string.IsNullOrEmpty(name)) s.Name = name; return s;
        }
        void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("SwagRustTopPlus/players", players);
            Interface.Oxide.DataFileSystem.WriteObject("SwagRustTopPlus/links", links);
        }

        void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) { var p = entity as BasePlayer; if (p != null) Add(Get(p.userID, p.displayName).Farm, item.info.shortname, item.amount); }
        void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer p, Item item) { if (p != null) Add(Get(p.userID, p.displayName).Farm, item.info.shortname, item.amount); }
        void OnCollectiblePickup(Item item, BasePlayer p) { if (p != null) Add(Get(p.userID, p.displayName).Farm, item.info.shortname, item.amount); }
        void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer p) { if (p != null) Add(Get(p.userID, p.displayName).Farming, item.info.shortname, item.amount); }
        void OnLootEntity(BasePlayer p, LootContainer c)
        {
            if (p == null || c == null) return; var n = c.ShortPrefabName.ToLowerInvariant();
            if (n.Contains("crate") || n.Contains("supply_drop")) Get(p.userID, p.displayName).CratesOpened++;
        }
        void OnExplosiveThrown(BasePlayer p, BaseEntity e) { if (p != null && e != null) Add(Get(p.userID, p.displayName).Raids, e.ShortPrefabName, 1); }
        void OnRocketLaunched(BasePlayer p, BaseEntity e) { if (p != null && e != null) Add(Get(p.userID, p.displayName).Raids, e.ShortPrefabName, 1); }
        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            var victim = entity as BasePlayer; var attacker = info == null ? null : info.InitiatorPlayer;
            if (victim != null && !victim.IsNpc)
            {
                Get(victim.userID, victim.displayName).Deaths++;
                if (attacker != null && attacker != victim) { var s = Get(attacker.userID, attacker.displayName); s.Kills++; string w = info.WeaponPrefab == null ? "unknown" : info.WeaponPrefab.ShortPrefabName; Add(s.WeaponKills, w, 1); }
                return;
            }
            if (attacker == null) return;
            var a = Get(attacker.userID, attacker.displayName); string n = entity.ShortPrefabName.ToLowerInvariant();
            if (n.Contains("barrel")) a.BarrelsDestroyed++;
            else if (entity is BaseNpc || (victim != null && victim.IsNpc) || n.Contains("scientist") || n.Contains("murderer")) a.NpcKilled++;
            else if (n.Contains("bear") || n.Contains("wolf") || n.Contains("boar") || n.Contains("stag") || n.Contains("chicken")) a.AnimalsKilled++;
        }
        void Add(Dictionary<string, long> d, string k, long v) { if (string.IsNullOrEmpty(k)) return; long x; d.TryGetValue(k, out x); d[k] = x + v; }
        void Add(Dictionary<string, int> d, string k, int v) { int x; d.TryGetValue(k, out x); d[k] = x + v; }
        double Score(PlayerStats s) => s.Kills * cfg.KillScore + s.Deaths * cfg.DeathScore + s.TotalFarm / 1000d * cfg.FarmScorePerThousand + s.TotalRaids * cfg.RaidScore;

        [ChatCommand("top")]
        void TopCommand(BasePlayer p, string command, string[] args)
        {
            if (!permission.UserHasPermission(p.UserIDString, PermUse)) { SendReply(p, "Нет права swagrusttop.use"); return; }
            if (args.Length > 1 && args[0].ToLower() == "find") { ShowProfile(p, Find(string.Join(" ", args.Skip(1).ToArray())), "farm"); return; }
            ShowProfile(p, Get(p.userID, p.displayName), "farm");
        }
        [ChatCommand("link")]
        void LinkCommand(BasePlayer p, string command, string[] args)
        {
            string code; do { code = random.Next(100000, 999999).ToString(); } while (links.Codes.ContainsKey(code));
            links.Codes[code] = new LinkCode { SteamId = p.userID, ExpiresAt = Now() + cfg.LinkCodeMinutes * 60 }; SaveData();
            SendReply(p, $"Код привязки Discord: <color=#D99A8C>{code}</color> (действует {cfg.LinkCodeMinutes} мин). В Discord: /link code:{code}");
        }
        [ConsoleCommand("st.ui")]
        void UiCommand(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return; string action = arg.GetString(0, "me");
            if (action == "close") { CuiHelper.DestroyUi(p, Ui); return; }
            if (action == "top") { ShowTop(p, arg.GetString(1, "score")); return; }
            if (action == "search") { ShowSearch(p, ""); return; }
            ShowProfile(p, Get(p.userID, p.displayName), action);
        }
        [ConsoleCommand("st.search")]
        void SearchCommand(ConsoleSystem.Arg arg) { var p = arg.Player(); if (p == null) return; string query = arg.Args == null ? "" : string.Join(" ", arg.Args); ShowSearch(p, query); }
        [ConsoleCommand("st.profile")]
        void ProfileCommand(ConsoleSystem.Arg arg) { var p = arg.Player(); ulong id; if (p != null && ulong.TryParse(arg.GetString(0, "0"), out id) && players.ContainsKey(id)) ShowProfile(p, players[id], arg.GetString(1, "farm")); }
        PlayerStats Find(string q)
        {
            ulong id; if (ulong.TryParse(q, out id) && players.ContainsKey(id)) return players[id];
            return players.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x.Name) && x.Name.IndexOf(q ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void BaseUi(BasePlayer p, CuiElementContainer c, string selected)
        {
            CuiHelper.DestroyUi(p, Ui);
            c.Add(new CuiPanel { Image = { Color = "0.03 0.06 0.09 0.72" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", Ui);
            c.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "st.ui close" }, Text = { Text = "X", FontSize = 22, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.90 0.92", AnchorMax = "0.94 0.97" } }, Ui);
            Tab(c, "Моя статистика", "st.ui me", "0.395 0.895", "0.465 0.94", selected == "me");
            Tab(c, "Топ-10 игроков", "st.ui top score", "0.465 0.895", "0.535 0.94", selected == "top");
            Tab(c, "Поиск", "st.ui search", "0.535 0.895", "0.595 0.94", selected == "search");
            c.Add(new CuiPanel { Image = { Color = "0.60 0.48 0.45 0.75" }, RectTransform = { AnchorMin = "0.395 0.892", AnchorMax = "0.595 0.894" } }, Ui);
        }
        void Tab(CuiElementContainer c, string text, string cmd, string min, string max, bool on)
        {
            string name = c.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = cmd }, Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = on ? "1 1 1 1" : "0.82 0.82 0.82 1" }, RectTransform = { AnchorMin = min, AnchorMax = max } }, Ui);
            if (on) c.Add(new CuiPanel { Image = { Color = "0.85 0.60 0.55 1" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.055" } }, name);
        }
        void Label(CuiElementContainer c, string text, string min, string max, int size = 14, TextAnchor align = TextAnchor.MiddleLeft, string color = "1 1 1 1") => c.Add(new CuiLabel { Text = { Text = text, FontSize = size, Align = align, Color = color }, RectTransform = { AnchorMin = min, AnchorMax = max } }, Ui);
        void Box(CuiElementContainer c, string text, string min, string max) { c.Add(new CuiPanel { Image = { Color = "0.32 0.34 0.35 0.52", Sprite = "assets/content/ui/ui.background.tile.psd", ImageType = UnityEngine.UI.Image.Type.Sliced }, RectTransform = { AnchorMin = min, AnchorMax = max } }, Ui); Label(c, "     " + text, min, max, 12, TextAnchor.MiddleLeft); }
        void TopBox(CuiElementContainer c, string text, string min, string max, int rank, string command) { string color = rank == 0 ? "0.55 0.46 0.25 0.88" : rank == 1 ? "0.42 0.46 0.52 0.82" : rank == 2 ? "0.48 0.32 0.27 0.82" : "0.18 0.21 0.23 0.64"; c.Add(new CuiPanel { Image = { Color = color, Sprite = "assets/content/ui/ui.background.tile.psd", ImageType = UnityEngine.UI.Image.Type.Sliced }, RectTransform = { AnchorMin = min, AnchorMax = max } }, Ui); Label(c, "  " + text, min, max, 11, TextAnchor.MiddleLeft); c.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = command }, Text = { Text = "" }, RectTransform = { AnchorMin = min, AnchorMax = max } }, Ui); }

        void ShowSearch(BasePlayer viewer, string query)
        {
            var c = new CuiElementContainer(); BaseUi(viewer, c, "search");
            string input = c.Add(new CuiPanel { Image = { Color = "0.10 0.14 0.18 0.90", Sprite = "assets/content/ui/ui.background.tile.psd", ImageType = UnityEngine.UI.Image.Type.Sliced }, RectTransform = { AnchorMin = "0.36 0.80", AnchorMax = "0.64 0.845" } }, Ui, Ui + ".SearchInput");
            c.Add(new CuiElement { Parent = input, Components = { new CuiInputFieldComponent { Text = query ?? "", FontSize = 13, Align = TextAnchor.MiddleLeft, CharsLimit = 40, Command = "st.search", NeedsKeyboard = true }, new CuiRectTransformComponent { AnchorMin = "0.055 0", AnchorMax = "0.97 1" } } });
            Label(c, "Введите имя или Steam ID и нажмите Enter", "0.36 0.85", "0.64 0.88", 10, TextAnchor.MiddleLeft, "0.80 0.84 0.87 1");
            var found = players.Values.Where(x => string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(x.Name) && x.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) || x.SteamId.ToString().Contains(query)).OrderBy(x => x.Name).Take(27).ToList();
            for (int i = 0; i < found.Count; i++)
            {
                int col = i % 3, row = i / 3; float x1 = .36f + col * .095f, x2 = x1 + .087f, y2 = .765f - row * .052f, y1 = y2 - .041f;
                TopBox(c, Trim(found[i].Name, 16), Point(x1, y1), Point(x2, y2), 4, $"st.profile {found[i].SteamId}");
            }
            if (found.Count == 0) Label(c, "Игроки не найдены", "0.36 0.60", "0.64 0.72", 14, TextAnchor.MiddleCenter, "0.75 0.78 0.80 1");
            CuiHelper.AddUi(viewer, c);
        }
        void ShowProfile(BasePlayer viewer, PlayerStats s, string section)
        {
            if (s == null) { SendReply(viewer, "Игрок не найден"); return; }
            AddSessionIfOnline(s); bool online = BasePlayer.FindByID(s.SteamId) != null; var c = new CuiElementContainer(); BaseUi(viewer, c, "me");
            Label(c, "Информация о профиле", "0.20 0.785", "0.35 0.82", 12);
            c.Add(new CuiPanel { Image = { Color = "0.18 0.20 0.20 0.9", Sprite = "assets/content/ui/ui.background.tile.psd", ImageType = UnityEngine.UI.Image.Type.Sliced }, RectTransform = { AnchorMin = "0.20 0.675", AnchorMax = "0.245 0.77" } }, Ui, Ui + ".Avatar");
            string avatar;
            if (avatarUrls.TryGetValue(s.SteamId, out avatar) && !string.IsNullOrEmpty(avatar)) c.Add(new CuiElement { Parent = Ui + ".Avatar", Components = { new CuiRawImageComponent { Url = avatar, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0.04 0.04", AnchorMax = "0.96 0.96" } } });
            else { c.Add(new CuiLabel { Text = { Text = string.IsNullOrEmpty(s.Name) ? "?" : s.Name.Substring(0, 1).ToUpper(), FontSize = 34, Align = TextAnchor.MiddleCenter, Color = "0.85 0.60 0.55 1" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, Ui + ".Avatar"); LoadAvatar(viewer, s, section); }
            Label(c, s.Name, "0.252 0.72", "0.35 0.765", 14);
            Label(c, "Место в топе: " + Rank(s), "0.20 0.625", "0.35 0.665", 12, TextAnchor.MiddleLeft, "0.85 0.85 0.85 1");
            Label(c, "Активность", "0.20 0.565", "0.35 0.61", 14);
            Label(c, $"Сегодня: {(online ? "Сейчас онлайн" : "Не в сети")}\n\nЗа всё время: {Time(s.PlaytimeSeconds)}\n\nSCORE: {Score(s):0.00}", "0.20 0.39", "0.35 0.565", 12, TextAnchor.UpperLeft, "0.88 0.88 0.88 1");
            Tab(c, "Добыча", $"st.profile {s.SteamId} farm", "0.385 0.785", "0.465 0.825", section == "farm");
            Tab(c, "Взрывчатка", $"st.profile {s.SteamId} raids", "0.465 0.785", "0.545 0.825", section == "raids");
            Tab(c, "Фермерство", $"st.profile {s.SteamId} farming", "0.545 0.785", "0.625 0.825", section == "farming");
            var data = section == "raids" ? s.Raids : section == "farming" ? s.Farming : s.Farm;
            int row = 0; foreach (var x in data.OrderByDescending(x => x.Value).Take(7)) { float y2 = .755f - row * .065f, y1 = y2 - .052f; Box(c, $"{Pretty(x.Key)}                                      {x.Value:N0}", Point(.385f, y1), Point(.625f, y2)); row++; }
            if (row == 0) Label(c, "Пока нет данных", "0.385 0.52", "0.625 0.7", 15, TextAnchor.MiddleCenter, "0.72 0.72 0.72 1");
            Label(c, "PVP СТАТИСТИКА", "0.65 0.785", "0.81 0.825", 13);
            Box(c, $"Убийств                                      {s.Kills}", "0.65 0.715", "0.81 0.77");
            Box(c, $"Смертей                                      {s.Deaths}", "0.65 0.65", "0.81 0.705");
            Box(c, $"K/D                                           {s.KD:0.00}", "0.65 0.585", "0.81 0.64");
            Label(c, "Любимое оружие", "0.65 0.525", "0.81 0.565", 12);
            Box(c, Favorite(s), "0.65 0.455", "0.81 0.515");
            Label(c, "ДРУГАЯ СТАТИСТИКА", "0.65 0.39", "0.81 0.435", 13);
            Box(c, $"Открыто ящиков: {s.CratesOpened}", "0.65 0.325", "0.81 0.38");
            Box(c, $"Разбито бочек: {s.BarrelsDestroyed}", "0.65 0.26", "0.81 0.315");
            Box(c, $"Убито животных: {s.AnimalsKilled}    NPC: {s.NpcKilled}", "0.65 0.195", "0.81 0.25");
            CuiHelper.AddUi(viewer, c);
        }
        void LoadAvatar(BasePlayer viewer, PlayerStats stats, string section)
        {
            if (avatarUrls.ContainsKey(stats.SteamId) || loadingAvatars.Contains(stats.SteamId)) return;
            loadingAvatars.Add(stats.SteamId);
            webrequest.Enqueue($"https://steamcommunity.com/profiles/{stats.SteamId}?xml=1", null, (code, response) =>
            {
                loadingAvatars.Remove(stats.SteamId);
                if (code != 200 || string.IsNullOrEmpty(response)) { avatarUrls[stats.SteamId] = ""; return; }
                var match = Regex.Match(response, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>", RegexOptions.Singleline);
                avatarUrls[stats.SteamId] = match.Success ? match.Groups[1].Value : "";
                if (match.Success && viewer != null && viewer.IsConnected) NextTick(() => ShowProfile(viewer, stats, section));
            }, this);
        }
        void ShowTop(BasePlayer p, string category)
        {
            var c = new CuiElementContainer(); BaseUi(p, c, "top");
            string[] cats = { "kills", "kd", "playtime", "farm", "raids", "score" }; string[] names = { "КИЛЛЕРОВ", "ПО K/D", "ПО ОНЛАЙНУ", "ПО ФАРМУ", "РЕЙДЕРОВ", "ПО ОЧКАМ" };
            for (int col = 0; col < 6; col++)
            {
                float x1 = .04f + col * .155f, x2 = x1 + .14f; Label(c, "Топ-10 " + names[col], Point(x1, .77f), Point(x2, .82f), 14, TextAnchor.MiddleCenter);
                var list = players.Values.OrderByDescending(s => Value(s, cats[col])).Take(10).ToList();
                for (int r = 0; r < list.Count; r++) { float y2 = .74f-r*.055f,y1=y2-.045f; TopBox(c,$"{r+1}. {Trim(list[r].Name,14)}     {ValueLabel(list[r],cats[col])}",Point(x1,y1),Point(x2,y2),r,$"st.profile {list[r].SteamId}"); }
            }
            Label(c, "Таблица лидеров обновляется каждые 5 минут", "0.3 0.12", "0.7 0.18", 16, TextAnchor.MiddleCenter); CuiHelper.AddUi(p, c);
        }
        void AddSessionIfOnline(PlayerStats s) { if (BasePlayer.FindByID(s.SteamId) != null) AddSession(s.SteamId); }
        double Value(PlayerStats s, string c) { switch(c){case "kills":return s.Kills;case "kd":return s.KD;case "playtime":return s.PlaytimeSeconds;case "farm":return s.TotalFarm;case "raids":return s.TotalRaids;default:return Score(s);} }
        string ValueLabel(PlayerStats s,string c) { if(c=="playtime")return Time(s.PlaytimeSeconds);if(c=="kd")return s.KD.ToString("0.00");if(c=="score")return Score(s).ToString("0.00");return Value(s,c).ToString("N0"); }
        string Time(long s) => $"{s/86400}д {(s%86400)/3600}ч {(s%3600)/60}м";
        string Favorite(PlayerStats s) { var x=s.WeaponKills.OrderByDescending(k=>k.Value).FirstOrDefault(); return x.Key==null?"Нет данных":$"{Pretty(x.Key)} — {x.Value} убийств"; }
        int Rank(PlayerStats s) => players.Values.OrderByDescending(Score).Select((x, i) => new { x.SteamId, Rank = i + 1 }).FirstOrDefault(x => x.SteamId == s.SteamId)?.Rank ?? players.Count;
        string Pretty(string s) => string.IsNullOrEmpty(s)?"Unknown":s.Replace(".prefab","").Replace("_"," ");
        string Trim(string s,int n) => string.IsNullOrEmpty(s)?"Unknown":s.Length<=n?s:s.Substring(0,n-1)+"…";
        string Point(float x, float y) => x.ToString("0.####", CultureInfo.InvariantCulture) + " " + y.ToString("0.####", CultureInfo.InvariantCulture);

        void PushSnapshot()
        {
            if (!cfg.EnablePush || string.IsNullOrEmpty(cfg.PushUrl)) return;
            TickOnline();
            var payload = new
            {
                name = ConVar.Server.hostname,
                map = ConVar.Server.level,
                players = BasePlayer.activePlayerList.Count,
                maxPlayers = ConVar.Server.maxplayers,
                joining = 0,
                sleepers = BasePlayer.sleepingPlayerList.Count,
                connect = cfg.ConnectAddress,
                stats = players.Values.Where(x => x.SteamId.IsSteamId()).Select(Dto).ToList(),
                codes = links.Codes.ToDictionary(x => x.Key, x => new { steamId = x.Value.SteamId.ToString(), expiresAt = x.Value.ExpiresAt }),
                links = links.DiscordToSteam.ToDictionary(x => x.Key, x => x.Value.ToString())
            };
            string body = JsonConvert.SerializeObject(payload);
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" }, { "X-SwagRust-Secret", cfg.ApiSecret } };
            webrequest.Enqueue(cfg.PushUrl, body, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response)) { PrintWarning($"Railway push failed: HTTP {code}"); return; }
                try
                {
                    var result = JsonConvert.DeserializeObject<PushResponse>(response); bool changed = false;
                    if (result != null && result.claims != null) foreach (var claim in result.claims)
                    {
                        LinkCode link; if (claim == null || string.IsNullOrEmpty(claim.discordId) || !links.Codes.TryGetValue(claim.code ?? "", out link) || link.ExpiresAt < Now()) continue;
                        links.DiscordToSteam[claim.discordId] = link.SteamId; links.Codes.Remove(claim.code); changed = true;
                    }
                    if (changed) SaveData();
                }
                catch (Exception e) { PrintWarning("Railway response error: " + e.Message); }
            }, this, RequestMethod.POST, headers, 15f);
        }
        void StartApi()
        {
            try { listener=new HttpListener(); listener.Prefixes.Add($"http://{cfg.ApiHost}:{cfg.ApiPort}/"); listener.Start(); listenerThread=new Thread(Listen){IsBackground=true}; listenerThread.Start(); Puts($"API: http://{cfg.ApiHost}:{cfg.ApiPort}/"); }
            catch(Exception e){PrintError("API не запущен: "+e.Message);}
        }
        void StopApi(){try{listener?.Stop();listener?.Close();}catch{} try{listenerThread?.Abort();}catch{}}
        void Listen(){while(listener!=null&&listener.IsListening){try{var ctx=listener.GetContext();NextTick(()=>Handle(ctx));}catch{break;}}}
        void Handle(HttpListenerContext ctx)
        {
            try
            {
                if(ctx.Request.Headers["X-SwagRust-Secret"]!=cfg.ApiSecret){Reply(ctx,401,new{error="unauthorized"});return;}
                string path=ctx.Request.Url.AbsolutePath;
                if(path.StartsWith("/api/stats/discord/")){string did=path.Substring(19);ulong sid;if(!links.DiscordToSteam.TryGetValue(did,out sid)||!players.ContainsKey(sid)){Reply(ctx,404,new{error="not linked"});return;}Reply(ctx,200,Dto(players[sid]));return;}
                if(path=="/api/top"){string cat=ctx.Request.QueryString["category"]??"score";int limit; if(!int.TryParse(ctx.Request.QueryString["limit"],out limit))limit=10;limit=Math.Max(1,Math.Min(50,limit));var list=players.Values.OrderByDescending(s=>Value(s,cat)).Take(limit).Select(s=>new{name=s.Name,steamId=s.SteamId.ToString(),value=Value(s,cat),valueLabel=ValueLabel(s,cat)});Reply(ctx,200,new{players=list});return;}
                if(path=="/api/link/claim"&&ctx.Request.HttpMethod=="POST"){string body;using(var r=new System.IO.StreamReader(ctx.Request.InputStream))body=r.ReadToEnd();var req=JsonConvert.DeserializeObject<Dictionary<string,string>>(body);string code=req.ContainsKey("code")?req["code"]:"",did=req.ContainsKey("discordId")?req["discordId"]:"";LinkCode lc;if(!links.Codes.TryGetValue(code,out lc)||lc.ExpiresAt<Now()){Reply(ctx,400,new{error="invalid or expired code"});return;}links.DiscordToSteam[did]=lc.SteamId;links.Codes.Remove(code);SaveData();var s=Get(lc.SteamId);Reply(ctx,200,new{name=s.Name,steamId=s.SteamId.ToString()});return;}
                Reply(ctx,404,new{error="not found"});
            }catch(Exception e){try{Reply(ctx,500,new{error=e.Message});}catch{}}
        }
        object Dto(PlayerStats s){AddSessionIfOnline(s);return new{steamId=s.SteamId.ToString(),name=s.Name,kills=s.Kills,deaths=s.Deaths,playtimeSeconds=s.PlaytimeSeconds,totalFarm=s.TotalFarm,totalRaids=s.TotalRaids,cratesOpened=s.CratesOpened,barrelsDestroyed=s.BarrelsDestroyed,animalsKilled=s.AnimalsKilled,npcKilled=s.NpcKilled,score=Score(s),farm=s.Farm,raids=s.Raids,farming=s.Farming};}
        void Reply(HttpListenerContext c,int status,object value){byte[] b=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));c.Response.StatusCode=status;c.Response.ContentType="application/json; charset=utf-8";c.Response.ContentLength64=b.Length;c.Response.OutputStream.Write(b,0,b.Length);c.Response.Close();}
    }
}
