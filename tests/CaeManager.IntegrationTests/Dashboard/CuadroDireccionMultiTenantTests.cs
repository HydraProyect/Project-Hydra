using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// <c>ObtenerCuadroDireccionQuery</c> contra PostgreSQL real con dos Tenants beneficiarios: cada fila del
/// cuadro tiene solo los datos de su Tenant (ninguna consulta cruza el filtro global, tampoco las dos que
/// leen filas eliminadas con <c>IgnoreQueryFilters()</c> y comparan el <c>TenantId</c> a mano), el total es
/// la suma, y las definiciones (Trabajador activo sin Asignaciones, baja = eliminación, solicitud = primer
/// entrante de una conversación viva) se traducen a SQL con el mismo resultado que en memoria.
/// </summary>
public class CuadroDireccionMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;

    private Guid _tenantConsultora;
    private Guid _tenantIbertec;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var tenantConsultora = new Tenant("Consultora Demo");
        var tenantIbertec = new Tenant("Ibertec S.A.");
        _dbContext.Tenants.AddRange(tenantConsultora, tenantIbertec);

        var delegacion = new DelegacionTenant(tenantConsultora.Id, tenantIbertec.Id);
        _dbContext.DelegacionesTenant.Add(delegacion);
        _dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "DireccionCae"));
        await _dbContext.SaveChangesAsync();

        _tenantConsultora = tenantConsultora.Id;
        _tenantIbertec = tenantIbertec.Id;

        await SembrarConsultoraAsync();
        await SembrarIbertecAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Cada_Tenant_beneficiario_ve_solo_lo_suyo_y_el_total_es_la_suma()
    {
        using var servicios = ConstruirServicios(new AlcanceDatosServiceFalso());
        var hoy = DiaDeNegocio.Hoy();

        var cuadro = await servicios.GetRequiredService<IMediator>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month));

        cuadro.Estado.Should().Be(EstadoCifraMensual.Parcial, "es el mes en curso");
        cuadro.TenantsSinAlcanceCompleto.Should().Be(0);
        cuadro.Tenants.Should().HaveCount(2);

        var consultora = cuadro.Tenants.Single(t => t.TenantId == _tenantConsultora).Cifras;
        consultora.TrabajadoresActivos.Valor.Should().Be(2, "sin Asignaciones siguen dados de alta");
        consultora.AltasTrabajadores.Valor.Should().Be(2);
        consultora.BajasTrabajadores.Valor.Should().Be(0);
        consultora.CentrosActivos.Valor.Should().Be(1);
        consultora.AltasCentros.Valor.Should().Be(1);
        consultora.DocumentosGestionados.Valor.Should().Be(0);
        consultora.Requerimientos.Valor.Should().Be(0);
        consultora.Solicitudes.Total.Valor.Should().Be(0);

        var ibertec = cuadro.Tenants.Single(t => t.TenantId == _tenantIbertec).Cifras;
        ibertec.TrabajadoresActivos.Valor.Should().Be(3, "4 dados de alta, 1 eliminado (baja por RNT/ITA o a mano)");
        ibertec.AltasTrabajadores.Valor.Should().Be(4, "el eliminado también se dio de alta este mes");
        ibertec.BajasTrabajadores.Valor.Should().Be(1);
        ibertec.CentrosActivos.Valor.Should().Be(2);
        ibertec.AltasCentros.Valor.Should().Be(3);
        ibertec.DocumentosGestionados.Valor.Should().Be(1);
        ibertec.Requerimientos.Valor.Should().Be(1, "la reclamación enviada hace 40 días es de otro mes");
        ibertec.Solicitudes.Total.Valor.Should().Be(1,
            "una conversación con dos entrantes es una solicitud; la que empezó el mes pasado y la eliminada no cuentan");
        ibertec.Cumplimiento.Porcentaje.Should().Be(100, "su único Documento de Trabajador está vigente");
        ibertec.Cumplimiento.SinDatos.Should().BeFalse();

        cuadro.Total.TrabajadoresActivos.Valor.Should().Be(5);
        cuadro.Total.BajasTrabajadores.Valor.Should().Be(1);
        cuadro.Total.CentrosActivos.Valor.Should().Be(3);
        cuadro.Total.Solicitudes.Total.Valor.Should().Be(1);
        cuadro.Tenants.Should().OnlyContain(t => t.GestorCaePrincipal == null, "ninguna Asignación de Operación lleva principal");
    }

    [Fact]
    public async Task Sin_alcance_del_Tenant_entero_el_Tenant_no_sale_en_el_cuadro()
    {
        // Una lista explícita (vacía) de Clientes: no es acceso total ni cartera universal.
        using var servicios = ConstruirServicios(new AlcanceDatosServiceFalso(clienteIds: []));
        var hoy = DiaDeNegocio.Hoy();

        var cuadro = await servicios.GetRequiredService<IMediator>().Send(new ObtenerCuadroDireccionQuery(hoy.Year, hoy.Month));

        cuadro.Tenants.Should().BeEmpty();
        cuadro.TenantsSinAlcanceCompleto.Should().Be(2);
        cuadro.Total.TrabajadoresActivos.Valor.Should().Be(0);
    }

    [Fact]
    public async Task Las_filas_eliminadas_de_un_Tenant_no_se_cuentan_en_el_otro()
    {
        // El IgnoreQueryFilters del cuadro compara el TenantId a mano: con el ámbito en la Consultora, la baja
        // de Ibertec no existe.
        using (AmbitoTenantExplicito.Establecer(_tenantConsultora))
        {
            (await _dbContext.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == _tenantConsultora)).Should().Be(2);
            (await _dbContext.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == _tenantConsultora && t.EstaEliminado)).Should().Be(0);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantIbertec))
        {
            (await _dbContext.Trabajadores.CountAsync()).Should().Be(3);
            (await _dbContext.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == _tenantIbertec)).Should().Be(4);
        }
    }

    private ServiceProvider ConstruirServicios(IAlcanceDatosService alcance)
    {
        var tenantActual = new TenantActualPorAmbito();
        var usuario = new CurrentUserServiceFalso(_usuario, () => _tenantConsultora);
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IRequisitosEventoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Subcontratas.ISubcontratasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Vehiculos.IVehiculosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Reclamaciones.IReclamacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<ICatalogoIncorporacionCartera>(new CatalogoIncorporacionCartera(_dbContext, usuario));
        servicios.AddSingleton<IDirectorioUsuariosService>(new DirectorioUsuariosFalso());
        servicios.AddSingleton(TimeProvider.System);
        servicios.AddSingleton(alcance);
        servicios.AddSingleton<ICurrentUserService>(usuario);
        return servicios.BuildServiceProvider();
    }

    private async Task SembrarConsultoraAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantConsultora);

        var (cliente, empresa) = await SembrarEmpresasAsync("Consultora");
        _dbContext.Centros.Add(new Centro(cliente.Id, empresa.Id, "Consultora Centro 0"));
        _dbContext.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Consultora0", "Apellido", GenerarDni(0)));
        _dbContext.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Consultora1", "Apellido", GenerarDni(1)));
        await _dbContext.SaveChangesAsync();
    }

    private async Task SembrarIbertecAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantIbertec);

        var (cliente, empresa) = await SembrarEmpresasAsync("Ibertec");
        var centros = Enumerable.Range(0, 3).Select(i => new Centro(cliente.Id, empresa.Id, $"Ibertec Centro {i}")).ToList();
        var trabajadores = Enumerable.Range(0, 4).Select(i => Trabajador.DeEmpresa(empresa.Id, $"Ibertec{i}", "Apellido", GenerarDni(10 + i))).ToList();
        var tipo = new TipoDocumento("EPI", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _dbContext.Centros.AddRange(centros);
        _dbContext.Trabajadores.AddRange(trabajadores);
        _dbContext.TiposDocumento.Add(tipo);
        await _dbContext.SaveChangesAsync();

        // La baja que propone la detección de RNT/ITA (ResolverDeteccionAusenteCommand) es este mismo borrado lógico.
        trabajadores[3].MarcarComoEliminado(Guid.NewGuid());
        centros[2].MarcarComoEliminado(Guid.NewGuid());

        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajadores[0].Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddMonths(6)));
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        _dbContext.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(cliente.Id, Guid.NewGuid(), "cliente@ibertec.test", ahora, [documento.Id]));
        _dbContext.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(cliente.Id, Guid.NewGuid(), "cliente@ibertec.test", ahora.AddDays(-40), [documento.Id]));

        // Dos entrantes en la misma conversación: una sola solicitud.
        var conVariosEntrantes = new Conversacion("Alta de trabajadores", cliente.Id);
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Primero</p>", ahora.AddMinutes(-30));
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "gestora@consultora.test", "<p>Respuesta</p>", ahora.AddMinutes(-20));
        conVariosEntrantes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Segundo</p>", ahora.AddMinutes(-10));

        // Empezó hace 40 días: su solicitud es de otro mes aunque reciba un entrante hoy.
        var deOtroMes = new Conversacion("Renovación", cliente.Id);
        deOtroMes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Antiguo</p>", ahora.AddDays(-40));
        deOtroMes.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ibertec.test", "<p>Nuevo</p>", ahora.AddMinutes(-5));

        // Eliminada: no es demanda.
        var eliminada = new Conversacion("Spam", cliente.Id);
        eliminada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "spam@spam.test", "<p>Oferta</p>", ahora.AddMinutes(-15));
        eliminada.MarcarComoEliminado(Guid.NewGuid());

        _dbContext.Conversaciones.AddRange(conVariosEntrantes, deOtroMes, eliminada);
        await _dbContext.SaveChangesAsync();
    }

    private async Task<(Empresa Cliente, Empresa Empresa)> SembrarEmpresasAsync(string prefijo)
    {
        var cliente = Empresa.CrearComoCliente($"{prefijo} Cliente", "B12345674", false, null, null);
        var empresa = new Empresa($"{prefijo} Empresa");
        _dbContext.Empresas.AddRange(cliente, empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();
        return (cliente, empresa);
    }

    private static string GenerarDni(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Perfil de dirección del Operador CAE para la lectura del principal; sin nombres que resolver.</summary>
    private sealed class DirectorioUsuariosFalso : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<bool> EsCuentaActivaConRolAsync(Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class CurrentUserServiceFalso(Guid usuarioId, Func<Guid?> tenantOrigenId)
        : ICurrentUserService, IEncargoDeAdministracionActual
    {
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;
        public Task<Guid?> EncargoPlenoAsync() => Task.FromResult<Guid?>(null);
        public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => false;

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("DireccionCae");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId());

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
