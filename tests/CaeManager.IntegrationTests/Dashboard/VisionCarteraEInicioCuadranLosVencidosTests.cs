using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// D-13 del recorrido en vivo de staging (2026-10-01): la Visión de cartera
/// decía 1 documento vencido para un Tenant beneficiario e Inicio, con ese
/// mismo Tenant beneficiario activo, decía 3. Las dos pantallas llaman a la
/// misma <see cref="ObtenerKpisDashboardQuery"/>; lo que cambia es CÓMO se fija
/// el Tenant: Inicio lo recibe de la selección del usuario
/// (<see cref="IClienteActivoSeleccionado"/> y Tenant de la petición) y la
/// Visión de cartera lo sella por vuelta con <see cref="AmbitoTenantExplicito"/>,
/// sin selección. Este fichero ejecuta las dos vías contra PostgreSQL real, con
/// <see cref="CurrentUserService"/> y <see cref="AlcanceDatosService"/> de
/// producción y bajo RLS (<c>cae_app_runtime</c>), para el mismo usuario y el
/// mismo Tenant beneficiario, y exige que cuenten lo mismo.
///
/// <para>
/// Escenario: un Gestor CAE del Operador CAE externo (Tenant de origen) con
/// Asignación de Cartera universal sobre el Tenant beneficiario, y otro con
/// cartera acotada a un Cliente empresarial. En el Tenant beneficiario hay
/// Trabajadores asignados a un Centro de ese Cliente empresarial y uno sin
/// asignación, con Documentos vencidos de varios tipos (Certificado de aptitud
/// médica ×2 y Documento de identidad en los asignados).
/// </para>
/// </summary>
public class VisionCarteraEInicioCuadranLosVencidosTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorUniversal = Guid.NewGuid();
    private readonly Guid _gestorDeCliente = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly List<IAsyncDisposable> _desechables = [];
    private CaeManagerDbContext _propietario = null!;
    private Guid _origen;
    private Guid _beneficiario;
    private Guid _operacion;
    private Guid _operacionDeCliente;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var beneficiario = new Tenant("Tenant beneficiario de prueba");
        _propietario.Tenants.AddRange(origen, beneficiario);
        await _propietario.SaveChangesAsync();
        _origen = origen.Id;
        _beneficiario = beneficiario.Id;

        foreach (var tenant in new[] { _origen, _beneficiario })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            _propietario.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
            await _propietario.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        var ayer = ahora.AddDays(-1);
        var hoy = DiaDeNegocio.Hoy();

        using (AmbitoTenantExplicito.Establecer(_beneficiario))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial de prueba", "B10380186", false, null, null);
            var contratista = new Empresa("Montajes de prueba S.L.", "B87654323");
            var contraparte = Empresa.CrearComoCliente("Otra contraparte de prueba", "B10380194", false, null, null);
            _propietario.Empresas.AddRange(cliente, contratista, contraparte);
            var tipoIdentidad = Tipo("Documento de identidad", 1);
            var tipoMedico = Tipo("Certificado de aptitud médica", 2);
            var tipoFormacion = Tipo("Formación en PRL", 3);
            _propietario.TiposDocumento.AddRange(tipoIdentidad, tipoMedico, tipoFormacion);
            await _propietario.SaveChangesAsync();

            var centro = new Centro(cliente.Id, contratista.Id, "Centro del cliente");
            var t1 = Trabajador.DeEmpresa(contratista.Id, "Ana", "Uno", "77189989B");
            var t2 = Trabajador.DeEmpresa(contratista.Id, "Beto", "Dos", "00000001R");
            var t3 = Trabajador.DeEmpresa(contratista.Id, "Cira", "Tres", "00000002W");
            var sinAsignar = Trabajador.DeEmpresa(contraparte.Id, "Dani", "Suelto", "00000003A");
            _propietario.Centros.Add(centro);
            _propietario.Trabajadores.AddRange(t1, t2, t3, sinAsignar);
            await _propietario.SaveChangesAsync();
            _propietario.Asignaciones.AddRange(
                new Asignacion(t1.Id, centro.Id, hoy), new Asignacion(t2.Id, centro.Id, hoy), new Asignacion(t3.Id, centro.Id, hoy));

            var vencido = VigenciaDocumento.VenceEl(hoy.AddDays(-10));
            var vigente = VigenciaDocumento.VenceEl(hoy.AddYears(1));
            var emision = hoy.AddYears(-2);
            _propietario.Documentos.AddRange(
                Documento.DeTrabajador(t1.Id, tipoIdentidad.Id, emision, vencido),
                Documento.DeTrabajador(t1.Id, tipoMedico.Id, emision, vencido),
                Documento.DeTrabajador(t2.Id, tipoMedico.Id, emision, vencido),
                Documento.DeTrabajador(t3.Id, tipoFormacion.Id, emision, vigente),
                // Fuera de la cartera acotada: no está asignado a ningún Centro del Cliente empresarial y su Empresa no es la propia.
                Documento.DeTrabajador(sinAsignar.Id, tipoFormacion.Id, emision, vencido));
            await _propietario.SaveChangesAsync();

            var operacion = AsignacionOperacion.Externa(
                _beneficiario, _origen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ayer, null, ahora);
            _propietario.AsignacionesOperacion.Add(operacion);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _gestorUniversal, Roles.GestorCae, AmbitoAsignacion.Universal, ayer, null, ahora));
            // El Gestor CAE «de Cliente»: su cartera es el Tenant entero (D-7) bajo una Asignación de Operación
            // ACOTADA a ese Cliente empresarial, así que su ámbito efectivo es solo ese Cliente.
            var operacionDeCliente = AsignacionOperacion.Externa(
                _beneficiario, _origen, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ayer, null, ahora);
            _propietario.AsignacionesOperacion.Add(operacionDeCliente);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacionDeCliente, _gestorDeCliente, Roles.GestorCae, AmbitoAsignacion.Universal, ayer, null, ahora));
            // Coordinador CAE con su propia cartera de rol Coordinador (el rol efectivo sale de ella) y
            // un Gestor CAE a su cargo, el de la operación acotada al Cliente empresarial: el alcance
            // de datos del Coordinador es el de su equipo, no el Tenant entero.
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _coordinador, Roles.CoordinadorCae, AmbitoAsignacion.Universal, ayer, null, ahora));
            await _propietario.SaveChangesAsync();
            _operacion = operacion.Id;
            _operacionDeCliente = operacionDeCliente.Id;
        }

        using (AmbitoTenantExplicito.Establecer(_origen))
        {
            _propietario.Users.Add(new ApplicationUser
            {
                Id = _coordinador,
                UserName = "coord@ejemplo.test",
                Email = "coord@ejemplo.test",
                TenantId = _origen
            });
            _propietario.Users.Add(new ApplicationUser
            {
                Id = _gestorDeCliente,
                UserName = "gestor@ejemplo.test",
                Email = "gestor@ejemplo.test",
                TenantId = _origen,
                CoordinadorUsuarioId = _coordinador
            });
            await _propietario.SaveChangesAsync();
        }

        static TipoDocumento Tipo(string nombre, int orden) =>
            new(nombre, null, aplicaVencimientoAutomatico: false, orden, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
    }

    public async Task DisposeAsync()
    {
        foreach (var desechable in _desechables)
            await desechable.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Gestor_con_cartera_universal_Vision_de_cartera_e_Inicio_cuentan_los_mismos_vencidos()
    {
        var (vision, inicio) = await MedirAmbasAsync(_gestorUniversal);

        inicio.DocumentosVencidos.Should().Be(4, "control de la siembra: tres de los asignados y uno suelto, el Tenant entero");
        vision.DocumentosVencidos.Should().Be(inicio.DocumentosVencidos);
        vision.DocumentosUrgentes.Should().Be(inicio.DocumentosUrgentes);
        vision.TasaCumplimientoDocumental.Should().Be(inicio.TasaCumplimientoDocumental);
    }

    [Fact]
    public async Task Gestor_con_cartera_de_un_Cliente_empresarial_Vision_de_cartera_e_Inicio_cuentan_los_mismos_vencidos()
    {
        var (vision, inicio) = await MedirAmbasAsync(_gestorDeCliente);

        inicio.DocumentosVencidos.Should().Be(3, "control de la siembra: Documento de identidad y Certificado de aptitud médica ×2 de los asignados");
        vision.DocumentosVencidos.Should().Be(inicio.DocumentosVencidos);
        vision.DocumentosUrgentes.Should().Be(inicio.DocumentosUrgentes);
        vision.TasaCumplimientoDocumental.Should().Be(inicio.TasaCumplimientoDocumental);
    }

    [Fact]
    public async Task Coordinador_con_equipo_acotado_a_un_Cliente_empresarial_Vision_de_cartera_e_Inicio_cuentan_los_mismos_vencidos()
    {
        var (vision, inicio) = await MedirAmbasAsync(_coordinador, Roles.CoordinadorCae);

        inicio.DocumentosVencidos.Should().Be(3, "control de la siembra: Documento de identidad y Certificado de aptitud médica ×2 de los asignados");
        vision.DocumentosVencidos.Should().Be(inicio.DocumentosVencidos);
        vision.DocumentosUrgentes.Should().Be(inicio.DocumentosUrgentes);
        vision.TasaCumplimientoDocumental.Should().Be(inicio.TasaCumplimientoDocumental);
    }

    private async Task<(ClienteRiesgoDto Vision, KpisDashboardDto Inicio)> MedirAmbasAsync(Guid usuario, string rol = Roles.GestorCae)
    {
        // Visión de cartera: fan-out. Sin Tenant seleccionado; cada Tenant se sella con el ámbito.
        await using var servicioVision = ServiciosDeAplicacion(usuario, rol, seleccionado: false);
        var global = await servicioVision.GetRequiredService<IMediator>().Send(new ObtenerKpisGlobalesQuery());
        var fila = global.ClientesConMasRiesgo.Should().ContainSingle(c => c.TenantId == _beneficiario,
            "el Tenant beneficiario entra en el fan-out por Operación").Subject;

        // Inicio: el Tenant beneficiario seleccionado, sin ámbito explícito.
        await using var servicioInicio = ServiciosDeAplicacion(usuario, rol, seleccionado: true);
        var inicio = await servicioInicio.GetRequiredService<IMediator>().Send(new ObtenerKpisDashboardQuery());
        return (fila, inicio);
    }

    private ServiceProvider ServiciosDeAplicacion(Guid usuario, string rol, bool seleccionado)
    {
        // Inicio llega con el Tenant beneficiario como Tenant de la petición; la Visión, con el de origen.
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(seleccionado ? _beneficiario : _origen);
        IClienteActivoSeleccionado seleccion = seleccionado
            ? new ClienteActivoSeleccionadoFijo(_beneficiario, usuario == _gestorDeCliente ? _operacionDeCliente : _operacion)
            : new SinClienteActivo();
        var runtime = CrearRuntime(usuario, tenantDeLaPeticion);
        _desechables.Add(runtime);

        var usuarioReal = CrearCurrentUserService(runtime, usuario, rol, seleccion);
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(runtime);
        servicios.AddSingleton<ITenantsQueryContext>(runtime);
        servicios.AddSingleton<IOperacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(runtime);
        servicios.AddSingleton<ICurrentUserService>(usuarioReal);
        servicios.AddSingleton<IAlcanceDatosService>(
            new AlcanceDatosService(runtime, usuarioReal, tenantDeLaPeticion, new SesionPrivilegiadaAusente()));
        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearRuntime(Guid usuario, ITenantActual tenantDeLaPeticion)
    {
        var usuarioInterceptor = new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(
                    tenantDeLaPeticion, new SinClienteActivo(), usuarioInterceptor, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    private CurrentUserService CrearCurrentUserService(
        CaeManagerDbContext contexto, Guid usuario, string rol, IClienteActivoSeleccionado seleccion)
    {
        // El rol de la sesión en el Tenant de origen NO es el de la cartera (Consulta frente a Gestor/Coordinador
        // CAE): lo que cuenta el fan-out tiene que salir de la cartera de cada Tenant (#571), no del claim. En
        // Inicio, el middleware de Workspace operativo derivado sustituye el claim de rol por el de la cartera y
        // conserva el de origen aparte.
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, usuario.ToString()),
                new Claim(ClaimTypes.Role, seleccion.TenantIdSeleccionado is null ? Roles.Consulta : rol),
                new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen, Roles.Consulta),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _origen.ToString())
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton<ITenantsQueryContext>(contexto);
        servicios.AddSingleton<IOperacionesQueryContext>(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorNulo(),
            seleccion,
            servicios.BuildServiceProvider());
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ClienteActivoSeleccionadoFijo(Guid tenant, Guid operacion) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;
        public Guid? AsignacionOperacionIdSeleccionada => operacion;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorNulo : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }
}
