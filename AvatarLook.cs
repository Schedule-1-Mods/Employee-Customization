using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppScheduleOne.AvatarFramework;
using MelonLoader;
using UnityEngine;

namespace MyFirstMod
{
    // Reads and edits an employee's hair and clothing colours, on whichever avatar system the
    // running game has. The mod is one file for every Steam branch, and they differ:
    //
    //  - Default Public (0.4.6) and older: Avatar.CurrentSettings (AvatarSettings) holds lists of
    //    body layers, face layers and accessories, each an asset path with a tint; colours are
    //    changed on those lists and pushed with Avatar.ApplyBodyLayerSettings (hair with
    //    OverrideHairColor). This is compiled normally - the mod is built against 0.4.6.
    //  - beta (0.4.7+): Avatar.Appearance (AvatarAppearance) is built from "avatar objects"
    //    (hair, face, each piece of clothing), each carrying named colour properties. Those types
    //    don't exist in 0.4.6, so this side is reached by name at runtime (reflection) - nothing
    //    0.4.7-only is compiled into the mod, so it loads on every version.
    //
    // Which one is used is decided once, by whether the running game's Avatar has "Appearance".
    internal static class AvatarLook
    {
        public sealed class Look
        {
            public Avatar Avatar;
            public object Appearance;   // 0.4.7+ only
        }

        public sealed class Slot
        {
            public string Key;          // stable across reloads: "<object id>|<colour name>" (0.4.7+) or "<kind>|<asset path>" (0.4.6)
            public string Name;         // shown in the editor
            public Color Colour;
        }

        private static bool? _newSystem;
        public static bool NewSystem
        {
            get
            {
                if (_newSystem == null)
                {
                    _newSystem = typeof(Avatar).GetProperty("Appearance") != null;
                    MelonLogger.Msg($"[CharacterEditor] Avatar system: {(_newSystem.Value ? "0.4.7+ (avatar objects)" : "0.4.6 (avatar settings)")}.");
                }
                return _newSystem.Value;
            }
        }

        public static Look Get(Avatar avatar)
        {
            if (avatar == null) return null;
            if (NewSystem)
            {
                var app = Modern.Appearance(avatar);
                return app == null ? null : new Look { Avatar = avatar, Appearance = app };
            }
            return Legacy.Ready(avatar) ? new Look { Avatar = avatar } : null;
        }

        public static Color GetHair(Look look) => NewSystem ? Modern.GetHair(look) : Legacy.GetHair(look);
        public static void SetHair(Look look, Color c) { if (NewSystem) Modern.SetHair(look, c); else Legacy.SetHair(look, c); }
        public static List<Slot> Slots(Look look, System.Func<string, bool> allowNaked) => NewSystem ? Modern.Slots(look, allowNaked) : Legacy.Slots(look, allowNaked);
        public static bool Set(Look look, string key, Color c) => NewSystem ? Modern.Set(look, key, c) : Legacy.Set(look, key, c);
        public static void Refresh(Look look) { try { if (NewSystem) Modern.Refresh(look); else Legacy.Refresh(look); } catch { } }
        public static IEnumerable<string> Describe(Look look) => NewSystem ? Modern.Describe(look) : Legacy.Describe(look);

        // Key for a colour saved by the pre-0.4.7 version of this mod (kind + list index).
        public static string LegacyKey(Look look, string kind, int index) => NewSystem ? null : Legacy.KeyAt(look, kind, index);

        // Items deliberately left out of the editor.
        static bool Hidden(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("respirator") || n.Contains("belt") || n.Contains("combat boots") || n.Contains("apron");
        }

        // "hazmat_suit" / "Avatar/Tops/HazmatSuit" -> "Hazmat Suit".
        public static string Friendly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Item";
            int slash = raw.LastIndexOf('/');
            if (slash >= 0) raw = raw.Substring(slash + 1);
            raw = raw.Replace('_', ' ').Replace('-', ' ');
            var b = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(raw[i - 1])) b.Append(' ');
                b.Append(i == 0 || raw[i - 1] == ' ' ? char.ToUpperInvariant(c) : c);
            }
            return b.ToString().Trim();
        }

        static string Hex(Color c) { Color32 x = c; return $"#{x.r:X2}{x.g:X2}{x.b:X2}"; }

        // ------------------------------------------------------------------ 0.4.6 (AvatarSettings)
        private static class Legacy
        {
            public static bool Ready(Avatar a) => a.CurrentSettings != null;

            public static Color GetHair(Look l) => l.Avatar.CurrentSettings.HairColor;

            public static void SetHair(Look l, Color c)
            {
                l.Avatar.CurrentSettings.HairColor = c;
                l.Avatar.OverrideHairColor(c);
            }

            public static List<Slot> Slots(Look l, System.Func<string, bool> allow)
            {
                var slots = new List<Slot>();
                var s = l.Avatar.CurrentSettings;
                if (s.BodyLayerSettings != null)
                    for (int i = 0; i < s.BodyLayerSettings.Count; i++)
                        Add(slots, allow, "BodyLayer", s.BodyLayerSettings[i].layerPath, s.BodyLayerSettings[i].layerTint);
                if (s.FaceLayerSettings != null)
                    for (int i = 0; i < s.FaceLayerSettings.Count; i++)
                        Add(slots, allow, "FaceLayer", s.FaceLayerSettings[i].layerPath, s.FaceLayerSettings[i].layerTint);
                if (s.AccessorySettings != null)
                    for (int i = 0; i < s.AccessorySettings.Count; i++)
                        Add(slots, allow, "Accessory", s.AccessorySettings[i].path, s.AccessorySettings[i].color);
                return slots;
            }

            static void Add(List<Slot> slots, System.Func<string, bool> allow, string kind, string path, Color c)
            {
                if (string.IsNullOrEmpty(path)) return;
                string name = Friendly(path);
                if (Hidden(name) || !allow(name)) return;
                slots.Add(new Slot { Key = kind + "|" + path, Name = name, Colour = c });
            }

            public static bool Set(Look l, string key, Color c)
            {
                int bar = key.IndexOf('|');
                if (bar < 0) return false;
                string kind = key.Substring(0, bar), path = key.Substring(bar + 1);
                var s = l.Avatar.CurrentSettings;
                bool done = false;
                if (kind == "BodyLayer" && s.BodyLayerSettings != null)
                    for (int i = 0; i < s.BodyLayerSettings.Count; i++)
                    {
                        var e = s.BodyLayerSettings[i];
                        if (e.layerPath != path) continue;
                        e.layerTint = c; s.BodyLayerSettings[i] = e; done = true;
                    }
                if (kind == "FaceLayer" && s.FaceLayerSettings != null)
                    for (int i = 0; i < s.FaceLayerSettings.Count; i++)
                    {
                        var e = s.FaceLayerSettings[i];
                        if (e.layerPath != path) continue;
                        e.layerTint = c; s.FaceLayerSettings[i] = e; done = true;
                    }
                if (kind == "Accessory" && s.AccessorySettings != null)
                    for (int i = 0; i < s.AccessorySettings.Count; i++)
                    {
                        var e = s.AccessorySettings[i];
                        if (e.path != path) continue;
                        e.color = c; s.AccessorySettings[i] = e; done = true;
                    }
                return done;
            }

            // Only the body layers are re-applied (not hair, face layers or accessories): a full
            // LoadAvatarSettings rebuilds everything and made masks and footwear vanish.
            public static void Refresh(Look l) => l.Avatar.ApplyBodyLayerSettings(l.Avatar.CurrentSettings);

            public static string KeyAt(Look l, string kind, int index)
            {
                var s = l.Avatar.CurrentSettings;
                if (kind == "BodyLayer" && s.BodyLayerSettings != null && index >= 0 && index < s.BodyLayerSettings.Count) return kind + "|" + s.BodyLayerSettings[index].layerPath;
                if (kind == "FaceLayer" && s.FaceLayerSettings != null && index >= 0 && index < s.FaceLayerSettings.Count) return kind + "|" + s.FaceLayerSettings[index].layerPath;
                if (kind == "Accessory" && s.AccessorySettings != null && index >= 0 && index < s.AccessorySettings.Count) return kind + "|" + s.AccessorySettings[index].path;
                return null;
            }

            public static IEnumerable<string> Describe(Look l)
            {
                var s = l.Avatar.CurrentSettings;
                var o = new List<string>();
                if (s.BodyLayerSettings != null) for (int i = 0; i < s.BodyLayerSettings.Count; i++) o.Add($"BodyLayer {s.BodyLayerSettings[i].layerPath} {Hex(s.BodyLayerSettings[i].layerTint)}");
                if (s.FaceLayerSettings != null) for (int i = 0; i < s.FaceLayerSettings.Count; i++) o.Add($"FaceLayer {s.FaceLayerSettings[i].layerPath} {Hex(s.FaceLayerSettings[i].layerTint)}");
                if (s.AccessorySettings != null) for (int i = 0; i < s.AccessorySettings.Count; i++) o.Add($"Accessory {s.AccessorySettings[i].path} {Hex(s.AccessorySettings[i].color)}");
                return o;
            }
        }

        // ------------------------------------------------------------------ 0.4.7+ (by name)
        private static class Modern
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            static System.Type _hairType, _faceType;
            static bool _typesLooked;

            public static object Appearance(Avatar a) => typeof(Avatar).GetProperty("Appearance")?.GetValue(a);

            static object Member(object o, string name)
            {
                if (o == null) return null;
                var t = o.GetType();
                var p = t.GetProperty(name, Any);
                if (p != null) return p.GetValue(o);
                return t.GetField(name, Any)?.GetValue(o);
            }

            // Items of an Il2Cpp list (Count + indexer).
            static IEnumerable<object> Items(object list)
            {
                if (list == null) yield break;
                var t = list.GetType();
                int n = (int)(t.GetProperty("Count")?.GetValue(list) ?? 0);
                var get = t.GetMethod("get_Item", new[] { typeof(int) });
                if (get == null) yield break;
                for (int i = 0; i < n; i++)
                {
                    var x = get.Invoke(list, new object[] { i });
                    if (x != null) yield return x;
                }
            }

            static System.Type Find(string fullName) =>
                System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);

            static bool Is(object o, ref System.Type cache)
            {
                if (!_typesLooked)
                {
                    _typesLooked = true;
                    _hairType = Find("Il2CppScheduleOne.Core.Avatar.HairAvatarObject");
                    _faceType = Find("Il2CppScheduleOne.Core.Avatar.FaceAvatarObject");
                }
                if (cache == null || !(o is Il2CppObjectBase b)) return false;
                var cast = typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(cache);
                return cast.Invoke(b, null) != null;
            }
            static bool IsHair(object o) => Is(o, ref _hairType);
            static bool IsFace(object o) => Is(o, ref _faceType);

            static IEnumerable<object> Objects(Look l) => Items(Member(l.Appearance, "_appliedObjects"));
            static IEnumerable<object> Colours(object obj) => Items(Member(Member(obj, "PropertyCollection"), "_colors"));
            static Color Value(object colourProp) => (Color)Member(colourProp, "Value");
            static bool Editable(object colourProp) => Member(colourProp, "Editable") is bool b && b;

            public static Color GetHair(Look l)
            {
                foreach (var obj in Objects(l))
                {
                    if (!IsHair(obj)) continue;
                    foreach (var c in Colours(obj)) return Value(c);
                }
                return Color.white;
            }

            public static void SetHair(Look l, Color c) =>
                l.Appearance.GetType().GetMethod("SetHairColor", new[] { typeof(Color) })?.Invoke(l.Appearance, new object[] { c });

            public static List<Slot> Slots(Look l, System.Func<string, bool> allowNaked)
            {
                var slots = new List<Slot>();
                var worn = new HashSet<System.IntPtr>();
                foreach (var o in Items(Member(l.Appearance, "_wornObjects")))
                    if (o is Il2CppObjectBase b) worn.Add(b.Pointer);

                foreach (var obj in Objects(l))
                {
                    if (IsHair(obj) || IsFace(obj)) continue;
                    string objName = Member(obj, "Name") as string, id = Member(obj, "Id") as string;
                    string name = Friendly(!string.IsNullOrEmpty(objName) ? objName : id);
                    if (Hidden(name)) continue;
                    if (!(obj is Il2CppObjectBase ob) || (!worn.Contains(ob.Pointer) && !allowNaked(name))) continue;

                    var colours = Colours(obj).Where(Editable).ToList();
                    foreach (var c in colours)
                    {
                        string cName = Member(c, "Name") as string;
                        slots.Add(new Slot
                        {
                            Key = id + "|" + cName,
                            Name = colours.Count > 1 ? $"{name} ({Friendly(cName)})" : name,
                            Colour = Value(c),
                        });
                    }
                }
                return slots;
            }

            public static bool Set(Look l, string key, Color colour)
            {
                foreach (var obj in Objects(l))
                {
                    string id = Member(obj, "Id") as string;
                    foreach (var c in Colours(obj))
                    {
                        if (id + "|" + (Member(c, "Name") as string) != key || !Editable(c)) continue;
                        var set = c.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "SetValue" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Color));
                        if (set == null) return false;
                        var ps = set.GetParameters();
                        var args = new object[ps.Length];
                        args[0] = colour;
                        for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? System.Activator.CreateInstance(ps[i].ParameterType) : null);
                        set.Invoke(c, args);
                        return true;
                    }
                }
                return false;
            }

            public static void Refresh(Look l) => l.Appearance.GetType().GetMethod("RepaintLayers", System.Type.EmptyTypes)?.Invoke(l.Appearance, null);

            public static IEnumerable<string> Describe(Look l)
            {
                foreach (var obj in Objects(l))
                {
                    var parts = Colours(obj).Select(c => $"{Member(c, "Name")}={Hex(Value(c))}{(Editable(c) ? "" : " (fixed)")}");
                    yield return $"{Member(obj, "Id")} \"{Member(obj, "Name")}\" {string.Join(", ", parts)}";
                }
            }
        }
    }
}
