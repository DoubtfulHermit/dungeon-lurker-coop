using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DungeonLurkerCoop;

/// Dumps the game's data (boons, spells, items, enemies, loot pools...) to
/// JSON + PNG icons for the "game bible". Reflection-walks every reachable
/// ScriptableObject once into a registry; live Bot stats are captured per
/// scene. Read-only with respect to the game.
public class BibleDumper : MonoBehaviour
{
    private const int MaxDepth = 7;
    private const int MaxList = 300;

    private readonly Dictionary<UnityEngine.Object, string> ids = new();
    private readonly Dictionary<string, string> objectJson = new();   // id -> json
    private readonly Queue<UnityEngine.Object> pending = new();
    private readonly Dictionary<string, string> sceneEnemies = new(); // scene -> json
    private readonly HashSet<string> exportedSprites = new();
    private string dumpDir;
    private int nextId;

    public static void Bootstrap()
    {
        if (!Plugin.BibleDump.Value) return;
        var go = new GameObject("DLBibleDumper");
        DontDestroyOnLoad(go);
        go.AddComponent<BibleDumper>();
        Plugin.Log.LogInfo("BibleDumper active.");
    }

    private void Awake()
    {
        dumpDir = Plugin.BibleDumpDir.Value;
        Directory.CreateDirectory(dumpDir);
        Directory.CreateDirectory(Path.Combine(dumpDir, "icons"));
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        StartCoroutine(DumpAfterDelay(scene.name));
    }

    private IEnumerator DumpAfterDelay(string sceneName)
    {
        yield return new WaitForSecondsRealtime(3f);
        try { DumpEverything(sceneName); }
        catch (Exception e) { Plugin.Log.LogError($"Bible dump failed: {e}"); }
    }

    private void DumpEverything(string sceneName)
    {
        // Roots: the global settings + every loaded data asset of interest.
        EnqueueRoots();
        int processed = 0;
        while (pending.Count > 0)
        {
            var obj = pending.Dequeue();
            if (obj == null) continue;
            string id = ids[obj];
            if (objectJson.ContainsKey(id)) continue;
            objectJson[id] = DumpObject(obj);
            processed++;
        }
        DumpLiveEnemies(sceneName);
        WriteFiles();
        Plugin.Log.LogInfo($"Bible: registry={objectJson.Count} (+{processed} new), scenes={sceneEnemies.Count}, icons={exportedSprites.Count} [{sceneName}]");
    }

    private void EnqueueRoots()
    {
        var rootTypes = new[]
        {
            typeof(ItemData), typeof(ScriptableObject)
        };
        // Broad sweep: every loaded ScriptableObject from the game assembly.
        foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
        {
            var t = so.GetType();
            if (t.Assembly != typeof(Player).Assembly) continue;
            Enroll(so);
        }
    }

    private string Enroll(UnityEngine.Object obj)
    {
        if (obj == null) return null;
        if (!ids.TryGetValue(obj, out var id))
        {
            id = $"{obj.GetType().Name}#{nextId++}";
            ids[obj] = id;
            if (obj is ScriptableObject) pending.Enqueue(obj);
        }
        return id;
    }

    private string DumpObject(UnityEngine.Object obj)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append($"\"$type\":{Quote(obj.GetType().Name)},\"$name\":{Quote(obj.name)}");
        foreach (var f in AllFields(obj.GetType()))
        {
            object val;
            try { val = f.GetValue(obj); } catch { continue; }
            sb.Append(',').Append(Quote(f.Name)).Append(':');
            RenderValue(sb, val, 0);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static IEnumerable<FieldInfo> AllFields(Type t)
    {
        while (t != null && t != typeof(UnityEngine.Object) && t != typeof(object))
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsSubclassOf(typeof(Delegate))) continue;
                yield return f;
            }
            t = t.BaseType;
        }
    }

    private void RenderValue(StringBuilder sb, object val, int depth)
    {
        if (val == null || val is UnityEngine.Object uo0 && uo0 == null) { sb.Append("null"); return; }
        if (depth > MaxDepth) { sb.Append("\"<depth>\""); return; }

        switch (val)
        {
            case string s: sb.Append(Quote(s)); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case Enum e: sb.Append(Quote(e.ToString())); return;
            case float f: sb.Append(!float.IsNaN(f) && !float.IsInfinity(f) ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : Quote(f.ToString())); return;
            case double d: sb.Append(!double.IsNaN(d) && !double.IsInfinity(d) ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : Quote(d.ToString())); return;
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                sb.Append(Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture)); return;
            case Vector2 v2: sb.Append(Quote($"({v2.x:R}, {v2.y:R})")); return;
            case Vector3 v3: sb.Append(Quote($"({v3.x:R}, {v3.y:R}, {v3.z:R})")); return;
            case Color c: sb.Append(Quote($"#{ColorUtility.ToHtmlStringRGBA(c)}")); return;
            case Sprite sp:
                sb.Append($"{{\"$sprite\":{Quote(sp.name)},\"$file\":{Quote(ExportSprite(sp))}}}");
                return;
            case AnimationCurve ac:
            {
                sb.Append('[');
                for (int i = 0; i < ac.keys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"[{ac.keys[i].time:R},{ac.keys[i].value:R}]");
                }
                sb.Append(']');
                return;
            }
            case Gradient g:
            {
                sb.Append('[');
                for (int i = 0; i < g.colorKeys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote($"#{ColorUtility.ToHtmlStringRGBA(g.colorKeys[i].color)}@{g.colorKeys[i].time:R}"));
                }
                sb.Append(']');
                return;
            }
            case GameObject go:
                sb.Append($"{{\"$gameObject\":{Quote(go.name)}}}");
                return;
            case UnityEngine.Object uo:
            {
                string id = Enroll(uo);
                sb.Append($"{{\"$ref\":{Quote(id)},\"$name\":{Quote(uo.name)}}}");
                return;
            }
            case IDictionary dict:
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(kv.Key?.ToString() ?? "null")).Append(':');
                    RenderValue(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                sb.Append('[');
                int n = 0;
                foreach (var item in list)
                {
                    if (n >= MaxList) { sb.Append(",\"<truncated>\""); break; }
                    if (n > 0) sb.Append(',');
                    RenderValue(sb, item, depth + 1);
                    n++;
                }
                sb.Append(']');
                return;
            }
        }

        var t = val.GetType();
        if (t.IsClass || (t.IsValueType && !t.IsPrimitive))
        {
            sb.Append('{');
            sb.Append($"\"$type\":{Quote(t.Name)}");
            foreach (var f in AllFields(t))
            {
                object inner;
                try { inner = f.GetValue(val); } catch { continue; }
                sb.Append(',').Append(Quote(f.Name)).Append(':');
                RenderValue(sb, inner, depth + 1);
            }
            sb.Append('}');
            return;
        }
        sb.Append(Quote(val.ToString()));
    }

    /// Sprites aren't CPU-readable; blit through a RenderTexture to export.
    private string ExportSprite(Sprite sp)
    {
        string safe = Sanitize(sp.name) + ".png";
        if (exportedSprites.Contains(safe)) return "icons/" + safe;
        try
        {
            var tex = sp.texture;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var r = sp.textureRect;
            var outTex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(r.x, tex.height - r.y - r.height, r.width, r.height), 0, 0);
            outTex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(Path.Combine(dumpDir, "icons", safe), outTex.EncodeToPNG());
            Destroy(outTex);
            exportedSprites.Add(safe);
            return "icons/" + safe;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Sprite export failed for {sp.name}: {e.Message}");
            return null;
        }
    }

    /// Live enemies: initialized Bots carry real computed stats.
    private void DumpLiveEnemies(string sceneName)
    {
        var actors = LevelManager.GetActorList();
        if (actors == null || actors.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append('[');
        bool first = true;
        foreach (var a in actors)
        {
            if (a is not Bot bot || bot == null) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{');
            sb.Append($"\"name\":{Quote(bot.name)},\"enemyName\":{Quote(bot.enemyName)}");
            try { sb.Append($",\"maxHealth\":{bot.GetHurtbox().MaxHealth()}"); } catch { }
            sb.Append($",\"team\":{Quote(bot.team.ToString())}");
            sb.Append($",\"behaviour\":{Quote(bot.behaviour.ToString())}");
            sb.Append($",\"targetingRange\":{bot.targetingRange.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
            try { sb.Append($",\"soulTokens\":{bot.actorSettings.baseSoulTokenValue}"); } catch { }
            if (bot.actorSettings != null) sb.Append($",\"actorSettings\":{Quote(Enroll(bot.actorSettings))}");
            sb.Append('}');
        }
        sb.Append(']');
        sceneEnemies[sceneName] = sb.ToString();
        // actorSettings enrolled above may be new — flush them.
        while (pending.Count > 0)
        {
            var obj = pending.Dequeue();
            if (obj == null) continue;
            string id = ids[obj];
            if (!objectJson.ContainsKey(id)) objectJson[id] = DumpObject(obj);
        }
    }

    private void WriteFiles()
    {
        var sb = new StringBuilder(1 << 20);
        sb.Append("{\"objects\":{");
        bool first = true;
        foreach (var kv in objectJson)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quote(kv.Key)).Append(':').Append(kv.Value);
        }
        sb.Append("},\"sceneEnemies\":{");
        first = true;
        foreach (var kv in sceneEnemies)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quote(kv.Key)).Append(':').Append(kv.Value);
        }
        sb.Append("}}");
        File.WriteAllText(Path.Combine(dumpDir, "bible.json"), sb.ToString());
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
