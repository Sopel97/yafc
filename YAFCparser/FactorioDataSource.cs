using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using YAFC.Model;

namespace YAFC.Parser
{
    public static class FactorioDataSource
    {
        internal static Dictionary<string, ModInfo> allMods = new Dictionary<string, ModInfo>();

        private static string ReadAllText(this Stream stream, int length)
        {
            var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes(length);
            stream.Dispose();
            return Encoding.UTF8.GetString(bytes);
        }

        private static readonly char[] fileSplittersLua = {'.', '/', '\\'};
        private static readonly char[] fileSplittersNormal = {'/'};
        public static string factorioPath;
        
        
        public static (string mod, string path) ResolveModPath(string currentMod, string fullPath, bool isLuaRequire = false)
        {
            var mod = currentMod;
            var path = fullPath.Split(isLuaRequire ? fileSplittersLua : fileSplittersNormal);
            if (Array.IndexOf(path, "..") >= 0)
                throw new InvalidOperationException("Attempt to traverse to parent directory");
            var pathEnumerable = (IEnumerable<string>) path;
            if (path[0].StartsWith("__") && path[0].EndsWith("__"))
            {
                mod = path[0].Substring(2, path[0].Length - 4);
                pathEnumerable = pathEnumerable.Skip(1);
            }
            return (mod, string.Join("/", pathEnumerable));
        }
        
        public static byte[] ReadModFile(string modName, string path)
        {
            var info = allMods[modName];
            if (info.zipArchive != null)
            {
                var entry = info.zipArchive.GetEntry(Path.Combine(info.folder, path));
                if (entry == null)
                    return null;
                var bytearr = new byte[entry.Length];
                using (var stream = entry.Open())
                {
                    var read = 0;
                    while (read < bytearr.Length)
                        read += stream.Read(bytearr, read, bytearr.Length - read);
                }

                return bytearr; 
            }
            var fileName = Path.Combine(info.folder, path);
            return File.Exists(fileName) ? File.ReadAllBytes(fileName) : null;
        }

        private static void LoadMods(string directory, IProgress<(string, string)> progress)
        {
            foreach (var entry in Directory.EnumerateDirectories(directory))
            {
                var infoFile = Path.Combine(entry, "info.json");
                if (File.Exists(infoFile))
                {
                    progress.Report(("Initializing", entry));
                    var info = JsonSerializer.Deserialize<ModInfo>(File.ReadAllText(infoFile));
                    if (!string.IsNullOrEmpty(info.name) && allMods.ContainsKey(info.name))
                    {
                        info.folder = entry;
                        allMods[info.name] = info;
                        var localeDirName = Path.Combine(entry, "locale/en");
                        if (Directory.Exists(localeDirName))
                        {
                            foreach (var file in Directory.EnumerateFiles(localeDirName))
                            {
                                if (file.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                                        FactorioLocalization.Parse(fs);
                            }
                        }
                    }
                } 
            }

            foreach (var fileName in Directory.EnumerateFiles(directory))
            {
                if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
                    var zipArchive = new ZipArchive(fileStream);
                    var infoEntry = zipArchive.Entries.FirstOrDefault(x =>
                        x.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase) &&
                        x.FullName.IndexOf('/') == x.FullName.Length-"info.json".Length-1);
                    if (infoEntry != null)
                    {
                        var info = JsonSerializer.Deserialize<ModInfo>(infoEntry.Open().ReadAllText((int)infoEntry.Length));
                        if (!string.IsNullOrEmpty(info.name) && allMods.ContainsKey(info.name))
                        {
                            info.folder = infoEntry.FullName.Substring(0, infoEntry.FullName.Length-"info.json".Length);
                            info.zipArchive = zipArchive;
                            allMods[info.name] = info;
                            var localeDirName = info.folder +  "locale/en";
                            foreach (var entry in zipArchive.Entries.Where(x => x.FullName.StartsWith(localeDirName) && x.FullName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
                            {
                                using (var fs = entry.Open())
                                    FactorioLocalization.Parse(fs);
                            }
                        }
                    }
                }
            }
        }

        public static void Parse(string factorioPath, string modPath, bool expensive, IProgress<(string, string)> progress)
        {
            FactorioDataSource.factorioPath = factorioPath;
            object modSettings;
            var modSettingsPath = Path.Combine(modPath, "mod-settings.dat");
            progress.Report(("Initializing", "Loading mod settings"));
            if (File.Exists(modSettingsPath))
            {
                using (var fs = new FileStream(Path.Combine(modPath, "mod-settings.dat"), FileMode.Open, FileAccess.Read))
                    modSettings = FactorioPropertyTree.ReadModSettings(new BinaryReader(fs));
                Console.WriteLine("Mod settings parsed");
            }
            else modSettings = null; // TODO default mod settings

            progress.Report(("Initializing", "Loading mod list"));
            var modListPath = Path.Combine(modPath, "mod-list.json");
            if (File.Exists(modListPath))
            {
                var mods = JsonSerializer.Deserialize<ModList>(File.ReadAllText(modListPath));
                allMods = mods.mods.Where(x => x.enabled).Select(x => x.name).ToDictionary(x => x, x => (ModInfo)null);
            }
            else
                allMods = new Dictionary<string, ModInfo> {{"base", null}};
            allMods["core"] = null;
            Console.WriteLine("Mod list parsed");
            
            LoadMods(factorioPath, progress);
            if (modPath != factorioPath && modPath != "")
                LoadMods(modPath, progress);

            foreach (var mod in allMods)
                if (mod.Value == null)
                    throw new NotSupportedException("Mod not found: "+mod.Key);
            progress.Report(("Initializing", "Creating LUA context"));
            var factorioVersion = allMods.TryGetValue("base", out var baseMod) ? baseMod.version : "0.18.0";

            var modorder = allMods.Values.OrderBy(x => x).Select(x => x.name).ToArray();
            Console.WriteLine("All mods found! Loading order: "+string.Join(", ", modorder));

            var preprocess = File.ReadAllText("Data/Sandbox.lua");
            var postprocess = File.ReadAllText("Data/Postprocess.lua");
            EnvironmentSettings.allMods = new HashSet<string>(allMods.Keys);

            using (var dataContext = new FactorioLuaContext(modSettings))
            {
                dataContext.Run(preprocess);
                //dataContext.Run("math.pow(1, 1)");
                dataContext.DoModFiles(modorder, "data.lua", progress);
                dataContext.DoModFiles(modorder, "data-updates.lua", progress);
                dataContext.DoModFiles(modorder, "data-final-fixes.lua", progress);
                dataContext.Run(postprocess);
                
                var deserializer = new FactorioDataDeserializer(expensive, dataContext.CreateEmptyTable(), new Version(factorioVersion));
                deserializer.LoadData(dataContext.data, progress);
            }
            Console.WriteLine("Completed!");
            progress.Report(("Completed!", ""));
        }
        
        internal class ModEntry
        {
            public string name { get; set; }
            public bool enabled { get; set; }
        }
        
        internal class ModList
        {
            public ModEntry[] mods { get; set; }
        }
        
        internal class ModInfo : IComparable<ModInfo>
        {
            public string name { get; set; }
            public string version { get; set; }
            public string[] dependencies { get; set; }
            
            public ZipArchive zipArchive;
            public string folder;

            public int DependencyStrength(ModInfo other)
            {
                foreach (var dependency in dependencies)
                {
                    var index = dependency.IndexOf(other.name, StringComparison.Ordinal);
                    if (index >= 0 && index <= 4)
                    {
                        var qindex = dependency.IndexOf("?", StringComparison.Ordinal);
                        return qindex == -1 ? 2 : 1;
                    }
                }
                return 0;
            }
            
            public int CompareTo(ModInfo other)
            {
                if (name == "core")
                    return -1;
                if (other.name == "core")
                    return 1;
                var str0 = DependencyStrength(other);
                var str1 = other.DependencyStrength(this);
                if (str0 != str1)
                    return str0 - str1;
                if (dependencies.Length != other.dependencies.Length)
                    return dependencies.Length - other.dependencies.Length;
                return string.CompareOrdinal(name, other.name);
            }
        }
    }
}