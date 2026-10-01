using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json;
using Rockwall;
using Rockwall2.Editor.Common.Input;
using Rockwall2.Editor.Mapper;
using Rockwall2.Editor.Mapper.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace Rockwall2.Editor.Common;

public static class MapTools
{
    public static string ActivePath = "";
    public static RawMap ActiveMap;
    public static bool MapModified;
    public static bool MapLoaded;
    public static Brush[] Brushes => ActiveMap.Brushes;
    public static BoundingBox[] BrushBounds { get; private set; }
    public static Hint[] Hints => ActiveMap.Hints;
    public static EntityReference[] Entities => ActiveMap.EntityReferences;
    public static Terrain[] Terrains => ActiveMap.Terrains;
    public static EditorGroup[] Groups => ActiveMap.Groups;

    public static VertexPosition[] LeakPoints;

    public static Dictionary<Guid, (int index, ObjType type)> GuidMapper { get; private set; } = new();

    public static int ResolveIndex(Guid? id, ObjType type)
    {
        if (id.HasValue && GuidMapper.TryGetValue(id.Value, out var entry) && entry.type == type)
            return entry.index;
        return -1;
    }

    public static void RebuildGuidMapper()
    {
        GuidMapper.Clear();

        for (int i = 0; i < Brushes.Length; i++)
        {
            if (!Brushes[i].GroupingID.HasValue) Brushes[i].GroupingID = Guid.NewGuid();
            GuidMapper[Brushes[i].GroupingID!.Value] = (i, ObjType.Brush);
        }

        for (int i = 0; i < Terrains.Length; i++)
        {
            if (!Terrains[i].GroupingID.HasValue) Terrains[i].GroupingID = Guid.NewGuid();
            GuidMapper[Terrains[i].GroupingID!.Value] = (i, ObjType.Terrain);
        }

        for (int i = 0; i < Entities.Length; i++)
        {
            if (!Entities[i].GroupingID.HasValue) Entities[i].GroupingID = Guid.NewGuid();
            GuidMapper[Entities[i].GroupingID!.Value] = (i, ObjType.Entity);

            // brushIndices is a derived cache of brushOwnerGUIDs (the authoritative reference)
            // resolved fresh here, same as Terrain.brushSource is resolved from BrushOwnerGUID above.
            if (Entities[i].brushOwnerGUIDs != null)
            {
                Entities[i].BrushIndices = Entities[i].brushOwnerGUIDs
                    .Select(g => ResolveIndex(g, ObjType.Brush))
                    .Where(idx => idx != -1)
                    .ToList();
            }
        }

        for (int i = 0; i < Hints.Length; i++)
        {
            if (!Hints[i].GroupingID.HasValue) Hints[i].GroupingID = Guid.NewGuid();
            GuidMapper[Hints[i].GroupingID!.Value] = (i, ObjType.Hint);
        }

        VisGroupManager.MarkDirty();
    }
    public static void BreakParentGroup(Guid guid)
    {
        var element = Array.Find(Groups, g => g.GroupMembers.Contains(guid));

        if (element != null) FreeGroup(element);
    }
    public static void RemoveFromParentGroup(Guid guid)
    {
        var element = Array.Find(Groups, g => g.GroupMembers.Contains(guid));

        if (element != null)
        {
            var lst = element.GroupMembers.ToList();
            lst.Remove(guid);
            element.GroupMembers = lst.ToArray();
        }
    }
    public static EditorGroup GetOwningGroup(Guid guid)
    {
        return Array.Find(Groups, g => g.GroupMembers.Contains(guid));
    }

    public static bool FastVisEnabled = true;
    public static int LightmapRes = 8;

    static Queue<Brush> removeBrushes = new Queue<Brush>();
    static Queue<EntityReference> removeEntities = new Queue<EntityReference>();
    static Queue<Terrain> removeTerrains = new Queue<Terrain>();
    static Queue<Hint> removeHints = new Queue<Hint>();

    public enum ObjType
    {
        Brush, Terrain, Entity, Hint
    }

    public static void AddGroup(EditorGroup g)
    {
        var lst = Groups.ToList();

        // One object can only be part of one group at a time.
        var members = g.GroupMembers.ToList();
        members.RemoveAll(m => Groups.Any(p => p.GroupMembers.Contains(m)));
        g.GroupMembers = members.ToArray();

        lst.Add(g);

        ActiveMap.Groups = lst.ToArray();
    }
    public static void FreeGroup(EditorGroup g)
    {
        var lst = Groups.ToList();
        lst.Remove(g);

        ActiveMap.Groups = lst.ToArray();
    }

    public static void AddBrush(Brush b)
    {
        b.GroupingID ??= Guid.NewGuid();

        var lst = MapTools.Brushes.ToList();

        GuidMapper.Add(b.GroupingID!.Value, (lst.Count, ObjType.Brush));

        lst.Add(b);

        ActiveMap.Brushes = lst.ToArray();

        BrushBounds = new BoundingBox[Brushes.Length];

        for (int i = 0; i < Brushes.Length; i++)
        {
            RecomputeBrushBounds(i);
        }

        if (Terrains == null) return;

        for (int i = 0; i < Terrains.Length; i++)
        {
            BrushOperations.UpdateTerrain(ref Terrains[i]);
        }
        VisGroupManager.MarkDirty();
    }
    public static void RemoveBrush(Brush b)
    {
        removeBrushes.Enqueue(b);
    }
    private static void RemoveBrushReal(Brush b)
    {
        if (b.GroupingID.HasValue)
        {
            RemoveFromParentGroup(b.GroupingID!.Value);
            GuidMapper.Remove(b.GroupingID!.Value);
        }

        var lst = MapTools.Brushes.ToList();
        int id = lst.FindIndex(brush => brush.Vertices == b.Vertices && brush.UVs == b.UVs && brush.Faces == b.Faces);
        if (id != -1)
        {
            lst.RemoveAt(id);
            RemapBrushOwnershipOnRemoval(b.GroupingID);
        }
        ActiveMap.Brushes = lst.ToArray();

        BrushBounds = new BoundingBox[Brushes.Length];

        foreach (var t in Terrains.Where(t => t.BrushOwnerGUID == b.GroupingID))
        {
            RemoveTerrain(t);
        }

        for (int i = 0; i < Brushes.Length; i++)
        {
            RecomputeBrushBounds(i);
        }

        SyncBrushOwnership();
    }

    private static void RemapBrushOwnershipOnRemoval(Guid? removedBrushGuid)
    {
        if (Entities == null) return;

        foreach (var entity in Entities)
        {
            if (entity?.brushOwnerGUIDs == null || entity.brushOwnerGUIDs.Count == 0) continue;

            entity.brushOwnerGUIDs.RemoveAll(g => g == removedBrushGuid);
            entity.BrushIndices = entity.brushOwnerGUIDs
                .Select(ResolveBrushIndexDirect)
                .Where(idx => idx != -1)
                .ToList();
        }
    }

    public static int ResolveBrushIndexDirect(Guid id)
    {
        for (int i = 0; i < Brushes.Length; i++)
            if (Brushes[i].GroupingID == id) return i;
        return -1;
    }

    public static List<Guid> GuidsForBrushIndices(IEnumerable<int> indices)
    {
        return indices
            .Where(i => i >= 0 && i < Brushes.Length)
            .Select(i => Brushes[i].GroupingID ??= Guid.NewGuid())
            .ToList();
    }

    public static void AddBrushToEntity(EntityReference owner, int brushIndex)
    {
        if (owner == null || brushIndex < 0 || brushIndex >= Brushes.Length) return;

        owner.BrushIndices ??= new List<int>();
        owner.brushOwnerGUIDs ??= new List<Guid>();

        owner.BrushIndices.Add(brushIndex);
        owner.brushOwnerGUIDs.Add(Brushes[brushIndex].GroupingID ??= Guid.NewGuid());
    }

    public static void RemoveBrushFromEntity(EntityReference owner, int brushIndex)
    {
        if (owner?.BrushIndices == null) return;

        Guid? guid = brushIndex >= 0 && brushIndex < Brushes.Length ? Brushes[brushIndex].GroupingID : null;
        owner.BrushIndices.RemoveAll(i => i == brushIndex);
        owner.brushOwnerGUIDs?.RemoveAll(g => g == guid);
    }

    public static void SyncBrushOwnership()
    {
        if (ActiveMap.Brushes == null || ActiveMap.EntityReferences == null) return;

        Rockwall.EntityOwnership.Sync(ActiveMap.Brushes, ActiveMap.EntityReferences);

        var orphaned = ActiveMap.EntityReferences
            .Where(e => e != null && e.BrushIndices != null && e.BrushIndices.Count == 0)
            .ToList();
        if (orphaned.Count == 0) return;

        ActiveMap.EntityReferences = ActiveMap.EntityReferences.Except(orphaned).ToArray();
    }

    public static EntityReference GetOwningEntity(int brushIndex)
    {
        if (Entities == null) return null;
        return Entities.FirstOrDefault(e => e?.BrushIndices != null && e.BrushIndices.Contains(brushIndex));
    }

    static readonly Regex trailingNumber = new(@"^(.*?)(\d+)$");

    public static string GenerateUniqueName(string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return baseName;
        if (!Entities.Any(e => e.Name == baseName)) return baseName;

        var match = trailingNumber.Match(baseName);
        string prefix = match.Success ? match.Groups[1].Value : baseName + "_";
        int width = match.Success ? match.Groups[2].Value.Length : 2;
        int next = match.Success ? int.Parse(match.Groups[2].Value) + 1 : 2;

        string candidate;
        do
        {
            candidate = prefix + next.ToString().PadLeft(width, '0');
            next++;
        }
        while (Entities.Any(e => e.Name == candidate));
        return candidate;
    }

    public static void AddEntity(EntityReference e)
    {
        e.GroupingID = Guid.NewGuid();

        var lst = Entities.ToList();

        GuidMapper.Add(e.GroupingID!.Value, (lst.Count, ObjType.Entity));

        lst.Add(e);
        ActiveMap.EntityReferences = lst.ToArray();
        VisGroupManager.MarkDirty();
    }
    public static void RemoveEntity(EntityReference e)
    {
        removeEntities.Enqueue(e);
    }
    private static void RemoveEntityReal(EntityReference e)
    {
        GuidMapper.Remove(e.GroupingID!.Value);

        var lst = Entities.ToList();
        lst.Remove(e);
        ActiveMap.EntityReferences = lst.ToArray();
    }
    public static void AddTerrain(Terrain t)
    {
        t.GroupingID = Guid.NewGuid();
        t.BrushOwnerGUID = Brushes[t.BrushSource].GroupingID;

        var lst = Terrains.ToList();

        GuidMapper.Add(t.GroupingID!.Value, (lst.Count, ObjType.Terrain));

        lst.Add(t);
        ActiveMap.Terrains = lst.ToArray();
        VisGroupManager.MarkDirty();
    }
    public static void RemoveTerrain(Terrain t)
    {
        removeTerrains.Enqueue(t);
    }
    private static void RemoveTerrainReal(Terrain t)
    {
        GuidMapper.Remove(t.GroupingID!.Value);

        var lst = Terrains.ToList();
        lst.Remove(t);

        ActiveMap.Terrains = lst.ToArray();
    }
    public static void AddHint(Hint h)
    {
        h.GroupingID = Guid.NewGuid();

        var lst = Hints.ToList();

        GuidMapper.Add(h.GroupingID!.Value, (lst.Count, ObjType.Terrain));

        lst.Add(h);
        ActiveMap.Hints = lst.ToArray();
        VisGroupManager.MarkDirty();
    }
    public static void RemoveHint(Hint h)
    {
        removeHints.Enqueue(h);
    }
    private static void RemoveHintReal(Hint h)
    {
        GuidMapper.Remove(h.GroupingID!.Value);

        var lst = Hints.ToList();
        lst.Remove(h);

        ActiveMap.Hints = lst.ToArray();
    }
    public static void FinalizeDeletedObjects()
    {
        while (removeBrushes.Count > 0) RemoveBrushReal(removeBrushes.Dequeue());
        while (removeEntities.Count > 0) RemoveEntityReal(removeEntities.Dequeue());
        while (removeTerrains.Count > 0) RemoveTerrainReal(removeTerrains.Dequeue());
        while (removeHints.Count > 0) RemoveHintReal(removeHints.Dequeue());

        for (int i = 0; i < Terrains.Length; i++)
        {
            Terrains[i].BrushSource = ResolveIndex(Terrains[i].BrushOwnerGUID, ObjType.Brush);
        }

        RebuildGuidMapper();
    }
    public static void LoadMap(string data)
    {
        Toolbelt.SelectedObjects?.Clear();
        LeakPoints = null;

        ActiveMap = Chisel.Formatter.MapMigration.LoadAndMigrate(data);

        // Add if they dont exist
        ActiveMap.Brushes ??= new Brush[0];
        ActiveMap.EntityReferences ??= new EntityReference[0];
        ActiveMap.Terrains ??= new Terrain[0];
        ActiveMap.Hints ??= new Hint[0];
        ActiveMap.Groups ??= new EditorGroup[0];
        ActiveMap.VisGroups ??= new List<UserVisGroup>();

        BrushBounds = new BoundingBox[Brushes.Length];
        MapLoaded = true;
        for (int i = 0; i < Brushes.Length; i++)
        {
            BrushOperations.RebuildBrush(ref Brushes[i]);
            BrushOperations.RebuildBrush(ref Brushes[i]);
            RecomputeBrushBounds(i);
        }

        GuidMapper.Clear();

        // Generate or load GUIDs for grouping, then add to the mapper.

        for (int i = 0; i < Brushes.Length; i++)
        {
            if (!Brushes[i].GroupingID.HasValue)
            {
                Brushes[i].GroupingID = Guid.NewGuid();
            }
            GuidMapper.Add(Brushes[i].GroupingID!.Value, (i, ObjType.Brush));
        }

        for (int i = 0; i < Terrains.Length; i++)
        {
            if (!Terrains[i].GroupingID.HasValue)
            {
                Terrains[i].GroupingID = Guid.NewGuid();
            }
            if (!Terrains[i].BrushOwnerGUID.HasValue)
            {
                Terrains[i].BrushOwnerGUID = Brushes[i].GroupingID;
            }
            if (Terrains[i].BlendedSurfaceName != null) Terrains[i].BlendedSurface = GlobalMapData.MaterialNameToIndex[Terrains[i].BlendedSurfaceName];
            if (Terrains[i].SurfaceName != null) Terrains[i].Surface = GlobalMapData.MaterialNameToIndex[Terrains[i].SurfaceName];
            Terrains[i].BrushSource = ResolveIndex(Terrains[i].BrushOwnerGUID, ObjType.Brush);
            GuidMapper.Add(Terrains[i].GroupingID!.Value, (i, ObjType.Terrain));
        }

        for (int i = 0; i < Entities.Length; i++)
        {
            if (!Entities[i].GroupingID.HasValue)
            {
                Entities[i].GroupingID = Guid.NewGuid();
            }

            if (Entities[i].BrushIndices != null && Entities[i].BrushIndices.Count > 0)
            {
                if (Entities[i].brushOwnerGUIDs == null || Entities[i].brushOwnerGUIDs.Count != Entities[i].BrushIndices.Count)
                {
                    Entities[i].brushOwnerGUIDs = Entities[i].BrushIndices
                        .Where(bi => bi >= 0 && bi < Brushes.Length)
                        .Select(bi => Brushes[bi].GroupingID!.Value)
                        .ToList();
                }

                Entities[i].BrushIndices = Entities[i].brushOwnerGUIDs
                    .Select(g => ResolveIndex(g, ObjType.Brush))
                    .Where(idx => idx != -1)
                    .ToList();
            }

            GuidMapper.Add(Entities[i].GroupingID!.Value, (i, ObjType.Entity));
        }

        for (int i = 0; i < Hints.Length; i++)
        {
            if (!Hints[i].GroupingID.HasValue)
            {
                Hints[i].GroupingID = Guid.NewGuid();
            }
            GuidMapper.Add(Hints[i].GroupingID!.Value, (i, ObjType.Hint));
        }

        SyncBrushOwnership();
        VisGroupManager.OnMapReplaced();
    }
    public static void NewMap()
    {
        LeakPoints = null;
        Toolbelt.SelectedObjects?.Clear();

        GuidMapper.Clear();

        ActiveMap = new RawMap();
        ActiveMap.Brushes = new Brush[0];
        ActiveMap.EntityReferences = new EntityReference[0];
        ActiveMap.Terrains = new Terrain[0];
        ActiveMap.Hints = new Hint[0];
        ActiveMap.Groups = new EditorGroup[0];
        ActiveMap.FormatVersion = Chisel.Formatter.MapMigration.CurrentFormatVersion;
        BrushBounds = new BoundingBox[0];
        ActiveMap.VisGroups = new List<UserVisGroup>();
        VisGroupManager.OnMapReplaced();

        MapLoaded = true;
        ActivePath = ""; // Literally just lost an awesome map because i didnt do this... damnit
    }
    public static void SaveMap()
    {
        VisGroupManager.PruneStale();
        SaveMap(ActivePath);
    }
    public static void BuildMap(bool run)
    {
        KeyboardManager.ClearKeys();

        if (string.IsNullOrEmpty(ActivePath) || !File.Exists(ActivePath))
        {
            Program.ShowMessageBox(Silk.NET.SDL.MessageBoxFlags.Warning, "Map not saved!", "You have to save your map first before compiling :)");

            return;
        }

        LeakPoints = null;

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.CreateNoWindow = false;
        startInfo.UseShellExecute = false;
        startInfo.FileName = ConfigManager.currentConfig.CompileTool;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        startInfo.Arguments = $"-\"{Path.GetFullPath(ActivePath)}\" -\"{Path.GetFullPath(ConfigManager.currentConfig.GameEDF)}\" -\"{Path.GetFullPath(GlobalEditorData.WorkingDirectory)}\"";
        startInfo.EnvironmentVariables.Add("lightmapUnitSize", $"{LightmapRes}");
        startInfo.EnvironmentVariables.Add("fastVis", $"{FastVisEnabled}");

        // Start the process with the info we specified.
        // Call WaitForExit and then the using statement will close.
        using Process? exeProcess = Process.Start(startInfo);

        if (exeProcess != null)
        {
            exeProcess.WaitForExit();

            if (exeProcess.HasExited)
            {
                Thread.Sleep(500); //????
                string leakPath = Path.ChangeExtension(Path.GetFullPath(ActivePath), "leak");
                if (File.Exists(leakPath))
                {
                    LeakPoints = File.ReadAllLines(leakPath)
                        .Select(line =>
                        {
                            var parts = line.Split(' ');
                            return new VertexPosition(new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2])));
                        })
                        .ToArray();

                    MapperView.Instance.Track(LeakPoints.Last().Position);
                }
            }

            // Hopefully this will fix the issue of the game just running anyway even if the map compiler fails.
            if (run && exeProcess.HasExited && exeProcess.ExitCode == 0)
            {
                RunCommands([$"{Path.GetFileNameWithoutExtension(ConfigManager.currentConfig.GamePath)} -map \"{Path.ChangeExtension(ActivePath, "crm")}\""],
                            Path.GetDirectoryName(ConfigManager.currentConfig.GamePath));
            }
        }
    }
    static void RunCommands(List<string> cmds, string workingDirectory = "")
    {
        var process = new Process();
        var psi = new ProcessStartInfo();
        psi.FileName = "cmd.exe";
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workingDirectory;
        process.StartInfo = psi;
        process.Start();
        using (StreamWriter sw = process.StandardInput)
        {
            foreach (var cmd in cmds)
            {
                sw.WriteLine(cmd);
            }
        }
        process.WaitForExit();
    }
    public static void SaveMap(string path)
    {
        SyncBrushOwnership();

        string data = JsonConvert.SerializeObject(ActiveMap);

        File.WriteAllText(path, data);
    }
    public static void RecomputeBrushBounds(int i)
    {
        var verts = Brushes[i].Vertices.Select(v => v + Brushes[i].Position);
        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);

        foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }

        BrushBounds[i] = new BoundingBox(min, max);
    }
    public static void RecomputeAllBrushBounds()
    {
        BrushBounds = new BoundingBox[Brushes.Length];
        for (int i = 0; i < Brushes.Length; i++)
        {
            RecomputeBrushBounds(i);
        }
    }
    public static float RaycastAllGeometry(Ray ray)
    {
        var maphit = RaycastMapGeometry(ray,true,skipTerrainSource:true);
        var terrainhit = RaycastTerrains(ray);

        return float.Min(maphit.distance, terrainhit.distance);
    }

    public static (int brush, int face, float distance) RaycastMapGeometry(Ray ray, bool skipLightNodeVolumes = true, params int[] ignoreBrush) =>
        RaycastMapGeometry(ray, skipLightNodeVolumes, false, ignoreBrush);

    public static (int brush, int face, float distance) RaycastMapGeometry(Ray ray, bool skipLightNodeVolumes, bool skipTerrainSource, params int[] ignoreBrush)
    {
        float minDistance = float.MaxValue;
        int brush = -1, face = -1;

        for (int i = 0; i < Brushes.Length; i++)
        {
            if (Brushes[i].IsLightNodeVolume && skipLightNodeVolumes) continue;
            if (!(BrushBounds[i].Intersects(ray) > 0)) continue;
            if (Brushes[i].isUsedForTerrain && skipTerrainSource) continue;
            if (VisGroupManager.IsBrushHidden(i)) continue;

            if (ignoreBrush != null && ignoreBrush.Contains(i)) continue;

            for (int j = 0; j < Brushes[i].Faces.Length; j++)
            {
                var plane = Brushes[i].Faces[j].Plane;
                if (!plane.HasValue) continue;

                // Ray-plane intersection
                float denom = Vector3.Dot(plane.Value.Normal, ray.Direction);

                // Only hit front faces, and skip parallel rays
                if (denom >= -1e-6f) continue;

                // t = -(n·origin + D) / (n·direction)
                // But our planes are in local space, so offset ray origin by -brush.position
                Vector3 localOrigin = ray.Position - Brushes[i].Position;
                float t = -(Vector3.Dot(plane.Value.Normal, localOrigin) + plane.Value.D) / denom;

                if (t < 0.01f || t >= minDistance) continue;

                // Check intersection point is inside all other planes (inside the brush)
                Vector3 localHit = localOrigin + ray.Direction * t;
                bool inside = true;
                for (int k = 0; k < Brushes[i].Faces.Length; k++)
                {
                    if (k == j) continue;
                    var otherPlane = Brushes[i].Faces[k].Plane;
                    if (!otherPlane.HasValue) continue;
                    if (otherPlane.Value.DotCoordinate(localHit) > 0.001f)
                    {
                        inside = false;
                        break;
                    }
                }

                if (!inside) continue;

                minDistance = t;
                brush = i;
                face = j;
            }
        }

        return (brush, face, minDistance);
    }
    public static (int entity, float distance) RaycastEntities(Ray ray)
    {
        bool any = false;
        float minDistance = float.MaxValue;
        int entity = -1;
        for (int i = 0; i < Entities.Length; i++)
        {
            var ent = Entities[i];
            if (ent.IsBrushEntity) continue; // picked via its brushes (RaycastMapGeometry) instead
            if (VisGroupManager.IsEntityHidden(ent)) continue;

            var box = MapperView.BoundsFor(ent.EntityName);
            box.Min += ent.Position;
            box.Max += ent.Position;

            float? dist = box.Intersects(ray);

            if (dist > 0.1f && dist < minDistance)
            {
                minDistance = dist.Value;
                any = true;
                entity = i;
            }
        }
        return (entity, minDistance);
    }
    public static (int hint, float distance) RaycastHints(Ray ray)
    {
        float minDistance = float.MaxValue;
        int hint = -1;
        for (int i = 0; i < Hints.Length; i++)
        {
            if (VisGroupManager.IsHintHidden(i)) continue;

            var pos = Hints[i].Position;
            var box = new BoundingBox(pos - Vector3.One * 0.25f, pos + Vector3.One * 0.25f);

            float? dist = box.Intersects(ray);
            if (dist > 0.1f && dist < minDistance)
            {
                minDistance = dist.Value;
                hint = i;
            }
        }
        return (hint, minDistance);
    }
    public static (int terrain, float distance) RaycastTerrains(Ray ray)
    {
        bool any = false;
        float minDistance = float.MaxValue;
        int terrain = -1;
        for (int i = 0; i < Terrains.Length; i++)
        {
            if (VisGroupManager.IsTerrainHidden(i)) continue;
            if (!(Terrains[i].Bounds.Intersects(ray) > 0)) continue;

            for (int j = 0; j < Terrains[i].Triangles.Length; j += 3)
            {
                float dist = BrushOperations.IntersectRayTriangle(Terrains[i].Vertices[Terrains[i].Triangles[j + 0]].Position,
                                                                  Terrains[i].Vertices[Terrains[i].Triangles[j + 1]].Position,
                                                                  Terrains[i].Vertices[Terrains[i].Triangles[j + 2]].Position, ray);

                if (dist > 0.1f && dist < minDistance)
                {
                    minDistance = dist;
                    terrain = i;
                    any = true;
                }
            }
        }
        return (terrain, minDistance);
    }
}