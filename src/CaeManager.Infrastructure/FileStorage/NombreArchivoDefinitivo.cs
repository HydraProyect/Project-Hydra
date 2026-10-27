namespace CaeManager.Infrastructure.FileStorage;

internal static class NombreArchivoDefinitivo
{
    internal static string Extension(string nombre)
    {
        var extension = Path.GetExtension(nombre);
        while (extension.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
        {
            nombre = nombre[..^extension.Length];
            extension = Path.GetExtension(nombre);
        }
        return extension.ToLowerInvariant();
    }
}
