using System.Text;
using WoWAddonLab.Addons;
using WoWAddonLab.Configuration;
using WoWAddonLab.Emulator;
using WoWAddonLab.Emulator.Addons;

namespace WoWAddonLab.Tests;

public sealed class SavedVariablesWriteBackTests
{
    [Fact]
    public void SavedVariablesKeepUtf8AndBinaryStringsByteForByte()
    {
        var root = NewRoot();
        var addon = WriteAddon(root, "Utf8Saved", "## SavedVariables: Utf8DB");
        var savedRoot = Path.Combine(root, "emulator-data");
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                using var session = new EmulatorSession(savedVariablesDirectory: savedRoot);
                session.Load(addon, savedRoot);
                if (cycle == 0)
                    session.Lua.Evaluate("Utf8DB = { name = \"Bank-Sw\\195\\180rn\", raw = \"a\\255\\0b\", quote = \"q\\\"\\\\\\n\" }");
                session.Lua.SaveVariables();
            }

            using var check = new EmulatorSession(savedVariablesDirectory: savedRoot);
            check.Load(addon, savedRoot);
            Assert.Equal("true", check.Lua.Evaluate("return tostring(Utf8DB.name == \"Bank-Sw\\195\\180rn\")"));
            Assert.Equal("true", check.Lua.Evaluate("return tostring(Utf8DB.raw == \"a\\255\\0b\")"));
            Assert.Equal("true", check.Lua.Evaluate("return tostring(Utf8DB.quote == \"q\\\"\\\\\\n\")"));
            Assert.Contains("Bank-Swôrn", File.ReadAllText(Path.Combine(savedRoot, "Account", "Utf8Saved.lua"), Encoding.UTF8));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WriteBackCopiesEmulatorDataAndKeepsNewerWowData()
    {
        var root = NewRoot();
        var addonPath = WriteAddon(root, "SyncAddon", "## SavedVariables: SyncDB");
        var installation = new WowInstallation(
            root,
            Path.Combine(root, "_forever_"),
            new WowProduct("Test", "_forever_", "NotRunningTestClient.exe", "wowf"),
            "1.60.1.70959");
        var wowSaved = Path.Combine(root, "_forever_", "WTF", "Account", "TEST", "SavedVariables");
        Directory.CreateDirectory(wowSaved);
        var profile = new SavedVariablesProfile("account:TEST", "Account · TEST", "TEST", null, null, wowSaved, null);
        var emulator = Path.Combine(root, "emulator");
        var manifest = AddonManifest.Load(addonPath);
        var addon = new InstalledAddon("SyncAddon", addonPath, manifest, "SyncAddon", "SyncAddon", null, null, [], false, null);
        var wowFile = Path.Combine(wowSaved, "SyncAddon.lua");
        var emulatorFile = Path.Combine(emulator, "Account", "SyncAddon.lua");
        try
        {
            File.WriteAllText(wowFile, "SyncDB = { score = 1 }\n");
            File.SetLastWriteTimeUtc(wowFile, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(1, SavedVariablesProfiles.ImportCopies(profile, [addon], emulator, onlyNewer: true));

            // An untouched import is not written back
            Assert.Empty(SavedVariablesProfiles.WriteBack(installation, profile, [addon], emulator));

            File.WriteAllText(emulatorFile, "-- Saved by WoW Addon Lab at now\nSyncDB = { score = 2 }\n");
            var messages = SavedVariablesProfiles.WriteBack(installation, profile, [addon], emulator);
            Assert.Contains(messages, value => value.Contains("wrote emulator data back"));
            Assert.Contains("score = 2", File.ReadAllText(wowFile));
            Assert.Contains("score = 1", File.ReadAllText(wowFile + ".addonlab.bak"));

            // WoW changed the file after the emulator saved: WoW's copy wins
            File.WriteAllText(wowFile, "SyncDB = { score = 3 }\n");
            File.SetLastWriteTimeUtc(emulatorFile, DateTime.UtcNow.AddMinutes(-5));
            messages = SavedVariablesProfiles.WriteBack(installation, profile, [addon], emulator);
            Assert.Contains(messages, value => value.Contains("kept WoW's copy"));
            Assert.Contains("score = 3", File.ReadAllText(wowFile));

            // And the newer WoW data is synced into the emulator at the next start
            Assert.Equal(1, SavedVariablesProfiles.ImportCopies(profile, [addon], emulator, onlyNewer: true));
            Assert.Contains("score = 3", File.ReadAllText(emulatorFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"wow-addon-lab-sync-{Guid.NewGuid():N}");

    private static string WriteAddon(string root, string name, string savedVariablesLine)
    {
        var path = Path.Combine(root, "AddOns", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, $"{name}.toc"), $"## Interface: 16001\n## Title: {name}\n{savedVariablesLine}\nmain.lua\n");
        File.WriteAllText(Path.Combine(path, "main.lua"), "-- test\n");
        return path;
    }
}
