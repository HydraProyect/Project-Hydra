using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
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

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// La composición de <c>ObtenerBandejaGestorQuery</c> vía <see cref="IMediator"/>
/// (fan-out a ObtenerAlertasQuery + ObtenerRevisionesIaPendientesQuery +
/// ObtenerRequisitosDocumentalesPendientesQuery) ya está probada en su lógica
/// de fusión pura por <c>ObtenerBandejaGestorQueryHandlerTests</c>
/// (Application.Tests, sin PostgreSQL). Este test cubre lo que aquel no
/// puede: que el cableado real de DI (mismo <c>AddApplication()</c> que usa
/// la app) resuelve y compone las tres Queries correctamente contra datos
/// reales — mismo motivo que <c>DashboardEjecutivoMultiTenantTests</c>
/// prueba su propio fan-out con un contenedor real en vez de solo mocks.
/// </summary>
public class ObtenerBandejaGestorQueryComposicionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Clientes.IClientesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant));
        _servicios = servicios.BuildServiceProvider();

        var cliente = new Cliente("Bandeja Composición S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa Bandeja S.L.", "B87654323");
        _dbContext.Clientes.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        await _dbContext.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro de la bandeja");
        _dbContext.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Bandeja", "Trabajador", "77189989B");
        _dbContext.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        var tipoBloqueante = new TipoDocumento("PSS firmado", null, false, 2, AmbitoAplicacion.Trabajador, esObligatorio: false);
        _dbContext.TiposDocumento.Add(tipoObligatorio);
        _dbContext.TiposDocumento.Add(tipoBloqueante);
        await _dbContext.SaveChangesAsync();

        // Faltante: asignación activa a un tipo obligatorio sin ningún Documento.
        _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));

        // Requisito bloqueante sin cumplir — TipoDocumentoCentro.BloqueaAcceso=true
        // sin ningún Documento Vigente del trabajador (PLAN-EJECUCION-UX.md § 0.4).
        // No obligatorio globalmente (esObligatorio: false), pero Incluido=true aquí
        // lo hace también "aplicar" (y por tanto Faltante en Alertas) en este Centro.
        _dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueante.Id, centro.Id, incluido: true, bloqueaAcceso: true));

        // Fase F: Visita confirmada dentro de la ventana crítica (mañana).
        var manana = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        _dbContext.Visitas.Add(new Visita(centro.Id, manana, manana, notas: null));

        // Fase F: sugerencia de visita sorpresa sin resolver, mismo día.
        var conversacion = new ConversacionCorreo("Solicitud urgente de entrada");
        _dbContext.ConversacionesCorreo.Add(conversacion);
        await _dbContext.SaveChangesAsync();
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, "cliente@ejemplo.com", "Necesitamos entrar hoy mismo");
        await _dbContext.SaveChangesAsync();
        _dbContext.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(mensaje.Id, centro.Id, null, null, "Pide entrar hoy mismo"));

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Compone_faltantes_requisitos_visitas_urgentes_y_sugerencias_en_una_sola_cola_priorizada()
    {
        var mediator = _servicios.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(new ObtenerBandejaGestorQuery());

        // El tipo bloqueante (PSS firmado) es también Faltante en Alertas — no
        // obligatorio globalmente, pero Incluido=true en este Centro lo hace
        // aplicar igual (ResolucionTipoDocumentoCentro) — de ahí dos Faltante.
        resultado.Select(i => i.Tipo).Should().ContainInOrder(
            TipoItemBandeja.SugerenciaVisitaUrgente,
            TipoItemBandeja.Faltante,
            TipoItemBandeja.VisitaUrgente,
            TipoItemBandeja.RequisitoPendiente);

        resultado.Should().HaveCount(5);
        resultado.Count(i => i.Tipo == TipoItemBandeja.Faltante).Should().Be(2);
        resultado.Should().ContainSingle(i => i.Tipo == TipoItemBandeja.RequisitoPendiente);
    }
}
