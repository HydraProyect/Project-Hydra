using System.Diagnostics;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra del piloto del Servicio TALVEG Outbound: un Operador CAE externo de
/// demostración con Asignación de Operación sobre siete Tenants propietarios
/// (<see cref="CatalogoPilotoOutbound"/>), cada uno con un estado objetivo
/// declarado y comprobable el día de la demostración. No retoca ninguna otra
/// siembra: es un lote aparte, con sus propios Tenants.
///
/// <para>
/// <b>Inerte por defecto.</b> <see cref="SeedAsync"/> corre solo con
/// <c>DatosPrueba:Activo</c> y <see cref="OpcionesPilotoOutbound.ClaveActivo"/> a
/// la vez, y en Producción <b>lanza</b>: usa la contraseña compartida de
/// <see cref="CredencialesDemo"/>. <see cref="SembrarLoteAsync"/> es el núcleo, sin
/// esa guarda ni esa contraseña: recibe las credenciales por cuenta, las
/// direcciones de las cuentas, la fecha y el correo de los contactos, para que una
/// vía administrativa con contraseñas propias pueda reutilizarlo.
/// </para>
///
/// <para>
/// <b>Interrumpible.</b> Cada Tenant lleva el marcador de datos de demo desde que
/// se aprovisiona, así que lo que deje una ejecución cortada se puede retirar
/// (<see cref="PilotoOutboundRetirada.RetirarLoteAsync"/>). Los datos de cada Tenant propietario se guardan
/// en un único <c>SaveChangesAsync</c>; sus PDF se escriben antes, de uno en uno, y
/// si el trabajo del Tenant termina con cualquier excepción se eliminan. La
/// idempotencia mira las Empresas del Tenant (<see cref="DatosSembrados"/>): si
/// está la Empresa propia con el identificador fiscal que esta versión le da, el
/// Tenant está sembrado y no se toca; si no tiene ninguna Empresa, se siembra
/// entero. Por eso una ejecución cortada también se puede reanudar sin retirar.
/// </para>
///
/// <para>
/// <b>Un lote de otra versión no se completa ni se duplica.</b> Un Tenant
/// propietario que tiene Empresas y ninguna con ese identificador fiscal lleva
/// datos sembrados por otra versión de la siembra: en él no se escribe nada. El
/// arranque lo avisa en el registro y sigue con los demás Tenants; la vía
/// administrativa se niega antes de escribir en ninguno
/// (<see cref="RechazarDatosDeOtraVersionAsync"/>). Para volver a sembrarlo hay
/// que retirar antes el lote. La autoverificación del arranque tampoco le exige
/// la matriz, y lo que hace con él depende de si la ejecución ha escrito. La que
/// escribe otros Tenants no lo mide, y avisa de que no lo mide. Un re-arranque
/// que no escribe nada sí lo mide, como a los demás, y de lo que descuadre solo
/// avisa. Qué Tenants son se decide aquí, antes de escribir
/// nada, y viaja en el resultado (<see cref="Resultado.TenantsConDatosDeOtraVersion"/>):
/// la autoverificación no vuelve a preguntarlo sobre los datos que verifica.
/// </para>
///
/// <para>
/// <b>Instrucción de tratamiento de IA.</b> Los siete Tenants reciben una, vigente y
/// marcada como borrador de demostración (<see cref="SembrarInstruccionTratamientoIaAsync"/>):
/// sin ella el Asistente IA falla cerrado para toda cuenta que los tenga en su cartera.
/// La autoverificación lo exige para la Gestora CAE y la Coordinadora CAE del piloto.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización. Cada escritura va dentro del
/// <see cref="AmbitoTenantExplicito"/> de su Tenant; la operación y las carteras
/// pasan por el mismo <see cref="AsignacionesOperativasWriter"/> que usa la
/// aplicación; los PDF, por el mismo <see cref="IFileStorageService"/>.
/// </para>
/// </summary>
public static class PilotoOutboundSeeder
{
    /// <param name="CredencialesDe">Contraseña de cada cuenta por su email: la misma para todas en la demo local, una por cuenta en una vía administrativa.</param>
    internal sealed record Parametros(
        Func<string, CredencialesDemo> CredencialesDe, CuentasPilotoOutbound Cuentas, DateOnly FechaDemostracion,
        ContactosPilotoOutbound Contactos);

    /// <param name="Escribio">
    /// Falso en un re-arranque que no encontró nada que sembrar: todo sembrado ya, o lo que falta es de un Tenant con datos
    /// de otra versión. Añadir a un Tenant ya sembrado la instrucción de tratamiento de IA que le faltaba no cuenta: no cambia
    /// ningún dato de la matriz, y contarlo haría exigirla en un re-arranque sobre datos que un ensayo ya cambió.
    /// </param>
    /// <param name="TenantsConDatosNuevos">Nombres de los Tenants propietarios cuyos datos escribió esta ejecución.</param>
    /// <param name="TenantsConDatosDeOtraVersion">
    /// Nombres de los Tenants propietarios que esta ejecución encontró con datos de otra versión de la siembra, leídos
    /// antes de escribir nada: en ellos no escribió. Es la lista con la que decide la autoverificación del arranque.
    /// </param>
    public sealed record Resultado(
        bool Escribio, IReadOnlyList<string> TenantsConDatosNuevos, IReadOnlyList<string> TenantsConDatosDeOtraVersion,
        int Documentos, int Pdf, TimeSpan Duracion);

    /// <summary>
    /// La guarda de Producción, aparte de <see cref="SeedAsync"/> para que el arranque la
    /// invoque antes de cualquier otra siembra: un rechazo tiene que ser previo a toda
    /// escritura. Inerte si la siembra no está activa.
    /// </summary>
    public static void RechazarEnProduccion(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!OpcionesPilotoOutbound.Activo(configuration))
            return;

        if (entorno.IsProduction())
            throw new InvalidOperationException(
                $"{OpcionesPilotoOutbound.ClaveActivo} no puede activarse en Producción: esta siembra usa la contraseña " +
                "compartida de la demo local (CredencialesDemo). A un servidor accesible desde Internet se llega por una " +
                "siembra administrativa con contraseñas únicas entregadas fuera de banda.");
    }

    /// <summary>La siembra del arranque en local. Devuelve <c>null</c> si está inactiva.</summary>
    public static async Task<Resultado?> SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        IFileStorageService almacen,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!OpcionesPilotoOutbound.Activo(configuration))
            return null;

        RechazarEnProduccion(configuration, entorno);

        var opciones = OpcionesPilotoOutbound.Leer(configuration);
        var credenciales = CredencialesDemo.Resolver(configuration, entorno);

        return await SembrarLoteAsync(
            dbContext, userManager, userStore, entorno, almacen,
            new Parametros(_ => credenciales, CuentasPilotoOutbound.Locales, opciones.FechaDemostracion, opciones.Contactos),
            logger, cancellationToken);
    }

    /// <summary>
    /// El lote entero, idempotente y sin guarda de entorno. Antes de escribir nada
    /// comprueba que ningún nombre del piloto esté ocupado por un Tenant sin
    /// marcador de demo, qué Tenants propietarios llevan datos de otra versión de
    /// la siembra y que la fecha de la demostración sirva para sembrar hoy.
    ///
    /// <para>
    /// Un Tenant propietario con datos de otra versión se deja como está: se avisa
    /// en el registro, con su nombre, y no se escribe nada en él —ni sus datos, ni
    /// su Asignación de Operación, ni su instrucción de tratamiento de IA, ni sus cuentas—; los demás siguen su curso. Aquí
    /// no lanza, para no tumbar el arranque; la vía administrativa se niega antes
    /// de llegar (<see cref="RechazarDatosDeOtraVersionAsync"/>). Tampoco lo tumba
    /// después la autoverificación: aunque esta ejecución escriba otros Tenants, ese
    /// no lo mide. La lista de esos Tenants, tal como se lee aquí antes de escribir,
    /// va en el resultado.
    /// </para>
    ///
    /// <para>
    /// La fecha solo se exige si hay algo que escribir. Sin nada por sembrar
    /// —el re-arranque del día siguiente a la demostración, con la clave todavía
    /// activa— una fecha fuera de margen no tumba el arranque: se avisa en el
    /// registro y no se escribe nada, tampoco la pasada idempotente.
    /// </para>
    /// </summary>
    internal static async Task<Resultado> SembrarLoteAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore,
        IHostEnvironment entorno, IFileStorageService almacen, Parametros parametros, ILogger logger,
        CancellationToken cancellationToken)
    {
        await RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(dbContext, cancellationToken);

        var deOtraVersion = await TenantsConDatosDeOtraVersionAsync(dbContext, cancellationToken);
        foreach (var nombre in deOtraVersion)
            logger.LogWarning(
                "Siembra del piloto Outbound: {Motivo} En ese Tenant no se escribe nada y el arranque continúa.",
                MensajeDeDatosDeOtraVersion([nombre]));

        // Un lote de una versión anterior sigue en la base: sembrar encima chocaría con sus cuentas. No se escribe nada
        // hasta que se retire (el aviso de arriba ya lo ha dicho).
        if (deOtraVersion.Any(CatalogoPilotoOutbound.NombresTenantsDeVersionesAnteriores.Contains))
            return new Resultado(false, [], deOtraVersion, 0, 0, TimeSpan.Zero);

        if (OpcionesPilotoOutbound.MotivoFechaNoUtilizable(parametros.FechaDemostracion, DiaDeNegocio.Hoy()) is { } motivo)
        {
            if (await PrimerTenantSinSembrarAsync(dbContext, cancellationToken) is { } pendiente)
                throw new InvalidOperationException($"{motivo} Queda por sembrar «{pendiente}», así que la siembra se niega y no escribe nada.");

            logger.LogWarning(
                "Siembra del piloto Outbound: {Motivo} No queda nada por sembrar, así que no se escribe nada y el " +
                "arranque continúa; los estados de los documentos ya no son los del día de la demostración.", motivo);
            return new Resultado(false, [], deOtraVersion, 0, 0, TimeSpan.Zero);
        }

        var cronometro = Stopwatch.StartNew();
        var recuento = new Recuento();

        var tenantOperadorId = await AprovisionarConMarcadorAsync(
            dbContext, CatalogoPilotoOutbound.NombreTenantOperador, PerfilVocabularioTenant.Consultora,
            esOperadorCaeExterno: true, recuento, logger, cancellationToken);

        var equipo = await SembrarEquipoAsync(
            dbContext, userManager, userStore, entorno, parametros, logger, tenantOperadorId, cancellationToken);

        await SembrarInstruccionTratamientoIaAsync(
            dbContext, tenantOperadorId, CatalogoPilotoOutbound.NombreTenantOperador, equipo.Administrador.Id, logger, cancellationToken);

        foreach (var tenant in CatalogoPilotoOutbound.EnOrdenDeSiembra)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ya avisado arriba: de un Tenant con datos de otra versión no se toca nada.
            if (deOtraVersion.Contains(tenant.Nombre))
                continue;

            var tenantPropietarioId = await AprovisionarConMarcadorAsync(
                dbContext, tenant.Nombre, PerfilVocabularioTenant.ClienteDirecto,
                esOperadorCaeExterno: false, recuento, logger, cancellationToken);

            // Antes de abrir la operación: el asistente falla cerrado con un solo Tenant propietario de la cartera sin
            // instrucción, así que ninguno entra en la cartera de un Gestor CAE sin la suya.
            await SembrarInstruccionTratamientoIaAsync(
                dbContext, tenantPropietarioId, tenant.Nombre, equipo.Administrador.Id, logger, cancellationToken);

            await AbrirOperacionExternaAsync(
                dbContext, tenant, tenantPropietarioId, tenantOperadorId, equipo, logger, cancellationToken);

            if (ReferenceEquals(tenant, CatalogoPilotoOutbound.TenantConCuentasPropias))
                await CrearCuentaAsync(
                    dbContext, userManager, userStore, entorno, parametros, logger, tenantPropietarioId,
                    parametros.Cuentas.AdministradorT1, CatalogoPilotoOutbound.NombreAdministradorT1, Roles.Administrador, cancellationToken);

            if (await SembrarDatosAsync(
                    dbContext, almacen, tenant, tenantPropietarioId, parametros, equipo.GestorDeReferencia(tenant), recuento, cancellationToken))
                recuento.TenantsConDatosNuevos.Add(tenant.Nombre);

            // La cuenta opcional de la matriz, después de los datos: su alcance es el primer Cliente empresarial de T1,
            // que tiene que existir. Idempotente, como las demás cuentas.
            if (ReferenceEquals(tenant, CatalogoPilotoOutbound.TenantConCuentasPropias))
                await CrearCuentaAsync(
                    dbContext, userManager, userStore, entorno, parametros, logger, tenantPropietarioId,
                    parametros.Cuentas.UsuarioDeClienteEmpresarialT1, CatalogoPilotoOutbound.NombreUsuarioDeClienteEmpresarialT1,
                    Roles.Cliente, cancellationToken, cifDeSuClienteEmpresarial: CifDe(tenant, 1));

            // Un Tenant a la vez en memoria: lo rastreado de este no acompaña al siguiente.
            dbContext.ChangeTracker.Clear();
        }

        cronometro.Stop();
        var resultado = new Resultado(
            recuento.TenantsCreados > 0 || recuento.TenantsConDatosNuevos.Count > 0,
            recuento.TenantsConDatosNuevos, deOtraVersion, recuento.Documentos, recuento.Pdf, cronometro.Elapsed);

        if (resultado.Escribio)
        {
            using var proceso = Process.GetCurrentProcess();
            logger.LogInformation(
                "Siembra del piloto Outbound: {TenantsCreados} Tenants creados, {TenantsConDatos} con datos nuevos, " +
                "{Documentos} documentos y {Pdf} PDF escritos en {Segundos:F1} s; memoria pico del proceso {PicoMiB} MiB.",
                recuento.TenantsCreados, resultado.TenantsConDatosNuevos.Count, resultado.Documentos, resultado.Pdf,
                resultado.Duracion.TotalSeconds, proceso.PeakWorkingSet64 / (1024 * 1024));
        }

        return resultado;
    }

    private sealed class Recuento
    {
        public int TenantsCreados { get; set; }
        public List<string> TenantsConDatosNuevos { get; } = [];
        public int Documentos { get; set; }
        public int Pdf { get; set; }
    }

    private sealed record Equipo(ApplicationUser Administrador, Guid GestoraPrimeraId, Guid GestorSegundoId, Guid CoordinadoraId)
    {
        /// <summary>Los Gestores CAE con la Asignación de Cartera del Tenant entero: uno, o los dos en el compartido.</summary>
        public IReadOnlyList<Guid> GestoresDe(TenantPilotoOutbound tenant) =>
        [
            .. tenant.EnCarteraDeLaGestoraPrimera ? [GestoraPrimeraId] : Array.Empty<Guid>(),
            .. tenant.EnCarteraDelGestorSegundo ? [GestorSegundoId] : Array.Empty<Guid>()
        ];

        /// <summary>
        /// El Gestor CAE que figura como referencia en los datos del Tenant (gestor del Cliente empresarial, remitente de
        /// las reclamaciones, quien anotó las verificaciones externas): el primero de su cartera.
        /// </summary>
        public Guid GestorDeReferencia(TenantPilotoOutbound tenant) => GestoresDe(tenant)[0];
    }

    /// <summary>
    /// Un nombre del piloto ocupado por un Tenant sin marcador de demo puede ser un
    /// Tenant real que se llama igual: no se escribe en él ni a su lado.
    /// </summary>
    internal static async Task RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(
        CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var sinMarcador = await dbContext.Tenants
            .Where(t => nombres.Contains(t.Nombre) && t.DatosDemoCompletadosEnUtc == null)
            .Select(t => t.Nombre).OrderBy(n => n).ToListAsync(cancellationToken);

        if (sinMarcador.Count > 0)
            throw new InvalidOperationException(
                $"Ya existe un Tenant llamado «{string.Join("», «", sinMarcador)}» que NO lleva el marcador de datos de demo: " +
                "podría ser un Tenant real con ese nombre. La siembra del piloto se niega y no escribe nada.");
    }

    /// <summary>
    /// El primer Tenant del lote que una ejecución todavía escribiría, o <c>null</c>
    /// si no queda ninguno: el del Operador CAE externo tiene que existir, y cada
    /// Tenant propietario, existir y tener datos. Uno con datos de otra versión de
    /// la siembra no cuenta: en él no se va a escribir
    /// (<see cref="TenantsConDatosDeOtraVersionAsync"/>). Solo lee.
    /// </summary>
    internal static async Task<string?> PrimerTenantSinSembrarAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var existentes = await TenantsExistentesAsync(dbContext, cancellationToken);

        if (!existentes.ContainsKey(CatalogoPilotoOutbound.NombreTenantOperador))
            return CatalogoPilotoOutbound.NombreTenantOperador;

        foreach (var tenant in CatalogoPilotoOutbound.EnOrdenDeSiembra)
        {
            if (!existentes.TryGetValue(tenant.Nombre, out var tenantId)
                || await DatosSembradosAsync(dbContext, tenant, tenantId, cancellationToken) == DatosSembrados.Ninguno)
                return tenant.Nombre;
        }

        return null;
    }

    /// <summary>
    /// Los Tenants propietarios del lote, en orden de siembra, que llevan datos
    /// sembrados por otra versión de la siembra (<see cref="DatosSembrados.DeOtraVersion"/>).
    /// Solo lee, y es lo primero que mira una ejecución después de los nombres.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> TenantsConDatosDeOtraVersionAsync(
        CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var existentes = await TenantsExistentesAsync(dbContext, cancellationToken);
        var deOtraVersion = new List<string>();

        foreach (var tenant in CatalogoPilotoOutbound.EnOrdenDeSiembra)
        {
            if (existentes.TryGetValue(tenant.Nombre, out var tenantId)
                && await DatosSembradosAsync(dbContext, tenant, tenantId, cancellationToken) == DatosSembrados.DeOtraVersion)
                deOtraVersion.Add(tenant.Nombre);
        }

        // Los Tenants de versiones anteriores del catálogo (con otros nombres) que sigan en la base: esta versión no los
        // siembra, pero sus cuentas (mismos correos) chocarían con las del Tenant que ahora las lleva.
        deOtraVersion.AddRange(CatalogoPilotoOutbound.NombresTenantsDeVersionesAnteriores.Where(existentes.ContainsKey));

        return deOtraVersion;
    }

    /// <summary>
    /// La negativa de la vía administrativa: si algún Tenant propietario lleva datos
    /// de otra versión de la siembra, lanza con sus nombres, antes de escribir nada
    /// en ningún Tenant. Quien lanza la orden mira el código de salida: un aviso con
    /// salida 0, como el del arranque, se leería como «sembrado».
    /// </summary>
    internal static async Task RechazarDatosDeOtraVersionAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var deOtraVersion = await TenantsConDatosDeOtraVersionAsync(dbContext, cancellationToken);

        if (deOtraVersion.Count > 0)
            throw new InvalidOperationException(
                $"{MensajeDeDatosDeOtraVersion(deOtraVersion)} La siembra del piloto se niega y no escribe nada.");
    }

    /// <summary>Lo que dicen, con los mismos nombres y la misma instrucción, el aviso del arranque y la negativa de la vía administrativa.</summary>
    public static string MensajeDeDatosDeOtraVersion(IReadOnlyList<string> nombresDeTenant) =>
        (nombresDeTenant.Count == 1
            ? $"El Tenant «{nombresDeTenant[0]}» tiene"
            : $"Los Tenants «{string.Join("», «", nombresDeTenant)}» tienen") +
        " datos sembrados por otra versión de la siembra del piloto: hay Empresas y ninguna lleva el identificador fiscal " +
        $"que esta versión da a la Empresa propia. Hay que retirar el lote ({PilotoOutboundRetirada.Argumento}) antes de volver a sembrar.";

    /// <summary>Qué hay sembrado en un Tenant propietario del piloto, según sus Empresas.</summary>
    internal enum DatosSembrados
    {
        /// <summary>Ninguna Empresa: recién aprovisionado, o con una siembra cortada antes de su único guardado. Se siembra.</summary>
        Ninguno,

        /// <summary>Está la Empresa propia con el identificador fiscal que esta versión le da. No se toca.</summary>
        DeEstaVersion,

        /// <summary>
        /// Hay Empresas y ninguna con ese identificador fiscal, o el Tenant grande no tiene el documento renovado: las
        /// sembró otra versión. No se toca y se dice.
        /// </summary>
        DeOtraVersion,
    }

    /// <summary>
    /// La pregunta de la idempotencia, dentro del ámbito del Tenant propietario. Mira
    /// las Empresas porque nada las crea antes que los datos: ni aprovisionar el
    /// Tenant (su fila, sus parámetros y su catálogo de tipos), ni la Asignación de
    /// Operación, ni las cuentas. Un Tenant sin ninguna está por sembrar. Solo lee.
    /// </summary>
    private static async Task<DatosSembrados> DatosSembradosAsync(
        CaeManagerDbContext dbContext, TenantPilotoOutbound tenant, Guid tenantPropietarioId, CancellationToken cancellationToken)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantPropietarioId);

        var cifEmpresaPropia = CifDe(tenant, 0);
        if (await dbContext.Empresas.AnyAsync(e => e.Cif == cifEmpresaPropia, cancellationToken))
        {
            return DatosSembrados.DeEstaVersion;
        }

        return await dbContext.Empresas.AnyAsync(cancellationToken) ? DatosSembrados.DeOtraVersion : DatosSembrados.Ninguno;
    }

    private static async Task<Dictionary<string, Guid>> TenantsExistentesAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var existentes = new Dictionary<string, Guid>();

        // El nombre de un Tenant no es único en la base: con dos iguales se mira uno, sin lanzar, como hace el aprovisionamiento.
        foreach (var tenant in await dbContext.Tenants
                     .Where(t => nombres.Contains(t.Nombre)).Select(t => new { t.Nombre, t.Id }).ToListAsync(cancellationToken))
            existentes.TryAdd(tenant.Nombre, tenant.Id);

        return existentes;
    }

    /// <summary>El identificador fiscal de una Empresa del piloto: el ordinal 0 es la Empresa propia del Tenant.</summary>
    private static string CifDe(TenantPilotoOutbound tenant, int ordinal) =>
        IdentidadesPilotoOutbound.Cif(tenant, ordinal);

    /// <summary>
    /// Aprovisiona el Tenant ya marcado como demo, en un único guardado: lo que quede
    /// de una ejecución cortada tiene que poder retirarse, y un Tenant con nombre del
    /// piloto y sin marcador haría que siembra y retirada se negaran. Al que ya existe
    /// no se le toca: <see cref="RechazarNombresOcupadosPorUnTenantSinMarcadorAsync"/>
    /// ya ha comprobado, antes de escribir nada, que lleva el marcador.
    /// </summary>
    private static async Task<Guid> AprovisionarConMarcadorAsync(
        CaeManagerDbContext dbContext, string nombre, PerfilVocabularioTenant perfil, bool esOperadorCaeExterno,
        Recuento recuento, ILogger logger, CancellationToken cancellationToken)
    {
        var existia = await dbContext.Tenants.AnyAsync(t => t.Nombre == nombre, cancellationToken);

        var tenantId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
            dbContext, nombre, perfil, logger, cancellationToken, esOperadorCaeExterno, marcarComoDemo: true);

        if (!existia) recuento.TenantsCreados++;
        return tenantId;
    }

    private static async Task<Equipo> SembrarEquipoAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore,
        IHostEnvironment entorno, Parametros parametros, ILogger logger, Guid tenantOperadorId, CancellationToken cancellationToken)
    {
        Task<ApplicationUser> CrearAsync(string email, string nombre, string rol) =>
            CrearCuentaAsync(dbContext, userManager, userStore, entorno, parametros, logger, tenantOperadorId, email, nombre, rol, cancellationToken);

        var cuentas = parametros.Cuentas;
        var administrador = await CrearAsync(cuentas.AdministradorOperador, CatalogoPilotoOutbound.NombreAdministradorOperador, Roles.Administrador);
        // La Dirección CAE del Operador CAE externo: sin cartera propia, su alcance es el del rol (ve todo el Operador CAE externo).
        await CrearAsync(cuentas.DireccionCae, CatalogoPilotoOutbound.NombreDireccionCae, Roles.DireccionCae);
        var coordinadora = await CrearAsync(cuentas.Coordinadora, CatalogoPilotoOutbound.NombreCoordinadora, Roles.CoordinadorCae);
        var gestoraPrimera = await CrearAsync(cuentas.GestoraPrimera, CatalogoPilotoOutbound.NombreGestoraPrimera, Roles.GestorCae);
        var gestorSegundo = await CrearAsync(cuentas.GestorSegundo, CatalogoPilotoOutbound.NombreGestorSegundo, Roles.GestorCae);

        // El alcance del Coordinador CAE sale de quién le tiene como coordinador
        // (AlcanceDatosService), no de un rol ni de una cartera propia.
        using (AmbitoTenantExplicito.Establecer(tenantOperadorId))
        {
            foreach (var gestor in new[] { gestoraPrimera, gestorSegundo })
            {
                if (gestor.CoordinadorUsuarioId == coordinadora.Id) continue;

                gestor.CoordinadorUsuarioId = coordinadora.Id;
                var resultado = await userManager.UpdateAsync(gestor);
                if (!resultado.Succeeded)
                    throw new InvalidOperationException(
                        "No se pudo vincular a un Gestor CAE del piloto con su Coordinadora CAE: " +
                        string.Join(", ", resultado.Errors.Select(e => e.Description)));
            }

            await CrearCuentaPendienteDeActivacionAsync(userManager, logger, tenantOperadorId, cuentas.GestorPendienteDeActivacion, coordinadora.Id);
        }


        return new Equipo(administrador, gestoraPrimera.Id, gestorSegundo.Id, coordinadora.Id);
    }

    /// <summary>
    /// La octava cuenta: un Gestor CAE del equipo de la Coordinadora CAE dado de alta y todavía sin activar, como
    /// queda tras el alta de la aplicación (<c>GestionCuentasUsuarioIdentity.CrearAsync</c>): sin contraseña, sin login
    /// externo y sin cambio de contraseña pendiente. Es lo que la aplicación entiende por «pendiente de activación».
    ///
    /// <para>
    /// <b>No genera enlace ni envía correo.</b> El alta de la aplicación tampoco lo hace por sí sola: el enlace lo
    /// genera aparte <c>GenerarActivacionUsuarioCommand</c>, y aquí no se llama. Sin aceptación de términos, que la
    /// persona aún no ha visto, y sin cartera: hasta que entre no opera nada. Idempotente por su correo: si ya existe
    /// —activada o no en un ensayo— no se toca.
    /// </para>
    /// </summary>
    private static async Task CrearCuentaPendienteDeActivacionAsync(
        UserManager<ApplicationUser> userManager, ILogger logger, Guid tenantOperadorId, string email, Guid coordinadoraId)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
            return;

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = CatalogoPilotoOutbound.NombreGestorPendienteDeActivacion,
            EmailConfirmed = true,
            TenantId = tenantOperadorId,
            CoordinadorUsuarioId = coordinadoraId,
            DebeCambiarContrasena = false,
        };

        var resultado = await userManager.CreateAsync(usuario);
        if (resultado.Succeeded)
            resultado = await userManager.AddToRoleAsync(usuario, Roles.GestorCae);
        if (!resultado.Succeeded)
            throw new InvalidOperationException(
                "No se pudo sembrar la cuenta del piloto pendiente de activación: " +
                string.Join(", ", resultado.Errors.Select(e => e.Description)));

        logger.LogInformation("Piloto Outbound: cuenta de Gestor CAE pendiente de activación creada, sin enlace ni correo.");
    }

    /// <summary>
    /// La instrucción de tratamiento de IA vigente de un Tenant del piloto: sin ella el
    /// Nivel 0 (<c>IInstruccionTratamientoIaService</c>) bloquea todo tratamiento con IA en
    /// ese Tenant, y el Asistente IA falla cerrado para cualquier cuenta que lo tenga en su
    /// cartera (<c>ComprobarInstruccionIaCarteraQuery</c>). La llevan todos: el Tenant
    /// del Operador CAE externo y cada Tenant propietario.
    ///
    /// <para>
    /// <b>Es un borrador de demostración, nunca una aceptación real.</b> Mismas versiones
    /// Draft y mismo origen que la siembra de demo
    /// (<see cref="DatosPruebaSeeder.SembrarInstruccionTratamientoIaTenantPrincipalAsync"/>):
    /// ningún Tenant propietario del piloto ha aceptado nada, y los siete llevan el marcador
    /// de datos de demo. Como allí, «quien la registró» es una cuenta de la propia siembra
    /// —aquí, el Administrador del Tenant del Operador CAE externo—, no un Actor de
    /// Plataforma TALVEG.
    /// </para>
    ///
    /// <para>
    /// Idempotente y aparte de los datos del Tenant: mira solo si el Tenant tiene ya alguna
    /// instrucción, así que también la añade a un Tenant que esta versión sembró antes de
    /// que existiera este paso, sin tocar nada más de él. Cuenta también una revocada: si
    /// alguien la retiró en un ensayo —para enseñar el fallo cerrado—, la siembra no la
    /// repone a sus espaldas en el siguiente arranque. A un Tenant con datos de otra versión
    /// no se llega (el bucle de <see cref="SembrarLoteAsync"/> lo salta antes), y con la
    /// fecha de la demostración fuera de margen y nada por sembrar tampoco se escribe: esa
    /// salida de <see cref="SembrarLoteAsync"/> es anterior. La retirada la borra con el
    /// resto de filas del Tenant.
    /// </para>
    /// </summary>
    private static async Task SembrarInstruccionTratamientoIaAsync(
        CaeManagerDbContext dbContext, Guid tenantId, string nombreTenant, Guid registradaPorUsuarioId, ILogger logger,
        CancellationToken cancellationToken)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        if (await dbContext.InstruccionesTratamientoIaTenantPropietario.AnyAsync(cancellationToken))
            return;

        dbContext.InstruccionesTratamientoIaTenantPropietario.Add(new InstruccionTratamientoIaTenantPropietario(
            DatosPruebaSeeder.VersionDpaDemo, DatosPruebaSeeder.VersionAnexoSubencargadosDemo, DateTime.UtcNow,
            OrigenInstruccionTratamientoIa.AltaManualPlataforma, registradaPorUsuarioId));
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Piloto Outbound, instrucción de tratamiento de IA (borrador de demostración) registrada en «{Tenant}».",
            nombreTenant);
    }

    /// <param name="cifDeSuClienteEmpresarial">
    /// Solo para un Usuario de Cliente: el identificador fiscal del Cliente empresarial cuya relación
    /// puede leer. Es el mismo dato que fija la aplicación al dar de alta una cuenta de ese rol; sin él, su alcance es vacío.
    /// </param>
    private static async Task<ApplicationUser> CrearCuentaAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore,
        IHostEnvironment entorno, Parametros parametros, ILogger logger, Guid tenantId, string email, string nombre, string rol,
        CancellationToken cancellationToken, string? cifDeSuClienteEmpresarial = null)
    {
        var usuario = await DelegacionDemoSeeder.CrearUsuarioConsultoraAsync(
                dbContext, userManager, parametros.CredencialesDe(email), logger, tenantId, email, nombre, rol, cancellationToken)
            ?? throw new InvalidOperationException($"No se pudo sembrar la cuenta de {rol} del piloto (ver el aviso anterior del registro).");

        // P1-13: todo Administrador con segundo factor. Solo en Development se asigna el de siembra;
        // en cualquier otro entorno la cuenta lo configura en su primer acceso.
        var faltaElSegundoFactor = rol == Roles.Administrador && !usuario.TwoFactorEnabled;
        if (!faltaElSegundoFactor && cifDeSuClienteEmpresarial is null)
            return usuario;

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            if (faltaElSegundoFactor)
                await IdentitySeeder.AsignarSegundoFactorDeSiembraAsync(usuario, userManager, userStore, entorno, cancellationToken);

            if (cifDeSuClienteEmpresarial is not null)
            {
                var clienteEmpresarialId = await dbContext.Empresas
                        .Where(e => e.Cif == cifDeSuClienteEmpresarial).Select(e => (Guid?)e.Id).SingleOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"No existe el Cliente empresarial al que iba a quedar ligada la cuenta de {rol} del piloto.");

                if (usuario.ClienteId != clienteEmpresarialId)
                {
                    usuario.ClienteId = clienteEmpresarialId;
                    var resultado = await userManager.UpdateAsync(usuario);
                    if (!resultado.Succeeded)
                        throw new InvalidOperationException(
                            $"No se pudo ligar la cuenta de {rol} del piloto a su Cliente empresarial: " +
                            string.Join(", ", resultado.Errors.Select(e => e.Description)));
                }
            }
        }

        return usuario;
    }

    /// <summary>
    /// La Asignación de Operación Outbound del Operador CAE externo sobre el Tenant
    /// propietario, en sus dos capas y en este orden: la heredada, bajo el ámbito
    /// del Operador CAE externo, y la del catálogo de asignaciones, bajo el del
    /// Tenant propietario, con la Asignación de Cartera del Tenant entero de cada
    /// Gestor CAE. Todo idempotente. Es el único método de esta siembra que nombra
    /// los identificadores heredados de la delegación.
    /// </summary>
    private static async Task AbrirOperacionExternaAsync(
        CaeManagerDbContext dbContext, TenantPilotoOutbound tenant, Guid tenantPropietarioId, Guid tenantOperadorId,
        Equipo equipo, ILogger logger, CancellationToken cancellationToken)
    {
        await DelegacionDemoSeeder.CrearDelegacionAsync(
            dbContext, tenantOperadorId, tenantPropietarioId, equipo.Administrador, logger, tenant.Nombre, cancellationToken);

        var gestores = equipo.GestoresDe(tenant);

        using (AmbitoTenantExplicito.Establecer(tenantOperadorId))
        {
            var delegacion = await dbContext.DelegacionesTenant.FirstAsync(
                d => d.TenantConsultoraId == tenantOperadorId && d.TenantClienteId == tenantPropietarioId, cancellationToken);

            var asignaciones = new List<(Guid UsuarioId, string Rol)> { (equipo.CoordinadoraId, Roles.CoordinadorCae) };
            asignaciones.AddRange(gestores.Select(id => (id, Roles.GestorCae)));

            foreach (var (usuarioId, rol) in asignaciones)
            {
                if (await dbContext.AsignacionesOperadorDelegado.AnyAsync(
                        a => a.DelegacionTenantId == delegacion.Id && a.UsuarioId == usuarioId, cancellationToken))
                    continue;

                dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, usuarioId, rol));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            var writer = new AsignacionesOperativasWriter(
                dbContext, new TenantActualAmbiental { TenantId = tenantPropietarioId }, new EscenariosDireccionDemoSeeder.ActorDeSiembra());

            await writer.AbrirOperacionDelegadaAsync(
                tenantPropietarioId, tenantOperadorId, DateTime.UtcNow.AddDays(-30), vigenciaHasta: null, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var gestorId in gestores)
            {
                await writer.AsegurarCarteraTenantEnteroAsync(tenantPropietarioId, gestorId, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Los datos de un Tenant propietario, en un solo guardado. Devuelve falso si
    /// ya estaba sembrado (existe su Empresa propia, con el identificador fiscal de
    /// esta versión). A un Tenant con datos de otra versión no se llega: el bucle
    /// de <see cref="SembrarLoteAsync"/> lo salta antes. Con cualquier excepción,
    /// cancelación incluida, elimina los PDF que ya había escrito para este Tenant
    /// antes de relanzar: nada queda en el almacén sin fila que lo referencie.
    /// </summary>
    private static async Task<bool> SembrarDatosAsync(
        CaeManagerDbContext dbContext, IFileStorageService almacen, TenantPilotoOutbound tenant, Guid tenantPropietarioId,
        Parametros parametros, Guid gestoraPrimeraId, Recuento recuento, CancellationToken cancellationToken)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantPropietarioId);

        var constructor = new ConstructorDeTenant(dbContext, almacen, tenant, parametros, gestoraPrimeraId, cancellationToken);
        if (await dbContext.Empresas.AnyAsync(e => e.Cif == constructor.CifEmpresaPropia, cancellationToken))
            return false;

        try
        {
            await constructor.ConstruirAsync();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Sin el token de la siembra: si la causa es su cancelación, la limpieza tiene que correr igual.
            foreach (var clave in constructor.ClavesDePdf)
            {
                try
                {
                    await almacen.EliminarAsync(clave, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Limpieza de mejor esfuerzo: no puede sustituir a la excepción original.
                }
            }

            dbContext.ChangeTracker.Clear();
            throw;
        }

        recuento.Documentos += constructor.Documentos;
        recuento.Pdf += constructor.ClavesDePdf.Count;
        return true;
    }

    /// <summary>
    /// Añade al contexto, sin guardar, todo lo de un Tenant propietario según su
    /// escenario. Una clase y no métodos sueltos para que el estado (contadores,
    /// claves de PDF, tipos) no viaje por veinte parámetros. Determinista: los
    /// nombres, identificadores fiscales y fechas salen del catálogo, del índice y
    /// del día de la demostración, sin azar ni reloj.
    /// </summary>
    private sealed class ConstructorDeTenant(
        CaeManagerDbContext dbContext, IFileStorageService almacen, TenantPilotoOutbound tenant, Parametros parametros,
        Guid gestoraPrimeraId, CancellationToken cancellationToken)
    {
        private readonly FechasPilotoOutbound _fechas = new(parametros.FechaDemostracion);
        private readonly int _indiceTenant = CatalogoPilotoOutbound.Tenants.ToList().IndexOf(tenant);
        private readonly List<string> _clavesDePdf = [];
        private readonly Dictionary<Guid, Guid> _clienteEmpresarialDelCentro = [];

        /// <summary>
        /// De cada Trabajador, la emisión más antigua —también la de una versión que luego se sustituye— de un documento de
        /// <see cref="CatalogoPilotoOutbound.TiposPosterioresAlAlta"/>: su alta en la Empresa tiene que ser anterior.
        /// </summary>
        private readonly Dictionary<Guid, DateOnly> _emisionMasAntiguaPosteriorAlAlta = [];
        private IReadOnlyList<TipoDocumento> _tipos = [];
        private int _semilla;

        public IReadOnlyList<string> ClavesDePdf => _clavesDePdf;
        public int Documentos { get; private set; }
        public string CifEmpresaPropia => Cif(0);

        private DateOnly D => parametros.FechaDemostracion;

        /// <summary>El tipo de Empresa cuyo documento lleva el PDF pesado, en el perfil multicliente.</summary>
        private const string TipoDeEmpresaConPdfPesado = "Evaluación de Riesgos Laborales";

        public async Task ConstruirAsync()
        {
            _tipos = await dbContext.TiposDocumento.ToListAsync(cancellationToken);
            ComprobarCatalogoDeTipos();

            if (tenant.TrabajadoresPorCentro.Count != tenant.Centros.Count || tenant.TrabajadoresPorCentro.Sum() != tenant.Trabajadores)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: el catálogo reparte {tenant.TrabajadoresPorCentro.Sum()} Trabajadores entre {tenant.TrabajadoresPorCentro.Count} " +
                    $"Centros y declara {tenant.Trabajadores} Trabajadores y {tenant.Centros.Count} Centros.");

            // La única Empresa con EsPropia del Tenant; sus Clientes empresariales y sus Subcontratas nacen con EsPropia a falso.
            var propia = new Empresa(tenant.Nombre, CifEmpresaPropia);
            dbContext.Empresas.Add(propia);
            AnadirContacto(ContactoAgenda.DeEmpresa(
                propia.Id, Persona(40), parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDePrevencion(tenant.Nombre)),
                cargo: "Responsable de prevención", esPredeterminado: true), RolContacto.ResponsablePrl);
            AnadirContacto(ContactoAgenda.DeEmpresa(
                propia.Id, Persona(41), parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeAdministracion(tenant.Nombre)),
                cargo: "Administración", recibeFacturacion: true), RolContacto.RepresentanteLegal);

            var centros = CrearClientesEmpresarialesYCentros(propia);
            var trabajadores = CrearTrabajadores(propia);
            var subcontratas = await CrearSubcontratasAsync(propia, trabajadores);
            var activas = CrearAsignaciones(centros, trabajadores);

            var desviaciones = Desviaciones(trabajadores.Count);
            var documentos = await DocumentosDeTrabajadoresAsync(trabajadores, desviaciones, FechasFijadasDelPerfil(trabajadores, desviaciones, activas));
            await DocumentosDeLosExtranjerosAsync(trabajadores);
            var documentosDeEmpresa = await DocumentosExigidosDeLaEmpresaPropiaAsync(propia);
            await VehiculosAsync(propia, trabajadores);
            RequisitosPropiosDeLosCentros(centros);
            await AcreditacionesExternasAsync(centros, trabajadores, documentos, activas);
            VisitasDelPerfil(centros, trabajadores, activas, desviaciones);
            CanalPorCorreoDelCentro(centros);
            if (tenant.ConSenalesDeIa)
                SenalesDeIa(propia, centros, trabajadores, documentos, documentosDeEmpresa, desviaciones, activas);
            ReclamacionesDelPerfil(propia, centros, trabajadores, documentos, desviaciones, activas);
            GestionesPendientesDelPerfil(centros, trabajadores, desviaciones, activas);
            await ProyectosDelPerfilAsync(centros, trabajadores, activas);
            EventosDelPerfil(centros, trabajadores);
            VerificacionesExternasDeLasSupervisadas(centros, trabajadores, subcontratas, activas);
            if (tenant.ConDocumentoRenovado)
                await DocumentoRenovadoAsync(trabajadores, documentos, desviaciones);
            FichasDeLosTrabajadores(trabajadores);

            MaterializarInclusionesDeLosCentros(centros);
        }

        /// <summary>
        /// Las fechas y los conjuntos exigidos de esta siembra dan por hecho qué tipos
        /// se piden por defecto. Si el catálogo del Tenant dijera otra cosa, los
        /// resultados no saldrían: mejor fallar aquí, con el motivo.
        /// </summary>
        private void ComprobarCatalogoDeTipos()
        {
            var exigidos = _tipos
                .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && t.Requerido == RequisitoDocumental.Si)
                .Select(t => t.Nombre).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var esperados = CatalogoPilotoOutbound.TiposExigidosDeTrabajador.OrderBy(n => n, StringComparer.Ordinal).ToList();

            if (!exigidos.SequenceEqual(esperados))
                throw new InvalidOperationException(
                    "El catálogo de tipos de documento ya no exige por defecto a un Trabajador lo que la siembra del piloto da por " +
                    $"hecho. Esperados: {string.Join(", ", esperados)}. Encontrados: {string.Join(", ", exigidos)}.");
        }

        private List<Centro> CrearClientesEmpresarialesYCentros(Empresa propia)
        {
            var ahora = DateTime.UtcNow;
            var centros = new List<Centro>();

            for (var i = 0; i < tenant.ClientesEmpresariales.Count; i++)
            {
                var especificacion = tenant.ClientesEmpresariales[i];
                var clienteEmpresarial = CrearClienteEmpresarial(especificacion.RazonSocial, Cif(i + 1));
                dbContext.Empresas.Add(clienteEmpresarial);
                dbContext.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(propia.Id, clienteEmpresarial.Id, ahora));
                AnadirContacto(ContactoDelClienteEmpresarial(
                    clienteEmpresarial.Id, Persona(50 + i),
                    parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeCoordinacionCae(especificacion.RazonSocial))), RolContacto.ContactoCae);

                foreach (var especificacionCentro in especificacion.Centros)
                {
                    var numero = centros.Count + 1;
                    var centro = new Centro(
                        clienteEmpresarial.Id, propia.Id, especificacionCentro.Nombre, tenant.CodigoDe(especificacionCentro),
                        IdentidadesPilotoOutbound.Direccion(numero, especificacionCentro.Localidad),
                        contratoVigenteHasta: D.AddDays(300 + numero * 20));
                    dbContext.Centros.Add(centro);
                    centros.Add(centro);
                    _clienteEmpresarialDelCentro[centro.Id] = clienteEmpresarial.Id;

                    AnadirContacto(ContactoAgenda.DeCentro(
                        centro.Id, Persona(OrdinalDelContactoDeCentro(numero)),
                        parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeCoordinacionDeAccesos(especificacionCentro)),
                        cargo: "Coordinación de accesos", esPredeterminado: true, recibeProgramacionVisitas: true), RolContacto.ResponsablePrl);
                }
            }

            return centros;
        }

        /// <summary>
        /// El único sitio que usa las fábricas heredadas «…Cliente»: lo que crean es un
        /// Cliente empresarial (la Empresa que recibe el servicio en la Relación
        /// Empresarial, con EsPropia a falso) y su contacto de agenda. La Gestora CAE queda como referencia;
        /// el alcance lo concede la Asignación de Cartera, no este dato.
        /// </summary>
        private Empresa CrearClienteEmpresarial(string razonSocial, string cif) =>
            Empresa.CrearComoCliente(razonSocial, cif, false, null, gestoraPrimeraId);

        private static ContactoAgenda ContactoDelClienteEmpresarial(Guid clienteEmpresarialId, string nombre, string email) =>
            ContactoAgenda.DeCliente(clienteEmpresarialId, nombre, email, cargo: "Técnico de coordinación CAE", esPredeterminado: true);

        private List<Trabajador> CrearTrabajadores(Empresa propia)
        {
            var trabajadores = new List<Trabajador>();
            for (var i = 0; i < tenant.Trabajadores; i++)
            {
                // El autónomo es su propia Trabajadora: su nombre es el del Tenant y su documento de identidad, el identificador fiscal de su Empresa.
                var (nombre, apellidos) = tenant.Perfil == PerfilPilotoOutbound.Autonomo
                    ? (tenant.Nombre.Split(' ', 2)[0], tenant.Nombre.Split(' ', 2)[1])
                    : IdentidadesPilotoOutbound.Trabajador(tenant, i);
                var trabajador = Trabajador.DeEmpresa(
                    propia.Id, nombre, apellidos, DniDe(i), fechaNacimiento: NacimientoDe(i), puesto: IdentidadesPilotoOutbound.Puesto(tenant, i));
                dbContext.Trabajadores.Add(trabajador);
                trabajadores.Add(trabajador);
            }

            return trabajadores;
        }

        private string DniDe(int i) => IdentidadesPilotoOutbound.Dni(tenant, i);

        private DateOnly NacimientoDe(int i) => D.AddYears(-(25 + i * 7 % 30)).AddDays(-i * 13);

        /// <summary>
        /// Las Asignaciones: los propios en su Centro (los de baja, con la Asignación dada de baja), las segundas
        /// Asignaciones del catálogo y los de cada Subcontrata repartidos entre los Centros. Devuelve, por Trabajador,
        /// los Centros donde está activo.
        /// </summary>
        private Dictionary<int, List<int>> CrearAsignaciones(List<Centro> centros, List<Trabajador> trabajadores)
        {
            var activas = new Dictionary<int, List<int>>();

            void Alta(int t, int c, DateOnly desde)
            {
                dbContext.Asignaciones.Add(new Asignacion(trabajadores[t].Id, centros[c].Id, desde));
                if (!activas.TryGetValue(t, out var lista)) activas[t] = lista = [];
                lista.Add(c);
            }

            var siguiente = 0;
            for (var c = 0; c < centros.Count; c++)
            {
                for (var k = 0; k < tenant.TrabajadoresPorCentro[c]; k++)
                {
                    var t = siguiente++;
                    if (tenant.DeBaja.Contains(t))
                    {
                        var deBaja = new Asignacion(trabajadores[t].Id, centros[c].Id, D.AddDays(-300));
                        deBaja.DarDeBaja(D.AddDays(-30));
                        dbContext.Asignaciones.Add(deBaja);
                        continue;
                    }

                    Alta(t, c, D.AddDays(-150 - t % 90));
                }
            }

            foreach (var (t, c) in tenant.DobleAsignacion)
                Alta(t, c, D.AddDays(-100 - t));

            for (var t = tenant.Trabajadores; t < trabajadores.Count; t++)
                Alta(t, (t - tenant.Trabajadores) % centros.Count, D.AddDays(-120 - t % 60));

            return activas;
        }

        /// <summary>
        /// Los pares (Trabajador, tipo exigido) que se apartan de «en regla», según la receta del perfil: la definición es
        /// <see cref="CatalogoPilotoOutbound.Desviaciones"/>, la misma con la que la autoverificación mide lo escrito.
        /// </summary>
        private List<DesviacionDocumentoPilotoOutbound> Desviaciones(int total) =>
            CatalogoPilotoOutbound.Desviaciones(tenant, total, nombre => Tipo(nombre).AplicaVencimientoAutomatico);

        /// <summary>
        /// Los cinco tipos exigidos por defecto de cada Trabajador, en regla salvo lo que digan las desviaciones. Devuelve
        /// los documentos por (índice del Trabajador, tipo); un par que no está es un documento que falta.
        /// </summary>
        private async Task<Dictionary<(int Trabajador, string Tipo), Documento>> DocumentosDeTrabajadoresAsync(
            List<Trabajador> trabajadores, IReadOnlyList<DesviacionDocumentoPilotoOutbound> desviaciones,
            IReadOnlyDictionary<(int Trabajador, string Tipo), (DateOnly Emision, DateOnly Vence)>? fijadas = null)
        {
            var documentos = new Dictionary<(int Trabajador, string Tipo), Documento>();

            for (var t = 0; t < trabajadores.Count; t++)
            {
                foreach (var nombreTipo in CatalogoPilotoOutbound.TiposExigidosDeTrabajador)
                {
                    var tipo = Tipo(nombreTipo);
                    var situacion = desviaciones.SingleOrDefault(d => d.Trabajador == t && d.Tipo == nombreTipo)?.Situacion;

                    switch (situacion)
                    {
                        case SituacionDocumentoPilotoOutbound.Ausente:
                            continue;

                        case null when fijadas is not null && fijadas.TryGetValue((t, nombreTipo), out var fechasFijadas):
                            documentos[(t, nombreTipo)] = await DocumentoDeTrabajadorAsync(trabajadores[t], tipo, fechasFijadas.Emision, fechasFijadas.Vence);
                            continue;

                        case null:
                            var (emision, vigente) = FechasEnReglaDeTrabajador(tipo);
                            documentos[(t, nombreTipo)] = await DocumentoDeTrabajadorAsync(trabajadores[t], tipo, emision, vigente);
                            continue;

                        case SituacionDocumentoPilotoOutbound.SinConfirmar:
                            documentos[(t, nombreTipo)] = await DocumentoDeTrabajadorAsync(
                                trabajadores[t], tipo, _fechas.Emision(Siguiente()), vence: null, sinConfirmar: true);
                            continue;
                    }

                    var vencimientoDeDiseno = situacion switch
                    {
                        SituacionDocumentoPilotoOutbound.Vencido => _fechas.Vencido(Siguiente()),
                        SituacionDocumentoPilotoOutbound.Urgente => _fechas.Urgente(Siguiente()),
                        SituacionDocumentoPilotoOutbound.Proximo => _fechas.Proximo(Siguiente()),
                        _ => throw new InvalidOperationException($"{tenant.Clave}: situación de documento sin fecha: {situacion}.")
                    };
                    var (emitido, vence) = _fechas.ConVencimiento(tipo, vencimientoDeDiseno);
                    documentos[(t, nombreTipo)] = await DocumentoDeTrabajadorAsync(trabajadores[t], tipo, emitido, vence);
                }
            }

            return documentos;
        }

        /// <summary>El permiso de trabajo de cada extranjero, con la caducidad que dice su ficha. No se exige por defecto: no añade pares.</summary>
        private async Task DocumentosDeLosExtranjerosAsync(List<Trabajador> trabajadores)
        {
            foreach (var extranjero in tenant.Extranjeros)
            {
                var tipo = Tipo("Permiso de trabajo");
                var (emision, vence) = _fechas.ConVencimiento(tipo, D.AddDays(extranjero.DiasHastaLaCaducidad));
                await DocumentoDeTrabajadorAsync(trabajadores[extranjero.Trabajador], tipo, emision, vence);
            }
        }

        /// <summary>
        /// Un documento, con su PDF, de cada tipo que se exige por defecto a una Empresa, para la Empresa propia, en regla
        /// (<see cref="FechasPilotoOutbound.EnRegla"/>): Vigente, salvo los dos certificados mensuales, que están «Próximo».
        /// Sin plantilla no hay RLC, RNT ni ITA: el autónomo no los tiene y lleva en su lugar el recibo de su cuota (mensual,
        /// «Próximo»). El perfil de obras suma el certificado de inscripción en el REA.
        /// </summary>
        private async Task<List<Documento>> DocumentosExigidosDeLaEmpresaPropiaAsync(Empresa propia)
        {
            var creados = new List<Documento>();
            var conPdfPesado = tenant.Perfil == PerfilPilotoOutbound.Multicliente ? new[] { TipoDeEmpresaConPdfPesado } : [];
            var tiposDeEmpresa = TiposExigidos(AmbitoAplicacion.Empresa)
                .Where(t => !(tenant.EmpresaSinPlantilla && CatalogoPilotoOutbound.TiposDeEmpresaDePlantilla.Contains(t.Nombre)))
                // Un documento mensual solo puede estar «Próximo» (vence al mes) y tiñe de ámbar todos los Centros de la Empresa
                // —las causas de Empresa no distinguen los Centros que lo exigen—: el perfil que quiere Centros «Vigente» no lo siembra.
                .Where(t => !tenant.TiposDeEmpresaNoExigidos.Contains(t.Nombre)).ToList();
            // Tipos del catálogo que no se exigen por defecto y que cada perfil lleva: el REA de quien pisa obras, el recibo del autónomo.
            if (tenant.Perfil == PerfilPilotoOutbound.ObrasYMaquinaria)
                tiposDeEmpresa.Add(Tipo(CatalogoPilotoOutbound.CertificadoReaConstruccion));
            if (tenant.Perfil == PerfilPilotoOutbound.Autonomo)
                tiposDeEmpresa.Add(Tipo(CatalogoPilotoOutbound.ReciboDeAutonomos));
            if (conPdfPesado.FirstOrDefault(n => tiposDeEmpresa.All(t => t.Nombre != n)) is { } ajeno)
                throw new InvalidOperationException($"{tenant.Clave}: «{ajeno}», que iba a llevar un PDF pesado, ya no se exige por defecto a una Empresa.");

            for (var i = 0; i < tiposDeEmpresa.Count; i++)
            {
                var tipo = tiposDeEmpresa[i];
                int? semillaDeVencimiento = tipo.AplicaVencimientoAutomatico || i % 2 == 0 ? Siguiente() : null;
                var (emision, vence) = _fechas.EnRegla(tipo, Siguiente(), semillaDeVencimiento);
                var clave = await GuardarPdfAsync(tipo.Nombre, tenant.Nombre, emision, vence, pesado: conPdfPesado.Contains(tipo.Nombre));
                var documentoDeEmpresa = Documento.DeEmpresa(propia.Id, tipo.Id, emision, Vigencia(vence), clave);
                Anadir(documentoDeEmpresa);
                creados.Add(documentoDeEmpresa);
            }

            return creados;
        }

        /// <summary>
        /// Los Vehículos de la Empresa propia, con su ficha completa (tipo, titularidad, aseguradora, póliza, recibo y
        /// conductores autorizados) y lo que se les exige por defecto, en regla salvo el primer documento de los que digan otra cosa.
        /// </summary>
        private async Task VehiculosAsync(Empresa propia, List<Trabajador> trabajadores)
        {
            var tiposDeVehiculo = TiposExigidos(AmbitoAplicacion.Vehiculo);

            for (var v = 0; v < tenant.Vehiculos.Count; v++)
            {
                var especificacion = tenant.Vehiculos[v];
                var vehiculo = Vehiculo.DeEmpresa(propia.Id, especificacion.Nombre, especificacion.Modelo, especificacion.Placa);
                var esMaquinaria = especificacion.Tipo == "Maquinaria de obra";
                vehiculo.ActualizarFicha(
                    especificacion.Tipo, especificacion.Titularidad,
                    revisionGrua: especificacion.Tipo is "Camión" or "Maquinaria de obra" ? "Revisión periódica al día" : null,
                    declaracionCe: esMaquinaria ? "Declaración CE disponible" : null,
                    especificacion.Aseguradora, poliza: $"POL-{(10_000_000 + v * 7_919 + _indiceTenant * 104_729) % 90_000_000:D8}",
                    recibo: "Recibo del último periodo pagado");
                foreach (var conductor in especificacion.Conductores)
                    vehiculo.AutorizarConductor(trabajadores[conductor].Id);
                dbContext.Vehiculos.Add(vehiculo);

                for (var j = 0; j < tiposDeVehiculo.Count; j++)
                {
                    var tipo = tiposDeVehiculo[j];
                    var situacion = j == 0 ? especificacion.Situacion : null;
                    var (emision, vence) = situacion switch
                    {
                        SituacionDocumentoPilotoOutbound.Vencido => _fechas.ConVencimiento(tipo, _fechas.Vencido(Siguiente())),
                        SituacionDocumentoPilotoOutbound.Urgente => _fechas.ConVencimiento(tipo, _fechas.Urgente(Siguiente())),
                        SituacionDocumentoPilotoOutbound.Proximo => _fechas.ConVencimiento(tipo, _fechas.Proximo(Siguiente())),
                        _ => _fechas.EnRegla(tipo, semillaDeVencimiento: Siguiente(), semillaDeEmision: Siguiente())
                    };
                    var clave = await GuardarPdfAsync(tipo.Nombre, $"{vehiculo.Nombre} ({vehiculo.NumeroPlaca})", emision, vence, pesado: false);
                    Anadir(Documento.DeVehiculo(vehiculo.Id, tipo.Id, emision, Vigencia(vence), clave));
                }
            }
        }

        /// <summary>Lo que los Centros del perfil piden a su manera: los tipos de Empresa que no exigen y el requisito que bloquea el acceso.</summary>
        private void RequisitosPropiosDeLosCentros(List<Centro> centros)
        {
            foreach (var centro in centros)
                foreach (var nombre in tenant.TiposDeEmpresaNoExigidos)
                    dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(Tipo(nombre).Id, centro.Id, incluido: false));

            // Lo que no se exige por defecto y el Centro del perfil sí pide: la obra, el REA de quien trae a sus Trabajadores; la
            // fábrica o la acería, el recibo de cuota de quien trabaja allí por cuenta propia.
            var propioDelPerfil = tenant.Perfil switch
            {
                PerfilPilotoOutbound.ObrasYMaquinaria => CatalogoPilotoOutbound.CertificadoReaConstruccion,
                PerfilPilotoOutbound.Autonomo => CatalogoPilotoOutbound.ReciboDeAutonomos,
                _ => null
            };
            if (propioDelPerfil is not null)
                foreach (var centro in centros)
                    dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(Tipo(propioDelPerfil).Id, centro.Id, incluido: true));

            if (tenant.Tolerancia is { } tolerancia)
                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                    Tipo(tolerancia.Tipo).Id, centros[tolerancia.Centro].Id, incluido: true, toleranciaDias: tolerancia.Dias));

            if (tenant.CentroConRequisitoBloqueante is { } c)
                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                    Tipo(CatalogoPilotoOutbound.AptitudMedica).Id, centros[c].Id, incluido: true, bloqueaAcceso: true));
        }

        /// <summary>
        /// Un canal de plataforma, principal, en el Centro con plataforma, y una acreditación externa por cada documento
        /// exigido de cada Trabajador asignado: las primeras Rechazadas (con su motivo), las siguientes Subidas
        /// (pendientes de validar) y el resto Aceptadas. Las plataformas son las dos primeras del catálogo, por código,
        /// que siguen en uso.
        /// </summary>
        private async Task AcreditacionesExternasAsync(
            List<Centro> centros, List<Trabajador> trabajadores, Dictionary<(int Trabajador, string Tipo), Documento> documentos,
            Dictionary<int, List<int>> activas)
        {
            if (tenant.CentroConPlataforma is not { } c) return;

            var proveedor = await dbContext.ProveedoresPlataformaCae
                    .Where(p => p.Activo && !p.Codigo.EndsWith(SufijoDePlataformaRetirada)).OrderBy(p => p.Codigo).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No hay ningún proveedor de plataforma CAE activo: {tenant.Clave} necesita un Centro con canal de plataforma.");

            // La contraseña es un texto fijo que no abre nada: es el dato del portal de un
            // Cliente empresarial ficticio, no una credencial de TALVEG.
            var canal = CanalGestionDocumental.DePlataforma(
                centros[c].Id, "Gestión general", proveedor.Id, $"https://portal-demo-{proveedor.Codigo}.local",
                IdentidadesPilotoOutbound.UsuarioDePortal(tenant.Nombre), "sin-credencial-real");
            canal.MarcarComoPrincipal();
            dbContext.CanalesGestionDocumental.Add(canal);

            (CausaRechazoAcreditacion Causa, string Motivo)[] motivos =
            [
                (CausaRechazoAcreditacion.Ilegible, "El certificado escaneado no se lee: falta la segunda página."),
                (CausaRechazoAcreditacion.FaltaFirmaOSello, "El documento no lleva la firma ni el sello del responsable.")
            ];
            var rechazo = D.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias - 1).ToDateTime(new TimeOnly(9, 30), DateTimeKind.Utc);
            var (n, rechazadas, subidas) = (0, 0, 0);

            for (var t = 0; t < trabajadores.Count; t++)
            {
                if (!activas.TryGetValue(t, out var centrosDelTrabajador) || !centrosDelTrabajador.Contains(c)) continue;

                foreach (var nombreTipo in CatalogoPilotoOutbound.TiposExigidosDeTrabajador)
                {
                    if (!documentos.TryGetValue((t, nombreTipo), out var documento)) continue;

                    var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
                    acreditacion.MarcarSubida();

                    if (rechazadas < tenant.Receta.AcreditacionesRechazadas)
                    {
                        var (causa, motivo) = motivos[rechazadas++ % motivos.Length];
                        acreditacion.Rechazar(causa, motivo, rechazo);
                    }
                    else if (subidas < tenant.Receta.AcreditacionesSubidas)
                    {
                        subidas++;
                    }
                    else
                    {
                        acreditacion.MarcarAceptada(n % 2 == 0 ? VigenciaEnPlataforma.NoVenceAqui : VigenciaEnPlataforma.VenceEl(D.AddDays(180 + n % 200)));
                    }

                    n++;
                    dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
                }
            }

            if (rechazadas != tenant.Receta.AcreditacionesRechazadas || subidas != tenant.Receta.AcreditacionesSubidas)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: la receta pide {tenant.Receta.AcreditacionesRechazadas} acreditaciones rechazadas y " +
                    $"{tenant.Receta.AcreditacionesSubidas} subidas, y el Centro con plataforma solo da para {rechazadas} y {subidas}.");
        }

        /// <summary>
        /// El Trabajador de la tolerancia: el primero asignado solo al Centro que la concede y sin ninguna desviación en ese tipo.
        /// Su documento vence hace unos días dentro de los que concede el Centro: allí se pinta «en tolerancia».
        /// </summary>
        private int? TrabajadorDeLaTolerancia(
            int total, List<DesviacionDocumentoPilotoOutbound> desviaciones, Dictionary<int, List<int>> activas)
        {
            if (tenant.Tolerancia is not { } tolerancia) return null;

            for (var t = 0; t < total; t++)
                if (activas.TryGetValue(t, out var lista) && lista.Count == 1 && lista[0] == tolerancia.Centro
                    && !desviaciones.Any(x => x.Trabajador == t && x.Tipo == tolerancia.Tipo))
                    return t;

            throw new InvalidOperationException($"{tenant.Clave}: ningún Trabajador del Centro {tolerancia.Centro} sirve para el caso de tolerancia.");
        }

        private int? _trabajadorDeLaTolerancia;

        private bool EsElDeLaTolerancia(int trabajador, Dictionary<int, List<int>> activas) => _trabajadorDeLaTolerancia == trabajador;

        private IReadOnlyDictionary<(int Trabajador, string Tipo), (DateOnly Emision, DateOnly Vence)>? FechasFijadasDelPerfil(
            List<Trabajador> trabajadores, List<DesviacionDocumentoPilotoOutbound> desviaciones, Dictionary<int, List<int>> activas)
        {
            _trabajadorDeLaTolerancia = TrabajadorDeLaTolerancia(trabajadores.Count, desviaciones, activas);
            if (_trabajadorDeLaTolerancia is not { } t || tenant.Tolerancia is not { } tolerancia) return null;

            return new Dictionary<(int, string), (DateOnly, DateOnly)>
            {
                [(t, tolerancia.Tipo)] = _fechas.ConVencimiento(Tipo(tolerancia.Tipo), D.AddDays(-tolerancia.DiasDesdeQueVencio))
            };
        }

        /// <summary>El Centro que se gestiona por correo: su canal principal es una dirección que sale de la misma regla que los contactos de agenda.</summary>
        private void CanalPorCorreoDelCentro(List<Centro> centros)
        {
            if (tenant.CentroPorCorreo is not { } c) return;
            if (tenant.CentroConPlataforma == c)
                throw new InvalidOperationException($"{tenant.Clave}: el Centro que se gestiona por correo no puede tener además canal de plataforma.");

            var canal = CanalGestionDocumental.PorEmail(
                centros[c].Id, "Solicitudes de acceso",
                parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeSolicitudesDeAcceso(tenant.Centros[c])),
                Persona(OrdinalDelContactoDeCentro(c + 1)));
            canal.MarcarComoPrincipal();
            dbContext.CanalesGestionDocumental.Add(canal);
        }

        /// <summary>
        /// Las tres filas de Mi trabajo que vienen de la IA y de los correos: una revisión de IA, una detección de alta y una
        /// sugerencia de Visita, todas pendientes. Son REGISTROS de lo que la aplicación habría dejado tras analizar un
        /// documento y un correo: aquí no se llama a ningún proveedor de IA ni se envía nada. El correo que origina la
        /// sugerencia es entrante, de la dirección de coordinación CAE del Cliente empresarial dueño del Centro.
        /// </summary>
        private void SenalesDeIa(
            Empresa propia, List<Centro> centros, List<Trabajador> trabajadores,
            Dictionary<(int Trabajador, string Tipo), Documento> documentos, List<Documento> documentosDeEmpresa,
            List<DesviacionDocumentoPilotoOutbound> desviaciones, Dictionary<int, List<int>> activas)
        {
            var revisado = Enumerable.Range(0, trabajadores.Count).FirstOrDefault(
                t => !desviaciones.Any(x => x.Trabajador == t) && activas.TryGetValue(t, out var lista) && lista.Count == 1
                     && t != _trabajadorDeLaTolerancia && !tenant.DeBaja.Contains(t) && tenant.TrabajadorDeEtt != t
                     && documentos.ContainsKey((t, CatalogoPilotoOutbound.FormacionArt19)), -1);
            if (revisado < 0)
                throw new InvalidOperationException($"{tenant.Clave}: ningún Trabajador en regla y en un solo Centro para la revisión de IA.");

            var documentoRevisado = documentos[(revisado, CatalogoPilotoOutbound.FormacionArt19)];
            dbContext.RevisionesIaDocumento.Add(RevisionIaDocumento.Crear(
                documentoRevisado.Id, 55, tipoDetectado: CatalogoPilotoOutbound.FormacionArt19,
                fechaEmisionDetectada: documentoRevisado.FechaEmision, fechaVencimientoDetectada: null, tieneFirmaDetectada: false,
                motivo: "La fecha de emisión no se lee con claridad y no se detecta firma. Conviene que alguien lo mire."));

            dbContext.DeteccionesTrabajador.Add(DeteccionTrabajador.Nuevo(
                documentosDeEmpresa[0].Id, propia.Id, "Ismael", "Cebrián Roldán", IdentidadesPilotoOutbound.DniDeLaDeteccion(tenant)));

            var centro = centros[0];
            var especificacion = tenant.ClientesEmpresariales.Single(c => c.Centros.Any(x => x.Nombre == centro.Nombre));
            var remitente = parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeCoordinacionCae(especificacion.RazonSocial));
            var recibido = D.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias - 1).ToDateTime(new TimeOnly(8, 40), DateTimeKind.Utc);
            var conversacion = new Conversacion($"Aviso de inspección en {centro.Nombre}", clienteId: centro.ClienteId);
            var mensaje = conversacion.AgregarMensaje(
                DireccionMensaje.Entrante, CanalConversacion.Correo, remitente,
                $"<p>Mañana pasa una inspección por {centro.Nombre} y entrará personal de otra contrata. Avisadnos de quién viene.</p>",
                recibido);
            conversacion.AgregarParticipante(remitente, RolParticipante.De, TipoParticipanteOrigen.Empresa, centro.ClienteId);
            dbContext.Conversaciones.Add(conversacion);

            dbContext.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(
                mensaje.Id, centro.Id, fechaInicioSugerida: null, fechaFinSugerida: null,
                resumen: "Inspección sorpresa en el Centro; el correo no precisa la fecha.",
                confianza: 70, confianzaCentro: 90, confianzaFechas: 40));
        }

        /// <summary>
        /// Las Visitas del perfil, en su estado: futura, realizada (con la documentación gestionada), vencida (pasó sin
        /// gestionarla) o cancelada. Acuden los dos primeros Trabajadores activos del Centro.
        /// </summary>
        private void VisitasDelPerfil(
            List<Centro> centros, List<Trabajador> trabajadores, Dictionary<int, List<int>> activas,
            List<DesviacionDocumentoPilotoOutbound> desviaciones)
        {
            var antesDeSembrar = D.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias - 1).ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc);

            foreach (var especificacion in tenant.Visitas)
            {
                var inicio = D.AddDays(especificacion.DiasDesdeLaDemostracion);
                var fin = inicio.AddDays(especificacion.DuracionDias - 1);
                var visita = new Visita(
                    centros[especificacion.Centro].Id, inicio, fin, especificacion.Notas,
                    especificacion.PorCorreo ? OrigenVisita.Correo : OrigenVisita.Manual);
                dbContext.Visitas.Add(visita);

                // La Visita por correo lleva a dos Trabajadores con todo en regla: lo que viaja en su ZIP es todo lo que el Centro exige.
                var acuden = Enumerable.Range(0, trabajadores.Count)
                    .Where(t => activas.TryGetValue(t, out var lista) && lista.Contains(especificacion.Centro)
                                && (!especificacion.PorCorreo || (!desviaciones.Any(x => x.Trabajador == t) && !EsElDeLaTolerancia(t, activas))))
                    .Take(2).ToList();
                if (especificacion.PorCorreo && acuden.Count < 2)
                    throw new InvalidOperationException(
                        $"{tenant.Clave}: la Visita por correo necesita dos Trabajadores del Centro {especificacion.Centro} sin ninguna desviación y solo hay {acuden.Count}.");
                foreach (var t in acuden)
                    dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajadores[t].Id));

                switch (especificacion.Estado)
                {
                    case EstadoVisitaPilotoOutbound.Realizada:
                        visita.MarcarDocumentacionGestionada(fin.AddDays(-2).ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc));
                        break;
                    case EstadoVisitaPilotoOutbound.EnCurso:
                        // Una Visita que ya ha empezado entró con la documentación gestionada el día antes de empezar.
                        visita.MarcarDocumentacionGestionada(inicio.AddDays(-1).ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc));
                        break;
                    case EstadoVisitaPilotoOutbound.Cancelada:
                        visita.Cancelar(antesDeSembrar, "Se aplaza el trabajo a petición del Cliente empresarial.");
                        break;
                }
            }
        }

        /// <summary>
        /// Las reclamaciones de documentos de Trabajador vencidos: sin respuesta (la primera con más de una semana, la
        /// segunda reciente) y respondidas. Son REGISTROS históricos —su fila, su hilo y su entrada en la cronología—: aquí
        /// no se envía ni se encola nada, la siembra no llama a IEmailService.
        /// </summary>
        private void ReclamacionesDelPerfil(
            Empresa propia, List<Centro> centros, List<Trabajador> trabajadores,
            Dictionary<(int Trabajador, string Tipo), Documento> documentos, List<DesviacionDocumentoPilotoOutbound> desviaciones,
            Dictionary<int, List<int>> activas)
        {
            var candidatos = desviaciones
                .Where(d => d.Situacion == SituacionDocumentoPilotoOutbound.Vencido && documentos.ContainsKey((d.Trabajador, d.Tipo))
                            && activas.TryGetValue(d.Trabajador, out var lista) && lista.Count == 1)
                .ToList();
            var pedidas = tenant.ReclamacionesSinRespuesta + tenant.ReclamacionesRespondidas;
            if (candidatos.Count < pedidas)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: pide {pedidas} reclamaciones de documentos vencidos de un Trabajador con un solo Centro y solo hay {candidatos.Count}.");

            var margen = OpcionesPilotoOutbound.MargenMaximoDias;
            for (var k = 0; k < pedidas; k++)
            {
                var respondida = k >= tenant.ReclamacionesSinRespuesta;
                var dias = respondida ? 20 : k == 0 ? 14 : margen + 1;
                Reclamar(
                    candidatos[k], D.AddDays(-dias).ToDateTime(new TimeOnly(9, 15), DateTimeKind.Utc),
                    respuesta: respondida ? D.AddDays(-dias + 3).ToDateTime(new TimeOnly(12, 10), DateTimeKind.Utc) : null);
            }

            void Reclamar(DesviacionDocumentoPilotoOutbound caso, DateTime enviada, DateTime? respuesta)
            {
                var documento = documentos[(caso.Trabajador, caso.Tipo)];
                var centro = centros[activas[caso.Trabajador][0]];
                var especificacion = tenant.ClientesEmpresariales.Single(c => c.Centros.Any(x => x.Nombre == centro.Nombre));
                var destinatario = parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDeCoordinacionCae(especificacion.RazonSocial));
                var trabajador = trabajadores[caso.Trabajador];

                var conversacion = new Conversacion($"Documentación pendiente de {trabajador.Nombre} {trabajador.Apellidos}", clienteId: centro.ClienteId);
                conversacion.AgregarMensaje(
                    DireccionMensaje.Saliente, CanalConversacion.Correo, $"gestion-cae@{ContactosPilotoOutbound.DominioPorDefecto}",
                    $"<p>El documento «{caso.Tipo}» de {trabajador.Nombre} {trabajador.Apellidos}, que trabaja en {centro.Nombre}, ha vencido. " +
                    "Por favor, enviadnos el renovado.</p>",
                    enviada);
                conversacion.AgregarParticipante(destinatario, RolParticipante.Para, TipoParticipanteOrigen.Empresa, centro.ClienteId);
                if (respuesta is { } respondida)
                    conversacion.AgregarMensaje(
                        DireccionMensaje.Entrante, CanalConversacion.Correo, destinatario,
                        "<p>Recibido. El reconocimiento está pedido y os enviamos el certificado en cuanto lo tengamos.</p>",
                        respondida);
                dbContext.Conversaciones.Add(conversacion);

                var reclamacion = ReclamacionDocumental.ParaCliente(
                    centro.ClienteId, gestoraPrimeraId, destinatario, enviada, [documento.Id], conversacion.Id);
                dbContext.ReclamacionesDocumentales.Add(reclamacion);
                dbContext.EventosConversacion.Add(new EventoConversacion(
                    conversacion.Id, TipoEventoConversacion.ReclamacionEnviada, reclamacion.Id, enviada));
            }
        }

        /// <summary>Una Gestión pendiente por cada uno de los primeros documentos que faltan de un Trabajador con Centro.</summary>
        private void GestionesPendientesDelPerfil(
            List<Centro> centros, List<Trabajador> trabajadores, List<DesviacionDocumentoPilotoOutbound> desviaciones,
            Dictionary<int, List<int>> activas)
        {
            var faltan = desviaciones
                .Where(d => d.Situacion == SituacionDocumentoPilotoOutbound.Ausente && activas.ContainsKey(d.Trabajador))
                .Take(tenant.GestionesPendientes).ToList();
            if (faltan.Count < tenant.GestionesPendientes)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: pide {tenant.GestionesPendientes} Gestiones pendientes y solo faltan {faltan.Count} documentos de un Trabajador con Centro.");

            foreach (var d in faltan)
                dbContext.Gestiones.Add(new Gestion(
                    trabajadores[d.Trabajador].Id, centros[activas[d.Trabajador][0]].Id, Tipo(d.Tipo).Id,
                    notas: "Pedido al responsable de prevención; pendiente de recibir."));
        }

        /// <summary>
        /// Los Proyectos de obra del perfil de obras, cada uno en el Centro de su obra y con sus técnicos (los primeros
        /// Trabajadores activos del Centro); el cerrado, con todos sus técnicos dados de baja el día del cierre.
        /// </summary>
        private async Task ProyectosDelPerfilAsync(List<Centro> centros, List<Trabajador> trabajadores, Dictionary<int, List<int>> activas)
        {
            foreach (var obra in tenant.Obras)
            {
                var centro = centros[obra.Centro];
                var inicio = D.AddDays(obra.DiasDesdeElInicio);
                var proyecto = Proyecto.Crear(
                    _clienteEmpresarialDelCentro[centro.Id], centro.Id, obra.Nombre, inicio, obra.MesesPrevistos is { } meses ? inicio.AddMonths(meses) : null, notas: null);
                var cierre = obra.Cerrada ? inicio.AddMonths(obra.MesesPrevistos!.Value) : (DateOnly?)null;
                if (cierre is { } fecha) proyecto.Cerrar(fecha);
                dbContext.Proyectos.Add(proyecto);

                // El Libro de Subcontratación de la obra (art. 8 Ley 32/2006): lo abre el contratista principal el día que
                // empieza la obra y no caduca mientras dure. Un documento del Proyecto, con su PDF.
                var libro = Tipo(CatalogoPilotoOutbound.LibroDeSubcontratacion);
                var claveDelLibro = await GuardarPdfAsync(libro.Nombre, obra.Nombre, inicio, vence: null, pesado: false);
                Anadir(Documento.DeProyecto(proyecto.Id, libro.Id, inicio, VigenciaDocumento.NoCaduca, claveDelLibro));

                var tecnicos = Enumerable.Range(0, trabajadores.Count)
                    .Where(t => activas.TryGetValue(t, out var lista) && lista.Contains(obra.Centro)).Take(obra.Tecnicos).ToList();
                for (var j = 0; j < tecnicos.Count; j++)
                {
                    var alta = inicio.AddDays(j * 3);
                    var tecnico = new ProyectoTecnico(proyecto.Id, trabajadores[tecnicos[j]].Id, alta);
                    if (cierre is { } cerrado) tecnico.DarDeBaja(cerrado);
                    else if (j == 3) tecnico.DarDeBaja(alta.AddDays(30));
                    dbContext.ProyectosTecnicos.Add(tecnico);
                }
            }
        }

        /// <summary>
        /// La verificación externa de la Subcontrata Supervisada: la plataforma del Cliente empresarial dice, para cada
        /// tipo que exige, si la documentación es válida (una próxima a caducar y una no válida, con su observación).
        /// </summary>
        private void VerificacionesExternasDeLasSupervisadas(
            List<Centro> centros, List<Trabajador> trabajadores, List<Empresa> subcontratas, Dictionary<int, List<int>> activas)
        {
            var primeraFecha = D.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias - 1);

            for (var s = 0; s < tenant.Subcontratas.Count; s++)
            {
                if (!tenant.Subcontratas[s].Supervisada) continue;

                var primerTrabajador = tenant.Trabajadores + tenant.Subcontratas.Take(s).Sum(x => x.Trabajadores);
                var centro = centros[activas[primerTrabajador][0]];

                for (var j = 0; j < CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata.Count; j++)
                {
                    var noValido = j == CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata.Count - 1;
                    dbContext.VerificacionesExternaSubcontrata.Add(new VerificacionExternaSubcontrata(
                        subcontratas[s].Id, centro.Id, Tipo(CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata[j]).Id,
                        primeraFecha.AddDays(-j),
                        noValido ? ResultadoVerificacionExterna.NoValido : ResultadoVerificacionExterna.Valido,
                        gestoraPrimeraId,
                        validoHasta: noValido ? null : D.AddDays(j == 0 ? 25 : 200),
                        observaciones: noValido ? "La plataforma del Cliente empresarial rechaza la modalidad preventiva: falta el anexo firmado." : null));
                }
            }
        }

        /// <summary>
        /// El documento renovado: el que ya está sembrado es el vigente, y se le da la versión anterior, del mismo titular y
        /// tipo, con su PDF y con las fechas que el tipo le habría dado: venció el día en que se emitió el vigente. La
        /// sustitución va por el mismo camino de dominio que «Renovar» (<see cref="Documento.SustituirPor"/>, motivo
        /// Renovación): la anterior queda en el historial y deja de contar. Es la aptitud médica del primer Trabajador que la tiene en regla.
        /// </summary>
        private async Task DocumentoRenovadoAsync(
            List<Trabajador> trabajadores, Dictionary<(int Trabajador, string Tipo), Documento> documentos,
            List<DesviacionDocumentoPilotoOutbound> desviaciones)
        {
            var tipo = Tipo(CatalogoPilotoOutbound.AptitudMedica);
            var t = Enumerable.Range(0, trabajadores.Count).FirstOrDefault(
                i => documentos.ContainsKey((i, tipo.Nombre)) && desviaciones.All(d => !(d.Trabajador == i && d.Tipo == tipo.Nombre)), -1);
            if (t < 0 || !tipo.AplicaVencimientoAutomatico)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: no hay un Trabajador con la aptitud médica en regla para dar el documento renovado, o el tipo no vence solo.");

            var vigente = documentos[(t, tipo.Nombre)];
            var (emision, vence) = _fechas.ConVencimiento(tipo, vigente.FechaEmision);
            var anterior = await DocumentoDeTrabajadorAsync(trabajadores[t], tipo, emision, vence);
            anterior.SustituirPor(vigente, MotivoSustitucionDocumento.Renovacion, vigente.FechaEmision.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc));
        }

        /// <summary>
        /// La ficha de cada Trabajador, completa: afiliación, nacionalidad, tipo de vínculo, fechas de alta y de baja, figura
        /// en el RNT y, en los extranjeros, el permiso de trabajo y su caducidad. El alta es siempre anterior a cualquier
        /// formación o aptitud de la persona.
        /// </summary>
        /// <summary>
        /// Los Proyectos de tipo Evento del perfil, con sus requisitos copiados de la plantilla del Cliente empresarial (la
        /// copia es una foto, como al crear un evento desde una plantilla) y sus participantes. El alta en el evento es
        /// anterior a su inicio: los requisitos cuentan desde la asignación, también mientras el evento es futuro. Al
        /// terminado se le da la baja de los participantes y el cierre el día en que acabó. Comprueba que las fechas dan el
        /// estado que el catálogo declara todos los días en que se puede sembrar.
        /// </summary>
        private void EventosDelPerfil(List<Centro> centros, List<Trabajador> trabajadores)
        {
            if (tenant.Eventos.Count == 0) return;

            var plantillaDeclarada = tenant.PlantillaDeEventos
                ?? throw new InvalidOperationException($"{tenant.Clave}: los eventos necesitan la plantilla de requisitos del perfil.");
            var tipos = plantillaDeclarada.Tipos.Select(n => Tipo(n).Id).ToList();
            var plantillas = new Dictionary<Guid, PlantillaRequisitosEvento>();

            foreach (var evento in tenant.Eventos)
            {
                var centro = centros[evento.Centro];
                var clienteEmpresarialId = _clienteEmpresarialDelCentro[centro.Id];
                if (!plantillas.ContainsKey(clienteEmpresarialId))
                {
                    var plantilla = PlantillaRequisitosEvento.Crear(clienteEmpresarialId, plantillaDeclarada.Nombre, tipos);
                    dbContext.PlantillasRequisitosEvento.Add(plantilla);
                    plantillas[clienteEmpresarialId] = plantilla;
                }

                var inicio = D.AddDays(evento.DiasDesdeLaDemostracion);
                var fin = inicio.AddDays(evento.DuracionDias - 1);
                var primerDia = D.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);
                var estadoEnTodoElMargen = fin < primerDia ? EstadoEventoPilotoOutbound.Terminado
                    : inicio <= primerDia && fin >= D ? EstadoEventoPilotoOutbound.EnCurso
                    : inicio > D ? EstadoEventoPilotoOutbound.Futuro
                    : (EstadoEventoPilotoOutbound?)null;
                if (estadoEnTodoElMargen != evento.Estado)
                    throw new InvalidOperationException(
                        $"{tenant.Clave}: el evento «{evento.Nombre}» no está «{evento.Estado}» todos los días en que se puede sembrar.");

                var proyecto = Proyecto.Crear(
                    clienteEmpresarialId, centro.Id, evento.Nombre, inicio, fin, notas: null, TipoProyecto.Evento);
                if (evento.Estado == EstadoEventoPilotoOutbound.Terminado) proyecto.Cerrar(fin);
                dbContext.Proyectos.Add(proyecto);

                foreach (var tipo in tipos)
                    dbContext.ProyectosRequisitos.Add(new ProyectoRequisito(proyecto.Id, tipo));

                for (var j = 0; j < evento.Participantes.Count; j++)
                {
                    // Asignado antes del primer día en que se puede sembrar, también al futuro: nunca un alta por venir.
                    var porElInicio = inicio.AddDays(-(10 + j % 5));
                    var antesDelMargen = D.AddDays(-(OpcionesPilotoOutbound.MargenMaximoDias + 1 + j % 5));
                    var participante = new ProyectoTecnico(
                        proyecto.Id, trabajadores[evento.Participantes[j]].Id, porElInicio < antesDelMargen ? porElInicio : antesDelMargen);
                    if (evento.Estado == EstadoEventoPilotoOutbound.Terminado) participante.DarDeBaja(fin);
                    dbContext.ProyectosTecnicos.Add(participante);
                }
            }
        }

        private void FichasDeLosTrabajadores(List<Trabajador> trabajadores)
        {
            for (var i = 0; i < trabajadores.Count; i++)
            {
                var extranjero = tenant.Extranjeros.SingleOrDefault(e => e.Trabajador == i);
                var esPropio = i < tenant.Trabajadores;
                // El alta es anterior a cualquier documento de la relación laboral de la persona, también a una versión que
                // luego se sustituyó: la mayor antigüedad que el modelo de fechas daba por hecha (900 días y más) no lo
                // garantizaba con los documentos de vigencia larga o con un vencimiento ya pasado.
                var altaPorAntiguedad = D.AddDays(-(900 + i * 11));
                var alta = _emisionMasAntiguaPosteriorAlAlta.TryGetValue(trabajadores[i].Id, out var primeraEmision)
                           && primeraEmision.AddDays(-(30 + i % 20)) < altaPorAntiguedad
                    ? primeraEmision.AddDays(-(30 + i % 20))
                    : altaPorAntiguedad;
                var vinculo = tenant.Perfil == PerfilPilotoOutbound.Autonomo ? "Trabajo por cuenta propia"
                    : tenant.EsVoluntario(i) ? "Voluntariado (Ley 45/2015)"
                    : i == tenant.TrabajadorDeEtt ? "ETT (puesta a disposición)"
                    : i % 6 == 5 ? "Contrato temporal"
                    : "Contrato indefinido";

                trabajadores[i].ActualizarFicha(
                    numeroAfiliacionSeguridadSocial:
                        $"{28 + i % 5 * 4:D2}{10_000_000 + (i * 7_919 + _indiceTenant * 104_729) % 89_000_000:D8}{(i * 13 + 7) % 97:D2}",
                    puesto: trabajadores[i].Puesto,
                    fechaNacimiento: NacimientoDe(i),
                    nacionalidad: extranjero?.Nacionalidad ?? "Española",
                    tipoVinculo: vinculo,
                    fechaAltaEmpresa: alta,
                    fechaBajaEmpresa: esPropio && tenant.DeBaja.Contains(i) ? D.AddDays(-30) : null,
                    figuraEnRnt: esPropio && tenant.Perfil != PerfilPilotoOutbound.Autonomo && !tenant.EsVoluntario(i) ? "Sí" : "No",
                    tipoPermisoTrabajo: extranjero?.TipoPermiso,
                    caducidadPermisoTrabajo: extranjero is null ? null : D.AddDays(extranjero.DiasHastaLaCaducidad),
                    motivoPermisoTrabajo: extranjero is null ? null : "Autorización de residencia y trabajo por cuenta ajena");
                if (tenant.Perfil == PerfilPilotoOutbound.Autonomo)
                    trabajadores[i].CambiarTipoContratacion(TipoContratacion.Autonomo);
                else if (tenant.EsVoluntario(i))
                    trabajadores[i].CambiarTipoContratacion(TipoContratacion.Voluntario);
            }
        }

        /// <summary>
        /// Las Empresas subcontratistas de la Empresa propia, cada una con su Relación Empresarial (la subcontrata presta
        /// el servicio; quien la contrata —la Empresa propia u otra Subcontrata, en cadena— lo recibe), su contacto de
        /// agenda, su documentación de Empresa, toda Vigente, y sus Trabajadores, que se añaden a la lista a continuación
        /// de los propios. Ninguna Subcontrata es decorativa.
        /// </summary>
        private async Task<List<Empresa>> CrearSubcontratasAsync(Empresa propia, List<Trabajador> trabajadores)
        {
            var ahora = DateTime.UtcNow;
            var subcontratas = new List<Empresa>();

            for (var i = 0; i < tenant.Subcontratas.Count; i++)
            {
                var especificacion = tenant.Subcontratas[i];
                var nivel = especificacion.Supervisada ? NivelServicioSubcontrata.Supervisada : NivelServicioSubcontrata.Gestionada;
                var subcontrata = Empresa.CrearComoSubcontrata(
                    especificacion.RazonSocial, Cif(IdentidadesPilotoOutbound.OrdinalDeLaPrimeraSubcontrata + i), nivel.ToString());
                dbContext.Empresas.Add(subcontrata);
                subcontratas.Add(subcontrata);

                var contratante = especificacion.ContratadaPor is { } c ? subcontratas[c] : propia;
                dbContext.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrata.Id, contratante.Id, ahora));
                AnadirContacto(ContactoAgenda.DeSubcontrata(
                    subcontrata.Id, Persona(45 + i), parametros.Contactos.DireccionDe(IdentidadesPilotoOutbound.EtiquetaDePrevencion(especificacion.RazonSocial)),
                    cargo: "Responsable de prevención", esPredeterminado: true), RolContacto.ResponsablePrl);

                foreach (var nombreTipo in CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata)
                {
                    var tipo = Tipo(nombreTipo);
                    var (emision, vence) = _fechas.EnRegla(tipo, semillaDeEmision: Siguiente(), semillaDeVencimiento: Siguiente());
                    var clave = await GuardarPdfAsync(tipo.Nombre, especificacion.RazonSocial, emision, vence, pesado: false);
                    Anadir(Documento.DeEmpresa(subcontrata.Id, tipo.Id, emision, Vigencia(vence), clave));
                }

                for (var k = 0; k < especificacion.Trabajadores; k++)
                {
                    var indice = trabajadores.Count;
                    var (nombre, apellidos) = IdentidadesPilotoOutbound.Trabajador(tenant, indice);
                    var trabajador = Trabajador.DeSubcontrata(
                        subcontrata.Id, nombre, apellidos, DniDe(indice), fechaNacimiento: NacimientoDe(indice),
                        puesto: IdentidadesPilotoOutbound.Puesto(tenant, indice));
                    dbContext.Trabajadores.Add(trabajador);
                    trabajadores.Add(trabajador);
                }
            }

            return subcontratas;
        }

        /// <summary>
        /// El catálogo del piloto declara estos requisitos para sus Centros. Se guardan como
        /// inclusiones explícitas para el porcentaje, conservando las exclusiones y ajustes
        /// particulares ya sembrados por cada escenario. No modifica Tenants existentes.
        /// </summary>
        private void MaterializarInclusionesDeLosCentros(IReadOnlyCollection<Centro> centros)
        {
            var configurados = dbContext.TiposDocumentoCentros.Local
                .Select(tc => (tc.CentroId, tc.TipoDocumentoId)).ToHashSet();
            foreach (var centro in centros)
            {
                foreach (var nombre in CatalogoPilotoOutbound.TiposExigidosDeTrabajador)
                {
                    var tipoId = Tipo(nombre).Id;
                    if (configurados.Add((centro.Id, tipoId)))
                        dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoId, centro.Id, incluido: true));
                }
            }
        }

        /// <summary>
        /// Las fechas de un documento de Trabajador en regla (<see cref="FechasPilotoOutbound.EnRegla"/>).
        /// Pide al contador lo mismo que cuando la emisión y el vencimiento se fechaban por separado
        /// —dos fechas si el tipo vence solo, y una si no—: así ningún otro documento cambia de fecha.
        /// </summary>
        private (DateOnly Emision, DateOnly? Vence) FechasEnReglaDeTrabajador(TipoDocumento tipo)
        {
            int? semillaDeVencimiento = tipo.AplicaVencimientoAutomatico ? Siguiente() : null;
            return _fechas.EnRegla(tipo, Siguiente(), semillaDeVencimiento);
        }

        /// <param name="sinConfirmar">Sin fecha de vencimiento y sin confirmar que no caduca: el documento nace «Sin confirmar» en vez de «Sin caducidad».</param>
        private async Task<Documento> DocumentoDeTrabajadorAsync(
            Trabajador trabajador, TipoDocumento tipo, DateOnly emision, DateOnly? vence, bool sinConfirmar = false)
        {
            if (sinConfirmar && vence is not null)
                throw new InvalidOperationException($"{tenant.Clave}: un documento sin confirmar no lleva fecha de vencimiento.");

            if (CatalogoPilotoOutbound.TiposPosterioresAlAlta.Contains(tipo.Nombre)
                && (!_emisionMasAntiguaPosteriorAlAlta.TryGetValue(trabajador.Id, out var masAntigua) || emision < masAntigua))
                _emisionMasAntiguaPosteriorAlAlta[trabajador.Id] = emision;

            var clave = await GuardarPdfAsync(tipo.Nombre, $"{trabajador.Nombre} {trabajador.Apellidos}", emision, vence, pesado: false);
            var documento = Documento.DeTrabajador(
                trabajador.Id, tipo.Id, emision, sinConfirmar ? VigenciaDocumento.SinConfirmar : Vigencia(vence), clave);
            Anadir(documento);
            return documento;
        }

        /// <summary>
        /// Genera el PDF y lo guarda por el almacén de la aplicación; solo se conserva la
        /// clave. Los bytes se sueltan al salir: nunca hay más de un PDF en memoria.
        /// </summary>
        private async Task<string> GuardarPdfAsync(string tipo, string titular, DateOnly emision, DateOnly? vence, bool pesado)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pdf = PilotoOutboundPdf.Generar(new PilotoOutboundPdf.Datos(tipo, titular, tenant.Nombre, emision, vence), pesado);
            var clave = await almacen.GuardarAsync(pdf, "documento.pdf", cancellationToken);
            _clavesDePdf.Add(clave);
            return clave;
        }

        private void Anadir(Documento documento)
        {
            dbContext.Documentos.Add(documento);
            Documentos++;
        }

        private void AnadirContacto(ContactoAgenda contacto, RolContacto rol)
        {
            contacto.EstablecerRoles([rol]);
            dbContext.ContactosAgenda.Add(contacto);
        }

        private static VigenciaDocumento Vigencia(DateOnly? vence) =>
            vence is { } fecha ? VigenciaDocumento.VenceEl(fecha) : VigenciaDocumento.NoCaduca;

        private TipoDocumento Tipo(string nombre) => _tipos.Single(t => t.Nombre == nombre);

        private List<TipoDocumento> TiposExigidos(AmbitoAplicacion ambito) =>
            [.. _tipos.Where(t => t.AmbitoAplicacion == ambito && t.Requerido == RequisitoDocumental.Si).OrderBy(t => t.Orden).ThenBy(t => t.Nombre, StringComparer.Ordinal)];

        /// <summary>Un entero creciente para repartir las fechas dentro de sus márgenes sin azar.</summary>
        private int Siguiente() => _semilla++ * 37 + _indiceTenant * 11;

        /// <summary>Con lo que acaba el código de una plataforma que el catálogo conserva solo por sus accesos antiguos: la demostración no abre canales en ella.</summary>
        private const string SufijoDePlataformaRetirada = "-legacy";

        private string Cif(int ordinal) => CifDe(tenant, ordinal);

        /// <summary>
        /// El nombre de un contacto de agenda. Cada uso tiene su tramo de ordinales —40 y 41 la Empresa propia, desde 42 sus
        /// zonas, desde 45 las subcontratas, desde 50 los Clientes empresariales y desde 71 los Centros— para que dos
        /// contactos de un mismo Tenant no sean la misma persona.
        /// </summary>
        private string Persona(int ordinal) => IdentidadesPilotoOutbound.Contacto(tenant, ordinal);

        /// <summary>El contacto del Centro número <paramref name="numero"/> (desde 1), que es también la persona de su canal por correo.</summary>
        private static int OrdinalDelContactoDeCentro(int numero) => 70 + numero;
    }
}
