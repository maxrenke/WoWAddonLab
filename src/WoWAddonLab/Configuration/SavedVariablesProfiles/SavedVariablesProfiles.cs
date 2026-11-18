using System.Security.Cryptography;
using System.Text;
using WoWAddonLab.Addons;
using WoWAddonLab.Configuration;

namespace WoWAddonLab.Configuration;

public static class SavedVariablesProfiles
{
    public static IReadOnlyList<SavedVariablesProfile> Discover(WowInstallation installation)
    {
        var profiles = new List<SavedVariablesProfile>
        {
            new("clean", "Emulator-only product data", null, null, null, null, null)
        };
        var accountRoot = Path.Combine(installation.ProductPath, "WTF", "Account");
        if (!Directory.Exists(accountRoot))
            return profiles;

        foreach (var accountPath in Directory.EnumerateDirectories(accountRoot).OrderBy(Path.GetFileName))
        {
            var account = Path.GetFileName(accountPath);
            var accountSaved = Path.Combine(accountPath, "SavedVariables");
            profiles.Add(new SavedVariablesProfile(
                $"account:{account}",
                $"Account · {account}",
                account,
                null,
                null,
                Directory.Exists(accountSaved) ? accountSaved : null,
                null));

            foreach (var realmPath in Directory.EnumerateDirectories(accountPath)
                         .Where(path => !Path.GetFileName(path).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName))
            {
                var realm = Path.GetFileName(realmPath);
                foreach (var characterPath in Directory.EnumerateDirectories(realmPath).OrderBy(Path.GetFileName))
                {
                    var characterSaved = Path.Combine(characterPath, "SavedVariables");
                    if (!Directory.Exists(characterSaved))
                        continue;
                    var character = Path.GetFileName(characterPath);
                    profiles.Add(new SavedVariablesProfile(
                        $"character:{account}:{realm}:{character}",
                        $"Character · {character} — {realm} ({account})",
                        account,
                        realm,
                        character,
                        Directory.Exists(accountSaved) ? accountSaved : null,
                        characterSaved));
                }
            }
        }
        return profiles;
    }

    public static string EmulatorDirectory(
        string settingsRoot,
        WowInstallation installation,
        SavedVariablesProfile profile)
    {
        var productKey = StableKey($"{installation.Product.ProductCode}|{installation.ProductPath}");
        var profileKey = StableKey(profile.Id);
        return Path.Combine(settingsRoot, "SavedVariables", productKey, profileKey);
    }

    public static int ImportCopies(
        SavedVariablesProfile profile,
        IEnumerable<InstalledAddon> addons,
        string destinationDirectory,
        bool onlyNewer = false)
    {
        Directory.CreateDirectory(destinationDirectory);
        var imported = 0;
        foreach (var addon in addons.Where(value => value.Manifest?.SavedVariables.Count > 0))
        {
            imported += CopyScope(
                profile.AccountSourceDirectory,
                addon.Manifest!.Name,
                addon.Manifest.AccountSavedVariables,
                Path.Combine(destinationDirectory, "Account"),
                onlyNewer);
            imported += CopyScope(
                profile.CharacterSourceDirectory,
                addon.Manifest.Name,
                addon.Manifest.CharacterSavedVariables,
                Path.Combine(destinationDirectory, "Character"),
                onlyNewer);
        }
        return imported;
    }

    public const string ImportedCopyHeader = "-- Imported read-only copy from World of Warcraft by WoW Addon Lab.";

    /// <summary>
    /// Copies the emulator's SavedVariables for the given addons back into the profile's WoW WTF folders.
    /// A WoW file newer than the emulator's copy is kept (it changed in the game since), and every replaced
    /// file is backed up as "&lt;Addon&gt;.lua.addonlab.bak". Nothing is written while that WoW product is running.
    /// </summary>
    public static IReadOnlyList<string> WriteBack(
        WowInstallation installation,
        SavedVariablesProfile profile,
        IEnumerable<InstalledAddon> addons,
        string emulatorDirectory)
    {
        var messages = new List<string>();
        if (profile.AccountSourceDirectory is null && profile.CharacterSourceDirectory is null)
            return messages;
        if (IsProductRunning(installation))
        {
            messages.Add($"Write-back skipped: {installation.Product.ExecutableName} is running and would overwrite the files when it exits.");
            return messages;
        }
        foreach (var addon in addons.Where(value => value.Manifest?.SavedVariables.Count > 0))
        {
            var manifest = addon.Manifest!;
            if (manifest.AccountSavedVariables.Count > 0)
                WriteBackScope(Path.Combine(emulatorDirectory, "Account"), profile.AccountSourceDirectory, manifest.Name, messages);
            if (manifest.CharacterSavedVariables.Count > 0)
                WriteBackScope(Path.Combine(emulatorDirectory, "Character"), profile.CharacterSourceDirectory, manifest.Name, messages);
        }
        return messages;
    }

    private static void WriteBackScope(
        string emulatorScopeDirectory,
        string? wowDirectory,
        string addonName,
        List<string> messages)
    {
        if (wowDirectory is null)
            return;
        var source = Path.Combine(emulatorScopeDirectory, $"{addonName}.lua");
        if (!File.Exists(source))
            return;
        var text = File.ReadAllText(source);
        if (text.StartsWith(ImportedCopyHeader, StringComparison.Ordinal))
            return; // untouched import, WoW already has this data
        var destination = Path.Combine(wowDirectory, $"{addonName}.lua");
        if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) > File.GetLastWriteTimeUtc(source))
        {
            messages.Add($"{addonName}: kept WoW's copy, it is newer than the emulator's ({destination}).");
            return;
        }
        Directory.CreateDirectory(wowDirectory);
        if (File.Exists(destination))
            File.Copy(destination, destination + ".addonlab.bak", true);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, destination, true);
        messages.Add($"{addonName}: wrote emulator data back to {destination}.");
    }

    private static bool IsProductRunning(WowInstallation installation)
    {
        var name = Path.GetFileNameWithoutExtension(installation.Product.ExecutableName);
        var expected = Path.GetFullPath(Path.Combine(installation.ProductPath, installation.Product.ExecutableName));
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || path.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception)
                {
                    return true; // cannot tell which install it belongs to: assume this one
                }
            }
        }
        return false;
    }

    private static int CopyScope(
        string? sourceDirectory,
        string addonName,
        IReadOnlyList<string> declaredVariables,
        string destinationDirectory,
        bool onlyNewer)
    {
        if (sourceDirectory is null || declaredVariables.Count == 0)
            return 0;
        var source = Path.Combine(sourceDirectory, $"{addonName}.lua");
        if (!File.Exists(source))
            return 0;

        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, $"{addonName}.lua");
        if (onlyNewer && File.Exists(destination) &&
            File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(source))
            return 0;
        var temporary = destination + ".tmp";
        var output =
            $"{ImportedCopyHeader}{Environment.NewLine}" +
            $"-- Source: {source}{Environment.NewLine}" +
            File.ReadAllText(source);
        File.WriteAllText(temporary, output, new UTF8Encoding(false));
        File.Move(temporary, destination, true);
        return 1;
    }

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
