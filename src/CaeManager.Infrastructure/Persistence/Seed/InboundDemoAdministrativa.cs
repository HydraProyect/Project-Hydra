using System.Globalization;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La siembra de la demo Inbound en un servidor que arranca como <c>Production</c> (staging): el mismo lote que
/// <see cref="InboundDemoSeeder"/> y <see cref="LineaInboundDemoSeeder"/> siembran en Development —el Tenant TCE de
/// Logística Delta Sur S.A. y los Tenants TEC de Montajes Moncayo S.L., Electricidad Ribera Baja S.L. y Limpiezas
/// Somontano S.L., con la línea Inbound del Producto Contratado del Tenant TCE—, pero como operación administrativa
/// explícita y sin la contraseña compartida de la demo. Mismo patrón que <c>PilotoOutboundAdministrativa</c>.
///
/// <para>
/// <b>Qué la separa de la siembra local.</b> (1) No usa <c>DatosPrueba:Activo</c>, <see cref="InboundDemoSeeder.ClaveConfiguracion"/>
/// ni <see cref="CredencialesDemo"/>, y se niega si están activos: cada cuenta nueva lleva una contraseña aleatoria distinta
/// que solo va a un fichero en memoria, nunca a un registro ni a la salida. (2) Las cuentas son de un dominio que controla
/// quien la lanza (<see cref="ClaveDominioCorreo"/>), nunca <c>@caemanager.local</c>. (3) Sus claves son propias
/// (<c>InboundDemo:*</c>), la confirmación del entorno y la fecha de la demostración son obligatorias, y todas las fechas
/// del lote se anclan a esa fecha. (4) El catálogo de cada Tenant se copia sin los tipos de documento de salud (D7,
/// DECISION_LOG 2026-11-17): la aptitud médica del lote local se sustituye por <see cref="InboundDemoSeeder.TipoTrabajosEnAltura"/>.
/// (5) Solo se alcanza desde el modo de línea de órdenes <see cref="ArgumentoSembrar"/> de <c>Program.cs</c>, que termina el
/// proceso antes del arranque normal (lo vigila <c>InboundDemoSoloDesdeElModoCliTests</c>). (6) Al terminar mide el lote y
/// devuelve sus discrepancias: el modo sale con 1 si no cuadra.
/// </para>
///
/// <para>
/// <b>No envía correo.</b> No llama a <see cref="IEmailService"/> ni a ningún Command: ni al alta de un Tenant, ni a la
/// contratación de la línea Inbound (<c>ContratarLineaInboundCommand</c>), ni a una invitación o a la recuperación de una
/// contraseña. Las cuentas nacen con el correo confirmado y su contraseña, por <see cref="UserManager{TUser}"/>.
/// </para>
///
/// <para>
/// <b>Falla cerrada, antes de escribir.</b> <see cref="ValidarPrecondiciones"/> no toca la base. Después, y todavía sin
/// crear el fichero de credenciales, se niega si un nombre del lote lo lleva un Tenant sin marcador de demo, si un Tenant
/// del lote trae datos de otra versión (tipos de salud en su catálogo o una Empresa propia con otro CIF), si queda algo por
/// sembrar y la fecha no sirve para sembrar hoy, o si una cuenta con un correo del lote pertenece a otro Tenant.
/// </para>
///
/// <para>
/// <b>Idempotente.</b> El núcleo (<see cref="InboundDemoSeeder.SembrarLoteInboundAsync"/>) no toca lo ya sembrado y siembra lo que
/// falta, así que una ejecución cortada se reanuda repitiendo la orden. Las cuentas que ya existen no se tocan y su
/// contraseña no se vuelve a entregar.
/// </para>
///
/// <para>
/// <b>Dos identidades, como en el arranque.</b> Todo el lote va con el contexto de tráfico, bajo RLS y dentro del ámbito de
/// su Tenant. La identidad de bootstrap se usa para dos cosas, las mismas que en el arranque de Development o en el del
/// piloto Outbound: la línea Inbound del Tenant TCE (<see cref="LineaInboundDemoSeeder.ContratarLineaAsync"/>; en
/// <c>ProductosContratados</c> cae_app_runtime solo lee) y el backfill de asignaciones
/// (<see cref="AsignacionesOperativasBackfillSeeder"/>). Por eso el modo se lanza con el servicio <c>migrador</c>.
/// </para>
///
/// <para>
/// <b>La confirmación de entorno no distingue staging de producción</b>, igual que en el piloto Outbound: los dos arrancan
/// como <c>Production</c>. Contra qué base se escribe lo deciden el fichero de composición y el de entorno.
/// </para>
/// </summary>
public static class InboundDemoAdministrativa
{
    public const string ArgumentoSembrar = "--sembrar-demo-inbound";

    public const string Seccion = "InboundDemo";
    public const string ClaveConfirmarEntorno = Seccion + ":ConfirmarEntorno";
    public const string ClaveDominioCorreo = Seccion + ":DominioCorreo";
    public const string ClaveDirectorioCredenciales = Seccion + ":DirectorioCredenciales";
    public const string ClaveFechaDemostracion = Seccion + ":FechaDemostracion";

    /// <summary>Días que la demostración puede quedar por delante de hoy: el mismo margen que el piloto Outbound.</summary>
    public const int MargenMaximoDias = 9;

    public sealed record Opciones(string DominioCorreo, string DirectorioCredenciales, string EntornoConfirmado, DateOnly FechaDemostracion);

    /// <param name="ContrasenaEntregada">True solo si esta ejecución crea la cuenta (y su contraseña está en el fichero).</param>
    public sealed record CuentaSembrada(string Email, string Rol, string NombreTenant, bool ContrasenaEntregada);

    /// <summary>Lo que quien lanza el modo puede ver y registrar: ningún dato secreto.</summary>
    /// <param name="Escribio">Si quedaba algo por sembrar al empezar.</param>
    /// <param name="Medido">Una línea por Tenant con lo medido, para la salida.</param>
    /// <param name="Discrepancias">Lo medido que no es lo que el lote declara. Vacía si cuadra.</param>
    public sealed record Resultado(
        bool Escribio, IReadOnlyList<(string Nombre, Guid Id)> Tenants, IReadOnlyList<CuentaSembrada> Cuentas,
        string FicheroCredenciales, IReadOnlyList<string> Medido, IReadOnlyList<string> Discrepancias);

    /// <summary>Lee la forma de las opciones. La fecha es obligatoria y con formato yyyy-MM-dd: no se asume «hoy».</summary>
    public static Opciones LeerOpciones(IConfiguration configuration)
    {
        var textoFecha = configuration[ClaveFechaDemostracion];
        if (string.IsNullOrWhiteSpace(textoFecha))
            throw new InvalidOperationException(
                $"Falta {ClaveFechaDemostracion}: la siembra Inbound ancla todas las fechas al día de la demostración (formato yyyy-MM-dd).");

        if (!DateOnly.TryParseExact(textoFecha.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            throw new InvalidOperationException($"{ClaveFechaDemostracion} = «{textoFecha}» no es una fecha con formato yyyy-MM-dd.");

        return new Opciones(
            configuration[ClaveDominioCorreo] ?? string.Empty,
            configuration[ClaveDirectorioCredenciales] ?? string.Empty,
            configuration[ClaveConfirmarEntorno] ?? string.Empty,
            fecha);
    }

    /// <summary>
    /// Por qué no se puede sembrar hoy con esa fecha, o <c>null</c>: tiene que ser hoy o quedar, como mucho, a
    /// <see cref="MargenMaximoDias"/> días. Solo se exige si queda algo por sembrar.
    /// </summary>
    public static string? MotivoFechaNoUtilizable(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (fechaDemostracion < hoy)
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} es anterior a hoy ({hoy:yyyy-MM-dd}): " +
                   "los documentos sembrados como vigentes ya habrían empezado a acercarse a su vencimiento.";

        if (fechaDemostracion > hoy.AddDays(MargenMaximoDias))
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} queda a más de {MargenMaximoDias} días de hoy " +
                   $"({hoy:yyyy-MM-dd}): el lote se siembra en la semana de la demostración.";

        return null;
    }

    /// <summary>Las cinco cuentas del lote en el dominio indicado, con la misma parte local que las de Development.</summary>
    public static CuentasInboundDemo CuentasDe(string dominio) => CuentasInboundDemo.Locales.EnDominio(dominio);

    // ── Precondiciones (antes de escribir nada) ─────────────────────────────

    /// <summary>
    /// Todo lo que se puede comprobar sin tocar la base. Lanza <see cref="InvalidOperationException"/> con el motivo exacto.
    /// El dominio y el directorio se validan con las reglas de <see cref="SiembraDemoDireccionAdministrativa"/>.
    /// </summary>
    /// <param name="informacionDeMontajes">Contenido de <c>/proc/self/mountinfo</c> (parámetro para poder probarlo).</param>
    public static void ValidarPrecondiciones(
        IConfiguration configuration, IHostEnvironment entorno, Opciones opciones,
        string? informacionDeMontajes, bool esLinux, UnixFileMode? modoDelDirectorio)
    {
        if (!string.Equals(opciones.EntornoConfirmado, entorno.EnvironmentName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{ClaveConfirmarEntorno} debe ser exactamente el entorno en el que se ejecuta ('{entorno.EnvironmentName}'), " +
                $"y es '{opciones.EntornoConfirmado}': se pide para que quien lanza la operación diga a mano contra qué base de datos va a escribir.");

        if (configuration.GetValue<bool>("DatosPrueba:Activo") || configuration.GetValue<bool>(InboundDemoSeeder.ClaveConfiguracion))
            throw new InvalidOperationException(
                $"Con DatosPrueba:Activo o {InboundDemoSeeder.ClaveConfiguracion} activos la siembra local escribiría con la contraseña " +
                "compartida de la demo (CredencialesDemo). Esta operación no se mezcla con ella: desactívalos para esta ejecución.");

        ConLasClavesDeEsteModo(() =>
        {
            SiembraDemoDireccionAdministrativa.ValidarDominio(opciones.DominioCorreo);
            SiembraDemoDireccionAdministrativa.ValidarDirectorioDeSalida(
                opciones.DirectorioCredenciales, informacionDeMontajes, esLinux, modoDelDirectorio);
        });
    }

    private static void ConLasClavesDeEsteModo(Action validacion)
    {
        try
        {
            validacion();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message
                .Replace(SiembraDemoDireccionAdministrativa.ClaveDominioCorreo, ClaveDominioCorreo, StringComparison.Ordinal)
                .Replace(SiembraDemoDireccionAdministrativa.ClaveDirectorioCredenciales, ClaveDirectorioCredenciales, StringComparison.Ordinal), ex);
        }
    }

    // ── Escritura ───────────────────────────────────────────────────────────

    /// <summary>
    /// La entrada del modo de CLI. Resuelve sus piezas del ámbito que le pasa <c>Program.cs</c> (el contexto de tráfico, la
    /// fábrica del de bootstrap, los gestores de Identity y el almacén), para que la capa Web no nombre los gestores de Identity.
    /// </summary>
    public static async Task<Resultado> SembrarAsync(
        IServiceProvider servicios, IConfiguration configuration, IHostEnvironment entorno, Opciones opciones, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var dbContext = servicios.GetRequiredService<CaeManagerDbContext>();
        var fabricaBootstrap = servicios.GetRequiredService<FabricaContextoDeBootstrap>();
        var userManager = servicios.GetRequiredService<UserManager<ApplicationUser>>();
        var userStore = servicios.GetRequiredService<IUserStore<ApplicationUser>>();
        var almacen = servicios.GetRequiredService<IFileStorageService>();

        var esLinux = OperatingSystem.IsLinux();
        ValidarPrecondiciones(
            configuration, entorno, opciones,
            esLinux ? await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken) : null,
            esLinux, esLinux && Directory.Exists(opciones.DirectorioCredenciales) ? File.GetUnixFileMode(opciones.DirectorioCredenciales) : null);

        return await EjecutarAsync(
            dbContext, () => fabricaBootstrap.Crear(), userManager, userStore, almacen, configuration, entorno, opciones,
            DiaDeNegocio.Hoy(), logger, cancellationToken);
    }

    /// <summary>
    /// La siembra propiamente dicha, DESPUÉS de las comprobaciones de <see cref="SembrarAsync"/> (que dependen del sistema
    /// operativo y de <c>/proc</c>, y por eso no se ejercitan en cualquier máquina de pruebas).
    /// </summary>
    /// <param name="hoy">El día de negocio de hoy, con el que se juzga la fecha de la demostración.</param>
    internal static async Task<Resultado> EjecutarAsync(
        CaeManagerDbContext dbContext, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore, IFileStorageService almacen,
        IConfiguration configuration, IHostEnvironment entorno, Opciones opciones, DateOnly hoy, ILogger logger,
        CancellationToken cancellationToken)
    {
        var cuentas = CuentasDe(opciones.DominioCorreo);
        var cuentasConSuTenant = CuentasConSuTenant(cuentas);

        // Las negativas que miran la base, antes de crear el fichero de credenciales y de escribir nada.
        await InboundDemoSeeder.RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(dbContext, cancellationToken);
        await RechazarDatosDeOtraVersionAsync(dbContext, cancellationToken);

        var pendientes = await PendientesDeSembrarAsync(dbContext, cancellationToken);
        if (pendientes.Count > 0 && MotivoFechaNoUtilizable(opciones.FechaDemostracion, hoy) is { } motivo)
            throw new InvalidOperationException(
                $"{motivo} Queda por sembrar «{pendientes[0]}», así que la siembra se niega y no escribe nada.");

        var correosNuevos = await VerificarCuentasReutilizablesAsync(dbContext, userManager, cuentasConSuTenant, cancellationToken);

        // El fichero se abre —con permisos 0600 DESDE su creación— antes de crear ninguna cuenta: si no se puede escribir,
        // no se crea nada cuya contraseña se perdería.
        var rutaFichero = Path.Combine(
            opciones.DirectorioCredenciales, $"credenciales-demo-inbound-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
        var opcionesDeFichero = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        // Windows no admite modos Unix: solo se usa en máquinas de desarrollo; el servidor es Linux (ValidarPrecondiciones lo exige).
        if (!OperatingSystem.IsWindows())
            opcionesDeFichero.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var contrasenas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in correosNuevos)
            contrasenas[email] = SiembraDemoDireccionAdministrativa.GenerarContrasena();

        var cuentasSembradas = cuentasConSuTenant
            .Select(c => new CuentaSembrada(c.Email, c.Rol, c.NombreTenant, contrasenas.ContainsKey(c.Email)))
            .ToList();

        // Las contraseñas llegan al fichero ANTES de crear ninguna cuenta: toda cuenta que exista tiene la suya entregable.
        await using (var fichero = new FileStream(rutaFichero, opcionesDeFichero))
            await EscribirCredencialesAsync(fichero, entorno.EnvironmentName, cuentasSembradas, contrasenas, cancellationToken);

        // CredencialesDemo aquí es solo el portador de la contraseña de cada cuenta nueva: nunca la compartida de la demo.
        var parametros = new InboundDemoSeeder.Parametros(
            email => new CredencialesDemo(contrasenas.GetValueOrDefault(email, string.Empty), string.Empty, string.Empty),
            cuentas, opciones.FechaDemostracion, SinTiposDeSalud: true);

        await InboundDemoSeeder.SembrarLoteInboundAsync(dbContext, userManager, userStore, almacen, entorno, parametros, logger, cancellationToken);

        // Con la identidad de bootstrap, como en el arranque: la línea Inbound del Tenant TCE (D10 y DN-7) y, como en el
        // piloto Outbound, la delegación de soporte —apagada— y el backfill de asignaciones de los Tenants recién creados.
        await using (var dbContextBootstrap = crearContextoDeBootstrap())
            await LineaInboundDemoSeeder.ContratarLineaAsync(dbContextBootstrap, logger, cancellationToken);
        await DelegacionesSoporteSeeder.SeedAsync(dbContext, configuration, logger, cancellationToken);
        await using (var dbContextBootstrap = crearContextoDeBootstrap())
            await AsignacionesOperativasBackfillSeeder.SeedAsync(dbContextBootstrap, logger, cancellationToken);

        dbContext.ChangeTracker.Clear();
        var (tenants, medido, discrepancias) = await MedirAsync(dbContext, userManager, cuentas, cancellationToken);

        // Solo cuentas y Tenants, nunca contraseñas: es lo que se registra.
        logger.LogInformation(
            "Demo Inbound sembrada en {Entorno} por la vía administrativa: {Tenants} Tenants, {Cuentas} cuentas ({Nuevas} nuevas), {Discrepancias} discrepancias.",
            entorno.EnvironmentName, tenants.Count, cuentasSembradas.Count, cuentasSembradas.Count(c => c.ContrasenaEntregada), discrepancias.Count);

        return new Resultado(pendientes.Count > 0, tenants, cuentasSembradas, rutaFichero, medido, discrepancias);
    }

    private static IReadOnlyList<(string Email, string Rol, string NombreTenant)> CuentasConSuTenant(CuentasInboundDemo cuentas) =>
    [
        (cuentas.AdministradorTce, Roles.Administrador, InboundDemoSeeder.NombreTenantTce),
        (cuentas.GestoraTce, Roles.GestorCae, InboundDemoSeeder.NombreTenantTce),
        (cuentas.UsuarioMoncayo, Roles.Administrador, InboundDemoSeeder.NombreTenantMoncayo),
        (cuentas.UsuarioRibera, Roles.Administrador, InboundDemoSeeder.NombreTenantRibera),
        (cuentas.UsuarioSomontano, Roles.Administrador, InboundDemoSeeder.NombreTenantSomontano),
    ];

    private static async Task<Dictionary<string, Guid>> TenantsDelLoteAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(t => InboundDemoSeeder.NombresTenants.Contains(t.Nombre))
            .ToDictionaryAsync(t => t.Nombre, t => t.Id, cancellationToken);

    /// <summary>
    /// Un Tenant del lote que ya existe (con su marcador: los que no lo llevan ya se han rechazado) solo se completa si sus
    /// datos son de esta versión: sin tipos de salud en su catálogo (D7) y, en un Tenant TEC, sin una Empresa propia con
    /// otro CIF. Si no, se niega con su nombre y no se escribe en ninguno.
    /// </summary>
    internal static async Task RechazarDatosDeOtraVersionAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var existentes = await TenantsDelLoteAsync(dbContext, cancellationToken);
        var deOtraVersion = new List<string>();

        foreach (var (nombre, tenantId) in existentes)
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var conSalud = await dbContext.TiposDocumento.AnyAsync(
                    t => t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud, cancellationToken);

                var cifEsperado = InboundDemoSeeder.TenantsTec.Where(t => t.NombreTenant == nombre).Select(t => InboundDemoSeeder.CifDeTec(t.Indice)).FirstOrDefault();
                var conOtroCif = cifEsperado is not null
                    && await dbContext.Empresas.AnyAsync(cancellationToken)
                    && !await dbContext.Empresas.AnyAsync(e => e.Cif == cifEsperado, cancellationToken);

                if (conSalud || conOtroCif)
                    deOtraVersion.Add(nombre);
            }

        dbContext.ChangeTracker.Clear();
        if (deOtraVersion.Count > 0)
            throw new InvalidOperationException(
                $"Los Tenants «{string.Join("», «", deOtraVersion)}» tienen datos de otra versión de la siembra Inbound (tipos de documento " +
                "de salud en su catálogo o una Empresa propia con otro CIF): hay que retirarlos (--retirar-tenant-demo) antes de sembrar. " +
                "La siembra se niega y no escribe nada.");
    }

    /// <summary>Los Tenants del lote a los que aún les falta su parte: no existen, o no tienen sus datos.</summary>
    private static async Task<IReadOnlyList<string>> PendientesDeSembrarAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var existentes = await TenantsDelLoteAsync(dbContext, cancellationToken);
        var pendientes = new List<string>();

        foreach (var nombre in InboundDemoSeeder.NombresTenants)
        {
            if (!existentes.TryGetValue(nombre, out var tenantId))
            {
                pendientes.Add(nombre);
                continue;
            }

            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var sembrado = nombre == InboundDemoSeeder.NombreTenantTce
                    ? await dbContext.HistorialVigenciaContratos.AnyAsync(cancellationToken)
                    : await dbContext.Trabajadores.AnyAsync(cancellationToken) || await dbContext.Documentos.AnyAsync(cancellationToken);
                if (!sembrado)
                    pendientes.Add(nombre);
            }
        }

        dbContext.ChangeTracker.Clear();
        return pendientes;
    }

    /// <summary>
    /// Una cuenta del lote que ya existe solo se reutiliza si pertenece al Tenant del lote en el que la siembra la crearía (una
    /// re-ejecución). Si el correo es de una cuenta de otro Tenant, la siembra no la toca: se niega antes de escribir.
    /// Devuelve los correos de las cuentas que hay que CREAR.
    /// </summary>
    private static async Task<HashSet<string>> VerificarCuentasReutilizablesAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager,
        IReadOnlyList<(string Email, string Rol, string NombreTenant)> cuentas, CancellationToken cancellationToken)
    {
        var tenantsExistentes = await TenantsDelLoteAsync(dbContext, cancellationToken);
        var nuevas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ajenas = new List<string>();

        // La pregunta es entre Tenants y AspNetUsers tiene RLS: sin este ámbito la búsqueda no vería ninguna cuenta.
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
                $"Ya existen cuentas con un correo del lote Inbound que NO pertenecen al Tenant que les corresponde: {string.Join(", ", ajenas)}. " +
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

        await JsonSerializer.SerializeAsync(fichero, contenido, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        await fichero.FlushAsync(cancellationToken);
    }

    // ── Autoverificación ────────────────────────────────────────────────────

    /// <summary>Los contratos de acceso del Tenant TCE por estado efectivo, como los deja la siembra.</summary>
    internal static readonly IReadOnlyDictionary<EstadoContratoAcceso, int> ContratosEsperados = new Dictionary<EstadoContratoAcceso, int>
    {
        [EstadoContratoAcceso.Activo] = 2,
        [EstadoContratoAcceso.PendienteDeConfirmar] = 1,
        [EstadoContratoAcceso.Suspendido] = 1,
        [EstadoContratoAcceso.Finalizado] = 1,
        [EstadoContratoAcceso.Cancelado] = 1,
        [EstadoContratoAcceso.Rechazado] = 1,
    };

    /// <summary>
    /// Mide el lote con el contexto de tráfico, dentro del ámbito de cada Tenant propietario: una fila en el Tenant equivocado
    /// no se cuenta. Devuelve los Tenants, una línea de lo medido por Tenant y las discrepancias con lo que el lote declara.
    /// </summary>
    internal static async Task<(IReadOnlyList<(string Nombre, Guid Id)> Tenants, IReadOnlyList<string> Medido, IReadOnlyList<string> Discrepancias)> MedirAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, CuentasInboundDemo cuentas, CancellationToken cancellationToken)
    {
        var existentes = await dbContext.Tenants
            .Where(t => InboundDemoSeeder.NombresTenants.Contains(t.Nombre))
            .Select(t => new { t.Nombre, t.Id, t.DatosDemoCompletadosEnUtc })
            .ToListAsync(cancellationToken);

        var tenants = InboundDemoSeeder.NombresTenants
            .SelectMany(n => existentes.Where(t => t.Nombre == n).Select(t => (t.Nombre, t.Id)))
            .ToList();
        var medido = new List<string>();
        var discrepancias = new List<string>();

        void Esperar<T>(string nombre, string que, T valor, T esperado)
        {
            if (!EqualityComparer<T>.Default.Equals(valor, esperado))
                discrepancias.Add($"«{nombre}» · {que}: medido {valor}, esperado {esperado}");
        }

        foreach (var nombre in InboundDemoSeeder.NombresTenants)
        {
            var tenant = existentes.SingleOrDefault(t => t.Nombre == nombre);
            if (tenant is null)
            {
                discrepancias.Add($"«{nombre}» · no existe");
                continue;
            }

            Esperar(nombre, "marcador de demo", tenant.DatosDemoCompletadosEnUtc is not null, true);

            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                var tiposDeSalud = await dbContext.TiposDocumento.CountAsync(
                    t => t.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud, cancellationToken);
                Esperar(nombre, "tipos de documento de salud (D7)", tiposDeSalud, 0);

                if (nombre == InboundDemoSeeder.NombreTenantTce)
                {
                    var porEstado = (await dbContext.ContratosAcceso.ToListAsync(cancellationToken))
                        .GroupBy(c => c.EstadoEfectivo).ToDictionary(g => g.Key, g => g.Count());
                    foreach (var (estado, esperado) in ContratosEsperados)
                        Esperar(nombre, $"contratos de acceso {estado}", porEstado.GetValueOrDefault(estado), esperado);

                    var plantillas = await dbContext.PlantillasRequisitos.CountAsync(cancellationToken);
                    Esperar(nombre, "plantillas de requisitos", plantillas, 2);

                    var ahora = DateTime.UtcNow;
                    var lineaVigente = (await dbContext.ProductosContratados
                            .Where(p => p.Linea == LineaProductoContratado.Inbound).ToListAsync(cancellationToken))
                        .Any(p => p.EstaVigenteEn(ahora));
                    Esperar(nombre, "línea Inbound vigente", lineaVigente, true);

                    medido.Add($"  Tenant TCE «{nombre}»: contratos de acceso {porEstado.Values.Sum()}, plantillas {plantillas}, " +
                               $"línea Inbound vigente {(lineaVigente ? "sí" : "no")}, tipos de salud {tiposDeSalud}.");
                }
                else
                {
                    var trabajadores = await dbContext.Trabajadores.CountAsync(cancellationToken);
                    var documentos = await dbContext.Documentos.Operativos().CountAsync(cancellationToken);
                    var sinArchivo = await dbContext.Documentos.Operativos().CountAsync(d => d.ArchivoUrl == null, cancellationToken);
                    var autorizaciones = await dbContext.AutorizacionesLecturaDocumental.ToListAsync(cancellationToken);
                    var esMoncayo = nombre == InboundDemoSeeder.NombreTenantMoncayo;

                    Esperar(nombre, "Trabajadores", trabajadores, 3);
                    Esperar(nombre, "Documentos", documentos, 9);
                    Esperar(nombre, "autorizaciones de lectura", autorizaciones.Count, esMoncayo ? 2 : 0);
                    Esperar(nombre, "autorizaciones de lectura revocadas", autorizaciones.Count(a => a.EstaRevocada), esMoncayo ? 1 : 0);
                    if (esMoncayo)
                        Esperar(nombre, "Documentos sin el PDF de prueba", sinArchivo, 0);

                    medido.Add($"  Tenant TEC «{nombre}»: Trabajadores {trabajadores}, Documentos {documentos} (sin PDF {sinArchivo}), " +
                               $"autorizaciones de lectura {autorizaciones.Count} (revocadas {autorizaciones.Count(a => a.EstaRevocada)}), tipos de salud {tiposDeSalud}.");
                }
            }

            dbContext.ChangeTracker.Clear();
        }

        foreach (var (email, rol, nombreTenant) in CuentasConSuTenant(cuentas))
        {
            if (existentes.SingleOrDefault(t => t.Nombre == nombreTenant) is not { } tenant)
                continue;

            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                var usuario = await userManager.FindByEmailAsync(email);
                if (usuario is null || usuario.TenantId != tenant.Id)
                    discrepancias.Add($"«{nombreTenant}» · cuenta {email}: no existe en su Tenant");
                else if (!await userManager.IsInRoleAsync(usuario, rol))
                    discrepancias.Add($"«{nombreTenant}» · cuenta {email}: sin el rol {rol}");
            }
        }

        return (tenants, medido, discrepancias);
    }

    // ── Salida ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Escribe los Tenants, las cuentas, el fichero y lo medido, y —si el lote no cuadra— cada discrepancia. Devuelve el
    /// código de salida del modo: 0 si cuadra, 1 si no. Nunca escribe una contraseña: no las tiene.
    /// </summary>
    public static int Informar(Resultado resultado, string nombreDelEntorno, TextWriter salida, TextWriter errores)
    {
        salida.WriteLine(resultado.Escribio
            ? $"Demo Inbound sembrada en {nombreDelEntorno}: {resultado.Tenants.Count} Tenants."
            : $"Demo Inbound en {nombreDelEntorno}: el lote ya estaba sembrado entero y esta ejecución no ha escrito datos.");

        foreach (var (nombre, id) in resultado.Tenants)
            salida.WriteLine($"  Tenant {id}  {nombre}");
        foreach (var cuenta in resultado.Cuentas)
            salida.WriteLine(
                $"  Cuenta {cuenta.Email}  {cuenta.Rol}  en «{cuenta.NombreTenant}»  contraseña en el fichero: {(cuenta.ContrasenaEntregada ? "sí" : "no (ya existía)")}");
        salida.WriteLine($"Credenciales: {resultado.FicheroCredenciales} (entregar y destruir).");

        salida.WriteLine("Medido por Tenant propietario:");
        foreach (var linea in resultado.Medido)
            salida.WriteLine(linea);

        if (resultado.Discrepancias.Count == 0)
        {
            salida.WriteLine("Autoverificación: el lote es el que la siembra declara.");
            return 0;
        }

        errores.WriteLine($"Autoverificación FALLIDA: la demo Inbound no es el lote que la siembra declara ({resultado.Discrepancias.Count} discrepancias).");
        foreach (var discrepancia in resultado.Discrepancias)
            errores.WriteLine($" - {discrepancia}");
        return 1;
    }

    /// <summary>
    /// Lo que el modo escribe por la salida de error cuando la siembra no termina, sea cual sea la excepción: el tipo y el
    /// mensaje (<see cref="Motivo"/>), nunca la traza ni los datos de la excepción.
    /// </summary>
    public static string MensajeDeInterrupcion(Exception excepcion) =>
        $"Siembra de la demo Inbound interrumpida: {Motivo(excepcion)}{Environment.NewLine}" +
        "Si llegó a escribir, cada Tenant creado lleva el marcador de demo: corregida la causa, se reanuda repitiendo la orden, " +
        "o se retira cada Tenant con --retirar-tenant-demo <TenantId>. Si ya se había creado un fichero de credenciales, se " +
        "conserva: las contraseñas de las cuentas ya creadas solo están en el fichero de la ejecución que las creó.";

    /// <summary>
    /// El motivo de una interrupción, con la regla de los modos del piloto Outbound (copiada, no referida: la siembra Inbound
    /// no depende del módulo Outbound). Un rechazo previsto (<see cref="InvalidOperationException"/>) va con su mensaje tal
    /// cual; cualquier otra excepción, con su tipo delante y, si la envuelve otra, el tipo y el mensaje de la causa más
    /// interna. Solo tipos y mensajes: ni la traza, ni <see cref="Exception.Data"/>, ni <c>ToString()</c>.
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
}
