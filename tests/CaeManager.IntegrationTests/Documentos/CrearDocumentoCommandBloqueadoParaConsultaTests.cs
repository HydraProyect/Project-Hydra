using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Tenants;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// AutorizacionEscrituraBehaviorTests prueba el behavior aislado —lo
/// construye a mano y le pasa un <c>next</c> falso—, así que nunca ejercita
/// ni el registro real en <see cref="ApplicationServiceCollectionExtensions.AddApplication"/>
/// ni el orden real del pipeline. Este test compone el <see cref="IMediator"/>
/// de verdad (mismo <c>AddApplication()</c> que usa la aplicación) y envía
/// un <see cref="CrearDocumentoCommand"/> real con un rol Consulta: si algún
/// día el behavior se saca del registro, se reordena detrás del handler, o
/// deja de aplicar a este Command en concreto, este test lo nota — el
/// aislado no puede, porque nunca pasa por el registro ni por el orden.
///
/// Todas las interfaces de consulta que <see cref="CrearDocumentoCommandHandler"/>
/// necesita (y las que necesita <c>DerivarCanalesAplicablesDocumentoService</c>,
/// que registra <c>AddApplication()</c>) las implementa el propio
/// <see cref="CaeManagerDbContext"/> — mismo patrón que
/// AcreditacionDocumentoPlataformaSincronizacionTests. Se registran de
/// verdad, no con fakes, porque la prueba solo vale si el escenario "un rol
/// con permiso SÍ crea el Documento" es alcanzable con la misma composición:
/// si alguien retira <c>AutorizacionEscrituraBehavior</c> del registro, el
/// Command tiene que poder llegar a crear la fila de verdad para que la
/// mutación de abajo falle por el motivo correcto (se creó el Documento),
/// no por una dependencia de infraestructura que faltaba.
/// </summary>
public class CrearDocumentoCommandBloqueadoParaConsultaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private CaeManagerDbContext _dbContext = null!;
    private Guid _trabajadorId;
    private Guid _tipoDocumentoId;

    public async Task InitializeAsync()
    {
        _dbContext = CrearContexto();
        await _dbContext.Database.MigrateAsync();

        var empresa = new Empresa("Empresa Subida Masiva S.L.");
        _dbContext.Empresas.Add(empresa);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Consulta", "11223344B");
        _dbContext.Trabajadores.Add(trabajador);

        var tipoDocumento = new TipoDocumento("Certificado de aptitud médica", 12, true, 1, AmbitoAplicacion.Trabajador);
        _dbContext.TiposDocumento.Add(tipoDocumento);

        await _dbContext.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _tipoDocumentoId = tipoDocumento.Id;
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Consulta_recibe_SoloLectura_y_no_deja_ningun_Documento_en_la_base()
    {
        await using var proveedor = ConstruirProveedor("Consulta");
        var mediator = proveedor.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(NuevoCommand());

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");

        (await _dbContext.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId)).Should().Be(0,
            "el rol Consulta no puede dejar ningún Documento creado, ni siquiera cuando el Command llega a enviarse de verdad");
    }

    /// <summary>
    /// Control positivo del propio test: con un rol de escritura, la MISMA
    /// composición (mismo registro, mismo Command) sí crea el Documento.
    /// Sin este control, el test de arriba podría estar en verde por un
    /// motivo ajeno a la autorización (una dependencia mal resuelta, un
    /// Command que nunca llega a enviarse) y nunca se notaría.
    /// </summary>
    [Fact]
    public async Task GestorCae_con_la_misma_composicion_si_crea_el_Documento()
    {
        await using var proveedor = ConstruirProveedor("GestorCae");
        var mediator = proveedor.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(NuevoCommand());

        resultado.EsExitoso.Should().BeTrue();
        (await _dbContext.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId)).Should().Be(1);
    }

    private CrearDocumentoCommand NuevoCommand() => new(
        TrabajadorId: _trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
        TipoDocumentoId: _tipoDocumentoId, FechaEmision: DateOnly.FromDateTime(DateTime.UtcNow),
        FechaVencimientoManual: null, ArchivoUrl: "documentos/prueba.pdf", Comentarios: "Test de composición.");

    private ServiceProvider ConstruirProveedor(string rol)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), rol));
        servicios.AddSingleton<ITenantActual>(_tenantActual);

        // Una sola instancia de CaeManagerDbContext resuelta bajo cada
        // interfaz que el grafo de dependencias real necesita — el mismo
        // patrón "contexto, contexto, contexto" que ya usan los tests de
        // integración de Documentos, solo que aquí lo arma el contenedor.
        servicios.AddSingleton(_dbContext);
        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITrabajadoresQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITiposDocumentoQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVehiculosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProyectosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IAsignacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ICentrosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // GateComercialTenantBehavior está registrado en el pipeline para
        // cualquier ICommand — MediatR construye TODOS los behaviors del
        // pipeline antes de ejecutar ninguno (incluso los que van detrás de
        // AutorizacionEscrituraBehavior), así que su dependencia tiene que
        // resolver aunque este test nunca dependa de lo que decide.
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // Mismo motivo, un escalón más allá: al crear el Documento, el
        // handler publica DocumentacionCambiadaEvent — MediatR resuelve
        // TODOS sus INotificationHandler (aquí, uno solo:
        // EvaluarExpedienteAlCambiarDocumentacionHandler → IEvaluadorExpedienteVisitaService)
        // antes de invocar ninguno, así que su árbol de dependencias también
        // tiene que resolver para que el control positivo (GestorCae) pueda
        // llegar a crear la fila de verdad.
        servicios.AddSingleton<IVisitasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IDocumentosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IConfiguracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVisitaRepository, VisitaRepository>();

        servicios.AddSingleton<IDocumentoRepository, DocumentoRepository>();
        servicios.AddSingleton<ITrabajoAnalisisDocumentoRepository, TrabajoAnalisisDocumentoRepository>();
        servicios.AddSingleton<IAcreditacionDocumentoPlataformaRepository, AcreditacionDocumentoPlataformaRepository>();

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
