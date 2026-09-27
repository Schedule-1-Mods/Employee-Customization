using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MelonLoader;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace MyFirstMod
{
    // In-game GUI: look at an employee and press E to select them (same as talking to them)
    // and edit their outfit/hair colour in a small panel pinned to the left of the screen.
    public static class EmployeeEditor
    {
        private const float InteractRange = 5f;
        private const float CloseRange = 8f;
        private const float ContentWidth = 300f;
        private const int GradientSteps = 64;

        public static bool Visible;

        private static readonly Rect WindowRect = new Rect(20f, 20f, 340f, 760f);
        private static Employee _selected;

        // 0..1 slider position: white, then grey, then black, then brown, then the full hue
        // spectrum at full saturation/brightness (see SpectrumTToColour).
        private static float _hairColourT;
        private static string _hairHex = "#FFFFFF";

        // 0..1 brightness multiplier applied on top of the Colour bar's (always fully bright)
        // hue - lets a colour be toned down instead of always being neon-saturated. 1 = unchanged.
        private static float _hairBrightnessT = 1f;

        // Every individually-coloured cosmetic other than hair - clothing layers (gloves, hazmat
        // suit...), face layers (masks are worn on the face, not tracked as body layers or
        // accessories), and accessories (hats etc, when the game stores them that way). Built
        // fresh each time an employee is selected, sorted into a fixed display order (see
        // GetPriority): Mask, Hazmat Suit, Gloves, Boots, then anything else.
        // (Kind/Index are from the pre-0.4.7 avatar system and are only kept so old saved entries
        // still deserialise; items are now identified by Key - see AvatarLook.)
        private enum CosmeticKind { BodyLayer, FaceLayer, Accessory, AvatarObject }

        private sealed class CosmeticItem
        {
            public CosmeticKind Kind;
            public int Index;
            public string Key;
            public string Name;
            public float ColourT;
            public float BrightnessT = 1f;
            public string Hex;
        }

        private static readonly List<CosmeticItem> _cosmetics = new List<CosmeticItem>();

        // The first appearance ever seen for each employee (by ID), captured before any edits -
        // lets "Restore to Default" undo everything this mod has changed, even across sessions.
        private sealed class OriginalLook
        {
            public Color Hair;
            public readonly Dictionary<string, Color> Colours = new Dictionary<string, Color>();
        }

        private static readonly Dictionary<string, OriginalLook> _originalLooks = new Dictionary<string, OriginalLook>();

        // The game's own save file only stores each employee's AppearanceIndex (a reference into
        // a fixed random-preset list) - not any colour we've applied - so a save/reload discards
        // our edits entirely. This is the mod's own side file that remembers customisations
        // (keyed by employee ID) and reapplies them, independent of the game's save system, so it
        // can never corrupt an actual save.
        private sealed class SavedCosmetic
        {
            public string Kind { get; set; }
            public int Index { get; set; }
            public string Key { get; set; }
            public string Hex { get; set; }
        }

        private sealed class SavedAppearance
        {
            public string HairHex { get; set; }
            public List<SavedCosmetic> Items { get; set; } = new List<SavedCosmetic>();
        }

        private static readonly Dictionary<string, SavedAppearance> _savedAppearances = new Dictionary<string, SavedAppearance>();
        private static bool _savedAppearancesLoaded;

        private static string GetDataFilePath()
        {
            string dllPath = Assembly.GetExecutingAssembly().Location;
            string modsDir = Path.GetDirectoryName(dllPath);
            return Path.Combine(modsDir, "MyFirstMod_Cosmetics.json");
        }

        private static void EnsureSavedAppearancesLoaded()
        {
            if (_savedAppearancesLoaded)
            {
                return;
            }

            _savedAppearancesLoaded = true;
            try
            {
                string path = GetDataFilePath();
                if (!File.Exists(path))
                {
                    return;
                }

                string json = File.ReadAllText(path);
                Dictionary<string, SavedAppearance> loaded = JsonSerializer.Deserialize<Dictionary<string, SavedAppearance>>(json);
                if (loaded == null)
                {
                    return;
                }

                _savedAppearances.Clear();
                foreach (KeyValuePair<string, SavedAppearance> pair in loaded)
                {
                    _savedAppearances[pair.Key] = pair.Value;
                }

                MelonLogger.Msg($"[CharacterEditor] Loaded {_savedAppearances.Count} saved employee customisation(s) from {path}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] Failed to load saved cosmetics: {ex}");
            }
        }

        private static void PersistSavedAppearances()
        {
            try
            {
                string json = JsonSerializer.Serialize(_savedAppearances, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetDataFilePath(), json);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] Failed to save cosmetics: {ex}");
            }
        }

        private static void SaveAppearanceForEmployee(Employee employee)
        {
            EnsureSavedAppearancesLoaded();

            SavedAppearance saved = new SavedAppearance { HairHex = ColourToHex(ComposeColour(_hairColourT, _hairBrightnessT)) };
            foreach (CosmeticItem item in _cosmetics)
            {
                saved.Items.Add(new SavedCosmetic
                {
                    Kind = item.Kind.ToString(),
                    Index = item.Index,
                    Key = item.Key,
                    Hex = ColourToHex(ComposeColour(item.ColourT, item.BrightnessT))
                });
            }

            _savedAppearances[employee.ID] = saved;
            PersistSavedAppearances();
        }

        // Applied whenever an employee is selected (covers "walk up and interact") and swept
        // across every employee right after a scene loads (covers "just loaded the save").
        private static void TryReapplySavedAppearance(Employee employee, Avatar avatar)
        {
            EnsureSavedAppearancesLoaded();

            var look = AvatarLook.Get(avatar);
            if (look == null || !_savedAppearances.TryGetValue(employee.ID, out SavedAppearance saved))
            {
                return;
            }

            if (TryParseHex(saved.HairHex, out Color hairColour))
            {
                AvatarLook.SetHair(look, hairColour);
            }

            if (saved.Items == null)
            {
                return;
            }

            // Entries saved by the older version of this mod have a kind + list index instead of a
            // Key: on the 0.4.6 avatar system those still point at the same layer, so they're
            // matched up; on 0.4.7+ they can't be, and only hair carries over from those.
            foreach (SavedCosmetic item in saved.Items)
            {
                if (!TryParseHex(item.Hex, out Color colour))
                {
                    continue;
                }

                string key = !string.IsNullOrEmpty(item.Key) ? item.Key : AvatarLook.LegacyKey(look, item.Kind, item.Index);
                if (!string.IsNullOrEmpty(key))
                {
                    AvatarLook.Set(look, key, colour);
                }
            }

            AvatarLook.Refresh(look);
        }

        private static bool _pendingReapply;
        private static float _reapplyDeadline;
        private static readonly HashSet<string> _reappliedEmployeeIds = new HashSet<string>();

        // Called once after a scene finishes loading (e.g. loading into a save). Employees can
        // still be mid-spawn at this point - especially right after launching the game and
        // loading a save, where they trickle in over the following seconds via network spawn
        // messages - so this doesn't reapply directly, it just arms a retry window that
        // TickPendingReapply drains every frame until every saved employee has been reached.
        public static void OnSceneLoaded()
        {
            EnsureSavedAppearancesLoaded();
            _reappliedEmployeeIds.Clear();

            if (_savedAppearances.Count == 0)
            {
                _pendingReapply = false;
                return;
            }

            _pendingReapply = true;
            _reapplyDeadline = Time.time + 20f;
        }

        // Drains the retry window armed by OnSceneLoaded. Cheap to call every frame - it only
        // touches employees that actually have saved customisation, and each employee is only
        // ever reapplied once per scene load.
        public static void TickPendingReapply()
        {
            if (!_pendingReapply)
            {
                return;
            }

            if (!EmployeeManager.InstanceExists)
            {
                if (Time.time > _reapplyDeadline)
                {
                    _pendingReapply = false;
                }

                return;
            }

            // A joining multiplayer client doesn't need to do any of this - the host already has
            // the customised AvatarSettings applied, and the game's own networking syncs that data
            // to every client as normal NPC data during the join. Reapplying on top of that here
            // raced against that same sync (both touching the same Avatar objects at once) and
            // could hang a joining client on "Syncing NPCInventory"/"Syncing NPCBehaviour" forever.
            // This mod's local save file is only ever written by this mod's own Apply button, so a
            // client has nothing useful to reapply from anyway.
            if (EmployeeManager.Instance.IsClientOnly)
            {
                _pendingReapply = false;
                return;
            }

            foreach (Employee employee in EmployeeManager.Instance.AllEmployees)
            {
                if (employee == null || _reappliedEmployeeIds.Contains(employee.ID) || !_savedAppearances.ContainsKey(employee.ID))
                {
                    continue;
                }

                Avatar avatar = employee.Avatar;
                if (AvatarLook.Get(avatar) == null)
                {
                    continue;
                }

                TryReapplySavedAppearance(employee, avatar);
                _reappliedEmployeeIds.Add(employee.ID);
            }

            bool allDone = true;
            foreach (string id in _savedAppearances.Keys)
            {
                if (!_reappliedEmployeeIds.Contains(id))
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone || Time.time > _reapplyDeadline)
            {
                if (_reappliedEmployeeIds.Count > 0)
                {
                    MelonLogger.Msg($"[CharacterEditor] Reapplied saved customisation to {_reappliedEmployeeIds.Count} employee(s) after scene load.");
                }

                _pendingReapply = false;
            }
        }

        // Which text field (if any) is currently capturing keystrokes. GUILayout.TextField is
        // unusable here (GUI.DoTextField is stripped in this game's IL2CPP build, same class of
        // bug as GUI.DrawTexture), so text entry is hand-rolled from Button clicks + raw key events.
        private static string _focusedField;

        private static Texture2D _spectrumTexture;
        private static Texture2D _brightnessTexture;
        private static GUIStyle _brightnessPercentStyle;
        private static Texture2D _circleTexture;
        private static GUIStyle _circleStyle;
        private static Texture2D _solidTexture;
        private static Texture2D _hairProceduralTexture;
        private static Texture2D _hairImageTexture;
        private static bool _hairImageLoadAttempted;
        private static GUIStyle _headerStyle;
        private static GUIStyle _hairIconStyle;
        private static GUIStyle _sectionTitleTextStyle;

        // One image + style pair per cosmetic category icon (cropped from the reference sheet).
        private static Texture2D _maskImageTexture;
        private static bool _maskImageLoadAttempted;
        private static GUIStyle _maskStyle;

        private static Texture2D _hazmatImageTexture;
        private static Texture2D _hazmatProceduralTexture;
        private static bool _hazmatImageLoadAttempted;
        private static GUIStyle _hazmatStyle;

        private static Texture2D _shirtImageTexture;
        private static bool _shirtImageLoadAttempted;
        private static GUIStyle _shirtStyle;

        private static Texture2D _overallsImageTexture;
        private static bool _overallsImageLoadAttempted;
        private static GUIStyle _overallsStyle;

        private static Texture2D _pantsImageTexture;
        private static bool _pantsImageLoadAttempted;
        private static GUIStyle _pantsStyle;

        private static Texture2D _glovesImageTexture;
        private static bool _glovesImageLoadAttempted;
        private static GUIStyle _glovesStyle;

        private static Texture2D _bootsImageTexture;
        private static bool _bootsImageLoadAttempted;
        private static GUIStyle _bootsStyle;

        // Content can be several sections tall depending on how many cosmetics an employee has,
        // so the body scrolls. GUILayout.BeginScrollView hasn't been tested in this build yet
        // (several other "obscure" Unity IMGUI calls turned out to be stripped) - it's only ever
        // attempted once, and permanently falls back to a plain non-scrolling layout if it throws,
        // so a bad first frame can't repeat itself.
        private static bool _scrollViewSupported = true;
        private static Vector2 _scrollPos;
        private static GUIStyle _inputBoxStyle;

        // Called on E. Looks at whatever the player is aiming at; if it's an employee, selects
        // them and opens the panel. E only opens/switches the panel - it never closes it, so
        // looking away or pressing E again doesn't dismiss it. Closing is Escape (TryClose) or
        // walking far enough away (UpdateVisibility).
        public static void TryInteract()
        {
            Employee target = GetLookedAtEmployee();
            if (target == null)
            {
                return;
            }

            SelectEmployee(target);
            Visible = true;
        }

        // Called on Escape. The only key press that closes the panel.
        public static void TryClose()
        {
            Visible = false;
        }

        // Called every frame. Closes the panel automatically once you've walked far enough away
        // from the selected employee, rather than requiring another E press. Uses distance rather
        // than "still looking at them" so you can look down at the panel itself to use it without
        // it snapping shut.
        public static void UpdateVisibility()
        {
            if (!Visible || _selected == null)
            {
                return;
            }

            Player player = Player.Local;
            if (player == null)
            {
                return;
            }

            if (Vector3.Distance(player.CameraPosition, _selected.CenterPoint) > CloseRange)
            {
                Visible = false;
            }
        }

        private static Employee GetLookedAtEmployee()
        {
            Player player = Player.Local;
            if (player == null)
            {
                return null;
            }

            Vector3 origin = player.CameraPosition;
            Vector3 direction = player.CameraRotation * Vector3.forward;
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, InteractRange))
            {
                return null;
            }

            return hit.collider != null ? hit.collider.GetComponentInParent<Employee>() : null;
        }

        private static void SelectEmployee(Employee employee)
        {
            _selected = employee;
            _focusedField = null;

            Avatar avatar = employee.Avatar;
            var look = AvatarLook.Get(avatar);
            if (look == null)
            {
                return;
            }

            LogAppearanceStructure(employee, look);

            // Capture the TRUE default look (the game's own random preset) before any saved
            // customisation gets reapplied below - otherwise, on a second selection after a
            // reload, this would wrongly cache our own reapplied colours as "default".
            if (!_originalLooks.ContainsKey(employee.ID))
            {
                CaptureOriginalLook(employee, look);
            }

            TryReapplySavedAppearance(employee, avatar);

            Color hair = AvatarLook.GetHair(look);
            DecomposeColour(hair, out _hairColourT, out _hairBrightnessT);
            _hairHex = ColourToHex(hair);

            List<CosmeticItem> items = new List<CosmeticItem>();
            foreach (var slot in AvatarLook.Slots(look, IsAllowedCosmetic))
            {
                Color colour = slot.Colour;
                var item = new CosmeticItem { Kind = CosmeticKind.AvatarObject, Key = slot.Key, Name = slot.Name, Hex = ColourToHex(colour) };
                DecomposeColour(colour, out item.ColourT, out item.BrightnessT);
                items.Add(item);
            }

            items.Sort((a, b) => GetPriority(a.Name).CompareTo(GetPriority(b.Name)));
            _cosmetics.Clear();
            _cosmetics.AddRange(items);
        }

        private static void CaptureOriginalLook(Employee employee, AvatarLook.Look look)
        {
            OriginalLook original = new OriginalLook { Hair = AvatarLook.GetHair(look) };
            foreach (var slot in AvatarLook.Slots(look, IsAllowedCosmetic))
            {
                original.Colours[slot.Key] = slot.Colour;
            }

            _originalLooks[employee.ID] = original;
        }

        // Only clothing (plus the always-first, separately-handled Hair) is ever shown as an
        // editable cosmetic - everything else the game tracks (face expressions, eyeshadow,
        // freckles, facial hair, masks) is deliberately left out of this mod's scope.
        private static bool IsAllowedCosmetic(string friendlyName)
        {
            string lower = friendlyName.ToLowerInvariant();
            return lower.Contains("hazmat") || lower.Contains("suit") || lower.Contains("top") || lower.Contains("shirt") || lower.Contains("overall")
                || lower.Contains("pant") || lower.Contains("jean") || lower.Contains("jort") || lower.Contains("short") || lower.Contains("skirt") || lower.Contains("cargo")
                || lower.Contains("glove")
                || lower.Contains("boot") || lower.Contains("shoe") || lower.Contains("feet") || lower.Contains("sandal") || lower.Contains("sneaker") || lower.Contains("flat");
        }

        // Fixed display order, right after the (always-first) Hair section: Hazmat Suit/Top,
        // Pants/Bottom, Gloves, Boots, then anything else that still matched the allow-list.
        private static int GetPriority(string friendlyName)
        {
            string lower = friendlyName.ToLowerInvariant();
            if (lower.Contains("hazmat") || lower.Contains("suit") || lower.Contains("top") || lower.Contains("shirt") || lower.Contains("overall"))
            {
                return 2;
            }

            if (lower.Contains("pant") || lower.Contains("jean") || lower.Contains("jort") || lower.Contains("short") || lower.Contains("skirt") || lower.Contains("cargo"))
            {
                return 3;
            }

            if (lower.Contains("glove"))
            {
                return 4;
            }

            if (lower.Contains("boot") || lower.Contains("shoe") || lower.Contains("feet") || lower.Contains("sandal") || lower.Contains("sneaker") || lower.Contains("flat"))
            {
                return 5;
            }

            return 6;
        }

        // Turns an asset path like "Avatar/Layers/Top/HazmatSuit" into "Hazmat Suit".
        private static string GetFriendlyName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "Item";
            }

            int slash = path.LastIndexOf('/');
            string raw = slash >= 0 ? path.Substring(slash + 1) : path;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(raw[i - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        // Diagnostic only: dumps every avatar object the selected employee has on and its
        // colours, so clothing that doesn't show up in the editor can be traced to real data.
        private static void LogAppearanceStructure(Employee employee, AvatarLook.Look look)
        {
            try
            {
                MelonLogger.Msg($"[CharacterEditor] {employee.FullName} ({employee.Type}):");
                foreach (string line in AvatarLook.Describe(look))
                {
                    MelonLogger.Msg($"[CharacterEditor]   {line}");
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] LogAppearanceStructure threw: {ex}");
            }
        }

        public static void Draw()
        {
            if (!Visible || _selected == null)
            {
                return;
            }

            GUI.Window(848210, WindowRect, (GUI.WindowFunction)DrawWindow, "Character Editor");
        }

        private static void DrawWindow(int id)
        {
            Avatar avatar = _selected.Avatar;
            var look = AvatarLook.Get(avatar);
            if (look == null)
            {
                GUILayout.Label("Appearance unavailable.");
                return;
            }

            bool scrolling = TryBeginScrollView();
            DrawWindowBody(look);
            if (scrolling)
            {
                try
                {
                    GUILayout.EndScrollView();
                }
                catch
                {
                }
            }
        }

        private static bool TryBeginScrollView()
        {
            if (!_scrollViewSupported)
            {
                return false;
            }

            try
            {
                GUILayoutOption[] size = { GUILayout.Width(WindowRect.width - 18f), GUILayout.Height(WindowRect.height - 30f) };
                try
                {
                    _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, size);
                }
                catch
                {
                    _scrollPos = GUILayout.BeginScrollView(_scrollPos, size);
                }

                return true;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] Scroll view unsupported, falling back to a plain layout: {ex}");
                _scrollViewSupported = false;
                return false;
            }
        }

        private static void DrawWindowBody(AvatarLook.Look look)
        {
            // Name and profession, each its own shaded box on the top line.
            GUILayout.BeginHorizontal();
            DrawShadedLabel(_selected.FullName);
            GUILayout.Space(8f);
            DrawShadedLabel(_selected.Type.ToString());
            GUILayout.EndHorizontal();

            GUILayout.Space(16f);

            // Hair first, then every piece of clothing in a fixed order: Hazmat Suit/Top,
            // Pants, Gloves, Boots, then anything else. Each is its own independent colour.
            DrawColourSection("Hair Colour", "hairHex", GetHairIconStyle(), ref _hairColourT, ref _hairBrightnessT, ref _hairHex);

            foreach (CosmeticItem item in _cosmetics)
            {
                GUILayout.Space(14f);
                float colourT = item.ColourT;
                float brightnessT = item.BrightnessT;
                string hex = item.Hex;
                DrawColourSection($"{item.Name} Colour", $"cos{item.Key}", GetIconStyleForLayer(item.Name), ref colourT, ref brightnessT, ref hex);
                item.ColourT = colourT;
                item.BrightnessT = brightnessT;
                item.Hex = hex;
            }

            GUILayout.Space(14f);
            if (GUILayout.Button("Apply to Character", GUILayout.Width(ContentWidth), GUILayout.Height(26f)))
            {
                AvatarLook.SetHair(look, ComposeColour(_hairColourT, _hairBrightnessT));
                foreach (CosmeticItem item in _cosmetics)
                {
                    AvatarLook.Set(look, item.Key, ComposeColour(item.ColourT, item.BrightnessT));
                }

                AvatarLook.Refresh(look);
                SaveAppearanceForEmployee(_selected);
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Restore to Default", GUILayout.Width(ContentWidth), GUILayout.Height(24f)))
            {
                RestoreToDefault(look);
                _savedAppearances.Remove(_selected.ID);
                PersistSavedAppearances();
            }
        }

        // Updates the existing _cosmetics entries and hair state in place, rather than rebuilding
        // the list via SelectEmployee. Rebuilding mid-frame changes the number/order of GUILayout
        // controls between IMGUI's Layout and Repaint passes for the very same frame, which
        // caused phantom sections and mismatched colours.
        private static void RestoreToDefault(AvatarLook.Look look)
        {
            if (!_originalLooks.TryGetValue(_selected.ID, out OriginalLook original))
            {
                return;
            }

            DecomposeColour(original.Hair, out _hairColourT, out _hairBrightnessT);
            _hairHex = ColourToHex(original.Hair);
            AvatarLook.SetHair(look, original.Hair);

            foreach (CosmeticItem item in _cosmetics)
            {
                if (!original.Colours.TryGetValue(item.Key, out Color originalColour))
                {
                    continue;
                }

                AvatarLook.Set(look, item.Key, originalColour);
                DecomposeColour(originalColour, out item.ColourT, out item.BrightnessT);
                item.Hex = ColourToHex(originalColour);
            }

            AvatarLook.Refresh(look);
        }

        // Matches a cosmetic's friendly name to one of the five reference icons; anything that
        // isn't hair/mask/suit/gloves/boots (e.g. a face expression layer) gets a plain swatch.
        private static GUIStyle GetIconStyleForLayer(string friendlyName)
        {
            string lower = friendlyName.ToLowerInvariant();
            if (lower.Contains("mask"))
            {
                return GetMaskStyle();
            }

            if (lower.Contains("hazmat"))
            {
                return GetHazmatStyle();
            }

            if (lower.Contains("overall"))
            {
                return GetOverallsStyle();
            }

            if (lower.Contains("suit") || lower.Contains("top") || lower.Contains("shirt"))
            {
                return GetShirtStyle();
            }

            if (lower.Contains("pant") || lower.Contains("jean") || lower.Contains("jort") || lower.Contains("short") || lower.Contains("skirt") || lower.Contains("cargo"))
            {
                return GetPantsStyle();
            }

            if (lower.Contains("glove"))
            {
                return GetGlovesStyle();
            }

            if (lower.Contains("boot") || lower.Contains("shoe") || lower.Contains("feet") || lower.Contains("sandal") || lower.Contains("sneaker") || lower.Contains("flat"))
            {
                return GetBootsStyle();
            }

            return GetSolidStyle();
        }

        // A full-width Colour slider (drag directly on the gradient bar - its circle is the drag
        // handle) plus a Brightness slider underneath (dims the selected hue for people who don't
        // want everything neon-saturated), then a row with the shape preview and editable final
        // hex code.
        private static void DrawColourSection(string title, string hexFieldId, GUIStyle shapeStyle, ref float colourT, ref float brightnessT, ref string hexBuffer)
        {
            DrawSectionTitle(title);

            GUILayout.Label("Colour");
            colourT = DrawDraggableGradientBar(GetSpectrumTexture(), colourT, SpectrumTToColour);

            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Brightness");
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{Mathf.RoundToInt(brightnessT * 100f)}%", GetBrightnessPercentStyle());
            GUILayout.EndHorizontal();
            brightnessT = DrawDraggableGradientBar(GetBrightnessTexture(), brightnessT, v => Color.Lerp(Color.black, Color.white, v));

            GUILayout.Space(6f);

            Color colour = ComposeColour(colourT, brightnessT);
            string liveHex = ColourToHex(colour);

            GUILayout.BeginHorizontal();
            GUILayout.Space(40f);
            DrawShapePreview(shapeStyle, colour, 70f, 70f);

            GUILayout.BeginVertical();
            GUILayout.Space(24f);
            string typed = DrawEditableField(hexFieldId, hexBuffer, 7, 90f);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            if (typed != hexBuffer)
            {
                hexBuffer = typed;
                if (TryParseHex(hexBuffer, out Color parsed))
                {
                    DecomposeColour(parsed, out colourT, out brightnessT);
                }
            }
            else if (_focusedField != hexFieldId && hexBuffer != liveHex)
            {
                hexBuffer = liveHex;
            }
        }

        // A full-width, bold, centred, uppercase banner - HAIR, MASK, HAZMAT SUIT, etc.
        private static void DrawSectionTitle(string title)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            GUILayout.BeginVertical(GetHeaderStyle(), GUILayout.Width(ContentWidth));
            GUI.color = previous;
            GUILayout.Label(title.ToUpperInvariant(), GetSectionTitleTextStyle());
            GUILayout.EndVertical();
        }

        private static GUIStyle GetSectionTitleTextStyle()
        {
            if (_sectionTitleTextStyle == null)
            {
                _sectionTitleTextStyle = new GUIStyle(GUI.skin.label);
                _sectionTitleTextStyle.fontSize = 16;
                _sectionTitleTextStyle.fontStyle = FontStyle.Bold;
                _sectionTitleTextStyle.alignment = TextAnchor.MiddleCenter;
                _sectionTitleTextStyle.normal.textColor = Color.white;
            }

            return _sectionTitleTextStyle;
        }

        private static GUIStyle GetBrightnessPercentStyle()
        {
            if (_brightnessPercentStyle == null)
            {
                _brightnessPercentStyle = new GUIStyle(GUI.skin.label);
                _brightnessPercentStyle.fontStyle = FontStyle.Bold;
                _brightnessPercentStyle.normal.textColor = new Color(0.95f, 0.75f, 0.25f);
            }

            return _brightnessPercentStyle;
        }

        // A label with a dark shaded background, sized to hug its own text rather than
        // stretching the full panel width - used for the name/profession boxes.
        private static void DrawShadedLabel(string text)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            GUILayout.BeginVertical(GetHeaderStyle());
            GUI.color = previous;
            GUILayout.Label(text);
            GUILayout.EndVertical();
        }

        private static GUIStyle GetHeaderStyle()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle();
                _headerStyle.padding = new RectOffset(6, 6, 4, 4);
                _headerStyle.normal.background = GetSolidTexture();
            }

            return _headerStyle;
        }

        private static GUIStyle GetInputBoxStyle()
        {
            if (_inputBoxStyle == null)
            {
                _inputBoxStyle = new GUIStyle(GUI.skin.textField);
                _inputBoxStyle.normal.textColor = Color.white;
                _inputBoxStyle.hover.textColor = Color.white;
                _inputBoxStyle.active.textColor = Color.white;
                _inputBoxStyle.focused.textColor = Color.white;
            }

            return _inputBoxStyle;
        }

        // Reads/writes the Windows clipboard directly via user32/kernel32 instead of
        // GUIUtility.systemCopyBuffer. Copy appeared to work but paste didn't, which points at
        // the Unity property's getter being stripped in this build the same way GUI.DrawTexture
        // and GUI.DoTextField were - this sidesteps that whole IL2CPP layer entirely since it's
        // plain .NET P/Invoke to the OS, not a Unity/Il2Cpp call.
        private const uint ClipboardFormatUnicodeText = 13;
        private const uint GlobalMemMoveableZeroed = 0x0042;

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private static void TrySetClipboard(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    MelonLogger.Msg("[CharacterEditor] Copy failed: OpenClipboard returned false.");
                    return;
                }

                try
                {
                    EmptyClipboard();

                    int byteCount = (text.Length + 1) * 2;
                    IntPtr hGlobal = GlobalAlloc(GlobalMemMoveableZeroed, (UIntPtr)byteCount);
                    if (hGlobal == IntPtr.Zero)
                    {
                        MelonLogger.Msg("[CharacterEditor] Copy failed: GlobalAlloc returned null.");
                        return;
                    }

                    IntPtr target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        MelonLogger.Msg("[CharacterEditor] Copy failed: GlobalLock returned null.");
                        return;
                    }

                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    GlobalUnlock(hGlobal);

                    IntPtr result = SetClipboardData(ClipboardFormatUnicodeText, hGlobal);
                    MelonLogger.Msg(result != IntPtr.Zero
                        ? $"[CharacterEditor] Copied \"{text}\" to clipboard."
                        : "[CharacterEditor] Copy failed: SetClipboardData returned null.");
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] Copy threw: {ex}");
            }
        }

        private static string TryGetClipboard()
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    MelonLogger.Msg("[CharacterEditor] Paste failed: OpenClipboard returned false.");
                    return null;
                }

                try
                {
                    IntPtr handle = GetClipboardData(ClipboardFormatUnicodeText);
                    if (handle == IntPtr.Zero)
                    {
                        MelonLogger.Msg("[CharacterEditor] Paste failed: GetClipboardData returned null (no CF_UNICODETEXT on the clipboard).");
                        return null;
                    }

                    IntPtr pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                    {
                        MelonLogger.Msg("[CharacterEditor] Paste failed: GlobalLock returned null.");
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(pointer);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"[CharacterEditor] Paste threw: {ex}");
                return null;
            }
        }

        // Which field (if any) currently has its whole text "selected" via Ctrl+A - the next
        // character typed replaces the whole value instead of appending to it.
        private static string _selectAllField;

        // A minimal, self-drawn text field: a Button styled to look like a text box (with a
        // vertical "|" cursor while focused) that toggles focus on click, and raw KeyDown events
        // append/delete characters while focused. Deliberately avoids GUILayout.TextField/GUI.DoTextField.
        private static string DrawEditableField(string fieldId, string value, int maxLength, float width)
        {
            bool isFocused = _focusedField == fieldId;
            bool isSelectedAll = isFocused && _selectAllField == fieldId;
            string display = isFocused ? value + "|" : value;

            Color previousBg = GUI.backgroundColor;
            if (isSelectedAll)
            {
                GUI.backgroundColor = new Color(0.3f, 0.5f, 1f, 1f);
            }

            bool clicked = GUILayout.Button(display, GetInputBoxStyle(), GUILayout.Width(width), GUILayout.Height(20f));
            GUI.backgroundColor = previousBg;

            if (clicked)
            {
                bool wasFocused = isFocused;
                _focusedField = wasFocused ? null : fieldId;
                isFocused = !wasFocused;
                // Clicking into a field selects its whole contents (like clicking a real text
                // box tends to), ready to be typed over or replaced with Ctrl+V - clicking it a
                // second time to unfocus just clears the selection.
                _selectAllField = isFocused ? fieldId : null;
            }

            if (!isFocused)
            {
                return value;
            }

            Event evt = Event.current;
            if (evt.type != EventType.KeyDown)
            {
                return value;
            }

            bool selectedAll = _selectAllField == fieldId;

            if (evt.control && evt.keyCode == KeyCode.A)
            {
                _selectAllField = fieldId;
            }
            else if (evt.control && evt.keyCode == KeyCode.C)
            {
                TrySetClipboard(value);
            }
            else if (evt.control && evt.keyCode == KeyCode.V)
            {
                string pasted = TryGetClipboard();
                if (!string.IsNullOrEmpty(pasted))
                {
                    value = pasted.Length > maxLength ? pasted.Substring(0, maxLength) : pasted;
                }

                _selectAllField = null;
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Escape)
            {
                _focusedField = null;
                _selectAllField = null;
            }
            else if (evt.keyCode == KeyCode.Backspace)
            {
                value = selectedAll ? string.Empty : (value.Length > 0 ? value.Substring(0, value.Length - 1) : value);
                _selectAllField = null;
            }
            else if (evt.character != '\0' && !char.IsControl(evt.character))
            {
                value = selectedAll ? string.Empty : value;
                if (value.Length < maxLength)
                {
                    value += evt.character;
                }

                _selectAllField = null;
            }

            evt.Use();
            return value;
        }

        private static bool TryParseHex(string text, out Color colour)
        {
            colour = Color.white;
            string t = text.TrimStart('#');
            if (t.Length != 6)
            {
                return false;
            }

            if (!byte.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return false;
            }

            colour = new Color32(r, g, b, 255);
            return true;
        }

        private const float WhiteCapWidth = 0.06f;
        private const float GreyCapWidth = 0.06f;
        private const float BlackCapWidth = 0.06f;
        private const float BrownCapWidth = 0.06f;
        private const float CapsWidth = WhiteCapWidth + GreyCapWidth + BlackCapWidth + BrownCapWidth;
        private static readonly Color GreyColour = new Color32(141, 141, 141, 255);
        private static readonly Color BrownColour = new Color32(114, 75, 53, 255);

        // t: 0..WhiteCapWidth = solid white, that..+GreyCapWidth = solid grey, that..+BlackCapWidth
        // = solid black, that..+BrownCapWidth = solid brown, then the rest of the bar is the full
        // hue spectrum at full saturation/brightness. Four dedicated swatches so white/grey/black/
        // brown hair are one-click picks instead of something you have to hand-tune out of the
        // rainbow.
        private static Color SpectrumTToColour(float t)
        {
            if (t <= WhiteCapWidth)
            {
                return Color.white;
            }

            if (t <= WhiteCapWidth + GreyCapWidth)
            {
                return GreyColour;
            }

            if (t <= WhiteCapWidth + GreyCapWidth + BlackCapWidth)
            {
                return Color.black;
            }

            if (t <= CapsWidth)
            {
                return BrownColour;
            }

            float hue = (t - CapsWidth) / (1f - CapsWidth);
            return Color.HSVToRGB(hue, 1f, 1f);
        }

        // Inverse of SpectrumTToColour: approximates a slider position for an arbitrary RGB
        // colour, so selecting an employee or typing a hex value lands the slider in a sensible
        // spot even though not every colour sits exactly on the white/grey/black/brown/spectrum line.
        private static float ColourToSpectrumT(Color colour)
        {
            float max = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
            float min = Mathf.Min(colour.r, Mathf.Min(colour.g, colour.b));
            float saturation = max <= 0f ? 0f : (max - min) / max;

            if (saturation < 0.08f)
            {
                float lightness = (max + min) / 2f;
                if (lightness > 0.85f)
                {
                    return WhiteCapWidth / 2f;
                }

                if (lightness < 0.15f)
                {
                    return WhiteCapWidth + GreyCapWidth + BlackCapWidth / 2f;
                }

                return WhiteCapWidth + GreyCapWidth / 2f;
            }

            float brownDistance = Mathf.Abs(colour.r - BrownColour.r) + Mathf.Abs(colour.g - BrownColour.g) + Mathf.Abs(colour.b - BrownColour.b);
            if (brownDistance < 0.3f)
            {
                return WhiteCapWidth + GreyCapWidth + BlackCapWidth + BrownCapWidth / 2f;
            }

            Color.RGBToHSV(colour, out float hue, out _, out _);
            return CapsWidth + hue * (1f - CapsWidth);
        }

        // Applies the Brightness slider on top of the (always fully bright) Colour bar - a plain
        // component-wise scale towards black, equivalent to scaling HSV's V while leaving hue and
        // saturation alone.
        private static Color ComposeColour(float colourT, float brightnessT)
        {
            Color baseColour = SpectrumTToColour(colourT);
            return new Color(baseColour.r * brightnessT, baseColour.g * brightnessT, baseColour.b * brightnessT, 1f);
        }

        // Inverse of ComposeColour: splits an arbitrary final RGB colour (from an employee's
        // existing appearance, or a typed hex code) into a Colour-bar position at full brightness
        // and the brightness fraction that was multiplied onto it, so both sliders land in a
        // sensible spot instead of the brightness always resetting to 100%.
        private static void DecomposeColour(Color colour, out float colourT, out float brightnessT)
        {
            float value = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
            if (value <= 0.0001f)
            {
                colourT = ColourToSpectrumT(Color.black);
                brightnessT = 1f;
                return;
            }

            Color fullBrightness = new Color(colour.r / value, colour.g / value, colour.b / value);
            colourT = ColourToSpectrumT(fullBrightness);
            brightnessT = Mathf.Clamp01(value);
        }

        private static Rect DrawGradientBar(Texture2D texture)
        {
            const float height = 12f;
            Rect r = GUILayoutUtility.GetRect(ContentWidth, height, GUILayout.Width(ContentWidth), GUILayout.Height(height));
            GUIStyle style = new GUIStyle();
            style.normal.background = texture;
            GUI.Box(r, GUIContent.none, style);
            return r;
        }

        // A small circle drawn on top of the gradient bar at the current slider position,
        // tinted to match the colour at that spot - like a colour-picker cursor.
        private static void DrawColourMarker(Rect barRect, float t, Color tint)
        {
            const float size = 16f;
            float cx = barRect.x + t * barRect.width;
            float cy = barRect.y + barRect.height / 2f;
            Rect circleRect = new Rect(cx - size / 2f, cy - size / 2f, size, size);

            Color previous = GUI.color;
            GUI.color = tint;
            GUI.Box(circleRect, GUIContent.none, GetCircleStyle());
            GUI.color = previous;
        }

        private static bool _draggableBarSupported = true;

        // Draws a gradient bar whose own circle marker is the drag handle - clicking or dragging
        // anywhere on the bar (not just precisely on the small circle) moves it - replacing the
        // separate GUILayout.HorizontalSlider row that used to sit underneath the bar as a second,
        // redundant control. tintAt maps a slider position back to the colour the marker should be
        // tinted (the spectrum's hue for the Colour bar, black-to-white for the Brightness bar).
        // Only ever attempted once - if GUIUtility's hotControl/GetControlID turn out to be
        // unsupported in this build (same class of surprise as the other stripped IMGUI calls in
        // this file), it falls back to a plain slider permanently rather than risk breaking every
        // colour control over one bad frame.
        private static float DrawDraggableGradientBar(Texture2D texture, float t, System.Func<float, Color> tintAt)
        {
            Rect barRect = DrawGradientBar(texture);

            if (_draggableBarSupported)
            {
                try
                {
                    const float handleSize = 16f;
                    Rect hitRect = new Rect(barRect.x, barRect.y + barRect.height / 2f - handleSize / 2f, barRect.width, handleSize);
                    int controlId = GUIUtility.GetControlID(FocusType.Passive);
                    Event e = Event.current;

                    if (e.type == EventType.MouseDown && hitRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = controlId;
                        t = Mathf.Clamp01((e.mousePosition.x - barRect.x) / barRect.width);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
                    {
                        t = Mathf.Clamp01((e.mousePosition.x - barRect.x) / barRect.width);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }

                    DrawColourMarker(barRect, t, tintAt(t));
                    return t;
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Msg($"[CharacterEditor] Draggable gradient bar unsupported, falling back to a plain slider: {ex}");
                    _draggableBarSupported = false;
                }
            }

            DrawColourMarker(barRect, t, tintAt(t));
            return GUILayout.HorizontalSlider(t, 0f, 1f, GUILayout.Width(ContentWidth));
        }

        private static Texture2D GetCircleTexture()
        {
            if (_circleTexture != null)
            {
                return _circleTexture;
            }

            const int size = 20;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float radius = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    bool inside = dx * dx + dy * dy <= radius * radius;
                    tex.SetPixel(x, y, inside ? Color.white : new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            _circleTexture = tex;
            return _circleTexture;
        }

        private static GUIStyle GetCircleStyle()
        {
            if (_circleStyle == null)
            {
                _circleStyle = new GUIStyle();
                _circleStyle.normal.background = GetCircleTexture();
            }

            return _circleStyle;
        }

        private static void DrawShapePreview(GUIStyle style, Color colour, float width, float height)
        {
            Rect r = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.Box(r, GUIContent.none, style);
            GUI.color = previous;
        }

        private static string ColourToHex(Color colour)
        {
            Color32 c = colour;
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        // GUI.DrawTexture(Rect, Texture) is stripped out of this game's IL2CPP build (throws
        // "Method unstripping failed" every call), so textures are drawn via GUI.Box with a
        // GUIStyle background instead - GUI.Box is a foundational call that stays unstripped.
        private static Texture2D GetSpectrumTexture()
        {
            if (_spectrumTexture == null)
            {
                _spectrumTexture = BuildGradient(SpectrumTToColour);
            }

            return _spectrumTexture;
        }

        private static Texture2D GetBrightnessTexture()
        {
            if (_brightnessTexture == null)
            {
                _brightnessTexture = BuildGradient(t => Color.Lerp(Color.black, Color.white, t));
            }

            return _brightnessTexture;
        }

        private static Texture2D BuildGradient(System.Func<float, Color> colourAt)
        {
            Texture2D tex = new Texture2D(GradientSteps, 1);
            for (int x = 0; x < GradientSteps; x++)
            {
                float t = (float)x / (GradientSteps - 1);
                tex.SetPixel(x, 0, colourAt(t));
            }

            tex.Apply();
            return tex;
        }

        // Icons: the cropped reference images if they'll load, otherwise a procedurally-drawn
        // fallback (ImageConversion.LoadImage is another API that may be stripped in this build -
        // it's only ever tried once per texture and wrapped in a try/catch, so a failure can't
        // repeat or cascade like the earlier GUI.DrawTexture/GUI.DoTextField breakages did).
        private static GUIStyle GetHairIconStyle()
        {
            if (_hairIconStyle == null)
            {
                _hairIconStyle = new GUIStyle();
                _hairIconStyle.normal.background = GetHairIconTexture();
            }

            return _hairIconStyle;
        }

        private static Texture2D GetHairIconTexture()
        {
            if (!_hairImageLoadAttempted)
            {
                _hairImageLoadAttempted = true;
                _hairImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.hair_icon.png");
            }

            if (_hairImageTexture != null)
            {
                return _hairImageTexture;
            }

            if (_hairProceduralTexture == null)
            {
                _hairProceduralTexture = BuildShapeTexture(IsHairPixel);
            }

            return _hairProceduralTexture;
        }

        private static GUIStyle GetMaskStyle()
        {
            if (_maskStyle == null)
            {
                if (!_maskImageLoadAttempted)
                {
                    _maskImageLoadAttempted = true;
                    _maskImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.mask_icon.png");
                }

                _maskStyle = new GUIStyle();
                _maskStyle.normal.background = _maskImageTexture != null ? _maskImageTexture : GetSolidTexture();
            }

            return _maskStyle;
        }

        private static GUIStyle GetHazmatStyle()
        {
            if (_hazmatStyle == null)
            {
                if (!_hazmatImageLoadAttempted)
                {
                    _hazmatImageLoadAttempted = true;
                    _hazmatImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.hazmatsuit_icon.png");
                }

                if (_hazmatImageTexture == null && _hazmatProceduralTexture == null)
                {
                    _hazmatProceduralTexture = BuildShapeTexture(IsShirtPixel);
                }

                _hazmatStyle = new GUIStyle();
                _hazmatStyle.normal.background = _hazmatImageTexture != null ? _hazmatImageTexture : _hazmatProceduralTexture;
            }

            return _hazmatStyle;
        }

        private static GUIStyle GetShirtStyle()
        {
            if (_shirtStyle == null)
            {
                if (!_shirtImageLoadAttempted)
                {
                    _shirtImageLoadAttempted = true;
                    _shirtImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.shirt_icon.png");
                }

                _shirtStyle = new GUIStyle();
                _shirtStyle.normal.background = _shirtImageTexture != null ? _shirtImageTexture : GetSolidTexture();
            }

            return _shirtStyle;
        }

        private static GUIStyle GetOverallsStyle()
        {
            if (_overallsStyle == null)
            {
                if (!_overallsImageLoadAttempted)
                {
                    _overallsImageLoadAttempted = true;
                    _overallsImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.overalls_icon.png");
                }

                _overallsStyle = new GUIStyle();
                _overallsStyle.normal.background = _overallsImageTexture != null ? _overallsImageTexture : GetSolidTexture();
            }

            return _overallsStyle;
        }

        private static GUIStyle GetPantsStyle()
        {
            if (_pantsStyle == null)
            {
                if (!_pantsImageLoadAttempted)
                {
                    _pantsImageLoadAttempted = true;
                    _pantsImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.pants_icon.png");
                }

                _pantsStyle = new GUIStyle();
                _pantsStyle.normal.background = _pantsImageTexture != null ? _pantsImageTexture : GetSolidTexture();
            }

            return _pantsStyle;
        }

        private static GUIStyle GetGlovesStyle()
        {
            if (_glovesStyle == null)
            {
                if (!_glovesImageLoadAttempted)
                {
                    _glovesImageLoadAttempted = true;
                    _glovesImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.gloves_icon.png");
                }

                _glovesStyle = new GUIStyle();
                _glovesStyle.normal.background = _glovesImageTexture != null ? _glovesImageTexture : GetSolidTexture();
            }

            return _glovesStyle;
        }

        private static GUIStyle GetBootsStyle()
        {
            if (_bootsStyle == null)
            {
                if (!_bootsImageLoadAttempted)
                {
                    _bootsImageLoadAttempted = true;
                    _bootsImageTexture = TryLoadEmbeddedImage("MyFirstMod.Assets.boots_icon.png");
                }

                _bootsStyle = new GUIStyle();
                _bootsStyle.normal.background = _bootsImageTexture != null ? _bootsImageTexture : GetSolidTexture();
            }

            return _bootsStyle;
        }

        private static Texture2D TryLoadEmbeddedImage(string resourceName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return null;
                }

                byte[] bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);

                Texture2D tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    return null;
                }

                ConvertToWhiteMask(tex);
                return tex;
            }
            catch
            {
                return null;
            }
        }

        // GUI tints a texture by multiplying it with GUI.color, so only pixels that start out
        // white can become any chosen colour - anything darker (e.g. a black icon's own fill)
        // stays stuck near black no matter what's selected. This turns every "foreground" pixel
        // fully white (keeping only its shape) and makes the background fully transparent, so the
        // whole icon - not just its outline - tints correctly to any selected colour.
        //
        // If the source PNG already has real alpha transparency, that's trusted directly. If not
        // (a flattened crop with no alpha channel), the background colour is sampled from the
        // image's own corners rather than assumed to be white - a fixed "near-white" threshold
        // previously kept a grey card background and dropped a white icon drawn on it, which is
        // backwards from what's wanted.
        private static void ConvertToWhiteMask(Texture2D tex)
        {
            bool hasRealAlpha = false;
            for (int y = 0; y < tex.height && !hasRealAlpha; y++)
            {
                for (int x = 0; x < tex.width; x++)
                {
                    if (tex.GetPixel(x, y).a < 0.95f)
                    {
                        hasRealAlpha = true;
                        break;
                    }
                }
            }

            if (hasRealAlpha)
            {
                for (int y = 0; y < tex.height; y++)
                {
                    for (int x = 0; x < tex.width; x++)
                    {
                        Color c = tex.GetPixel(x, y);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, c.a));
                    }
                }
            }
            else
            {
                Color bg = AverageCorners(tex);
                const float threshold = 0.18f;

                for (int y = 0; y < tex.height; y++)
                {
                    for (int x = 0; x < tex.width; x++)
                    {
                        Color c = tex.GetPixel(x, y);
                        float dist = Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b);
                        bool isBackground = dist < threshold;
                        tex.SetPixel(x, y, isBackground ? new Color(1f, 1f, 1f, 0f) : new Color(1f, 1f, 1f, 1f));
                    }
                }
            }

            tex.Apply();
        }

        private static Color AverageCorners(Texture2D tex)
        {
            int w = tex.width - 1;
            int h = tex.height - 1;
            Color c0 = tex.GetPixel(0, 0);
            Color c1 = tex.GetPixel(w, 0);
            Color c2 = tex.GetPixel(0, h);
            Color c3 = tex.GetPixel(w, h);
            return new Color((c0.r + c1.r + c2.r + c3.r) / 4f, (c0.g + c1.g + c2.g + c3.g) / 4f, (c0.b + c1.b + c2.b + c3.b) / 4f);
        }

        // u/v run 0..1 across the texture, v = 0 at the bottom (Texture2D.SetPixel convention).
        private static bool IsShirtPixel(float u, float v)
        {
            bool body = u >= 0.22f && u <= 0.78f && v <= 0.75f;
            bool collar = u >= 0.30f && u <= 0.70f && v > 0.75f && v <= 0.85f;
            bool leftSleeve = u >= 0.02f && u <= 0.24f && v >= 0.55f && v <= 0.85f;
            bool rightSleeve = u >= 0.76f && u <= 0.98f && v >= 0.55f && v <= 0.85f;

            float neckDx = u - 0.5f;
            float neckDy = v - 0.82f;
            bool neckNotch = (neckDx * neckDx) / (0.08f * 0.08f) + (neckDy * neckDy) / (0.06f * 0.06f) <= 1f;

            return (body || collar || leftSleeve || rightSleeve) && !neckNotch;
        }

        private static bool IsHairPixel(float u, float v)
        {
            float dx = (u - 0.5f) / 0.4f;
            float dy = (v - 0.32f) / 0.5f;
            return dx * dx + dy * dy <= 1f && v >= 0.32f;
        }

        private static Texture2D BuildShapeTexture(System.Func<float, float, bool> insideShape)
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);

            for (int y = 0; y < size; y++)
            {
                float v = (float)y / (size - 1);
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / (size - 1);
                    tex.SetPixel(x, y, insideShape(u, v) ? Color.white : new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }

        private static Texture2D GetSolidTexture()
        {
            if (_solidTexture != null)
            {
                return _solidTexture;
            }

            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _solidTexture = tex;
            return _solidTexture;
        }

        private static GUIStyle _solidStyle;

        // Generic swatch for accessories - unlike outfit/hair there's no single icon that fits
        // every possible accessory (mask, hat, gloves...), so this is just a plain colour block.
        private static GUIStyle GetSolidStyle()
        {
            if (_solidStyle == null)
            {
                _solidStyle = new GUIStyle();
                _solidStyle.normal.background = GetSolidTexture();
            }

            return _solidStyle;
        }

    }
}
