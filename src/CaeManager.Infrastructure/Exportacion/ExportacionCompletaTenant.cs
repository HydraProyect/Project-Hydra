using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Infrastructure.Exportacion;

/// <summary>
/// Exportación completa de los datos de UN Tenant propietario (devolución de datos al terminar el
/// contrato, Términos y condiciones): un zip con un JSON por tipo de entidad del Tenant, sus
/// Usuarios sin secretos de Identity, los binarios de sus Documentos descifrados con el mismo
/// almacén que usa la descarga y el manifiesto de <see cref="ArchivoExportacion"/>.
///
/// <para>
/// <b>Aislamiento.</b> Lee con el contexto inyectado —rol restringido, con RLS— y bajo
/// <see cref="AmbitoTenantExplicito"/> fijado al Tenant pedido, así que el filtro global de EF y
/// las políticas RLS acotan las dos al mismo Tenant. Antes de leer nada exige, contra PostgreSQL,
/// que esa identidad no pueda saltarse RLS (<see cref="VerificacionIdentidadDeRuntime"/>): nunca
/// corre con <c>FabricaContextoDeBootstrap</c>. Y cada fila leída se comprueba otra vez contra el
/// Tenant pedido: una sola fila de otro Tenant aborta la exportación entera.
/// </para>
///
/// <para>
/// <b>Autorización.</b> Solo se alcanza desde el modo de CLI <see cref="Argumento"/> (acceso
/// operativo al servidor), y exige además nombrar a la cuenta que la pide, que tiene que ser un
/// Administrador activo del propio Tenant propietario. La exportación queda en la auditoría del
/// Tenant antes de escribir el primer byte: si esa fila no se guarda, no se exporta nada.
/// </para>
///
/// <para>
/// <b>Qué no sale.</b> Ninguna columna cifrada en reposo (credenciales de portales, tokens de
/// integraciones, clientState, payloads crudos de webhooks: todas las de conversor texto→texto),
/// ni el hash de las claves de API, ni hashes de contraseña, sellos de seguridad o secretos TOTP
/// de Identity (de los Usuarios solo se proyectan campos de perfil). Tampoco las filas con
/// borrado lógico, que el filtro global oculta, ni los catálogos globales sin Tenant.
/// </para>
/// </summary>
public static class ExportacionCompletaTenant
{
    public const string Argumento = "--exportar-tenant";
    public const string ArgumentoSolicitante = "--solicitante";
    public const string ArgumentoDestino = "--destino";

    /// <summary>Propiedades que nunca se exportan aunque no lleven conversor de cifrado.</summary>
    private static readonly HashSet<string> NombresSecretos = new(StringComparer.Ordinal)
    {
        "Contrasena", "HashClave", "RefreshToken", "AccessToken", "ClientState", "TokenAcceso", "PayloadCrudo",
        "PasswordHash", "SecurityStamp",
    };

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly MethodInfo LeerConjuntoGenerico =
        typeof(ExportacionCompletaTenant).GetMethod(nameof(LeerConjunto), BindingFlags.NonPublic | BindingFlags.Static)!;

    public const string Uso =
        "Uso: --exportar-tenant <TenantId guid> --solicitante <correo de un Administrador del Tenant> --destino <ruta.zip>.";

    public sealed record Resultado(Guid TenantId, string NombreTenant, IReadOnlyDictionary<string, long> Recuentos, int DocumentosSinFichero);

    /// <summary>Salida del modo de CLI: el Tenant, la ruta y las entidades con alguna fila.</summary>
    public static void Informar(Resultado resultado, string ruta, TextWriter salida)
    {
        salida.WriteLine($"Exportado: '{resultado.NombreTenant}' ({resultado.TenantId}) en {ruta}.");
        foreach (var (entidad, filas) in resultado.Recuentos.Where(r => r.Value > 0))
            salida.WriteLine($"  {entidad}: {filas}");
        if (resultado.DocumentosSinFichero > 0)
            salida.WriteLine($"  Documentos sin fichero en el almacén: {resultado.DocumentosSinFichero} (en el manifiesto).");
    }

    /// <summary>Solo el tipo y el mensaje: la traza y los datos de la excepción no se imprimen.</summary>
    public static string MensajeDeInterrupcion(Exception ex) =>
        $"Exportación rechazada o interrumpida ({ex.GetType().Name}): {ex.Message}";

    /// <summary>
    /// Columna que la exportación omite: cualquiera cifrada en reposo (conversor de texto a texto,
    /// el único que usa <see cref="CaeManagerDbContext"/> para cifrar) o con nombre de secreto.
    /// </summary>
    public static bool EsSecreta(IReadOnlyProperty propiedad) =>
        NombresSecretos.Contains(propiedad.Name) ||
        propiedad.GetValueConverter() is { } conversor &&
        conversor.ModelClrType == typeof(string) && conversor.ProviderClrType == typeof(string);

    /// <summary>
    /// Raíces de las jerarquías de entidades con Tenant que tienen tabla propia, ordenadas por
    /// nombre: lo que se exporta, un fichero por raíz.
    /// </summary>
    public static IReadOnlyList<IEntityType> TiposExportables(IModel modelo) =>
        modelo.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.FindPrimaryKey() is not null &&
                        t.GetTableName() is not null && !t.HasSharedClrType &&
                        typeof(EntidadConTenant).IsAssignableFrom(t.ClrType))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    public static async Task<Resultado> ExportarAsync(
        CaeManagerDbContext contexto, IFileStorageService almacen, Guid tenantId, string correoSolicitante,
        Stream destino, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("El TenantId del Tenant propietario es obligatorio.");
        if (string.IsNullOrWhiteSpace(correoSolicitante))
            throw new InvalidOperationException("Falta el correo de la cuenta que pide la exportación.");

        await VerificacionIdentidadDeRuntime.ExigirIdentidadSometidaARlsAsync(contexto, cancellationToken);

        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var tenant = await contexto.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.Nombre, t.EsPlataforma, t.CreadoEnUtc, t.Estado })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"No existe ningún Tenant {tenantId}.");
        if (tenant.EsPlataforma)
            throw new InvalidOperationException("El Tenant de plataforma TALVEG no es un Tenant propietario exportable.");

        var solicitanteId = await ExigirAdministradorActivoAsync(contexto, tenantId, correoSolicitante, cancellationToken);

        contexto.RegistrosAuditoria.Add(new RegistroAuditoria(
            "Tenant", tenantId, RegistroAuditoria.AccionExportacion, null,
            JsonSerializer.Serialize(new { alcance = "ExportacionCompleta", via = Argumento, solicitanteUsuarioId = solicitanteId }),
            usuarioId: null, TipoActorAuditoria.Sistema));
        await contexto.SaveChangesAsync(cancellationToken);
        contexto.ChangeTracker.Clear();

        using var archivo = new ArchivoExportacion(destino, tenantId);

        await archivo.EscribirAsync("datos/Tenant.json",
            salida => JsonSerializer.SerializeAsync(salida, tenant, OpcionesJson, cancellationToken), cancellationToken);

        var recuentos = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var tipo in TiposExportables(contexto.Model))
        {
            var filas = 0L;
            await archivo.EscribirAsync($"datos/{tipo.ClrType.Name}.json", async salida =>
            {
                await using var escritor = new Utf8JsonWriter(salida);
                escritor.WriteStartArray();
                await foreach (var fila in Leer(contexto, tipo.ClrType).WithCancellation(cancellationToken))
                {
                    var conTenant = (EntidadConTenant)fila;
                    GuardaTenantExportacion.Exigir(tenantId, conTenant.TenantId, tipo.ClrType.Name);
                    EscribirEntidad(escritor, contexto.Model.FindEntityType(fila.GetType()) ?? tipo, fila);
                    filas++;
                    if (escritor.BytesPending > 64 * 1024)
                        await escritor.FlushAsync(cancellationToken);
                }
                escritor.WriteEndArray();
            }, cancellationToken);
            archivo.RegistrarRecuento(tipo.ClrType.Name, filas);
            recuentos[tipo.ClrType.Name] = filas;
        }

        var usuarios = await EscribirUsuariosAsync(contexto, archivo, tenantId, cancellationToken);
        archivo.RegistrarRecuento("Usuario", usuarios);
        recuentos["Usuario"] = usuarios;

        var sinFichero = await EscribirDocumentosAsync(contexto, almacen, archivo, cancellationToken);
        await archivo.TerminarAsync(cancellationToken);

        return new Resultado(tenantId, tenant.Nombre, recuentos, sinFichero);
    }

    /// <summary>
    /// La cuenta que pide la exportación: del propio Tenant propietario (no de un Operador CAE ni de
    /// TALVEG), con rol Administrador y no desactivada. Se lee ya bajo el ámbito del Tenant.
    /// </summary>
    private static async Task<Guid> ExigirAdministradorActivoAsync(
        CaeManagerDbContext contexto, Guid tenantId, string correo, CancellationToken cancellationToken)
    {
        var normalizado = correo.Trim().ToUpperInvariant();
        var cuenta = await contexto.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.NormalizedEmail == normalizado)
            .SingleOrDefaultAsync(cancellationToken);

        var esAdministrador = cuenta is not null && await (
            from ur in contexto.UserRoles.AsNoTracking()
            join r in contexto.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where ur.UserId == cuenta.Id && r.Name == Roles.Administrador
            select ur.UserId).AnyAsync(cancellationToken);

        if (cuenta is null || !esAdministrador || cuenta.EstaDesactivada(DateTimeOffset.UtcNow))
            throw new InvalidOperationException(
                "La exportación solo la puede pedir un Administrador activo del propio Tenant propietario; " +
                "esa cuenta no existe en el Tenant, no es Administrador o está desactivada.");

        return cuenta.Id;
    }

    private static async Task<long> EscribirUsuariosAsync(
        CaeManagerDbContext contexto, ArchivoExportacion archivo, Guid tenantId, CancellationToken cancellationToken)
    {
        var rolesPorUsuario = (await (
                from u in contexto.Users.AsNoTracking()
                join ur in contexto.UserRoles.AsNoTracking() on u.Id equals ur.UserId
                join r in contexto.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where u.TenantId == tenantId
                select new { u.Id, r.Name })
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.Id, x => x.Name);

        var tipoUsuario = contexto.Model.FindEntityType(typeof(ApplicationUser))!;
        var columnas = ColumnasExportablesDeUsuario(tipoUsuario);
        var ahora = DateTimeOffset.UtcNow;
        var filas = 0L;
        await archivo.EscribirAsync("datos/Usuario.json", async salida =>
        {
            await using var escritor = new Utf8JsonWriter(salida);
            escritor.WriteStartArray();
            var consulta = contexto.Users.AsNoTracking().Where(u => u.TenantId == tenantId).AsAsyncEnumerable();
            await foreach (var usuario in consulta.WithCancellation(cancellationToken))
            {
                GuardaTenantExportacion.Exigir(tenantId, usuario.TenantId, "Usuario");
                escritor.WriteStartObject();
                EscribirPropiedades(escritor, columnas, usuario);
                escritor.WriteBoolean("Desactivada", usuario.EstaDesactivada(ahora));
                escritor.WritePropertyName("Roles");
                JsonSerializer.Serialize(escritor, rolesPorUsuario[usuario.Id].Order(StringComparer.Ordinal).ToArray(), OpcionesJson);
                escritor.WriteEndObject();
                filas++;
                if (escritor.BytesPending > 64 * 1024)
                    await escritor.FlushAsync(cancellationToken);
            }
            escritor.WriteEndArray();
        }, cancellationToken);
        return filas;
    }

    /// <summary>
    /// El binario de cada Documento vivo con fichero, descifrado por el almacén con la clave del
    /// Tenant del ámbito. Primero se leen los pares (Id, clave) y después se abren los ficheros,
    /// para no abrir el almacén con un lector de la base aún abierto. Un fichero que ya no está
    /// queda en el manifiesto; cualquier otro fallo aborta la exportación.
    /// </summary>
    private static async Task<int> EscribirDocumentosAsync(
        CaeManagerDbContext contexto, IFileStorageService almacen, ArchivoExportacion archivo, CancellationToken cancellationToken)
    {
        var documentos = await contexto.Documentos.AsNoTracking()
            .Where(d => d.ArchivoUrl != null)
            .OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.ArchivoUrl })
            .ToListAsync(cancellationToken);

        var sinFichero = 0;
        foreach (var documento in documentos)
        {
            Stream contenido;
            try
            {
                contenido = await almacen.AbrirAsync(documento.ArchivoUrl!, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                archivo.RegistrarDocumentoSinFichero(documento.Id);
                sinFichero++;
                continue;
            }

            await using (contenido)
                await archivo.AgregarAsync(
                    $"documentos/{documento.Id}/documento{ExtensionSegura(documento.ArchivoUrl!)}", contenido, cancellationToken);
        }

        archivo.RegistrarRecuento("DocumentoFichero", documentos.Count - sinFichero);
        return sinFichero;
    }

    /// <summary>Extensión de la clave del almacén solo si es corta y alfanumérica; si no, ninguna.</summary>
    public static string ExtensionSegura(string clave)
    {
        var extension = Path.GetExtension(clave);
        return extension.Length is > 1 and <= 8 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>
    /// Columnas de la cuenta que salen en <c>datos/Usuario.json</c>: las del modelo, menos los
    /// secretos de Identity, el sello de concurrencia y las copias normalizadas del nombre y el correo.
    /// </summary>
    public static IReadOnlyList<IReadOnlyProperty> ColumnasExportablesDeUsuario(IReadOnlyEntityType tipoUsuario) =>
        tipoUsuario.GetProperties()
            .Where(p => !p.IsShadowProperty() && p.PropertyInfo is not null && !EsSecreta(p) &&
                        p.Name is not ("ConcurrencyStamp" or "NormalizedUserName" or "NormalizedEmail"))
            .ToList();

    private static void EscribirPropiedades(Utf8JsonWriter escritor, IEnumerable<IReadOnlyProperty> propiedades, object entidad)
    {
        foreach (var propiedad in propiedades)
        {
            escritor.WritePropertyName(propiedad.Name);
            var valor = propiedad.PropertyInfo!.GetValue(entidad);
            JsonSerializer.Serialize(escritor, valor, valor?.GetType() ?? typeof(object), OpcionesJson);
        }
    }

    private static void EscribirEntidad(Utf8JsonWriter escritor, IReadOnlyEntityType tipo, object entidad)
    {
        escritor.WriteStartObject();
        EscribirPropiedades(escritor,
            tipo.GetProperties().Where(p => !p.IsShadowProperty() && p.PropertyInfo is not null && !EsSecreta(p)), entidad);

        foreach (var navegacion in tipo.GetNavigations().Where(n => n.TargetEntityType.IsOwned()))
        {
            if (navegacion.PropertyInfo is not { } info)
                continue;
            escritor.WritePropertyName(navegacion.Name);
            switch (info.GetValue(entidad))
            {
                case null:
                    escritor.WriteNullValue();
                    break;
                case System.Collections.IEnumerable coleccion when navegacion.IsCollection:
                    escritor.WriteStartArray();
                    foreach (var elemento in coleccion)
                        EscribirEntidad(escritor, navegacion.TargetEntityType, elemento);
                    escritor.WriteEndArray();
                    break;
                case var propio:
                    EscribirEntidad(escritor, navegacion.TargetEntityType, propio);
                    break;
            }
        }
        escritor.WriteEndObject();
    }

    private static IAsyncEnumerable<object> Leer(CaeManagerDbContext contexto, Type tipo) =>
        (IAsyncEnumerable<object>)LeerConjuntoGenerico.MakeGenericMethod(tipo).Invoke(null, [contexto])!;

    private static IAsyncEnumerable<object> LeerConjunto<T>(CaeManagerDbContext contexto) where T : class =>
        contexto.Set<T>().AsNoTracking().AsAsyncEnumerable();
}
