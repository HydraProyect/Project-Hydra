using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EvidenciasInbound;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Infrastructure.FileStorage;

public sealed record AdjuntoTemporalDefinitivo(string RutaAnterior, string RutaNueva, bool ExisteEnDisco)
{
    /// <summary>
    /// <c>false</c> cuando ni los metadatos ni el nombre en disco conservan la extensión original:
    /// el definitivo queda sin extensión, igual que lo dejaría hoy <c>GuardarAsync</c>.
    /// </summary>
    public bool ExtensionRecuperada { get; init; } = true;
}

/// <summary>
/// Mantenimiento administrativo explícito, con el contexto de bootstrap y la aplicación detenida.
/// No modifica RLS. Solo considera referencias persistidas; nunca enumera ni elimina huérfanos.
/// Disco y PostgreSQL no comparten transacción: ante un error se compensan los renombrados.
/// Una interrupción del proceso durante la operación exige revisar las rutas antes de reintentar.
/// Nunca inventa una extensión: si no puede recuperarse, el definitivo queda sin ella y se
/// registra un aviso con el tipo y el id de cada registro afectado, sin nombres de fichero.
/// </summary>
public sealed class ReparacionAdjuntosTemporales(DbContext db, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    public const string Argumento = "--reparar-adjuntos-temporales";
    private static readonly Regex Sufijo = new(@"\.tmp-[0-9a-f]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool Aplicar(string[] argumentos)
    {
        if (argumentos.Contains("--aplicar") && argumentos.Contains("--simular"))
            throw new ArgumentException("--simular y --aplicar son incompatibles.");
        return argumentos.Contains("--aplicar");
    }

    public async Task<IReadOnlyList<AdjuntoTemporalDefinitivo>> EjecutarAsync(
        string raiz, bool aplicar = false, CancellationToken cancellationToken = default)
    {
        raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raiz));
        if (!Directory.Exists(raiz))
            throw new DirectoryNotFoundException(raiz);
        if (aplicar && db.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("El mantenimiento de ficheros exige un contexto sin reintentos automáticos.");
        // Sin DeleteOnClose: este mantenimiento nunca borra ningún fichero.
        using var bloqueo = aplicar
            ? new FileStream(Path.Combine(raiz, ".reparar-adjuntos-temporales.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            : null;
        await using var transaccion = aplicar ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var referencias = new List<PropertyEntry>();
        await RecogerAsync<Documento>("ArchivoUrl", referencias, cancellationToken);
        await RecogerAsync<AdjuntoMensaje>("ArchivoUrl", referencias, cancellationToken);
        await RecogerAsync<TipoDocumentoCentro>("ArchivoUrl", referencias, cancellationToken);
        await RecogerAsync<EvidenciaInbound>("ArchivoUrl", referencias, cancellationToken);
        await RecogerAsync<PlantillaDocumentoVersion>("ArchivoOriginalUrl", referencias, cancellationToken);
        await RecogerAsync<SolicitudCertificacionTgss>("EvidenciaArchivoRuta", referencias, cancellationToken);
        await RecogerAsync<VerificacionExternaSubcontrata>("EvidenciaArchivoRuta", referencias, cancellationToken);
        await RecogerAsync<FirmaGuardadaUsuario>("ImagenUrl", referencias, cancellationToken);
        await RecogerAsync<SelloEmpresa>("ImagenUrl", referencias, cancellationToken);
        await RecogerAsync<Tenant>("LogoArchivoClave", referencias, cancellationToken);

        var resultado = new List<AdjuntoTemporalDefinitivo>();
        var movidos = new List<(string Origen, string Destino)>();
        var cambiadas = new List<(PropertyEntry Propiedad, string Anterior)>();
        var confirmacionIniciada = false;
        try
        {
            foreach (var grupo in referencias.GroupBy(p => (string)p.CurrentValue!, StringComparer.Ordinal))
            {
                // Las referencias eliminadas no activan una reparación, pero se actualizan
                // junto a la vigente si comparten el mismo blob, para permitir restaurarlas.
                if (!grupo.Any(p => EsVigente(p.EntityEntry)))
                    continue;
                var anterior = grupo.Key;
                if (!Sufijo.IsMatch(anterior)) continue;
                var nombre = Path.GetFileName(anterior);
                while (Path.GetExtension(nombre).StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
                    nombre = nombre[..^Path.GetExtension(nombre).Length];
                var extensionesOriginales = grupo.Select(p => NombreOriginal(p.EntityEntry))
                    .Where(n => n is not null).Select(n => NombreArchivoDefinitivo.Extension(n!))
                    .Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (extensionesOriginales.Length > 1)
                    throw new InvalidOperationException($"Las referencias a {anterior} discrepan sobre la extensión original.");
                var extension = extensionesOriginales.SingleOrDefault() ?? NombreArchivoDefinitivo.Extension(anterior);
                nombre = Path.GetFileNameWithoutExtension(nombre) + extension;
                var nueva = anterior[..^(Path.GetFileName(anterior).Length)] + nombre;
                var origen = Resolver(raiz, anterior);
                var destino = Resolver(raiz, nueva);
                resultado.Add(new(anterior, nueva, File.Exists(origen)) { ExtensionRecuperada = extension.Length > 0 });
                if (extension.Length == 0)
                    foreach (var propiedad in grupo)
                        _logger.LogWarning(
                            "Adjunto definitivo sin extensión original recuperable: {Entidad} {Id}. Se deja sin extensión.",
                            propiedad.EntityEntry.Metadata.ClrType.Name, propiedad.EntityEntry.Property("Id").CurrentValue);
                if (!aplicar) continue;

                // Move sin sobrescritura: una colisión o un origen ausente falla antes del UPDATE.
                File.Move(origen, destino);
                movidos.Add((origen, destino));
                foreach (var delTenant in grupo.GroupBy(p => TenantPropietario(p.EntityEntry)))
                {
                    using var ambito = AmbitoTenantExplicito.Establecer(delTenant.Key);
                    foreach (var propiedad in delTenant)
                    {
                        cambiadas.Add((propiedad, anterior));
                        propiedad.CurrentValue = nueva;
                    }
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            if (aplicar)
            {
                confirmacionIniciada = true;
                await transaccion!.CommitAsync(cancellationToken);
            }
            return resultado;
        }
        catch (Exception fallo)
        {
            var errores = new List<Exception> { fallo };
            foreach (var (origen, destino) in movidos.AsEnumerable().Reverse())
            {
                try
                {
                    // Si COMMIT devolvió un error, su resultado puede ser incierto.
                    // Ambas rutas conservan el contenido: ninguna posible referencia queda rota.
                    if (confirmacionIniciada) File.Copy(destino, origen, overwrite: false);
                    else File.Move(destino, origen);
                }
                catch (Exception compensacion) { errores.Add(compensacion); }
            }
            foreach (var (propiedad, anterior) in cambiadas)
            {
                propiedad.CurrentValue = anterior;
                propiedad.IsModified = false;
            }
            if (errores.Count > 1)
                throw new AggregateException("Reparación fallida; revisar los renombrados cuya compensación falló. No se ha borrado ningún fichero.", errores);
            if (confirmacionIniciada)
                throw new InvalidOperationException("Confirmación de base de datos incierta: se conservan ambas rutas. Revisar las referencias antes de reintentar.", fallo);
            throw;
        }
    }

    private async Task RecogerAsync<T>(string campo, List<PropertyEntry> referencias, CancellationToken ct) where T : class
    {
        if (db.Model.FindEntityType(typeof(T)) is null) return;
        // Solo este comando administrativo usa el contexto de bootstrap; los filtros
        // de la aplicación no deben ocultar referencias de otros Tenants propietarios.
        var filas = await db.Set<T>().IgnoreQueryFilters()
            .Where(e => EF.Property<string>(e, campo) != null && EF.Property<string>(e, campo).ToLower().Contains(".tmp-"))
            .ToListAsync(ct);
        referencias.AddRange(filas.Select(e => db.Entry(e).Property(campo)));
    }

    private static string? NombreOriginal(EntityEntry entidad)
    {
        foreach (var campo in new[] { "NombreArchivoOriginal", "NombreArchivo", "EvidenciaNombreArchivo" })
            if (entidad.Metadata.FindProperty(campo) is not null && entidad.Property(campo).CurrentValue is string nombre)
                return nombre;
        return null;
    }

    // El Tenant es su propio Tenant propietario; cualquier otra entidad debe declarar TenantId.
    // Sin él no se fija ningún ámbito inventado: la reparación se detiene antes de escribir.
    private static Guid TenantPropietario(EntityEntry entidad) =>
        entidad.Entity is Tenant tenant ? tenant.Id
        : entidad.Metadata.FindProperty("TenantId") is not null && entidad.Property("TenantId").CurrentValue is Guid id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException($"{entidad.Metadata.ClrType.Name} no declara Tenant propietario.");

    private static bool EsVigente(EntityEntry entidad) =>
        (entidad.Metadata.FindProperty("EstaEliminado") is null || entidad.Property("EstaEliminado").CurrentValue is false)
        && (entidad.Metadata.FindProperty("SustituidoEnUtc") is null || entidad.Property("SustituidoEnUtc").CurrentValue is null);

    private static readonly StringComparison ComparacionRutas =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Resolver(string raiz, string identificador)
    {
        var ruta = Path.GetFullPath(Path.Combine(raiz, identificador));
        if (!ruta.StartsWith(Path.TrimEndingDirectorySeparator(raiz) + Path.DirectorySeparatorChar,
                ComparacionRutas))
            throw new InvalidOperationException("La referencia sale del directorio raíz del almacén.");
        // Del fichero hacia arriba hasta la raíz, que fija la configuración: ningún tramo
        // bajo ella puede ser un enlace que saque el renombrado fuera del almacén.
        FileSystemInfo? actual = new FileInfo(ruta);
        while (actual is not null
            && !string.Equals(Path.TrimEndingDirectorySeparator(actual.FullName), raiz, ComparacionRutas))
        {
            if (actual.Exists && (actual.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("No se reparan referencias a través de enlaces simbólicos.");
            actual = actual is FileInfo fichero ? fichero.Directory : ((DirectoryInfo)actual).Parent;
        }
        return ruta;
    }
}
