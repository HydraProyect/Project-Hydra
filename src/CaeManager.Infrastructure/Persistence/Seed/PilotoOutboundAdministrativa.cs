using System.Globalization;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La siembra del piloto Outbound en un servidor que arranca como <c>Production</c>
/// (staging): el mismo lote que <see cref="PilotoOutboundSeeder"/> siembra en local
/// —un Operador CAE externo de demostración y seis Tenants propietarios—, pero como
/// operación administrativa explícita, sin la contraseña compartida de la demo.
///
/// <para>
/// <b>Qué la separa de la siembra local.</b> (1) No usa <c>DatosPrueba:Activo</c> ni
/// <see cref="CredencialesDemo"/>, y se niega si están activos: cada cuenta lleva una
/// contraseña aleatoria distinta que solo va a un fichero en memoria, nunca a un
/// registro ni a la salida. (2) Las cuentas son de un dominio de correo que controla
/// quien la lanza, nunca <c>@caemanager.local</c>. (3) Sus claves de configuración
/// son propias (<c>PilotoOutbound:*</c>), fuera de <c>DatosPrueba</c>, y la fecha de
/// la demostración es obligatoria: no se asume «hoy». (4) Solo se alcanza desde el
/// modo de línea de órdenes <see cref="ArgumentoSembrar"/> de <c>Program.cs</c>,
/// que termina el proceso antes del arranque normal (lo vigila
/// <c>PilotoOutboundSoloDesdeElModoCliTests</c>). (5) Al terminar mide el lote con
/// la autoverificación y devuelve sus discrepancias: el modo sale con código
/// distinto de cero si la matriz no cuadra.
/// </para>
///
/// <para>
/// <b>Falla cerrada, antes de escribir.</b> <see cref="ValidarPrecondiciones"/> no
/// toca la base; después, y todavía sin escribir ni crear el fichero de
/// credenciales, se comprueba que ningún nombre del piloto esté ocupado por un
/// Tenant sin marcador de demo, que ningún Tenant propietario lleve datos sembrados
/// por otra versión de la siembra, que la fecha sirva para sembrar hoy y que ninguna
/// cuenta con un correo del piloto pertenezca a otro Tenant.
/// </para>
///
/// <para>
/// <b>Un lote de otra versión se retira, no se completa.</b> La siembra es
/// idempotente por Tenant propietario: no toca el que ya tiene la Empresa propia con
/// el identificador fiscal de esta versión y siembra entero el que no tiene ninguna
/// Empresa, así que una ejecución cortada se reanuda repitiendo la orden. Pero si
/// uno tiene Empresas y ninguna con ese identificador, sus datos son de otra versión:
/// la orden se niega con su nombre, sin escribir en ningún Tenant, y el modo sale
/// con código distinto de cero y con <see cref="MensajeDeInterrupcion"/> en la salida
/// de error, como con cualquier otra negativa. El arranque, en ese mismo caso, solo
/// avisa; aquí un aviso con salida 0 se leería como «sembrado».
/// </para>
///
/// <para>
/// <b>La confirmación de entorno no distingue staging de producción.</b> Se compara
/// con <see cref="IHostEnvironment.EnvironmentName"/>, que es <c>Production</c> en
/// los dos: la aplicación no recibe hoy ningún valor que los separe. Contra qué
/// base se escribe lo deciden el fichero de composición y el fichero de entorno con
/// los que se lanza el contenedor, no esta clase.
/// </para>
///
/// <para>
/// <b>Dos identidades, como en el arranque.</b> Todo lo que siembra va con el
/// contexto de tráfico, bajo RLS y dentro del ámbito de su Tenant. La identidad de
/// bootstrap se usa para una sola cosa: el backfill de asignaciones que el arranque
/// ejecuta entre la siembra y la autoverificación
/// (<see cref="AsignacionesOperativasBackfillSeeder"/>, administrativo por diseño),
/// para que el lote quede —y se mida— igual que tras un arranque. Por eso el modo
/// se lanza con el servicio <c>migrador</c>, el único que recibe esa credencial.
/// </para>
/// </summary>
public static class PilotoOutboundAdministrativa
{
    public const string ArgumentoSembrar = "--sembrar-piloto-outbound";

    public const string Seccion = "PilotoOutbound";
    public const string ClaveConfirmarEntorno = Seccion + ":ConfirmarEntorno";
    public const string ClaveDominioCorreo = Seccion + ":DominioCorreo";
    public const string ClaveDirectorioCredenciales = Seccion + ":DirectorioCredenciales";
    public const string ClaveFechaDemostracion = Seccion + ":FechaDemostracion";
    public const string ClaveCorreoContactos = Seccion + ":CorreoContactos";

    /// <param name="Contactos">Con <see cref="ClaveCorreoContactos"/>, todos los contactos de agenda y canales de Centro escriben a ese buzón; sin ella, a un dominio no entregable, como en la siembra local.</param>
    public sealed record Opciones(
        string DominioCorreo, string DirectorioCredenciales, string EntornoConfirmado, DateOnly FechaDemostracion,
        ContactosPilotoOutbound Contactos);

    /// <param name="ContrasenaEntregada">True solo si esta ejecución va a crear la cuenta (y por tanto su contraseña está en el fichero); false si ya existía y no se toca.</param>
    public sealed record CuentaSembrada(string Email, string Rol, string NombreTenant, bool ContrasenaEntregada);

    /// <summary>Lo que quien lanza el modo puede ver y registrar: ningún dato secreto.</summary>
    /// <param name="Discrepancias">Lo medido que no coincide con la matriz, con su Tenant y su contador. Vacía si cuadra.</param>
    /// <param name="Advertencias">Las divergencias declaradas del catálogo: no hacen fallar, pero hay que leerlas.</param>
    public sealed record Resultado(
        PilotoOutboundSeeder.Resultado Siembra, IReadOnlyList<(string Nombre, Guid Id)> Tenants,
        IReadOnlyList<CuentaSembrada> Cuentas, string FicheroCredenciales, PilotoOutboundAutoverificacion.Informe Informe,
        IReadOnlyList<string> Advertencias, IReadOnlyList<string> Discrepancias);

    /// <summary>
    /// Lee y valida la forma de las opciones. Lanza <see cref="InvalidOperationException"/>
    /// con el nombre de la clave de ESTE modo. La fecha y el correo de contactos se
    /// validan con las mismas reglas que la siembra local.
    /// </summary>
    public static Opciones LeerOpciones(IConfiguration configuration)
    {
        var comoLaSiembraLocal = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpcionesPilotoOutbound.ClaveFechaDemostracion] = configuration[ClaveFechaDemostracion],
                [OpcionesPilotoOutbound.ClaveCorreoContactos] = configuration[ClaveCorreoContactos],
            })
            .Build();

        var locales = ConLasClavesDeEsteModo(() => OpcionesPilotoOutbound.Leer(comoLaSiembraLocal));

        return new Opciones(
            configuration[ClaveDominioCorreo] ?? string.Empty,
            configuration[ClaveDirectorioCredenciales] ?? string.Empty,
            configuration[ClaveConfirmarEntorno] ?? string.Empty,
            locales.FechaDemostracion, locales.Contactos);
    }

    /// <summary>Las seis cuentas del piloto en el dominio indicado, con la misma parte local que las de la siembra local.</summary>
    public static CuentasPilotoOutbound CuentasDe(string dominio)
    {
        static string ParteLocal(string email) => email[..email.IndexOf('@')];

        var locales = CuentasPilotoOutbound.Locales;
        return new CuentasPilotoOutbound(
            $"{ParteLocal(locales.GestoraPrimera)}@{dominio}", $"{ParteLocal(locales.GestorSegundo)}@{dominio}",
            $"{ParteLocal(locales.Coordinadora)}@{dominio}", $"{ParteLocal(locales.AdministradorOperador)}@{dominio}",
            $"{ParteLocal(locales.AdministradorT1)}@{dominio}", $"{ParteLocal(locales.UsuarioDeClienteEmpresarialT1)}@{dominio}");
    }

    // ── Precondiciones (antes de escribir nada) ─────────────────────────────

    /// <summary>
    /// La confirmación a mano del entorno, común a la siembra y a la retirada del
    /// piloto: quien lanza la operación escribe el nombre del entorno contra el que
    /// cree estar. No distingue staging de producción (ver el resumen de la clase).
    /// </summary>
    public static void ExigirEntornoConfirmado(string? entornoConfirmado, IHostEnvironment entorno)
    {
        if (!string.Equals(entornoConfirmado, entorno.EnvironmentName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{ClaveConfirmarEntorno} debe ser exactamente el entorno en el que se ejecuta ('{entorno.EnvironmentName}'), " +
                $"y es '{entornoConfirmado}': se pide para que quien lanza la operación diga a mano contra qué base de datos va a escribir.");
    }

    /// <summary>
    /// Todo lo que se puede comprobar sin tocar la base de datos. Lanza
    /// <see cref="InvalidOperationException"/> con el motivo exacto. El dominio y el
    /// directorio se validan con las reglas de <see cref="SiembraDemoDireccionAdministrativa"/>.
    /// </summary>
    /// <param name="informacionDeMontajes">Contenido de <c>/proc/self/mountinfo</c> (parámetro para poder probarlo).</param>
    public static void ValidarPrecondiciones(
        IConfiguration configuration, IHostEnvironment entorno, Opciones opciones,
        string? informacionDeMontajes, bool esLinux, UnixFileMode? modoDelDirectorio)
    {
        ExigirEntornoConfirmado(opciones.EntornoConfirmado, entorno);

        if (configuration.GetValue<bool>("DatosPrueba:Activo") || configuration.GetValue<bool>(OpcionesPilotoOutbound.ClaveActivo))
            throw new InvalidOperationException(
                $"Con DatosPrueba:Activo o {OpcionesPilotoOutbound.ClaveActivo} activos la siembra local escribiría con la contraseña " +
                "compartida de la demo. Esta operación no se mezcla con ella: desactívalos para esta ejecución.");

        ConLasClavesDeEsteModo(() =>
        {
            SiembraDemoDireccionAdministrativa.ValidarDominio(opciones.DominioCorreo);
            SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida(
                opciones.DirectorioCredenciales, informacionDeMontajes, esLinux, modoDelDirectorio);
            return 0;
        });
    }

    /// <summary>
    /// Los mensajes de las validaciones reutilizadas nombran las claves de su propio
    /// modo (<c>DatosPrueba:PilotoOutbound:*</c>, <c>DemoDireccion:*</c>): aquí se
    /// sustituyen por las de este, para que quien lea el rechazo sepa qué corregir.
    /// </summary>
    private static string ConLasClavesDeEsteModo(string mensaje) => mensaje
        .Replace(OpcionesPilotoOutbound.Seccion + ":", Seccion + ":", StringComparison.Ordinal)
        .Replace(SiembraDemoDireccionAdministrativa.ClaveDominioCorreo, ClaveDominioCorreo, StringComparison.Ordinal)
        .Replace(SiembraDemoDireccionAdministrativa.ClaveDirectorioCredenciales, ClaveDirectorioCredenciales, StringComparison.Ordinal);

    private static T ConLasClavesDeEsteModo<T>(Func<T> validacion)
    {
        try
        {
            return validacion();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ConLasClavesDeEsteModo(ex.Message), ex);
        }
    }

    // ── Escritura ───────────────────────────────────────────────────────────

    public static async Task<Resultado> SembrarAsync(
        CaeManagerDbContext dbContext, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore, IFileStorageService almacen,
        IServiceScopeFactory fabricaDeAmbitos, IConfiguration configuration, IHostEnvironment entorno, Opciones opciones,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        var esLinux = OperatingSystem.IsLinux();
        ValidarPrecondiciones(
            configuration, entorno, opciones,
            esLinux ? await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken) : null,
            esLinux, esLinux && Directory.Exists(opciones.DirectorioCredenciales) ? File.GetUnixFileMode(opciones.DirectorioCredenciales) : null);

        return await EjecutarAsync(
            dbContext, crearContextoDeBootstrap, userManager, userStore, almacen, fabricaDeAmbitos, configuration, entorno,
            opciones, logger, cancellationToken);
    }

    /// <summary>
    /// La siembra propiamente dicha, DESPUÉS de las comprobaciones de <see cref="SembrarAsync"/> (que dependen
    /// del sistema operativo y de <c>/proc</c>, y por eso no se pueden ejercitar en cualquier máquina de pruebas).
    /// </summary>
    internal static async Task<Resultado> EjecutarAsync(
        CaeManagerDbContext dbContext, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore, IFileStorageService almacen,
        IServiceScopeFactory fabricaDeAmbitos, IConfiguration configuration, IHostEnvironment entorno, Opciones opciones,
        ILogger logger, CancellationToken cancellationToken)
    {
        var cuentas = CuentasDe(opciones.DominioCorreo);
        var cuentasConSuTenant = CuentasConSuTenant(cuentas);

        // Las cuatro negativas que miran la base, antes de crear el fichero de credenciales y de escribir nada.
        await PilotoOutboundSeeder.RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(dbContext, cancellationToken);
        await PilotoOutboundSeeder.RechazarDatosDeOtraVersionAsync(dbContext, cancellationToken);

        if (OpcionesPilotoOutbound.MotivoFechaNoUtilizable(opciones.FechaDemostracion, DiaDeNegocio.Hoy()) is { } motivo
            && await PilotoOutboundSeeder.PrimerTenantSinSembrarAsync(dbContext, cancellationToken) is { } pendiente)
            throw new InvalidOperationException(ConLasClavesDeEsteModo(
                $"{motivo} Queda por sembrar «{pendiente}», así que la siembra se niega y no escribe nada."));

        var correosNuevos = await VerificarCuentasReutilizablesAsync(dbContext, userManager, cuentasConSuTenant, cancellationToken);

        // El fichero se abre —con permisos 0600 DESDE su creación, no después— antes de crear ninguna
        // cuenta: si no se puede escribir, no se crea nada cuya contraseña se perdería.
        var rutaFichero = Path.Combine(
            opciones.DirectorioCredenciales, $"credenciales-piloto-outbound-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
        var opcionesDeFichero = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        // Windows no admite modos Unix (lanza): solo se usa en máquinas de desarrollo; el servidor es Linux
        // (ValidarPrecondiciones lo exige), donde el fichero nace ya con 0600.
        if (!OperatingSystem.IsWindows())
            opcionesDeFichero.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Una contraseña distinta por cuenta NUEVA; las que ya existen (del propio lote) no se tocan.
        var contrasenas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in correosNuevos)
            contrasenas[email] = SiembraDemoDireccionAdministrativa.GenerarContrasena();

        var cuentasSembradas = cuentasConSuTenant
            .Select(c => new CuentaSembrada(c.Email, c.Rol, c.NombreTenant, contrasenas.ContainsKey(c.Email)))
            .ToList();

        // Las contraseñas llegan al fichero ANTES de crear ninguna cuenta: si algo falla después, toda cuenta
        // que exista tiene su contraseña entregable en el fichero; lo peor que puede pasar es que el fichero
        // liste una cuenta que no llegó a crearse.
        await using (var fichero = new FileStream(rutaFichero, opcionesDeFichero))
            await EscribirCredencialesAsync(fichero, entorno.EnvironmentName, cuentasSembradas, contrasenas, cancellationToken);

        var parametros = new PilotoOutboundSeeder.Parametros(
            email => new CredencialesDemo(contrasenas.GetValueOrDefault(email, string.Empty), string.Empty, string.Empty),
            cuentas, opciones.FechaDemostracion, opciones.Contactos);

        PilotoOutboundSeeder.Resultado siembra;
        try
        {
            siembra = await PilotoOutboundSeeder.SembrarLoteAsync(
                dbContext, userManager, userStore, entorno, almacen, parametros, logger, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ConLasClavesDeEsteModo(ex.Message), ex);
        }

        // Los dos pasos del arranque que van entre la siembra y la autoverificación y que alcanzan a
        // los Tenants recién creados, en su mismo orden y con su misma identidad: la delegación de
        // soporte —apagada— con el contexto de tráfico, y el backfill de asignaciones (la operación
        // raíz de cada Tenant) con el de bootstrap. Los dos son idempotentes y recorren todos los
        // Tenants, igual que en cada arranque. Sin ellos el lote quedaría a medias hasta el siguiente
        // despliegue, y lo que se mide a continuación no sería lo que se verá después.
        await DelegacionesSoporteSeeder.SeedAsync(dbContext, configuration, logger, cancellationToken);
        await using (var dbContextBootstrap = crearContextoDeBootstrap())
            await AsignacionesOperativasBackfillSeeder.SeedAsync(dbContextBootstrap, logger, cancellationToken);

        var informe = await PilotoOutboundAutoverificacion.MedirAsync(
            fabricaDeAmbitos, cuentas, new OpcionesPilotoOutbound(opciones.FechaDemostracion, opciones.Contactos), cancellationToken);

        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var tenants = (await dbContext.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => new { t.Nombre, t.Id }).ToListAsync(cancellationToken))
            .OrderBy(t => nombres.IndexOf(t.Nombre)).Select(t => (t.Nombre, t.Id)).ToList();

        // Solo cuentas y Tenants, nunca contraseñas: es lo que se registra.
        logger.LogInformation(
            "Piloto Outbound sembrado en {Entorno} por la vía administrativa: {Tenants} Tenants, {Cuentas} cuentas ({Nuevas} nuevas).",
            entorno.EnvironmentName, tenants.Count, cuentasSembradas.Count, cuentasSembradas.Count(c => c.ContrasenaEntregada));

        return new Resultado(
            siembra, tenants, cuentasSembradas, rutaFichero, informe,
            PilotoOutboundAutoverificacion.Advertencias(informe), PilotoOutboundAutoverificacion.Discrepancias(informe));
    }

    private static IReadOnlyList<(string Email, string Rol, string NombreTenant)> CuentasConSuTenant(CuentasPilotoOutbound cuentas) =>
    [
        (cuentas.AdministradorOperador, Roles.Administrador, CatalogoPilotoOutbound.NombreTenantOperador),
        (cuentas.Coordinadora, Roles.CoordinadorCae, CatalogoPilotoOutbound.NombreTenantOperador),
        (cuentas.GestoraPrimera, Roles.GestorCae, CatalogoPilotoOutbound.NombreTenantOperador),
        (cuentas.GestorSegundo, Roles.GestorCae, CatalogoPilotoOutbound.NombreTenantOperador),
        (cuentas.AdministradorT1, Roles.Administrador, CatalogoPilotoOutbound.NombreTenantT1),
        (cuentas.UsuarioDeClienteEmpresarialT1, Roles.Cliente, CatalogoPilotoOutbound.NombreTenantT1),
    ];

    /// <summary>
    /// Una cuenta del piloto que ya existe solo se reutiliza si pertenece al Tenant del
    /// lote en el que la siembra la crearía (una re-ejecución). Si el correo es de una
    /// cuenta de otro Tenant, la siembra no puede crearla ni debe tocarla: se niega
    /// antes de escribir. Devuelve los correos de las cuentas que hay que CREAR.
    /// </summary>
    private static async Task<HashSet<string>> VerificarCuentasReutilizablesAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager,
        IReadOnlyList<(string Email, string Rol, string NombreTenant)> cuentas, CancellationToken cancellationToken)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var tenantsExistentes = await dbContext.Tenants
            .Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Nombre, t => t.Id, cancellationToken);

        var nuevas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ajenas = new List<string>();

        // La pregunta es entre Tenants —¿existe ese correo en CUALQUIER Tenant?— y AspNetUsers tiene RLS:
        // sin este ámbito la búsqueda no vería ninguna cuenta y esta guarda dejaría de proteger nada.
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        foreach (var (email, _, nombreTenant) in cuentas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existente = await userManager.FindByEmailAsync(email);
            if (existente is null) nuevas.Add(email);
            else if (!tenantsExistentes.TryGetValue(nombreTenant, out var tenantId) || existente.TenantId != tenantId) ajenas.Add(email);
        }

        if (ajenas.Count > 0)
            throw new InvalidOperationException(
                $"Ya existen cuentas con un correo del piloto que NO pertenecen al Tenant del lote que les corresponde: {string.Join(", ", ajenas)}. " +
                "Podrían ser cuentas reales: no se siembra nada.");

        return nuevas;
    }

    private static async Task EscribirCredencialesAsync(
        FileStream fichero, string entorno, IReadOnlyList<CuentaSembrada> cuentas,
        IReadOnlyDictionary<string, string> contrasenas, CancellationToken cancellationToken)
    {
        var contenido = new
        {
            aviso = "FICHERO TEMPORAL con contraseñas en claro: entregar y destruir inmediatamente; no copiar, no respaldar.",
            entorno,
            generadoEnUtc = DateTime.UtcNow,
            cuentas = cuentas.Select(c => new
            {
                email = c.Email,
                rol = c.Rol,
                tenant = c.NombreTenant,
                contrasena = contrasenas.GetValueOrDefault(c.Email),
                nota = c.ContrasenaEntregada ? null : "La cuenta ya existía: su contraseña no se ha tocado.",
            }),
        };

        await JsonSerializer.SerializeAsync(
            fichero, contenido, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        await fichero.FlushAsync(cancellationToken);
    }

    // ── Salida ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Escribe el resumen medido por Tenant y las divergencias declaradas, y —si la
    /// matriz no cuadra— cada discrepancia con su Tenant y su contador. Devuelve el
    /// código de salida del modo: 0 si lo medido es lo que la matriz declara, 1 si no.
    /// Nunca escribe una contraseña: no las tiene.
    /// </summary>
    public static int Informar(Resultado resultado, string nombreDelEntorno, TextWriter salida, TextWriter errores)
    {
        var siembra = resultado.Siembra;
        salida.WriteLine(siembra.Escribio
            ? string.Create(CultureInfo.InvariantCulture,
                $"Piloto Outbound sembrado en {nombreDelEntorno}: {resultado.Tenants.Count} Tenants; datos nuevos en {siembra.TenantsConDatosNuevos.Count}, {siembra.Documentos} documentos y {siembra.Pdf} PDF en {siembra.Duracion.TotalSeconds:F0} s.")
            : $"Piloto Outbound en {nombreDelEntorno}: el lote ya estaba sembrado entero y esta ejecución no ha escrito datos.");

        foreach (var (nombre, id) in resultado.Tenants)
            salida.WriteLine($"  Tenant {id}  {nombre}");
        foreach (var cuenta in resultado.Cuentas)
            salida.WriteLine(
                $"  Cuenta {cuenta.Email}  {cuenta.Rol}  en «{cuenta.NombreTenant}»  contraseña en el fichero: {(cuenta.ContrasenaEntregada ? "sí" : "no (ya existía)")}");
        salida.WriteLine($"Credenciales: {resultado.FicheroCredenciales} (entregar y destruir).");

        salida.WriteLine("Medido por Tenant propietario, con las consultas de las pantallas:");
        foreach (var m in resultado.Informe.Tenants)
            salida.WriteLine(
                $"  {m.Clave} «{m.Nombre}»: Inicio {m.InicioCumplimiento} %, Visión de cartera {Texto(m.VisionCarteraCumplimiento)} %, " +
                $"Empresas {Texto(m.EmpresaCumplimiento)} %; vencidos {m.InicioVencidos}, urgentes {m.InicioUrgentes}, próximos {m.InicioProximos}, " +
                $"sin confirmar {m.InicioSinConfirmar}; Centros {m.Centros.Count} (bloqueados {m.InicioCentrosBloqueados}), Trabajadores bloqueados " +
                $"{m.InicioTrabajadoresBloqueados}; Mi trabajo {m.MiTrabajoFilas} filas; documentos {m.Documentos} (sin PDF {m.DocumentosSinPdf}); " +
                $"contactos de agenda {m.ContactosDeAgenda} (fuera de la regla de correo {m.ContactosFueraDeLaReglaDeCorreo}).");

        foreach (var advertencia in resultado.Advertencias)
            salida.WriteLine($"Divergencia declarada: {advertencia}");

        if (resultado.Discrepancias.Count == 0)
        {
            salida.WriteLine("Autoverificación: lo medido es lo que la matriz declara.");
            return 0;
        }

        errores.WriteLine(
            $"Autoverificación FALLIDA: la siembra del piloto Outbound no da los resultados de su matriz ({resultado.Discrepancias.Count} discrepancias)." +
            (siembra.Escribio
                ? string.Empty
                : " Esta ejecución no ha escrito datos: el lote ya existía y sus datos ya no son los de la matriz. Para volver a ella, retirar y sembrar."));
        foreach (var discrepancia in resultado.Discrepancias)
            errores.WriteLine($" - {discrepancia}");
        return 1;
    }

    /// <summary>
    /// Lo que el modo escribe por la salida de error cuando la siembra no termina, sea cual sea
    /// la excepción. Puede ser un rechazo previo a toda escritura o un fallo a mitad, y el
    /// mensaje no los distingue: lo que ya exista lleva el marcador de demo, así que en los dos
    /// casos vale lo mismo. Lleva el tipo y el mensaje (<see cref="Motivo"/>), nunca la traza.
    /// </summary>
    public static string MensajeDeInterrupcion(Exception excepcion) =>
        $"Siembra del piloto Outbound interrumpida: {Motivo(excepcion)}{Environment.NewLine}" +
        "Si llegó a escribir, cada Tenant creado lleva el marcador de demo: corregida la causa, se reanuda " +
        $"repitiendo la orden o se retira con {PilotoOutboundRetirada.Argumento}. Si ya se había creado un fichero de " +
        "credenciales, se conserva: las contraseñas de las cuentas ya creadas solo están en el fichero de la ejecución que las creó.";

    /// <summary>
    /// El motivo de una interrupción, para la salida de error de los modos del piloto. Un rechazo
    /// previsto (<see cref="InvalidOperationException"/>) va con su mensaje tal cual; cualquier otra
    /// excepción, con su tipo delante y, si la envuelve otra, el tipo y el mensaje de la causa más
    /// interna, que es donde un fallo de base de datos o de disco dice qué pasó. Solo tipos y
    /// mensajes: ni la traza, ni <see cref="Exception.Data"/>, ni <c>ToString()</c>, que es por
    /// donde un proveedor podría volcar valores de la operación fallida.
    /// </summary>
    internal static string Motivo(Exception excepcion)
    {
        if (excepcion is InvalidOperationException && excepcion.InnerException is null)
            return excepcion.Message;

        var motivo = $"{excepcion.GetType().Name}: {excepcion.Message}";
        var causa = excepcion.GetBaseException();
        return ReferenceEquals(causa, excepcion)
            ? motivo
            : $"{motivo} Causa: {causa.GetType().Name}: {causa.Message}";
    }

    private static string Texto<T>(T valor) => valor is null ? "(nada)" : valor.ToString()!;
}
