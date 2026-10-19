using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

public class SharedCrossRefs : LoadedObjectDirectory
{
    // Used in CrossRefs patches
    public HashSet<string> tempKeys = new();

    // Same as LoadedObjectsRegisterPatch, for code paths that don't go through the Harmony patch
    public void RegisterLoadedTemp(ILoadReferenceable reffable)
    {
        if (reffable == null) return;

        string key = reffable.GetUniqueLoadID();
        if (allObjectsByLoadID.ContainsKey(key)) return;

        tempKeys.Add(key);
        allObjectsByLoadID.Add(key, reffable);
    }

    public void Unregister(ILoadReferenceable reffable)
    {
        allObjectsByLoadID.Remove(reffable.GetUniqueLoadID());
    }

    public void UnregisterAllTemp()
    {
        foreach (var key in tempKeys)
            allObjectsByLoadID.Remove(key);

        tempKeys.Clear();
    }

    public List<ILoadReferenceable> UnregisterAllFrom(Map map)
    {
        var unregistered = new List<ILoadReferenceable>();

        foreach (var val in allObjectsByLoadID.Values.ToArray())
        {
            if (val is Thing thing && thing.Map == map ||
                val is PassingShip ship && ship.Map == map ||
                val is Bill bill && bill.Map == map
               )
            {
                Unregister(val);
                unregistered.Add(val);
            }
        }

        return unregistered;
    }

    public void Reregister(List<ILoadReferenceable> items)
    {
        foreach (var item in items)
            allObjectsByLoadID[item.GetUniqueLoadID()] = item;
    }
}
