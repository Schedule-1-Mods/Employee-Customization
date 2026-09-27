using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.MainMenu;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MyFirstMod
{
    // The character on the main menu wears your whole outfit. The game dresses it from the save's
    // Appearance.json, which only has room for a top, bottoms, shoes, a hat and glasses - so
    // anything else you wear (an apron, gloves, ...) is missing there. While you play, this notes
    // what your character is wearing (every piece, with its colour) for that save; on the main
    // menu it puts on whatever the game left off.
    //
    // (0.4.6 avatar system. On 0.4.7+ the menu character is left as the game makes it.)
    internal static class MenuOutfit
    {
        private sealed class Piece
        {
            public bool Accessory;   // accessory (hat, apron, shoes...) or body layer (clothes painted on)
            public string Path;
            public Color Colour;
        }

        private static string FilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "EmployeeCustomization_MenuOutfit.txt");

        private static string _scene;
        private static float _nextCheck, _menuUntil;
        private static string _lastWritten;

        public static void OnSceneLoaded(string sceneName)
        {
            _scene = sceneName;
            _nextCheck = Time.unscaledTime + 1f;
            // On the menu, keep an eye on the character for a while: the game dresses it once
            // it has read the save list, which can be a moment after the scene loads.
            _menuUntil = Time.unscaledTime + 60f;
            _lastWait = null;
        }

        public static void Tick()
        {
            if (AvatarLook.NewSystem || Time.unscaledTime < _nextCheck) return;
            try
            {
                if (_scene == "Main") { _nextCheck = Time.unscaledTime + 5f; Remember(); }
                else if (_scene == "Menu" && Time.unscaledTime < _menuUntil) { _nextCheck = Time.unscaledTime + 0.5f; DressMenu(); }
            }
            catch (System.Exception e)
            {
                _nextCheck = Time.unscaledTime + 10f;
                MelonLogger.Warning("[MenuOutfit] " + e.Message);
            }
        }

        // ---------------------------------------------------------------- in game

        private static void Remember()
        {
            var player = Player.Local;
            var settings = player != null && player.Avatar != null ? player.Avatar.CurrentSettings : null;
            string save = LoadManager.Instance != null ? LoadManager.Instance.ActiveSaveInfo?.SavePath : null;
            if (settings == null || string.IsNullOrEmpty(save)) return;

            var pieces = new List<Piece>();
            if (settings.BodyLayerSettings != null)
                for (int i = 0; i < settings.BodyLayerSettings.Count; i++)
                {
                    var l = settings.BodyLayerSettings[i];
                    if (IsClothingLayer(l.layerPath)) pieces.Add(new Piece { Accessory = false, Path = l.layerPath, Colour = l.layerTint });
                }
            if (settings.AccessorySettings != null)
                for (int i = 0; i < settings.AccessorySettings.Count; i++)
                {
                    var a = settings.AccessorySettings[i];
                    if (a != null && !string.IsNullOrEmpty(a.path)) pieces.Add(new Piece { Accessory = true, Path = a.path, Colour = a.color });
                }

            var all = Load();
            all[Key(save)] = pieces;
            string text = Serialise(all);
            if (text == _lastWritten) return;
            File.WriteAllText(FilePath, text);
            _lastWritten = text;
        }

        // Clothes painted onto the body (not skin, face, eyes or underwear).
        private static bool IsClothingLayer(string path) =>
            !string.IsNullOrEmpty(path) &&
            (path.Contains("/Layers/Top/") || path.Contains("/Layers/Bottom/") || path.Contains("/Layers/Accessories/"));

        // ---------------------------------------------------------------- main menu

        private static void DressMenu()
        {
            var save = LoadManager.LastPlayedGame?.SavePath;
            if (string.IsNullOrEmpty(save)) { Waiting("the game hasn't picked the last-played save yet"); return; }
            if (!Load().TryGetValue(Key(save), out var pieces) || pieces.Count == 0) { Waiting("no outfit noted for " + save); return; }

            var rig = Object.FindObjectOfType<MainMenuRig>();
            var avatar = rig != null ? rig.Avatar : null;
            var current = avatar != null ? avatar.CurrentSettings : null;
            if (current == null) { Waiting(rig == null ? "no menu character in the scene" : "the menu character isn't dressed yet"); return; }

            // What the game's version of the character is missing, or has in the wrong colour.
            var dressed = Object.Instantiate(current);
            var changed = new List<string>();
            foreach (var p in pieces)
            {
                bool has = false;
                if (p.Accessory)
                {
                    for (int i = 0; i < dressed.AccessorySettings.Count && !has; i++)
                    {
                        var a = dressed.AccessorySettings[i];
                        if (a == null || a.path != p.Path) continue;
                        has = true;
                        if (!Same(a.color, p.Colour)) { a.color = p.Colour; changed.Add(Path.GetFileName(p.Path) + " colour"); }
                    }
                    if (!has) { dressed.AccessorySettings.Add(new AvatarSettings.AccessorySetting { path = p.Path, color = p.Colour }); changed.Add(Path.GetFileName(p.Path)); }
                }
                else
                {
                    for (int i = 0; i < dressed.BodyLayerSettings.Count && !has; i++)
                    {
                        var l = dressed.BodyLayerSettings[i];
                        if (l.layerPath != p.Path) continue;
                        has = true;
                        if (!Same(l.layerTint, p.Colour))
                        {
                            dressed.BodyLayerSettings[i] = new AvatarSettings.LayerSetting { layerPath = p.Path, layerTint = p.Colour };
                            changed.Add(Path.GetFileName(p.Path) + " colour");
                        }
                    }

                    // No exact match - replace whichever existing slot shares this item's category
                    // (Top/Bottom/body-layer Accessories) instead of appending a new entry, or the
                    // menu character ends up wearing both the game's default piece and ours at once.
                    string category = !has ? Category(p.Path) : null;
                    for (int i = 0; category != null && i < dressed.BodyLayerSettings.Count && !has; i++)
                    {
                        if (Category(dressed.BodyLayerSettings[i].layerPath) != category) continue;
                        has = true;
                        dressed.BodyLayerSettings[i] = new AvatarSettings.LayerSetting { layerPath = p.Path, layerTint = p.Colour };
                        changed.Add(Path.GetFileName(p.Path));
                    }

                    if (!has) { dressed.BodyLayerSettings.Add(new AvatarSettings.LayerSetting { layerPath = p.Path, layerTint = p.Colour }); changed.Add(Path.GetFileName(p.Path)); }
                }
            }
            if (changed.Count == 0) return;
            avatar.LoadAvatarSettings(dressed);
            MelonLogger.Msg($"[MenuOutfit] Dressed the menu character in your outfit: {string.Join(", ", changed)}.");
        }

        // "Avatar/Layers/Top/HazmatSuit" -> "Top". Only Top and Bottom are one-at-a-time slots -
        // body-layer "Accessories" (gloves, a belt, ...) can legitimately coexist, so those are
        // left out here and always just added instead of replacing each other.
        private static string Category(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.Contains("/Layers/Top/")) return "Top";
            if (path.Contains("/Layers/Bottom/")) return "Bottom";
            return null;
        }

        private static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        // Says (once per visit to the menu) why the character isn't being dressed yet.
        private static string _lastWait;
        private static void Waiting(string why)
        {
            if (why == _lastWait) return;
            _lastWait = why;
            MelonLogger.Msg("[MenuOutfit] Waiting: " + why + ".");
        }

        // ---------------------------------------------------------------- the file
        // One line per piece: save folder | A (accessory) or L (layer) | asset path | r,g,b,a

        private static string Key(string savePath)
        {
            try { return Path.GetFullPath(savePath).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return savePath.ToLowerInvariant(); }
        }

        private static Dictionary<string, List<Piece>> Load()
        {
            var all = new Dictionary<string, List<Piece>>();
            if (!File.Exists(FilePath)) return all;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split('|');
                if (parts.Length != 4) continue;
                var c = parts[3].Split(',');
                if (c.Length != 4) continue;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (!all.TryGetValue(parts[0], out var list)) all[parts[0]] = list = new List<Piece>();
                list.Add(new Piece
                {
                    Accessory = parts[1] == "A",
                    Path = parts[2],
                    Colour = new Color(float.Parse(c[0], inv), float.Parse(c[1], inv), float.Parse(c[2], inv), float.Parse(c[3], inv)),
                });
            }
            return all;
        }

        private static string Serialise(Dictionary<string, List<Piece>> all)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in all)
                foreach (var p in kv.Value)
                    sb.Append(kv.Key).Append('|').Append(p.Accessory ? "A" : "L").Append('|').Append(p.Path).Append('|')
                      .Append(p.Colour.r.ToString(inv)).Append(',').Append(p.Colour.g.ToString(inv)).Append(',')
                      .Append(p.Colour.b.ToString(inv)).Append(',').Append(p.Colour.a.ToString(inv)).Append('\n');
            return sb.ToString();
        }
    }
}
