using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Infrastructure.Exportacion;

/// <summary>
/// Exportación completa de los datos de UN Tenant propietario (devolución de datos al terminar el
/// contrato, Términos y condiciones): un zip cifrado con un JSON por tipo de entidad del Tenant, sus
/// Usuarios sin secretos de Identity, los binarios de sus Documentos descifrados con el mismo
/// almacén que usa la descarga y el manifiesto de <see cref="ArchivoExportacion"/>.
///
/// <para>
/// <b>Aislamiento.</b> Lee con el contexto inyectado —rol restringido, con RLS— y bajo
/// <see cref="AmbitoTenantExplicito"/> fijado al Tenant pedido, así que la condición explícita de
/// <see cref="ConsultaDeTenant{T}"/> y las políticas RLS acotan las dos al mismo Tenant. Antes de leer nada exige, contra PostgreSQL,
/// que esa identidad no pueda saltarse RLS (<see cref="VerificacionIdentidadDeRuntime"/>): nunca
/// corre con <c>FabricaContextoDeBootstrap</c>. Y cada fila leída se comprueba otra vez contra el
/// Tenant pedido: una sola fila de otro Tenant aborta la exportación entera.
/// </para>
///
/// <para>
/// <b>Autorización.</b> Solo se alcanza desde el modo de CLI <see cref="Argumento"/> (acceso
/// operativo al servidor), y exige además nombrar a un Actor de Plataforma TALVEG: una cuenta activa
/// de un Tenant de plataforma con una concesión AdminPlataforma vigente que alcance el Tenant
/// propietario (ASVS 4.0.3 V4.1.3). Lo comprueba la función <c>app_actor_plataforma_que_exporta</c>,
/// un contrato explícito sobre el plano de plataforma, sin ensanchar RLS, que solo responde en el contexto
/// firmado del Tenant exportado y sin usuario: se llama dentro de su ámbito. Quien ejecuta la CLI no se
/// autentica en la aplicación, así que el Actor real consta como declarado en los datos de la fila.
/// No hay Usuario simulado. Va en los datos y no en las columnas de autor, porque esas columnas
/// afirman una identidad autenticada y abren la cuenta a la lectura bajo RLS del Tenant propietario
/// (<c>cuentas_lectura</c>). La exportación queda en la auditoría del Tenant antes de leer nada (si
/// esa fila no se guarda, no se exporta nada) y otra vez al terminar, con los recuentos.
/// </para>
///
/// <para>
/// <b>Contraseña.</b> Aleatoria por exportación, la genera la CLI y se muestra una sola vez en la
/// consola. Nunca entra en la auditoría, ni en el manifiesto, ni en <see cref="Resultado"/>, ni en
/// un registro (ASVS 4.0.3 V7.1.1), y nunca se acepta como argumento, que dejaría rastro en el
/// historial. Se pasa al Tenant propietario por un canal distinto del enlace de descarga.
/// </para>
///
/// <para>
/// <b>Qué no sale.</b> Ninguna columna cifrada en reposo (credenciales de portales, tokens de
/// integraciones, clientState, payloads crudos de webhooks: todas las de conversor texto→texto),
/// ni el hash de las claves de API, ni hashes de contraseña, sellos de seguridad o secretos TOTP
/// de Identity (de los Usuarios solo se proyectan campos de perfil). Tampoco los catálogos
/// globales sin Tenant; <c>ReclamacionesBuzonIntegracion</c> no lo es, aunque no herede de
/// <see cref="EntidadConTenant"/>, y sale en su propio fichero.
/// </para>
///
/// <para>
/// <b>Papelera.</b> Las filas con borrado lógico salen mientras sigan en el servidor, cada una con
/// su <c>EstaEliminado</c>, y sus binarios también. Por eso la lectura salta el filtro global de EF
/// y repone el TenantId a mano (<see cref="ConsultaDeTenant{T}"/>): RLS y la comprobación fila a
/// fila no cambian, y las columnas secretas siguen fuera también en esas filas.
/// </para>
/// </summary>
public static class ExportacionCompletaTenant
{
    public const string Argumento = "--exportar-tenant";
    public const string ArgumentoSolicitante = "--solicitante";
    public const string ArgumentoDestino = "--destino";

    /// <summary>Recuento del manifiesto: filas exportadas que están en la papelera.</summary>
    public const string RecuentoEnPapelera = "FilasEnPapelera";

    /// <summary>Propiedades que nunca se exportan aunque no lleven conversor de cifrado.</summary>
    private static readonly HashSet<string> NombresSecretos = new(StringComparer.Ordinal)
    {
        "Contrasena", "HashClave", "RefreshToken", "AccessToken", "ClientState", "TokenAcceso", "PayloadCrudo",
        "PasswordHash", "SecurityStamp",
        // SesionesUsuario sí se exporta (emisión y revocación de cada sesión de las cuentas del
        // Tenant), pero no su identificador: es lo que hace valer una cookie.
        "Sid",
    };

    /// <summary>
    /// Tablas con Tenant que RLS acota además a la persona que las creó (política restrictiva
    /// <c>solo_su_persona</c>): sin sesión de usuario no se ven, y exportarlas vacías diría que no hay.
    /// </summary>
    private static readonly HashSet<string> AcotadasALaPersona = new(StringComparer.Ordinal)
    {
        "TareaAsistente", "TurnoTareaAsistente", "PasoTareaAsistente",
    };

    /// <summary>Lo que la exportación deja fuera a sabiendas; va al manifiesto con su motivo.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoIncluido = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["TareaAsistente, TurnoTareaAsistente, PasoTareaAsistente"] =
            "RLS las acota a la persona que las creó; la exportación corre sin sesión de usuario y no las ve.",
        ["Asignaciones de Operación y de Cartera, encargos, solicitudes y propuestas de cartera"] =
            "No son filas del Tenant propietario sino relaciones con un Operador CAE externo (sin TenantId).",
        ["Aceptaciones de términos, preferencias de panel y tablas de Identity de claims, logins y tokens"] =
            "Van por cuenta de usuario, sin TenantId; los tokens de Identity guardan secretos TOTP.",
        ["Adjuntos de mensajes, evidencias inbound, ficheros de tipos de documento de Centro y logo"] =
            "Sus filas se exportan con su clave de almacén; el binario solo se incluye para los Documentos.",
        ["Columnas cifradas en reposo, hash de claves de API y secretos de Identity"] = "Secretos: nunca se exportan.",
    };

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly MethodInfo LeerConjuntoGenerico =
        typeof(ExportacionCompletaTenant).GetMethod(nameof(LeerConjunto), BindingFlags.NonPublic | BindingFlags.Static)!;

    public const string Uso =
        "Uso: --exportar-tenant <TenantId guid> --solicitante <correo de un Actor de Plataforma TALVEG con AdminPlataforma> --destino <ruta.zip>.";

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

    /// <summary>
    /// La única salida de la contraseña: va a la consola de quien ejecuta, nunca al registro. Solo se
    /// llama con el zip ya completo.
    /// </summary>
    public static void InformarContrasena(string contrasena, TextWriter salida)
    {
        salida.WriteLine($"Contraseña del zip, se muestra solo esta vez: {contrasena}");
        salida.WriteLine("Pásala al Tenant propietario por un canal distinto del enlace de descarga.");
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
                        typeof(EntidadConTenant).IsAssignableFrom(t.ClrType) && !AcotadasALaPersona.Contains(t.ClrType.Name))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    public static async Task<Resultado> ExportarAsync(
        CaeManagerDbContext contexto, IFileStorageService almacen, Guid tenantId, string correoSolicitante,
        string contrasena, Stream destino, CancellationToken cancellationToken = default)
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

        var actorId = await ExigirActorDePlataformaAsync(contexto, tenantId, correoSolicitante, cancellationToken);

        // Quien ejecuta la CLI no se autentica en la aplicación: el Actor real consta como declarado.
        // Esta fila se guarda antes de leer nada; la de cierre, al terminar.
        await AuditarAsync(contexto, tenantId, new
        {
            fase = "Iniciada",
            alcance = "ExportacionCompleta",
            via = Argumento,
            actorRealDeclaradoUsuarioId = actorId,
            usuarioSimuladoId = (Guid?)null,
        }, cancellationToken);

        var recuentos = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var enPapelera = 0L;
        int sinFichero;
        using (var archivo = new ArchivoExportacion(destino, tenantId, contrasena))
        {
            // Una sola foto de la base para todas las tablas: sin ella, una fila creada o borrada a mitad
            // dejaría claves foráneas que apuntan fuera del zip.
            await using (var foto = await contexto.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
            {
                await archivo.EscribirAsync("datos/Tenant.json",
                    salida => JsonSerializer.SerializeAsync(salida, tenant, OpcionesJson, cancellationToken), cancellationToken);

                foreach (var tipo in TiposExportables(contexto.Model))
                {
                    var filas = 0L;
                    await archivo.EscribirAsync($"datos/{tipo.ClrType.Name}.json", async salida =>
                    {
                        await using var escritor = new Utf8JsonWriter(salida);
                        escritor.WriteStartArray();
                        await foreach (var fila in Leer(contexto, tipo.ClrType, tenantId).WithCancellation(cancellationToken))
                        {
                            GuardaTenantExportacion.Exigir(tenantId, ((EntidadConTenant)fila).TenantId, tipo.ClrType.Name);
                            EscribirEntidad(escritor, contexto.Model.FindEntityType(fila.GetType()) ?? tipo, fila);
                            filas++;
                            if (fila is EntidadBase { EstaEliminado: true })
                                enPapelera++;
                            if (escritor.BytesPending > 64 * 1024)
                                await escritor.FlushAsync(cancellationToken);
                        }
                        escritor.WriteEndArray();
                    }, cancellationToken);
                    archivo.RegistrarRecuento(tipo.ClrType.Name, filas);
                    recuentos[tipo.ClrType.Name] = filas;
                }

                archivo.RegistrarRecuento(RecuentoEnPapelera, enPapelera);
                recuentos[RecuentoEnPapelera] = enPapelera;

                // Lleva TenantPropietarioId y no hereda de EntidadConTenant: TiposExportables no la alcanza. Se lee
                // igual, bajo la RLS del Tenant pedido y con la guarda fila a fila.
                var reclamaciones = 0L;
                await archivo.EscribirAsync("datos/ReclamacionBuzonIntegracion.json", async salida =>
                {
                    await using var escritor = new Utf8JsonWriter(salida);
                    escritor.WriteStartArray();
                    var tipo = contexto.Model.FindEntityType(typeof(CaeManager.Domain.Integraciones.ReclamacionBuzonIntegracion))!;
                    await foreach (var reclamacion in contexto.ReclamacionesBuzonIntegracion.AsNoTracking()
                        .Where(r => r.TenantPropietarioId == tenantId).AsAsyncEnumerable().WithCancellation(cancellationToken))
                    {
                        GuardaTenantExportacion.Exigir(tenantId, reclamacion.TenantPropietarioId, tipo.ClrType.Name);
                        EscribirEntidad(escritor, tipo, reclamacion);
                        reclamaciones++;
                        if (escritor.BytesPending > 64 * 1024)
                            await escritor.FlushAsync(cancellationToken);
                    }
                    escritor.WriteEndArray();
                }, cancellationToken);
                archivo.RegistrarRecuento("ReclamacionBuzonIntegracion", reclamaciones);
                recuentos["ReclamacionBuzonIntegracion"] = reclamaciones;

                var usuarios = await EscribirUsuariosAsync(contexto, archivo, tenantId, cancellationToken);
                archivo.RegistrarRecuento("Usuario", usuarios);
                recuentos["Usuario"] = usuarios;

                sinFichero = await EscribirDocumentosAsync(contexto, almacen, archivo, tenantId, cancellationToken);
                // Foto de solo lectura: se deshace siempre; las dos filas de auditoría van fuera de ella.
                await foto.RollbackAsync(cancellationToken);
            }

            foreach (var (que, motivo) in NoIncluido)
                archivo.RegistrarNoIncluido(que, motivo);
            await archivo.TerminarAsync(cancellationToken);
        }

        await AuditarAsync(contexto, tenantId, new
        {
            fase = "Completada",
            alcance = "ExportacionCompleta",
            via = Argumento,
            actorRealDeclaradoUsuarioId = actorId,
            usuarioSimuladoId = (Guid?)null,
            recuentos,
            documentosSinFichero = sinFichero,
        }, cancellationToken);

        return new Resultado(tenantId, tenant.Nombre, recuentos, sinFichero);
    }

    /// <summary>
    /// Sin autor en las columnas: el Actor es declarado, no autenticado. El tipo de actor queda
    /// Desconocido, y la vía también, porque Normal diría que actuó el propio Tenant.
    /// </summary>
    private static async Task AuditarAsync(CaeManagerDbContext contexto, Guid tenantId, object datos, CancellationToken cancellationToken)
    {
        contexto.RegistrosAuditoria.Add(new RegistroAuditoria(
            "Tenant", tenantId, RegistroAuditoria.AccionExportacion, null, JsonSerializer.Serialize(datos),
            usuarioId: null, TipoActorAuditoria.Desconocido, viaAcceso: TipoViaAccesoAuditoria.Desconocida));
        await contexto.SaveChangesAsync(cancellationToken);
        contexto.ChangeTracker.Clear();
    }

    /// <summary>
    /// El Actor de Plataforma TALVEG que pide la exportación. La función de la base devuelve la cuenta
    /// solo si es de un Tenant de plataforma, no está desactivada y tiene una concesión AdminPlataforma
    /// vigente que alcanza este Tenant.
    /// </summary>
    private static async Task<Guid> ExigirActorDePlataformaAsync(
        CaeManagerDbContext contexto, Guid tenantId, string correo, CancellationToken cancellationToken)
    {
        var normalizado = correo.Trim().ToUpperInvariant();
        var cuentas = await contexto.Database.SqlQuery<Guid>($"SELECT cuenta_id AS \"Value\" FROM app_actor_plataforma_que_exporta({normalizado}, {tenantId})").ToListAsync(cancellationToken);

        if (cuentas is not [var cuentaId])
            throw new InvalidOperationException(
                "La exportación solo la puede pedir un Actor de Plataforma TALVEG activo con concesión AdminPlataforma " +
                "vigente sobre ese Tenant; esa cuenta no existe en la plataforma, no tiene la concesión o está desactivada.");

        return cuentaId;
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
    /// El binario de cada Documento con fichero, también de los que están en la papelera, descifrado por el almacén con la clave del
    /// Tenant del ámbito. Primero se leen los pares (Id, clave) y después se abren los ficheros,
    /// para no abrir el almacén con un lector de la base aún abierto. Un fichero que ya no está
    /// queda en el manifiesto; cualquier otro fallo aborta la exportación.
    /// </summary>
    private static async Task<int> EscribirDocumentosAsync(
        CaeManagerDbContext contexto, IFileStorageService almacen, ArchivoExportacion archivo, Guid tenantId, CancellationToken cancellationToken)
    {
        var documentos = await ConsultaDeTenant<Documento>(contexto, tenantId)
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

    private static IAsyncEnumerable<object> Leer(CaeManagerDbContext contexto, Type tipo, Guid tenantId) =>
        (IAsyncEnumerable<object>)LeerConjuntoGenerico.MakeGenericMethod(tipo).Invoke(null, [contexto, tenantId])!;

    private static IAsyncEnumerable<object> LeerConjunto<T>(CaeManagerDbContext contexto, Guid tenantId) where T : EntidadConTenant =>
        ConsultaDeTenant<T>(contexto, tenantId).AsAsyncEnumerable();

    /// <summary>
    /// Todas las filas del Tenant, las de la papelera incluidas. Saltar el filtro global quita también
    /// su comparación del TenantId, y por eso se repone aquí.
    /// </summary>
    public static IQueryable<T> ConsultaDeTenant<T>(CaeManagerDbContext contexto, Guid tenantId) where T : EntidadConTenant =>
        contexto.Set<T>().IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId);
}
