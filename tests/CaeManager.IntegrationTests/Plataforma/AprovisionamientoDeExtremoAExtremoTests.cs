using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// PD-A3: el test que <b>compone el flujo completo</b> — <see cref="IMediator"/>
/// real (mismo <c>AddApplication()</c> que usa la aplicación),
/// <see cref="ISesionPrivilegiadaActual"/> real, <see cref="IElevacionEscrituraPrivilegiada"/>
/// real y <see cref="TenantRlsConnectionInterceptor"/> real, contra Postgres
/// real. Mismo patrón que <c>CrearDocumentoCommandBloqueadoParaConsultaTests</c>:
/// un handler probado con dobles nunca ejercita el registro real ni el orden
/// real del pipeline.
///
/// <b>Por qué este test, y no solo los que ya existían.</b> Una revisión
/// adversarial (Codex) encontró que <c>IAutorizacionEscrituraEfectiva</c>,
/// invocada desde dentro de estos handlers, hacía una tercera consulta a la
/// base con <c>RevalidarAsync</c> — bajo el rol YA elevado a
/// <c>cae_app_aprovisionamiento</c>, que no tiene GRANT sobre las tablas de
/// plataforma que esa consulta toca. Ningún test existente lo detectó porque
/// cada pieza se probaba aislada con dobles: el behavior de elevación con una
/// <c>IElevacionEscrituraPrivilegiada</c> falsa, la capa de datos abriendo el
/// <c>AsyncLocal</c> a mano sin pasar por MediatR, la importación con
/// <c>IAutorizacionEscrituraEfectiva</c> falsa. Solo componiendo las cuatro
/// piezas de verdad se manifiesta el error real de Postgres.
/// </summary>
public class AprovisionamientoDeExtremoAExtremoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _tenantObjetivo = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    private Guid _sesionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantObjetivo);
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _admin, CapacidadPrivilegio.Aprovisionamiento, [_tenantObjetivo],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

        contexto.ConcesionesPrivilegio.Add(concesion);

        var sesion = SesionPrivilegiada.Abrir(
            concesion, _tenantObjetivo, "Alta de tenant de prueba", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        contexto.SesionesPrivilegiadas.Add(sesion);

        await contexto.SaveChangesAsync();
        _sesionId = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// El caso que Codex encontró roto: una sesión de Aprovisionamiento
    /// ejecutando un comando de importación de verdad, a través del pipeline
    /// completo. Plan vacío a propósito — lo que se demuestra es que el
    /// comando ATRAVIESA el pipeline y su guard interno sin reventar por
    /// permisos de Postgres, no la lógica de negocio de la importación (ya
    /// cubierta en otros tests).
    /// </summary>
    [Fact]
    public async Task Una_sesion_de_Aprovisionamiento_ejecuta_una_importacion_por_el_pipeline_completo()
    {
        await using var proveedor = ConstruirProveedor(_tenantObjetivo, sesionSeleccionada: _sesionId);
        var mediator = proveedor.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new EjecutarImportacionCommand(PlanVacio()));

        resultado.EsExitoso.Should().BeTrue(
            resultado.EsFallido ? $"código: {resultado.Error!.Codigo}" : string.Empty);
    }

    /// <summary>
    /// Control negativo: la MISMA composición, pero el tenant activo de esta
    /// conexión es otro distinto del objetivo de la sesión.
    ///
    /// <b>El código de error real no es el de <c>AutorizacionEscrituraBehavior</c>,
    /// y es correcto que no lo sea.</b> Una barrera MÁS TEMPRANA e
    /// independiente —<c>SesionPrivilegiadaActual.ResolverAsync</c>, la
    /// coherencia entre el tenant que nombra el token
    /// (<c>IClienteActivoSeleccionado.TenantIdSeleccionado</c>) y el
    /// <c>TenantObjetivoId</c> de la sesión— ya deniega la resolución de la
    /// sesión antes de que el behavior de escritura llegue a evaluar su
    /// propio chequeo de tenant: sin sesión resuelta, el rol efectivo cae a
    /// <c>null</c> y la lista blanca de <c>AutorizacionEscrituraBehavior</c>
    /// deniega con <c>Autorizacion.SoloLectura</c>. Dos barreras
    /// independientes, cualquiera de las dos deniega — exactamente la defensa
    /// en profundidad que PD-A3 exige.
    /// </summary>
    [Fact]
    public async Task La_misma_sesion_no_ejecuta_nada_sobre_un_tenant_distinto_del_objetivo()
    {
        await using var proveedor = ConstruirProveedor(_otroTenant, sesionSeleccionada: _sesionId);
        var mediator = proveedor.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new EjecutarImportacionCommand(PlanVacio()));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura",
            "SesionPrivilegiadaActual.ResolverAsync ya deniega la resolución de la sesión por " +
            "tenant incoherente, antes de que AutorizacionEscrituraBehavior evalúe su propio chequeo");
    }

    private static PlanImportacionDto PlanVacio() => new(
        OperacionId: Guid.NewGuid(), ClientesCentros: [], Empresas: [], Trabajadores: [], Documentos: [],
        Asignaciones: [], Advertencias: [], Omitidos: []);

    private ServiceProvider ConstruirProveedor(Guid tenantActivo, Guid sesionSeleccionada)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantActivo };
        var currentUser = new CurrentUserServiceFalso(_admin, rol: null, tenantOrigenId: tenantActivo);
        var clienteActivoSeleccionado = new ClienteActivoSeleccionadoFalso(tenantActivo, sesionSeleccionada);
        var dbContext = CrearContexto(tenantActivo, clienteActivoSeleccionado, currentUser);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(currentUser);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IClienteActivoSeleccionado>(clienteActivoSeleccionado);
        servicios.AddSingleton(dbContext);

        // Las piezas reales de PD-A3 — sustituyen los valores por defecto
        // inertes que AddApplication() registra con TryAdd.
        servicios.AddSingleton<IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ISesionPrivilegiadaActual>(sp => new SesionPrivilegiadaActual(
            sp.GetRequiredService<IPlataformaQueryContext>(), clienteActivoSeleccionado, currentUser));
        servicios.AddSingleton<IElevacionEscrituraPrivilegiada>(sp =>
            new ElevacionEscrituraPrivilegiada(sp.GetRequiredService<CaeManagerDbContext>()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IAsignacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ICentrosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IDocumentosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITiposDocumentoQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITrabajadoresQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        servicios.AddSingleton<IEmpresaRepository, EmpresaRepository>();
        servicios.AddSingleton<ITrabajadorRepository, TrabajadorRepository>();
        servicios.AddSingleton<IDocumentoRepository, DocumentoRepository>();
        servicios.AddSingleton<IAsignacionRepository, AsignacionRepository>();
        servicios.AddSingleton<IOperacionImportacionRepository, OperacionImportacionRepository>();

        return servicios.BuildServiceProvider();
    }

    /// <summary>
    /// Sin parámetros extra: para la siembra en <see cref="InitializeAsync"/>,
    /// donde todavía no hay ninguna sesión privilegiada que seleccionar.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid tenantId) =>
        CrearContexto(tenantId, new ClienteActivoSeleccionadoAusente(), new CurrentUserServiceFalso(_admin, rol: null, tenantOrigenId: tenantId));

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado clienteActivoSeleccionado, ICurrentUserService currentUserService)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, clienteActivoSeleccionado, currentUserService))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoAusente : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantSeleccionado, Guid sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
