using System.Diagnostics;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
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
/// demostración con Asignación de Operación sobre seis Tenants propietarios
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
/// idempotencia mira la Empresa propia: si existe, el Tenant está sembrado y no se
/// toca; si no, se siembra entero. Por eso una ejecución cortada también se puede
/// reanudar sin retirar.
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

    /// <param name="Escribio">Falso en un re-arranque que encontró todo sembrado.</param>
    /// <param name="TenantsConDatosNuevos">Nombres de los Tenants propietarios cuyos datos escribió esta ejecución.</param>
    public sealed record Resultado(bool Escribio, IReadOnlyList<string> TenantsConDatosNuevos, int Documentos, int Pdf, TimeSpan Duracion);

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
    /// marcador de demo y que la fecha de la demostración sirva para sembrar hoy.
    ///
    /// <para>
    /// La fecha solo se exige si hay algo que escribir. Con el lote ya sembrado
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

        if (OpcionesPilotoOutbound.MotivoFechaNoUtilizable(parametros.FechaDemostracion, DiaDeNegocio.Hoy()) is { } motivo)
        {
            if (await PrimerTenantSinSembrarAsync(dbContext, cancellationToken) is { } pendiente)
                throw new InvalidOperationException($"{motivo} Queda por sembrar «{pendiente}», así que la siembra se niega y no escribe nada.");

            logger.LogWarning(
                "Siembra del piloto Outbound: {Motivo} El lote ya está sembrado entero, así que no se escribe nada y el " +
                "arranque continúa; los estados de los documentos ya no son los del día de la demostración.", motivo);
            return new Resultado(false, [], 0, 0, TimeSpan.Zero);
        }

        var cronometro = Stopwatch.StartNew();
        var recuento = new Recuento();

        var tenantOperadorId = await AprovisionarConMarcadorAsync(
            dbContext, CatalogoPilotoOutbound.NombreTenantOperador, PerfilVocabularioTenant.Consultora,
            esOperadorCaeExterno: true, recuento, logger, cancellationToken);

        var equipo = await SembrarEquipoAsync(
            dbContext, userManager, userStore, entorno, parametros, logger, tenantOperadorId, cancellationToken);

        foreach (var tenant in CatalogoPilotoOutbound.EnOrdenDeSiembra)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tenantPropietarioId = await AprovisionarConMarcadorAsync(
                dbContext, tenant.Nombre, PerfilVocabularioTenant.ClienteDirecto,
                esOperadorCaeExterno: false, recuento, logger, cancellationToken);

            await AbrirOperacionExternaAsync(
                dbContext, tenant, tenantPropietarioId, tenantOperadorId, equipo, logger, cancellationToken);

            if (ReferenceEquals(tenant, CatalogoPilotoOutbound.T1))
                await CrearCuentaAsync(
                    dbContext, userManager, userStore, entorno, parametros, logger, tenantPropietarioId,
                    parametros.Cuentas.AdministradorT1, CatalogoPilotoOutbound.NombreAdministradorT1, Roles.Administrador, cancellationToken);

            if (await SembrarDatosAsync(
                    dbContext, almacen, tenant, tenantPropietarioId, parametros, equipo.GestoraPrimeraId, recuento, cancellationToken))
                recuento.TenantsConDatosNuevos.Add(tenant.Nombre);

            // Un Tenant a la vez en memoria: lo rastreado de este no acompaña al siguiente.
            dbContext.ChangeTracker.Clear();
        }

        cronometro.Stop();
        var resultado = new Resultado(
            recuento.TenantsCreados > 0 || recuento.TenantsConDatosNuevos.Count > 0,
            recuento.TenantsConDatosNuevos, recuento.Documentos, recuento.Pdf, cronometro.Elapsed);

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

    private sealed record Equipo(ApplicationUser Administrador, Guid GestoraPrimeraId, Guid GestorSegundoId, Guid CoordinadoraId);

    /// <summary>
    /// Un nombre del piloto ocupado por un Tenant sin marcador de demo puede ser un
    /// Tenant real que se llama igual: no se escribe en él ni a su lado.
    /// </summary>
    private static async Task RechazarNombresOcupadosPorUnTenantSinMarcadorAsync(
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
    /// El primer Tenant del lote que una ejecución todavía tendría que escribir, o
    /// <c>null</c> si están todos: el del Operador CAE externo tiene que existir, y
    /// cada Tenant propietario, existir y tener ya su Empresa propia, que es lo que
    /// mira la idempotencia de sus datos (<see cref="SembrarDatosAsync"/>). Solo lee.
    /// </summary>
    private static async Task<string?> PrimerTenantSinSembrarAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var existentes = await dbContext.Tenants
            .Where(t => nombres.Contains(t.Nombre))
            .Select(t => new { t.Id, t.Nombre }).ToListAsync(cancellationToken);

        if (existentes.All(t => t.Nombre != CatalogoPilotoOutbound.NombreTenantOperador))
            return CatalogoPilotoOutbound.NombreTenantOperador;

        foreach (var tenant in CatalogoPilotoOutbound.EnOrdenDeSiembra)
        {
            if (existentes.FirstOrDefault(t => t.Nombre == tenant.Nombre) is not { } existente)
                return tenant.Nombre;

            var cifEmpresaPropia = CifDe(tenant, 0);
            using (AmbitoTenantExplicito.Establecer(existente.Id))
            {
                if (!await dbContext.Empresas.AnyAsync(e => e.Cif == cifEmpresaPropia, cancellationToken))
                    return tenant.Nombre;
            }
        }

        return null;
    }

    /// <summary>El identificador fiscal de una Empresa del piloto: el ordinal 0 es la Empresa propia del Tenant.</summary>
    private static string CifDe(TenantPilotoOutbound tenant, int ordinal) =>
        DatosPruebaSeeder.GenerarCifValido(8_100_000 + CatalogoPilotoOutbound.Tenants.ToList().IndexOf(tenant) * 100 + ordinal);

    /// <summary>
    /// Aprovisiona el Tenant y lo marca como demo de inmediato, antes de escribir
    /// sus datos: lo que quede de una ejecución cortada tiene que poder retirarse.
    /// </summary>
    private static async Task<Guid> AprovisionarConMarcadorAsync(
        CaeManagerDbContext dbContext, string nombre, PerfilVocabularioTenant perfil, bool esOperadorCaeExterno,
        Recuento recuento, ILogger logger, CancellationToken cancellationToken)
    {
        var existia = await dbContext.Tenants.AnyAsync(t => t.Nombre == nombre, cancellationToken);

        var tenantId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
            dbContext, nombre, perfil, logger, cancellationToken, esOperadorCaeExterno);
        await SiembraDemoDireccionAdministrativa.MarcarComoDemoAsync(dbContext, tenantId, cancellationToken);

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
        }

        return new Equipo(administrador, gestoraPrimera.Id, gestorSegundo.Id, coordinadora.Id);
    }

    private static async Task<ApplicationUser> CrearCuentaAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, IUserStore<ApplicationUser> userStore,
        IHostEnvironment entorno, Parametros parametros, ILogger logger, Guid tenantId, string email, string nombre, string rol,
        CancellationToken cancellationToken)
    {
        var usuario = await DelegacionDemoSeeder.CrearUsuarioConsultoraAsync(
                dbContext, userManager, parametros.CredencialesDe(email), logger, tenantId, email, nombre, rol, cancellationToken)
            ?? throw new InvalidOperationException($"No se pudo sembrar la cuenta de {rol} del piloto (ver el aviso anterior del registro).");

        // P1-13: todo Administrador con segundo factor. Solo en Development se asigna el de siembra;
        // en cualquier otro entorno la cuenta lo configura en su primer acceso.
        if (rol == Roles.Administrador && !usuario.TwoFactorEnabled)
        {
            using (AmbitoTenantExplicito.Establecer(tenantId))
                await IdentitySeeder.AsignarSegundoFactorDeSiembraAsync(usuario, userManager, userStore, entorno, cancellationToken);
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

        var gestores = new List<Guid> { equipo.GestoraPrimeraId };
        if (tenant.EnCarteraDelGestorSegundo) gestores.Add(equipo.GestorSegundoId);

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
    /// ya estaba sembrado (su Empresa propia existe). Con cualquier excepción,
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
        private IReadOnlyList<TipoDocumento> _tipos = [];
        private int _semilla;

        public IReadOnlyList<string> ClavesDePdf => _clavesDePdf;
        public int Documentos { get; private set; }
        public string CifEmpresaPropia => Cif(0);

        private DateOnly D => parametros.FechaDemostracion;

        public async Task ConstruirAsync()
        {
            _tipos = await dbContext.TiposDocumento.ToListAsync(cancellationToken);
            ComprobarCatalogoDeTipos();

            var propia = new Empresa(tenant.Nombre, CifEmpresaPropia);
            dbContext.Empresas.Add(propia);
            AnadirContacto(ContactoAgenda.DeEmpresa(
                propia.Id, Persona(40), parametros.Contactos.DireccionDe(Etiqueta("propia")),
                cargo: "Responsable de prevención", esPredeterminado: true), RolContacto.ResponsablePrl);
            AnadirContacto(ContactoAgenda.DeEmpresa(
                propia.Id, Persona(41), parametros.Contactos.DireccionDe(Etiqueta("administracion")),
                cargo: "Administración", recibeFacturacion: true), RolContacto.RepresentanteLegal);

            if (tenant.Escenario == EscenarioPilotoOutbound.Esqueleto)
                return;

            var centros = CrearClientesEmpresarialesYCentros(propia);
            var trabajadores = CrearTrabajadores(propia);

            switch (tenant.Escenario)
            {
                case EscenarioPilotoOutbound.TodoAlDia:
                    await ConstruirTodoAlDiaAsync(propia, centros, trabajadores);
                    break;
                case EscenarioPilotoOutbound.Mitad:
                    await ConstruirMitadAsync(centros, trabajadores);
                    break;
                case EscenarioPilotoOutbound.TodoPendiente:
                    ConstruirTodoPendiente(centros, trabajadores);
                    break;
                case EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros:
                    await ConstruirPocosTrabajadoresMuchosCentrosAsync(centros, trabajadores);
                    break;
                case EscenarioPilotoOutbound.Territorial:
                    await ConstruirTerritorialAsync(propia, centros, trabajadores);
                    break;
                default:
                    throw new InvalidOperationException($"El escenario {tenant.Escenario} de {tenant.Clave} no tiene constructor en la siembra del piloto.");
            }
        }

        /// <summary>
        /// Las fechas y los conjuntos exigidos de esta siembra dan por hecho qué tipos
        /// se piden por defecto. Si el catálogo del Tenant dijera otra cosa, los
        /// resultados de la matriz no saldrían: mejor fallar aquí, con el motivo.
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
                    clienteEmpresarial.Id, Persona(50 + i), parametros.Contactos.DireccionDe(Etiqueta($"cliente{i + 1}"))), RolContacto.ContactoCae);

                foreach (var especificacionCentro in especificacion.Centros)
                {
                    var numero = centros.Count + 1;
                    var centro = new Centro(
                        clienteEmpresarial.Id, propia.Id, especificacionCentro.Nombre, tenant.CodigoDe(especificacionCentro),
                        $"Avenida de la Industria, {numero * 7}, {especificacionCentro.Localidad}",
                        contratoVigenteHasta: D.AddDays(300 + numero * 20));
                    dbContext.Centros.Add(centro);
                    centros.Add(centro);

                    AnadirContacto(ContactoAgenda.DeCentro(
                        centro.Id, Persona(60 + numero), parametros.Contactos.DireccionDe(Etiqueta($"centro{numero}")),
                        cargo: "Coordinación de accesos", esPredeterminado: true, recibeProgramacionVisitas: true), RolContacto.ResponsablePrl);
                }
            }

            return centros;
        }

        /// <summary>
        /// El único sitio que usa las fábricas heredadas «…Cliente»: lo que crean es un
        /// Cliente empresarial (la Empresa que recibe el servicio en la Relación
        /// Empresarial) y su contacto de agenda. La Gestora CAE queda como referencia;
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
                var trabajador = Trabajador.DeEmpresa(
                    propia.Id,
                    CatalogoPilotoOutbound.NombresDePila[(i + _indiceTenant * 3) % CatalogoPilotoOutbound.NombresDePila.Length],
                    // El sumando de la vuelta: con más de dieciséis Trabajadores, el 17.º no repite el nombre completo del 1.º.
                    CatalogoPilotoOutbound.Apellidos[(i * 5 + _indiceTenant * 7 + i / 16 * 3) % CatalogoPilotoOutbound.Apellidos.Length],
                    DatosPruebaSeeder.GenerarDniValido(71_000_000 + _indiceTenant * 1_000 + i),
                    fechaNacimiento: D.AddYears(-(25 + i * 7 % 30)).AddDays(-i * 13));
                dbContext.Trabajadores.Add(trabajador);
                trabajadores.Add(trabajador);
            }

            return trabajadores;
        }

        /// <summary>
        /// T2: las seis condiciones de «todo al día» a la vez. Cada Trabajador —también los
        /// dos con la Asignación dada de baja— tiene los cinco tipos exigidos, Vigentes o
        /// sin caducidad; la Empresa, todos los suyos (uno con PDF pesado); un Vehículo
        /// con los suyos; un Centro con plataforma y todo Aceptado, y con un requisito
        /// que bloquea el acceso, cumplido; una Visita lejos de las 48 horas.
        /// </summary>
        private async Task ConstruirTodoAlDiaAsync(Empresa propia, List<Centro> centros, List<Trabajador> trabajadores)
        {
            foreach (var (trabajador, centro) in CatalogoPilotoOutbound.AsignacionesActivasT2)
                dbContext.Asignaciones.Add(new Asignacion(trabajadores[trabajador].Id, centros[centro].Id, D.AddDays(-200 - trabajador * 9)));

            foreach (var (trabajador, centro) in CatalogoPilotoOutbound.AsignacionesDeBajaT2)
            {
                var asignacion = new Asignacion(trabajadores[trabajador].Id, centros[centro].Id, D.AddDays(-300));
                asignacion.DarDeBaja(D.AddDays(-40));
                dbContext.Asignaciones.Add(asignacion);
            }

            var documentosPorTrabajador = new Dictionary<Guid, List<Documento>>();
            foreach (var trabajador in trabajadores)
            {
                var documentos = new List<Documento>();
                foreach (var nombreTipo in CatalogoPilotoOutbound.TiposExigidosDeTrabajador)
                {
                    var tipo = Tipo(nombreTipo);
                    var vence = tipo.AplicaVencimientoAutomatico ? _fechas.Vigente(Siguiente()) : (DateOnly?)null;
                    documentos.Add(await DocumentoDeTrabajadorAsync(trabajador, tipo, _fechas.Emision(Siguiente()), vence));
                }

                documentosPorTrabajador[trabajador.Id] = documentos;
            }

            var tiposDeEmpresa = TiposExigidos(AmbitoAplicacion.Empresa);
            for (var i = 0; i < tiposDeEmpresa.Count; i++)
            {
                var tipo = tiposDeEmpresa[i];
                var vence = tipo.AplicaVencimientoAutomatico || i % 2 == 0 ? _fechas.Vigente(Siguiente()) : (DateOnly?)null;
                var emision = _fechas.Emision(Siguiente());
                var clave = await GuardarPdfAsync(
                    tipo.Nombre, tenant.Nombre, emision, vence, pesado: tipo.Nombre == CatalogoPilotoOutbound.TipoDeEmpresaConPdfPesado);
                Anadir(Documento.DeEmpresa(propia.Id, tipo.Id, emision, Vigencia(vence), clave));
            }

            var vehiculo = Vehiculo.DeEmpresa(propia.Id, "Furgón de taller", "Furgón de carga media", "4821KBT");
            dbContext.Vehiculos.Add(vehiculo);
            foreach (var tipo in TiposExigidos(AmbitoAplicacion.Vehiculo))
            {
                var (emision, vence) = (_fechas.Emision(Siguiente()), _fechas.Vigente(Siguiente()));
                var clave = await GuardarPdfAsync(tipo.Nombre, $"{vehiculo.Nombre} ({vehiculo.NumeroPlaca})", emision, vence, pesado: false);
                Anadir(Documento.DeVehiculo(vehiculo.Id, tipo.Id, emision, Vigencia(vence), clave));
            }

            var centroConPlataforma = centros[CatalogoPilotoOutbound.IndiceCentroConPlataformaT2];
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                Tipo(CatalogoPilotoOutbound.TipoBloqueanteCumplidoT2).Id, centroConPlataforma.Id, incluido: true, bloqueaAcceso: true));

            var proveedor = await dbContext.ProveedoresPlataformaCae
                    .Where(p => p.Activo).OrderBy(p => p.Codigo).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No hay ningún proveedor de plataforma CAE activo: {tenant.Clave} necesita un Centro con canal de plataforma.");

            // La contraseña es un texto fijo que no abre nada: es el dato del portal de un
            // Cliente empresarial ficticio, no una credencial de TALVEG.
            var canal = CanalGestionDocumental.DePlataforma(
                centroConPlataforma.Id, "Gestión general", proveedor.Id,
                $"https://portal-demo-{proveedor.Codigo}.local", $"gestion.{tenant.Clave.ToLowerInvariant()}", "sin-credencial-real");
            canal.MarcarComoPrincipal();
            dbContext.CanalesGestionDocumental.Add(canal);

            var enElCentro = CatalogoPilotoOutbound.AsignacionesActivasT2
                .Where(a => a.Centro == CatalogoPilotoOutbound.IndiceCentroConPlataformaT2)
                .Select(a => trabajadores[a.Trabajador].Id);
            var n = 0;
            foreach (var documento in enElCentro.SelectMany(id => documentosPorTrabajador[id]))
            {
                var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
                acreditacion.MarcarSubida();
                acreditacion.MarcarAceptada(n++ % 2 == 0 ? VigenciaEnPlataforma.NoVenceAqui : VigenciaEnPlataforma.VenceEl(D.AddDays(180 + n)));
                dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            }

            var visita = new Visita(centros[2].Id, D.AddDays(15), D.AddDays(15), "Revisión trimestral de instalaciones", OrigenVisita.Plataforma);
            dbContext.Visitas.Add(visita);
            foreach (var (trabajador, _) in CatalogoPilotoOutbound.AsignacionesActivasT2.Where(a => a.Centro == 2))
                dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajadores[trabajador].Id));
        }

        /// <summary>T3: ver <see cref="ConstruccionMitadPilotoOutbound"/>.</summary>
        private async Task ConstruirMitadAsync(List<Centro> centros, List<Trabajador> trabajadores)
        {
            var parejas = centros.Count / 2;
            for (var pareja = 0; pareja < parejas; pareja++)
            {
                ExigirExactamente(centros[pareja * 2], [ConstruccionMitadPilotoOutbound.TipoVigente, ConstruccionMitadPilotoOutbound.TipoVencido]);
                ExigirExactamente(centros[pareja * 2 + 1], [ConstruccionMitadPilotoOutbound.TipoVigente, ConstruccionMitadPilotoOutbound.TipoAusente]);
            }

            for (var i = 0; i < trabajadores.Count; i++)
            {
                var pareja = i % parejas;
                dbContext.Asignaciones.Add(new Asignacion(trabajadores[i].Id, centros[pareja * 2].Id, D.AddDays(-180 - i * 5)));
                dbContext.Asignaciones.Add(new Asignacion(trabajadores[i].Id, centros[pareja * 2 + 1].Id, D.AddDays(-150 - i * 5)));

                await DocumentoDeTrabajadorAsync(
                    trabajadores[i], Tipo(ConstruccionMitadPilotoOutbound.TipoVigente), _fechas.Emision(Siguiente()), _fechas.Vigente(Siguiente()));

                var vencido = _fechas.Vencido(Siguiente());
                await DocumentoDeTrabajadorAsync(
                    trabajadores[i], Tipo(ConstruccionMitadPilotoOutbound.TipoVencido), FechasPilotoOutbound.EmisionDe(vencido), vencido);
            }
        }

        /// <summary>
        /// T4: requisitos definidos por Centro y cada Trabajador en un solo Centro.
        /// Ningún documento, nunca: es una alta reciente.
        /// </summary>
        private void ConstruirTodoPendiente(List<Centro> centros, List<Trabajador> trabajadores)
        {
            var siguienteTrabajador = 0;
            for (var c = 0; c < centros.Count; c++)
            {
                var (cuantos, tiposDelCentro) = CatalogoPilotoOutbound.RequisitosPorCentroT4[c];
                ExigirExactamente(centros[c], tiposDelCentro);

                for (var k = 0; k < cuantos; k++)
                    dbContext.Asignaciones.Add(new Asignacion(trabajadores[siguienteTrabajador++].Id, centros[c].Id, D.AddDays(-20 - k)));
            }

            if (siguienteTrabajador != trabajadores.Count)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: el catálogo reparte {siguienteTrabajador} Trabajadores entre sus Centros y declara {trabajadores.Count}.");
        }

        /// <summary>T5: ver <see cref="DisenoT5PilotoOutbound"/>.</summary>
        private async Task ConstruirPocosTrabajadoresMuchosCentrosAsync(List<Centro> centros, List<Trabajador> trabajadores)
        {
            for (var t = 0; t < trabajadores.Count; t++)
            {
                for (var c = 0; c < DisenoT5PilotoOutbound.CentrosPorTrabajador[t]; c++)
                    dbContext.Asignaciones.Add(new Asignacion(trabajadores[t].Id, centros[c].Id, D.AddDays(-120 - t * 11 - c * 3)));
            }

            // El mismo tipo, requisito que bloquea el acceso en tres Centros, cada uno con sus condiciones.
            var conCondiciones = Tipo(DisenoT5PilotoOutbound.TipoConCondicionesPorCentro);
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                conCondiciones.Id, centros[DisenoT5PilotoOutbound.CentroSinCondicionesPropias].Id, incluido: true, bloqueaAcceso: true));
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                conCondiciones.Id, centros[DisenoT5PilotoOutbound.CentroConPeriodicidadEspecial].Id, incluido: true,
                periodicidadEspecialMeses: DisenoT5PilotoOutbound.MesesDePeriodicidadEspecial, bloqueaAcceso: true));
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                conCondiciones.Id, centros[DisenoT5PilotoOutbound.CentroConPeriodicidadEspecialYTolerancia].Id, incluido: true,
                periodicidadEspecialMeses: DisenoT5PilotoOutbound.MesesDePeriodicidadEspecial, bloqueaAcceso: true,
                toleranciaDias: DisenoT5PilotoOutbound.DiasDeTolerancia));

            // Tolerancia sin bloqueo: aquí solo cambia cómo se rotula el documento vencido.
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                Tipo(DisenoT5PilotoOutbound.TipoVencidoEnTolerancia).Id, centros[DisenoT5PilotoOutbound.CentroConToleranciaParaElVencido].Id,
                incluido: true, toleranciaDias: DisenoT5PilotoOutbound.DiasDeToleranciaParaElVencido));

            var fechasFijadas = new Dictionary<(int Trabajador, string Tipo), (DateOnly Emision, DateOnly Vence)>();
            for (var t = 0; t < trabajadores.Count; t++)
            {
                // Emitido hace seis meses y veinte días: Vigente por su fecha, vencido donde se renueva cada seis meses.
                var emision = DisenoT5PilotoOutbound.TrabajadoresConAptitudAntigua.Contains(t)
                    ? D.AddDays(-DisenoT5PilotoOutbound.DiasDesdeElVencimientoPorPeriodicidad).AddMonths(-DisenoT5PilotoOutbound.MesesDePeriodicidadEspecial)
                    : D.AddDays(-60);
                fechasFijadas[(t, DisenoT5PilotoOutbound.TipoConCondicionesPorCentro)] = (emision, emision.AddYears(1));
            }

            var vencido = D.AddDays(-DisenoT5PilotoOutbound.DiasDesdeQueVencio);
            fechasFijadas[(DisenoT5PilotoOutbound.TrabajadorConElVencido, DisenoT5PilotoOutbound.TipoVencidoEnTolerancia)] =
                (FechasPilotoOutbound.EmisionDe(vencido), vencido);

            await DocumentosDeTrabajadoresAsync(trabajadores, DisenoT5PilotoOutbound.Desviaciones, fechasFijadas);
        }

        /// <summary>T6: ver <see cref="DisenoT6PilotoOutbound"/>.</summary>
        private async Task ConstruirTerritorialAsync(Empresa propia, List<Centro> centros, List<Trabajador> trabajadores)
        {
            // «centros» va en el orden de tenant.Centros: la posición dentro de la zona se resuelve por el catálogo.
            var enCatalogo = tenant.Centros.ToList();
            Centro CentroDe(ZonaPilotoOutbound zona, int posicion) => centros[enCatalogo.IndexOf(tenant.CentrosDe(zona)[posicion])];

            for (var z = 0; z < DisenoT6PilotoOutbound.Zonas.Count; z++)
            {
                var zona = DisenoT6PilotoOutbound.Zonas[z];
                AnadirContacto(ContactoAgenda.DeEmpresa(
                    propia.Id, Persona(42 + z), parametros.Contactos.DireccionDe(Etiqueta($"coordinacion-{zona.Codigo.ToLowerInvariant()}")),
                    cargo: DisenoT6PilotoOutbound.CargoDeCoordinacion(zona)), RolContacto.ContactoCae);
            }

            foreach (var (trabajador, zona, centro) in DisenoT6PilotoOutbound.AsignacionesActivas)
                dbContext.Asignaciones.Add(new Asignacion(trabajadores[trabajador].Id, CentroDe(zona, centro).Id, D.AddDays(-90 - trabajador * 4)));

            // El desplazamiento temporal: el mismo Trabajador, otra Asignación, con alta y con baja.
            var (zonaDeDestino, centroDeDestino) = DisenoT6PilotoOutbound.CentroDelDesplazamiento;
            var desplazamiento = new Asignacion(
                trabajadores[DisenoT6PilotoOutbound.TrabajadorDesplazado].Id, CentroDe(zonaDeDestino, centroDeDestino).Id,
                D.AddDays(-DisenoT6PilotoOutbound.DiasDelAltaAntesDeLaDemostracion));
            desplazamiento.DarDeBaja(D.AddDays(DisenoT6PilotoOutbound.DiasDeLaBajaTrasLaDemostracion));
            dbContext.Asignaciones.Add(desplazamiento);

            var (zonaBloqueante, centroBloqueante) = DisenoT6PilotoOutbound.CentroConRequisitoBloqueante;
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                Tipo(DisenoT6PilotoOutbound.TipoBloqueante).Id, CentroDe(zonaBloqueante, centroBloqueante).Id, incluido: true, bloqueaAcceso: true));

            await DocumentosDeTrabajadoresAsync(trabajadores, DisenoT6PilotoOutbound.Desviaciones);
            await CrearSubcontratasAsync(propia);
        }

        /// <summary>
        /// Las Empresas subcontratistas de la Empresa propia, cada una con su Relación
        /// Empresarial (la subcontrata presta el servicio; la Empresa propia lo recibe),
        /// su contacto de agenda y su documentación de Empresa, toda Vigente. Sin
        /// Trabajadores: no añaden pares exigidos ni filas a Mi trabajo.
        /// </summary>
        private async Task CrearSubcontratasAsync(Empresa propia)
        {
            var ahora = DateTime.UtcNow;
            var razonesSociales = tenant.Subcontratas ?? [];

            for (var i = 0; i < razonesSociales.Count; i++)
            {
                var subcontrata = Empresa.CrearComoSubcontrata(razonesSociales[i], Cif(20 + i), NivelServicioSubcontrata.Gestionada.ToString());
                dbContext.Empresas.Add(subcontrata);
                dbContext.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrata.Id, propia.Id, ahora));
                AnadirContacto(ContactoAgenda.DeSubcontrata(
                    subcontrata.Id, Persona(45 + i), parametros.Contactos.DireccionDe(Etiqueta($"subcontrata{i + 1}")),
                    cargo: "Responsable de prevención", esPredeterminado: true), RolContacto.ResponsablePrl);

                foreach (var nombreTipo in DisenoT6PilotoOutbound.TiposDeDocumentoDeSubcontrata)
                {
                    var tipo = Tipo(nombreTipo);
                    var (emision, vence) = (_fechas.Emision(Siguiente()), _fechas.Vigente(Siguiente()));
                    var clave = await GuardarPdfAsync(tipo.Nombre, razonesSociales[i], emision, vence, pesado: false);
                    Anadir(Documento.DeEmpresa(subcontrata.Id, tipo.Id, emision, Vigencia(vence), clave));
                }
            }
        }

        /// <summary>
        /// Los cinco tipos exigidos por defecto de cada Trabajador, Vigentes o sin
        /// caducidad, salvo lo que digan las desviaciones (un documento Vencido,
        /// Urgente o Próximo, o ninguno) y las fechas fijadas a mano. Una desviación
        /// que no pueda cumplirse lanza: la matriz no saldría y no se sabría por qué.
        /// </summary>
        private async Task DocumentosDeTrabajadoresAsync(
            List<Trabajador> trabajadores, IReadOnlyList<DesviacionDocumentoPilotoOutbound> desviaciones,
            IReadOnlyDictionary<(int Trabajador, string Tipo), (DateOnly Emision, DateOnly Vence)>? fechasFijadas = null)
        {
            if (desviaciones.FirstOrDefault(d =>
                    d.Trabajador < 0 || d.Trabajador >= trabajadores.Count || !CatalogoPilotoOutbound.TiposExigidosDeTrabajador.Contains(d.Tipo)) is { } ajena)
                throw new InvalidOperationException(
                    $"{tenant.Clave}: la desviación ({ajena.Trabajador}, {ajena.Tipo}) no es de un Trabajador ni de un tipo exigido de este Tenant.");

            for (var i = 0; i < trabajadores.Count; i++)
            {
                foreach (var nombreTipo in CatalogoPilotoOutbound.TiposExigidosDeTrabajador)
                {
                    var tipo = Tipo(nombreTipo);
                    var desviacion = desviaciones.SingleOrDefault(d => d.Trabajador == i && d.Tipo == nombreTipo);

                    if (fechasFijadas is not null && fechasFijadas.TryGetValue((i, nombreTipo), out var fijadas))
                    {
                        if (desviacion is not null)
                            throw new InvalidOperationException($"{tenant.Clave}: el documento ({i}, {nombreTipo}) tiene fechas fijadas y además una desviación.");

                        await DocumentoDeTrabajadorAsync(trabajadores[i], tipo, fijadas.Emision, fijadas.Vence);
                        continue;
                    }

                    if (desviacion is null)
                    {
                        var vigente = tipo.AplicaVencimientoAutomatico ? _fechas.Vigente(Siguiente()) : (DateOnly?)null;
                        await DocumentoDeTrabajadorAsync(trabajadores[i], tipo, _fechas.Emision(Siguiente()), vigente);
                        continue;
                    }

                    if (desviacion.Situacion == SituacionDocumentoPilotoOutbound.Ausente)
                        continue;

                    if (!tipo.AplicaVencimientoAutomatico)
                        throw new InvalidOperationException(
                            $"{tenant.Clave}: «{nombreTipo}» no caduca, así que no puede sembrarse como {desviacion.Situacion}.");

                    var vence = desviacion.Situacion switch
                    {
                        SituacionDocumentoPilotoOutbound.Vencido => _fechas.Vencido(Siguiente()),
                        SituacionDocumentoPilotoOutbound.Urgente => _fechas.Urgente(Siguiente()),
                        SituacionDocumentoPilotoOutbound.Proximo => _fechas.Proximo(Siguiente()),
                        _ => throw new InvalidOperationException($"{tenant.Clave}: situación de documento sin fecha: {desviacion.Situacion}.")
                    };
                    await DocumentoDeTrabajadorAsync(trabajadores[i], tipo, FechasPilotoOutbound.EmisionDe(vence), vence);
                }
            }
        }

        /// <summary>
        /// Deja el Centro exigiendo exactamente esos tipos de ámbito Trabajador: excluye
        /// los demás que se piden por defecto e incluye los que no. Ninguno bloqueante.
        /// </summary>
        private void ExigirExactamente(Centro centro, IReadOnlyCollection<string> nombresDeTipo)
        {
            foreach (var nombre in CatalogoPilotoOutbound.TiposExigidosDeTrabajador.Where(n => !nombresDeTipo.Contains(n)))
                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(Tipo(nombre).Id, centro.Id, incluido: false));

            foreach (var nombre in nombresDeTipo.Where(n => !CatalogoPilotoOutbound.TiposExigidosDeTrabajador.Contains(n)))
                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(Tipo(nombre).Id, centro.Id, incluido: true));
        }

        private async Task<Documento> DocumentoDeTrabajadorAsync(Trabajador trabajador, TipoDocumento tipo, DateOnly emision, DateOnly? vence)
        {
            var clave = await GuardarPdfAsync(tipo.Nombre, $"{trabajador.Nombre} {trabajador.Apellidos}", emision, vence, pesado: false);
            var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, emision, Vigencia(vence), clave);
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

        private string Cif(int ordinal) => CifDe(tenant, ordinal);

        private string Etiqueta(string papel) => $"{tenant.Clave.ToLowerInvariant()}-{papel}";

        /// <summary>Un nombre de contacto. El sumando de la vuelta evita que dos ordinales separados por dieciséis den el mismo.</summary>
        private string Persona(int ordinal) =>
            $"{CatalogoPilotoOutbound.NombresDePila[(ordinal + _indiceTenant * 5) % CatalogoPilotoOutbound.NombresDePila.Length]} " +
            $"{CatalogoPilotoOutbound.Apellidos[(ordinal * 3 + _indiceTenant + ordinal / 16 * 7) % CatalogoPilotoOutbound.Apellidos.Length]}";
    }
}
