using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace MultiplayerLoader
{
    // This class has to be named Multiplayer for backwards compatibility of the settings file location
    public class Multiplayer : Mod
    {
        public static Multiplayer instance;
        public static Action<Rect> settingsWindowDrawer;

        public Multiplayer(ModContentPack content) : base(content)
        {
            instance = this;
            LoadAssembliesCustom();

            FindTypeInAppDomain("Multiplayer.Client.Multiplayer")!.GetMethod("InitMultiplayer")!.Invoke(null, [content]);
        }

        public static Type? FindTypeInAppDomain(string typeFullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeFullName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(type => type != null);

        private void LoadAssembliesCustom()
        {
            var assemblies = new List<Assembly>();

            foreach (FileInfo item in ModContentPack
                         .GetAllFilesForModPreserveOrder(Content, "AssembliesCustom/", e => e.ToLower() == ".dll")
                         .Select(f => f.Item2))
            {
                var asmName = AssemblyName.GetAssemblyName(item.FullName);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(asm => asm.GetName().FullName == asmName.FullName);

                if (assembly == null)
                    try
                    {
                        // Same as 1.7's ModAssemblyHandler. On .NET, loading from a byte array (which kept the files
                        // unlocked for rebuilding while a game instance runs) puts every assembly in its own load
                        // context. LoadFrom keeps them in the default context with the game and the other mods, and
                        // picks up the .pdb next to the .dll by itself.
                        assembly = Assembly.LoadFrom(item.FullName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Exception loading {item.Name}: {ex}");
                        break;
                    }

                assemblies.Add(assembly);
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                Content.assemblies.loadedAssemblies.FirstOrDefault(a => a.FullName == args.Name);

            foreach (var asm in assemblies.Where(asm => Content.assemblies.AssemblyIsUsable(asm)))
            {
                GenTypes.ClearCache();
                Content.assemblies.loadedAssemblies.Add(asm);
            }
        }

        public override string SettingsCategory() => "Multiplayer";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            settingsWindowDrawer?.Invoke(inRect);
        }
    }
}
