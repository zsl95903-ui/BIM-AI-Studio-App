using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RevitAi.Addin.Services;

internal sealed class RuntimeSettings
{
    public string ApiUrl { get; set; } = "https://api.deepseek.com/v1";

    public string Model { get; set; } = "deepseek-flash";

    public string? ProtectedApiKey { get; set; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ProtectedApiKey);

}

internal sealed class RuntimeSettingsStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("RevitAiConsole.Settings.v1");

    private readonly string _settingsPath;

    public RuntimeSettingsStore()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAi",
            "settings.json");
    }

    public RuntimeSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new RuntimeSettings();
        }

        var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<RuntimeSettings>(json)
            ?? new RuntimeSettings();
    }

    public void Save(RuntimeSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        var encrypted = Convert.FromBase64String(protectedValue);
        var decrypted = ProtectedData.Unprotect(
            encrypted,
            Entropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    public string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(
            plain,
            Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string? UnprotectApiKey(string? protectedApiKey) =>
        Unprotect(protectedApiKey);

    public string ProtectApiKey(string apiKey) => Protect(apiKey);
}
