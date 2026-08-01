using CaeManager.Application.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Almacenamiento sobre disco local, particionado por tenant (ver
/// docs/MULTITENANCY.md § 4.6 y PLAN-MIGRACION-MULTITENANT.md § 5, Etapa 4):
/// todo archivo nuevo se guarda bajo <c>{tenantId}/{archivo}</c>, nunca en la
/// carpeta plana. Registrado como Scoped (antes Singleton) precisamente para
/// poder depender de <see cref="ITenantActual"/>, que es scoped por
/// naturaleza — inyectar un servicio scoped en un singleton sería una
/// dependencia cautiva (capturaría el primer tenant resuelto para siempre).
///
/// <b>Cifrado en reposo</b> (P1-12 de docs/business/MATURITY_REVIEW.md): el
/// contenido se cifra con <see cref="IDataProtector"/> antes de escribirse a
/// disco — mismo mecanismo que ya protege las credenciales de plataformas
/// externas en <c>CaeManagerDbContext</c> y las propias claves de Data
/// Protection (envueltas con KMS, ver <c>DataProtectionKmsOptions</c>). Era
/// el dato más sensible del sistema sin cifrar: estos archivos son PDFs de
/// reconocimientos médicos y otra documentación de trabajadores, categoría
/// especial del art. 9 RGPD.
///
/// El fichero se lee entero en memoria para cifrar/descifrar — no hay API de
/// streaming en <see cref="IDataProtector"/> y los documentos de este
/// sistema están acotados a 10 MB (ver <c>TamanoMaximoArchivoBytes</c> en
/// las páginas de subida), así que el coste es aceptable y coherente con
/// cómo el resto del código ya maneja archivos subidos.
///
/// <b>Compatibilidad con lo ya escrito antes de este cambio</b>: un archivo
/// que se guardó en claro (todo lo sembrado o subido antes de esta versión)
/// no tiene el formato de payload de Data Protection, así que
/// <c>Unprotect</c> lanza <see cref="CryptographicException"/> — se
/// interpreta como "archivo legado sin cifrar" y se sirve tal cual, en vez
/// de romper la descarga. No hay migración automática de lo ya existente: un
/// archivo legado queda cifrado la próxima vez que algo lo reescriba (p. ej.
/// <c>RenovarDocumentoCommand</c>), no antes.
/// </summary>
public class DiskFileStorageService : IFileStorageService
{
    private readonly string _rutaBase;
    private readonly ITenantActual _tenantActual;
    private readonly IDataProtector _protector;

    public DiskFileStorageService(
        IOptions<DiskFileStorageServiceOptions> opciones,
        IHostEnvironment entorno,
        ITenantActual tenantActual,
        IDataProtectionProvider dataProtectionProvider)
    {
        _rutaBase = Path.IsPathRooted(opciones.Value.Ruta)
            ? opciones.Value.Ruta
            : Path.Combine(entorno.ContentRootPath, opciones.Value.Ruta);
        _tenantActual = tenantActual;
        // Nombre de protector sin cambiar: renombrar rompería el descifrado
        // de archivos ya cifrados con él (mismo criterio que los protectores
        // de credenciales en CaeManagerDbContext).
        _protector = dataProtectionProvider.CreateProtector("CaeManager.Archivos.v1");

        Directory.CreateDirectory(_rutaBase);
    }

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantActual.TenantId
            ?? throw new InvalidOperationException("No se puede guardar un archivo sin un tenant resuelto.");

        var carpetaTenant = CarpetaDeTenant(tenantId);
        Directory.CreateDirectory(Path.Combine(_rutaBase, carpetaTenant));

        var extension = Path.GetExtension(nombreArchivoOriginal);
        var nombreArchivo = $"{Guid.NewGuid():N}{extension}";
        var identificador = $"{carpetaTenant}/{nombreArchivo}";
        var rutaCompleta = Path.Combine(_rutaBase, carpetaTenant, nombreArchivo);

        using var memoria = new MemoryStream();
        await contenido.CopyToAsync(memoria, cancellationToken);
        var cifrado = _protector.Protect(memoria.ToArray());

        await File.WriteAllBytesAsync(rutaCompleta, cifrado, cancellationToken);

        return identificador;
    }

    public async Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Un identificador de otro tenant (o sin tenant resuelto) se
        // comporta exactamente igual que "no existe" — nunca se revela que
        // pertenece a otro tenant, mismo criterio que el fix IDOR del
        // Issue #18 para los Ids de entidades.
        if (rutaCompleta is null || !File.Exists(rutaCompleta))
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        var bytesDisco = await File.ReadAllBytesAsync(rutaCompleta, cancellationToken);

        byte[] contenido;
        try
        {
            contenido = _protector.Unprotect(bytesDisco);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Archivo legado, guardado antes de que existiera el cifrado —
            // ver el comentario de clase. Se sirve en claro tal cual está.
            contenido = bytesDisco;
        }

        return new MemoryStream(contenido, writable: false);
    }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Misma resolución segura que al abrir: un identificador de otro
        // tenant no resuelve, así que la purga de un tenant nunca puede
        // borrar archivos de otro.
        if (rutaCompleta is not null && File.Exists(rutaCompleta))
            File.Delete(rutaCompleta);

        return Task.CompletedTask;
    }

    private string? ResolverRutaSegura(string identificador)
    {
        if (_tenantActual.TenantId is not { } tenantId) return null;

        var partes = identificador.Split('/', 2);
        if (partes.Length != 2) return null;

        // El identificador nunca debe permitir escapar del directorio base
        // (path traversal) — se sanea cada segmento por separado.
        var carpetaSolicitada = Path.GetFileName(partes[0]);
        var nombreArchivo = Path.GetFileName(partes[1]);

        if (!string.Equals(carpetaSolicitada, CarpetaDeTenant(tenantId), StringComparison.Ordinal))
            return null;

        return Path.Combine(_rutaBase, carpetaSolicitada, nombreArchivo);
    }

    private static string CarpetaDeTenant(Guid tenantId) => tenantId.ToString("N");
}
