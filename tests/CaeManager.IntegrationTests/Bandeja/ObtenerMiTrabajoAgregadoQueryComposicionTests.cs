using System.Diagnostics;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// Mismo criterio que <c>DashboardEjecutivoMultiTenantTests</c>: la lógica de
/// fusión pura de <c>ObtenerMiTrabajoAgregadoQueryHandler</c> ya está probada
/// sin PostgreSQL en <c>ObtenerMiTrabajoAgregadoQueryHandlerTests</c>
/// (Application.Tests) — lo que este test cubre es lo que aquel no puede: que
/// el fan-out real por <see cref="AmbitoTenantExplicito"/> sobre PostgreSQL
/// (a) no cruza el filtro global de tenant entre Tenants de la cartera del
/// Gestor CAE, y (b) tiene un coste medido, no asumido, con varios Tenants
/// (contrato CONTRATO-MI-TRABAJO-GEN2-MULTI-TENANT-2026-09-22.md, UNKNOWN del
/// checkpoint de este incremento).
/// </summary>
public class ObtenerMiTrabajoAgregadoQueryComposicionTests(ITestOutputHelper salida) : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantDelegante;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var tenantOrigen = new Tenant("Consultora Mi Trabajo");
        var tenantDelegante = new Tenant("Delegante Mi Trabajo");
        _dbContext.Tenants.AddRange(tenantOrigen, tenantDelegante);
        _tenantOrigen = tenantOrigen.Id;
        _tenantDelegante = tenantDelegante.Id;

        var delegacion = new DelegacionTenant(_tenantOrigen, _tenantDelegante);
        _dbContext.DelegacionesTenant.Add(delegacion);
        _dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));
        await _dbContext.SaveChangesAsync();

        _servicios = ConstruirServicios(tenantActual, () => _tenantOrigen);

        await SembrarAsync(_tenantOrigen, "Origen");
        await SembrarAsync(_tenantDelegante, "Delegante");
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Agrega_los_dos_tenants_autorizados_sin_fugar_datos_entre_ellos()
    {
        var mediator = _servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerMiTrabajoAgregadoQuery());

        resultado.Tenants.Should().HaveCount(2);

        var origen = resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantOrigen).Subject;
        origen.EsOrigen.Should().BeTrue();
        origen.Resumen.Proximos.Should().Be(1);
        origen.Proximos.Should().ContainSingle().Which.Titulo.Should().Be("Apto médico Origen");

        var delegante = resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantDelegante).Subject;
        delegante.EsOrigen.Should().BeFalse();
        delegante.Resumen.Proximos.Should().Be(1);
        delegante.Proximos.Should().ContainSingle().Which.Titulo.Should().Be("Apto médico Delegante");

        // El "Próximo" de un tenant nunca aparece en el otro — ni en el
        // bucket de Próximos, ni colado como Faltante/Vencido en Bloqueo+Actuación.
        origen.Proximos.Should().NotContain(i => i.Titulo == "Apto médico Delegante");
        delegante.Proximos.Should().NotContain(i => i.Titulo == "Apto médico Origen");
        origen.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Should().BeEmpty();
        delegante.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Should().BeEmpty();
    }

    [Fact]
    public async Task Cada_tenant_consultado_por_separado_solo_ve_su_propio_trabajador()
    {
        // Mismo control que "Ningun_tenant_individual_ve_los_datos_del_otro" en
        // DashboardEjecutivoMultiTenantTests: confirma que el resultado
        // fusionado no se obtiene cruzando el filtro global de RLS.
        using (AmbitoTenantExplicito.Establecer(_tenantOrigen))
            (await _dbContext.Trabajadores.CountAsync()).Should().Be(1);

        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
            (await _dbContext.Trabajadores.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Contrato § 14: el sujeto de una tarea de Empresa se rotula distinto si es
    /// la Empresa propia del Tenant o una Subcontrata, así que la Query tiene que
    /// traer <see cref="ItemBandejaDto.EmpresaEsPropia"/>. La consulta de Empresas
    /// corre dentro del <see cref="AmbitoTenantExplicito"/> de cada Tenant: fuera
    /// de él, RLS no deja ver ninguna fila y el campo se quedaría en null sin
    /// que nada fallara, que es justo lo que este test descarta.
    /// </summary>
    [Fact]
    public async Task Las_tareas_de_Empresa_distinguen_la_Empresa_propia_de_una_Subcontrata()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
        {
            var propia = await _dbContext.Empresas.SingleAsync(e => e.RazonSocial == "Delegante Empresa");
            var subcontrata = Empresa.CrearComoSubcontrata("Delegante Subcontrata", null, "Estandar");
            _dbContext.Empresas.Add(subcontrata);
            var centro = await _dbContext.Centros.SingleAsync();
            var proveedor = await _dbContext.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, null, null, null);
            _dbContext.CanalesGestionDocumental.Add(canal);
            var tipo = new TipoDocumento("Seguro RC Delegante", 12, true, 1, AmbitoAplicacion.Empresa);
            _dbContext.TiposDocumento.Add(tipo);
            await _dbContext.SaveChangesAsync();

            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var documentoPropia = Documento.DeEmpresa(propia.Id, tipo.Id, hoy.AddMonths(-1), hoy.AddYears(1));
            var documentoSubcontrata = Documento.DeEmpresa(subcontrata.Id, tipo.Id, hoy.AddMonths(-1), hoy.AddYears(1));
            _dbContext.Documentos.AddRange(documentoPropia, documentoSubcontrata);
            await _dbContext.SaveChangesAsync();

            // La de la Empresa propia, pendiente de subir (Bloqueo+Actuación);
            // la de la Subcontrata, ya subida (Seguimiento): cubre los dos
            // caminos por los que llega una tarea de Empresa.
            var subida = new AcreditacionDocumentoPlataforma(documentoSubcontrata.Id, canal.Id);
            subida.MarcarSubida();
            _dbContext.AcreditacionesDocumentoPlataforma.AddRange(
                new AcreditacionDocumentoPlataforma(documentoPropia.Id, canal.Id), subida);
            await _dbContext.SaveChangesAsync();
        }

        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

        var delegante = resultado.Tenants.Single(t => t.TenantId == _tenantDelegante);
        var pendiente = delegante.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Concat(delegante.BloqueoActuacion.SinGrupo)
            .Should().ContainSingle(i => i.Tipo == TipoItemBandeja.PlataformaPendiente).Subject;
        pendiente.EmpresaEsPropia.Should().BeTrue();
        pendiente.EmpresaNombre.Should().Be("Delegante Empresa");

        var seguimiento = delegante.Seguimiento.Should().ContainSingle().Subject;
        seguimiento.EmpresaEsPropia.Should().BeFalse();
        seguimiento.EmpresaNombre.Should().Be("Delegante Subcontrata");

        // Las tareas de persona no llevan el dato: su sujeto no es una Empresa.
        delegante.Proximos.Should().ContainSingle().Which.EmpresaEsPropia.Should().BeNull();
    }

    /// <summary>
    /// Coste real con varios Tenants — medido, no asumido (UNKNOWN del
    /// checkpoint). No se afirma un umbral de latencia como gate de CI
    /// (sería frágil e inestable en una máquina compartida); se deja el
    /// número medido en la salida del test para que quede como evidencia.
    /// </summary>
    [Fact]
    public async Task Coste_medido_con_seis_tenants_autorizados()
    {
        var tenants = new List<Tenant>();
        for (var i = 0; i < 4; i++)
        {
            var tenant = new Tenant($"Cartera {i}");
            tenants.Add(tenant);
            _dbContext.Tenants.Add(tenant);
        }
        await _dbContext.SaveChangesAsync();

        foreach (var tenant in tenants)
        {
            var delegacion = new DelegacionTenant(_tenantOrigen, tenant.Id);
            _dbContext.DelegacionesTenant.Add(delegacion);
            _dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));
        }
        await _dbContext.SaveChangesAsync();

        foreach (var tenant in tenants)
            await SembrarAsync(tenant.Id, tenant.Nombre);

        var mediator = _servicios.GetRequiredService<IMediator>();
        var cronometro = Stopwatch.StartNew();

        var resultado = await mediator.Send(new ObtenerMiTrabajoAgregadoQuery());

        cronometro.Stop();
        salida.WriteLine($"ObtenerMiTrabajoAgregadoQuery con {resultado.Tenants.Count} Tenants: {cronometro.ElapsedMilliseconds} ms.");

        resultado.Tenants.Should().HaveCount(6, "el origen + el Delegante del setup + los 4 nuevos de este test");
        resultado.Tenants.Select(t => t.TenantId).Should().OnlyHaveUniqueItems();
    }

    private ServiceProvider ConstruirServicios(ITenantActual tenantActual, Func<Guid?> tenantOrigenId)
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenantOrigenId()));
        return servicios.BuildServiceProvider();
    }

    private async Task SembrarAsync(Guid tenantId, string prefijo)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var cliente = Empresa.CrearComoCliente($"{prefijo} Cliente", "B12345674", false, null, null);
        var empresa = new Empresa($"{prefijo} Empresa");
        _dbContext.Empresas.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, $"{prefijo} Centro");
        _dbContext.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, prefijo, "Trabajador", GenerarDni(tenantId));
        _dbContext.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento($"Apto médico {prefijo}", 12, true, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _dbContext.TiposDocumento.Add(tipoObligatorio);
        await _dbContext.SaveChangesAsync();

        _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await _dbContext.SaveChangesAsync();

        // Vence en 20 días: por debajo del umbral ámbar (30) y por encima del
        // rojo (15) de ParametroSistema de este tenant —
        // CalculadoraEstadoDocumento.Calcular da EstadoDocumento.Proximo, el
        // bucket nuevo que Nivel 0 excluye y Mi trabajo Gen2 necesita.
        var documento = Documento.DeTrabajador(
            trabajador.Id, tipoObligatorio.Id,
            fechaEmision: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1),
            fechaVencimiento: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20));
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();
    }

    private static string GenerarDni(Guid semilla)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        var numero = Math.Abs(semilla.GetHashCode()) % 100_000_000;
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
