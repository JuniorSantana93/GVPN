using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Tasks;

namespace GVPN.Services;

internal static class EmbeddedResources
{
    private static Assembly Assembly => typeof(EmbeddedResources).Assembly;

    public static async Task<string> ReadTextAsync(string resourceName)
    {
        await using Stream stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"O recurso embutido '{resourceName}' não foi encontrado.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task ExtractFileAsync(string resourceName, string destination)
    {
        await using Stream source = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"O recurso embutido '{resourceName}' não foi encontrado.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            byte[] embeddedHash = SHA256.HashData(source);
            source.Position = 0;
            await using var existing = File.OpenRead(destination);
            byte[] existingHash = await SHA256.HashDataAsync(existing);
            if (CryptographicOperations.FixedTimeEquals(embeddedHash, existingHash)) return;
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(output);

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
