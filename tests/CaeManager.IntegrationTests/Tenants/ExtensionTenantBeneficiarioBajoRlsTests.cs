using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// La extensión opera sobre los Tenants beneficiarios con las garantías de la web: el Tenant de la cabecera es una
/// solicitud y lo resuelve el servidor en cada petición (OWASP ASVS 4.0.3 V4.1.3, V4.2.1).
///
/// <para>
/// Cada petición pasa por los middlewares de producción (<see cref="ContextoExtensionMiddleware"/> y
/// <see cref="RolEfectivoDelWorkspaceMiddleware"/>) sobre una conexión <c>cae_app_runtime</c> con el interceptor de RLS
/// real, y lo que el endpoint vería se lee sin filtros de EF: quien aísla es la RLS con el contexto firmado. Solo la
/// credencial es de la prueba (<see cref="AutenticacionDeLaPrueba"/>): el token lo cubren las pruebas del esquema.
/// </para>
///
/// <para>
/// Escenario: un Gestor CAE del Operador CAE externo con cartera vigente en <b>A</b> y <b>B</b>, y un Tenant <b>ajeno</b>
/// gestionado por otro Operador CAE, en el que no tiene nada.
/// </para>
/// </summary>
public class ExtensionTenantBeneficiarioBajoRlsTests : IAsyncLifetime
{
    private const string Portal = "app.portal-prueba.test";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly List<IAsyncDisposable> _desechables = [];
    private CaeManagerDbContext _propietario = null!;

    private Guid _origen;
    private Guid _a;
    private Guid _b;
    private Guid _ajeno;
    private AsignacionOperacion _operacionA = null!;
    private AsignacionOperacion _operacionB = null!;
    private AsignacionCartera _carteraA = null!;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito), new ConcurrenciaOptimistaInterceptor())
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        Guid NuevoTenant(string nombre)
        {
            var tenant = new Tenant(nombre);
            _propietario.Tenants.Add(tenant);
            return tenant.Id;
        }

        _origen = NuevoTenant("Operador CAE externo de prueba");
        var otroOperador = NuevoTenant("Otro Operador CAE externo");
        _a = NuevoTenant("Tenant beneficiario A");
        _b = NuevoTenant("Tenant beneficiario B");
        _ajeno = NuevoTenant("Tenant de otro Operador CAE");
        await _propietario.SaveChangesAsync();

        foreach (var (tenant, nombre) in new[] { (_origen, "Cliente de origen"), (_a, "Cliente de A"), (_b, "Cliente de B"), (_ajeno, "Cliente ajeno") })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            _propietario.Empresas.Add(Empresa.CrearComoCliente(nombre, "B10380186", false, null, null));
            await _propietario.SaveChangesAsync();
        }

        var ayer = DateTime.UtcNow.AddDays(-1);
        (_operacionA, _carteraA) = await OperacionConCarteraAsync(_a, _origen, _gestor, ayer);
        (_operacionB, _) = await OperacionConCarteraAsync(_b, _origen, _gestor, ayer);
        await OperacionConCarteraAsync(_ajeno, otroOperador, Guid.NewGuid(), ayer);
    }

    public async Task DisposeAsync()
    {
        foreach (var desechable in _desechables)
            await desechable.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Cada_peticion_opera_solo_en_el_Tenant_beneficiario_que_solicita()
    {
        var enA = await PedirAsync(_a);
        var enB = await PedirAsync(_b);

        enA.Paso.Should().BeTrue();
        enA.Tenant.Should().Be(_a);
        enA.Operacion.Should().Be(_operacionA.Id);
        enA.Rol.Should().Be(Roles.GestorCae);
        enA.Empresas.Should().Equal(["Cliente de A"], "sin filtros de EF, la RLS firmada solo deja ver A");

        enB.Tenant.Should().Be(_b);
        enB.Operacion.Should().Be(_operacionB.Id);
        enB.Empresas.Should().Equal(["Cliente de B"]);
    }

    [Fact]
    public async Task Sin_cabecera_opera_en_el_Tenant_de_origen()
    {
        var resultado = await PedirAsync(tenantSolicitado: null);

        resultado.Paso.Should().BeTrue();
        resultado.Tenant.Should().BeNull();
        resultado.Empresas.Should().Equal(["Cliente de origen"]);
    }

    [Fact]
    public async Task Un_Tenant_ajeno_se_rechaza_en_cerrado_y_deja_senal()
    {
        var resultado = await PedirAsync(_ajeno);

        resultado.Paso.Should().BeFalse();
        resultado.Estado.Should().Be(StatusCodes.Status403Forbidden);
        resultado.Empresas.Should().BeEmpty();
        resultado.SenalesDeRechazo.Should().Be(1);
    }

    [Fact]
    public async Task Revocada_la_cartera_entre_la_emision_y_la_recogida_del_ticket_no_se_recoge_nada()
    {
        string? ticket = null;
        await PedirAsync(_a, peticion =>
        {
            ticket = TicketEntregaExtension.Proteger(
                _protector, _gestor, peticion.TenantActual.TenantId!.Value, peticion.Seleccion.AsignacionOperacionIdSeleccionada,
                Guid.NewGuid(), Guid.NewGuid(), Portal);
            return Task.CompletedTask;
        });
        TicketEntregaExtension.Leer(_protector, ticket).Should().NotBeNull("el ticket sigue siendo auténtico");

        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _carteraA.Suspender();
            await _propietario.SaveChangesAsync();
        }

        var recogida = await PedirAsync(_a);

        recogida.Paso.Should().BeFalse("un ticket no sustituye a la autorización de la petición que recoge");
        recogida.Estado.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Un_cambio_de_cartera_deja_sin_valor_el_ticket_anterior()
    {
        var ticket = TicketEntregaExtension.Leer(_protector, TicketEntregaExtension.Proteger(
            _protector, _gestor, _a, _operacionA.Id, Guid.NewGuid(), Guid.NewGuid(), Portal))!;

        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _carteraA.Suspender();
            _operacionA.Suspender();
            await _propietario.SaveChangesAsync();
        }

        var (nueva, _) = await OperacionConCarteraAsync(_a, _origen, _gestor, DateTime.UtcNow.AddHours(-1));

        bool? vale = null;
        var recogida = await PedirAsync(_a, peticion =>
        {
            vale = EntregaArchivoExtensionEndpoints.TicketEsDeEstaPeticion(ticket, peticion.TenantActual, peticion.Seleccion);
            return Task.CompletedTask;
        });

        recogida.Paso.Should().BeTrue("el Tenant sigue autorizado, por otra Asignación de Operación");
        recogida.Operacion.Should().Be(nueva.Id);
        vale.Should().BeFalse();
    }

    [Fact]
    public async Task Confirmar_y_deshacer_quedan_en_el_Tenant_solicitado()
    {
        var acreditacionId = await SembrarAcreditacionPendienteEnBAsync();

        bool? desdeOtroTenant = null;
        await PedirAsync(_a, async peticion =>
            desdeOtroTenant = (await ConfirmarAsync(peticion.Runtime, acreditacionId)).EsFallido);
        desdeOtroTenant.Should().BeTrue("desde A la Acreditación de B no existe");

        CambioEstadoAnotadoDto? anotado = null;
        await PedirAsync(_b, async peticion =>
        {
            var confirmado = await ConfirmarAsync(peticion.Runtime, acreditacionId);
            confirmado.EsExitoso.Should().BeTrue(confirmado.EsFallido ? confirmado.Error.Mensaje : null);
            anotado = confirmado.Valor;
        });
        (await EstadoEnBAsync(acreditacionId)).Should().Be(EstadoAcreditacion.Subida);

        await PedirAsync(_b, async peticion =>
        {
            var contexto = peticion.Runtime;
            var deshecho = await new DeshacerSubidaDesdeExtensionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(contexto), new DocumentoRepository(contexto),
                    new AlcanceDatosServiceFalso(), contexto, contexto, contexto)
                .Handle(new DeshacerSubidaDesdeExtensionCommand(acreditacionId, anotado!.CambioId, anotado.Version), CancellationToken.None);
            deshecho.EsExitoso.Should().BeTrue(deshecho.EsFallido ? deshecho.Error.Mensaje : null);
        });
        (await EstadoEnBAsync(acreditacionId)).Should().Be(EstadoAcreditacion.PendienteDeSubir);
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_en_la_cookie_no_entra_en_una_peticion_de_extension()
    {
        var cookie = ClienteActivoSeleccionado.Proteger(_protector, _gestor, _ajeno, null, Guid.NewGuid());

        var sinCabecera = await PedirAsync(tenantSolicitado: null, cookie: cookie);
        var conCabecera = await PedirAsync(_ajeno, cookie: cookie);

        sinCabecera.Paso.Should().BeTrue();
        sinCabecera.Tenant.Should().BeNull();
        sinCabecera.SesionPrivilegiada.Should().BeNull();
        sinCabecera.Empresas.Should().Equal(["Cliente de origen"], "la cookie no abre el Tenant que nombra");
        sinCabecera.Rol.Should().Be(Roles.GestorCae, "tampoco quita el rol: en esta petición no se lee");
        conCabecera.Paso.Should().BeFalse("la Sesión Privilegiada no autoriza una solicitud de la extensión");
        conCabecera.Estado.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ---------- Arnés ----------

    /// <summary>Una petición completa, con su contenedor, su selección y su conexión propios, como en producción.</summary>
    private async Task<Resultado> PedirAsync(Guid? tenantSolicitado, Func<PeticionEnCurso, Task>? alPasar = null, string? cookie = null)
    {
        var usuario = _gestor;
        var http = new DefaultHttpContext();
        http.Request.Path = "/extension/acreditaciones-pendientes";
        http.Request.Headers.Authorization = "Extension token-de-la-prueba";
        http.Response.Body = new MemoryStream();
        if (tenantSolicitado is { } tenant)
            http.Request.Headers[ContextoExtension.CabeceraTenant] = tenant.ToString();
        if (cookie is not null)
            http.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={cookie}";

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, usuario.ToString()),
                new Claim(ClaimTypes.Role, Roles.GestorCae),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _origen.ToString()),
            ],
            ExtensionAuthenticationSchemeOptions.NombreEsquema));

        var accesor = new HttpContextAccessor { HttpContext = http };
        var seleccion = new ClienteActivoSeleccionado(accesor, _protector);
        var tenantActual = new TenantDeLaPeticion(http, seleccion, _origen);
        var runtime = CrearRuntime(usuario, tenantActual, seleccion);
        _desechables.Add(runtime);

        var datosDelUsuario = new ServiceCollection();
        datosDelUsuario.AddSingleton<ITenantsQueryContext>(runtime);
        datosDelUsuario.AddSingleton<IOperacionesQueryContext>(runtime);
        var usuarioActual = new CurrentUserService(
            new AuthenticationStateProviderFalso(principal), accesor, seleccion,
            datosDelUsuario.AddTechoDeRolSinEncargoSembrado().BuildServiceProvider());

        var senales = DispatchProxy.Create<ISenalesIncidenteSeguridad, EspiaDeSenales>();
        http.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new AutenticacionDeLaPrueba(principal))
            .AddSingleton<IClienteActivoSeleccionado>(seleccion)
            .AddSingleton<ICurrentUserService>(usuarioActual)
            .AddSingleton<IOperacionesQueryContext>(runtime)
            .AddSingleton(senales)
            .BuildServiceProvider();

        var paso = false;
        IReadOnlyList<string> empresas = [];
        (Guid? Tenant, Guid? Operacion, Guid? Sesion) vista = default;
        string? rol = null;

        var rolEfectivo = new RolEfectivoDelWorkspaceMiddleware(async _ =>
        {
            paso = true;
            vista = (seleccion.TenantIdSeleccionado, seleccion.AsignacionOperacionIdSeleccionada, seleccion.SesionPrivilegiadaIdSeleccionada);
            rol = http.User.FindFirst(ClaimTypes.Role)?.Value;
            empresas = await runtime.Empresas.IgnoreQueryFilters().AsNoTracking().Select(e => e.RazonSocial).ToListAsync();
            if (alPasar is not null)
                await alPasar(new PeticionEnCurso(runtime, tenantActual, seleccion));
        });

        var sinRolDeNegocio = new SesionPrivilegiadaSinRolDeNegocioMiddleware(contexto => rolEfectivo.InvokeAsync(
            contexto, seleccion, usuarioActual, usuarioActual, NullLogger<RolEfectivoDelWorkspaceMiddleware>.Instance));

        await new ContextoExtensionMiddleware(contexto => sinRolDeNegocio.InvokeAsync(contexto, _protector))
            .InvokeAsync(http);

        return new Resultado(
            http.Response.StatusCode, paso, vista.Tenant, vista.Operacion, vista.Sesion, empresas, rol,
            ((EspiaDeSenales)(object)senales).Rechazos);
    }

    private CaeManagerDbContext CrearRuntime(Guid usuario, ITenantActual tenantActual, IClienteActivoSeleccionado seleccion)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(usuario))),
                new TenantSelladoInterceptor(tenantActual),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<(AsignacionOperacion Operacion, AsignacionCartera Cartera)> OperacionConCarteraAsync(
        Guid propietario, Guid operador, Guid usuario, DateTime desde)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(propietario);
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(
            propietario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var cartera = AsignacionCartera.Externa(
            operacion, usuario, Roles.GestorCae, AmbitoAsignacion.Universal, desde, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(cartera);
        await _propietario.SaveChangesAsync();
        return (operacion, cartera);
    }

    private Task<Result<CambioEstadoAnotadoDto>> ConfirmarAsync(CaeManagerDbContext contexto, Guid acreditacionId) =>
        new ConfirmarSubidaDesdeExtensionCommandHandler(
                new AcreditacionDocumentoPlataformaRepository(contexto), new DocumentoRepository(contexto),
                new AlcanceDatosServiceFalso(), contexto, contexto, contexto, new CurrentUserServiceFalso(_gestor), contexto)
            .Handle(new ConfirmarSubidaDesdeExtensionCommand(acreditacionId, Portal), CancellationToken.None);

    private async Task<EstadoAcreditacion> EstadoEnBAsync(Guid acreditacionId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_b);
        return await _propietario.AcreditacionesDocumentoPlataforma.AsNoTracking()
            .Where(a => a.Id == acreditacionId).Select(a => a.Estado).SingleAsync();
    }

    private async Task<Guid> SembrarAcreditacionPendienteEnBAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_b);
        var titular = Empresa.CrearComoCliente("Titular de B", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista de B", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Norte");
        var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "Pérez", "12345678Z");
        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(5)));
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        _propietario.Empresas.AddRange(titular, proveedora);
        _propietario.Centros.Add(centro);
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        _propietario.DominiosProveedorPlataformaCae.Add(new DominioProveedorPlataformaCae(proveedor.Id, "portal-prueba.test"));
        _propietario.CanalesGestionDocumental.Add(acceso);
        _propietario.TiposDocumento.Add(tipo);
        _propietario.Trabajadores.Add(trabajador);
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-1)));
        _propietario.Documentos.Add(documento);
        _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _propietario.SaveChangesAsync();
        return acreditacion.Id;
    }

    private sealed record PeticionEnCurso(CaeManagerDbContext Runtime, ITenantActual TenantActual, ClienteActivoSeleccionado Seleccion);

    private sealed record Resultado(
        int Estado, bool Paso, Guid? Tenant, Guid? Operacion, Guid? SesionPrivilegiada,
        IReadOnlyList<string> Empresas, string? Rol, int SenalesDeRechazo);

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>La misma precedencia que <c>TenantActual</c>; sin usuario autenticado no hay Tenant.</summary>
    private sealed class TenantDeLaPeticion(HttpContext http, IClienteActivoSeleccionado seleccion, Guid origen) : ITenantActual
    {
        public Guid? TenantId =>
            AmbitoTenantExplicito.TenantIdActual
            ?? seleccion.TenantIdSeleccionado
            ?? (http.User.Identity?.IsAuthenticated == true ? origen : null);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class AutenticacionDeLaPrueba(ClaimsPrincipal principal) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme!)));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    /// <summary>Cuenta los rechazos de acceso a otro Tenant; ninguna otra señal debe saltar en estas pruebas.</summary>
    public class EspiaDeSenales : DispatchProxy
    {
        public int Rechazos { get; private set; }

        protected override object? Invoke(MethodInfo? metodo, object?[]? argumentos)
        {
            if (metodo?.Name != nameof(ISenalesIncidenteSeguridad.AccesoAOtroTenantRechazadoAsync))
                throw new NotSupportedException(metodo?.Name);

            Rechazos++;
            return Task.CompletedTask;
        }
    }
}
