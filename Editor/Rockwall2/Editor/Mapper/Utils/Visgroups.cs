using Rockwall;
using Rockwall2.Editor.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rockwall2.Editor.Mapper.Utils;

[Flags]
public enum AutoGroupFlags
{
    None = 0,
    Solid = 1,
    Sky = 2,
    Terrain = 4,
    World = Solid | Sky | Terrain,
    PointEntities = 8,
    BrushEntities = 16,
    Lights = 32,
    Triggers = 64,
    Entities = PointEntities | BrushEntities | Lights | Triggers,
}

public static class VisGroupManager
{
    public static event Action Changed;
    public static AutoGroupFlags HiddenAuto { get; private set; }

    static readonly HashSet<Guid> hidden = new();
    static bool dirty = true;

    public static List<UserVisGroup> Groups => MapTools.MapLoaded ? MapTools.ActiveMap.VisGroups : null;

    public static void MarkDirty() => dirty = true;
    public static void RaiseChanged() => Changed?.Invoke();
    public static void OnMapReplaced() { HiddenAuto = AutoGroupFlags.None; dirty = true; Changed?.Invoke(); }

    public static bool IsHidden(Guid? id)
    {
        if (!id.HasValue) return false;
        if (dirty) Recompute();
        return hidden.Contains(id.Value);
    }
    public static bool IsBrushHidden(int i) => IsHidden(MapTools.Brushes[i].GroupingID);
    public static bool IsTerrainHidden(int i) => IsHidden(MapTools.Terrains[i].GroupingID);
    public static bool IsHintHidden(int i) => IsHidden(MapTools.Hints[i].GroupingID);
    public static bool IsEntityHidden(EntityReference e) => IsHidden(e.GroupingID);

    static bool IsTriggerBrush(Brush b) =>
        b.Faces != null && b.Faces.Any(f => f.MaterialName?.Contains("tool_trigger") == true);
    static AutoGroupFlags ClassifyBrush(Brush b)
    {
        if (b.isUsedForTerrain) return AutoGroupFlags.Terrain;
        if (IsTriggerBrush(b)) return AutoGroupFlags.Triggers;
        if (b.Faces != null && b.Faces.Any(f => f.MaterialName?.Contains("skybox") == true)) return AutoGroupFlags.Sky;
        return AutoGroupFlags.Solid;
    }
    static AutoGroupFlags ClassifyEntity(EntityReference e)
    {
        var f = e.IsBrushEntity ? AutoGroupFlags.BrushEntities : AutoGroupFlags.PointEntities;
        var n = e.EntityName ?? "";
        if (n.Contains("Light")) f |= AutoGroupFlags.Lights;

        if (e.brushOwnerGUIDs != null)
        {
            foreach (var g in e.brushOwnerGUIDs)
            {
                int bi = MapTools.ResolveIndex(g, MapTools.ObjType.Brush);
                if (bi != -1 && IsTriggerBrush(MapTools.Brushes[bi])) { f |= AutoGroupFlags.Triggers; break; }
            }
        }
        return f;
    }
    static void Recompute()
    {
        dirty = false;
        hidden.Clear();
        if (!MapTools.MapLoaded) return;

        var groups = Groups ?? new List<UserVisGroup>();
        var byId = groups.GroupBy(g => g.ID).ToDictionary(g => g.Key, g => g.First());
        foreach (var g in groups)
            if (EffectivelyHidden(g, byId)) hidden.UnionWith(g.Members);

        var owned = new HashSet<Guid>();
        foreach (var e in MapTools.Entities)
        {
            if (e == null) continue;
            bool hide = (ClassifyEntity(e) & HiddenAuto) != 0 || (e.GroupingID.HasValue && hidden.Contains(e.GroupingID.Value));
            if (hide && e.GroupingID.HasValue) hidden.Add(e.GroupingID.Value);
            if (e.brushOwnerGUIDs == null) continue;
            foreach (var g in e.brushOwnerGUIDs) { owned.Add(g); if (hide) hidden.Add(g); }
        }

        foreach (var b in MapTools.Brushes)
        {
            if (!b.GroupingID.HasValue || owned.Contains(b.GroupingID.Value)) continue;
            if ((ClassifyBrush(b) & HiddenAuto) != 0) hidden.Add(b.GroupingID.Value);
        }

        foreach (var t in MapTools.Terrains)
        {
            if (!t.GroupingID.HasValue) continue;
            if ((HiddenAuto & AutoGroupFlags.Terrain) != 0 ||
                (t.BrushOwnerGUID.HasValue && hidden.Contains(t.BrushOwnerGUID.Value)))
                hidden.Add(t.GroupingID.Value);
        }
    }

    static bool EffectivelyHidden(UserVisGroup g, Dictionary<Guid, UserVisGroup> byId)
    {
        var cur = g;
        for (int depth = 0; cur != null && depth < 64; depth++)
        {
            if (!cur.Visible) return true;
            cur = cur.ParentID.HasValue && byId.TryGetValue(cur.ParentID.Value, out var p) ? p : null;
        }
        return false;
    }

    public static IEnumerable<UserVisGroup> ChildrenOf(Guid? parent)
    {
        var all = Groups; if (all == null) yield break;
        foreach (var g in all)
        {
            bool isRoot = g.ParentID == null || !all.Any(p => p.ID == g.ParentID);
            if (parent == null ? isRoot : g.ParentID == parent) yield return g;
        }
    }
    public static UserVisGroup Create(string name, Guid? parent)
    {
        var g = new UserVisGroup { Name = name, ParentID = parent };
        Groups.Add(g);
        Changed?.Invoke();
        return g;
    }
    public static void Delete(Guid id)
    {
        var g = Groups.Find(x => x.ID == id); if (g == null) return;
        foreach (var c in Groups.Where(x => x.ParentID == id)) c.ParentID = g.ParentID;
        Groups.Remove(g);
        Refresh();
    }
    public static void Rename(Guid id, string name)
    {
        var g = Groups.Find(x => x.ID == id); if (g == null) return;
        g.Name = name; Changed?.Invoke();
    }
    public static void SetVisible(Guid id, bool v)
    {
        var g = Groups.Find(x => x.ID == id); if (g == null) return;
        g.Visible = v; Refresh();
    }
    public static void SetAutoVisible(AutoGroupFlags f, bool v)
    {
        HiddenAuto = v ? HiddenAuto & ~f : HiddenAuto | f;
        Refresh();
    }
    public static void ShowAll()
    {
        HiddenAuto = AutoGroupFlags.None;
        foreach (var g in Groups) g.Visible = true;
        Refresh();
    }
    public static void AddSelection(Guid groupId)
    {
        var g = Groups.Find(x => x.ID == groupId); if (g == null) return;
        foreach (var o in Toolbelt.SelectedObjects)
            if (TargetGuid(o, preferOwner: true) is { } id && !g.Members.Contains(id)) g.Members.Add(id);
        Refresh();
    }
    public static void RemoveSelection(Guid groupId)
    {
        var g = Groups.Find(x => x.ID == groupId); if (g == null) return;
        foreach (var o in Toolbelt.SelectedObjects)
            if (TargetGuid(o, preferOwner: true) is { } id) g.Members.Remove(id);
        Refresh();
    }
    public static IEnumerable<Guid> MembersOf(Guid groupId) => Groups.Find(x => x.ID == groupId)?.Members ?? new();

    public static void PruneStale()
    {
        if (Groups == null) return;
        foreach (var g in Groups) g.Members.RemoveAll(m => !MapTools.GuidMapper.ContainsKey(m));
    }

    static void Refresh()
    {
        dirty = true;
        var sel = Toolbelt.SelectedObjects;
        if (sel != null)
            for (int i = sel.Count - 1; i >= 0; i--)
                if (IsHidden(TargetGuid(sel[i], preferOwner: false))) sel.RemoveAt(i);
        Changed?.Invoke();
    }

    static Guid? TargetGuid(object o, bool preferOwner)
    {
        Guid? Brush(int bi) => preferOwner && MapTools.GetOwningEntity(bi) is { } ent
            ? ent.GroupingID : MapTools.Brushes[bi].GroupingID;
        return o switch
        {
            BrushMoveable b => Brush(b.brush),
            FaceMoveable f => Brush(f.brush),
            BrushEdgeMoveable e => Brush(e.brush),
            EntityMoveable en => MapTools.Entities[en.entity].GroupingID,
            TerrainMoveable t => MapTools.Terrains[t.terrain].GroupingID,
            HintMoveable h => MapTools.Hints[h.hint].GroupingID,
            _ => null
        };
    }
}
