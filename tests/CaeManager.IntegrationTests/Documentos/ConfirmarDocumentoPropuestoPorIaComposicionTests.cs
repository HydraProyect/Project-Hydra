using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.ConfirmarDocumentoPropuestoPorIa;
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
/// "La IA propone y una persona confirma antes de que exista el Documento"
/// (decisión del propietario, 2026-09-19). El test unitario del handler prueba
/// la guarda aislada; este compone el <see cref="IMediator"/> de verdad
/// (mismo <c>AddApplication()</c> que la aplicación) contra PostgreSQL y
/// comprueba lo que un test aislado no puede: que el Command anidado atraviesa
/// la autorización de escritura real (Consulta no crea nada), que solo una
/// Persona lo ejecuta (un servicio de fondo no deja ninguna fila) y que lo que
/// queda guardado son las fechas confirmadas y la constancia de la propuesta.
///
/// Mismo patrón de composición que
/// <c>CrearDocumentoCommandBloqueadoParaConsultaTests</c>; el control positivo
/// (GestorCae sí crea) evita que los tests negativos estén en verde por una
/// dependencia mal resuelta.
/// </summary>
public class ConfirmarDocumentoPropuestoPorIaComposicionTests : IAsyncLifetime
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

        var empresa = new Empresa("Empresa Propuesta IA S.L.");
        _dbContext.Empresas.Add(empresa);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Propuesta", "11223344B");
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
    public async Task Una_persona_con_permiso_de_escritura_crea_el_Documento_con_la_fecha_confirmada_y_la_constancia_de_la_propuesta()
    {
        await using var proveedor = ConstruirProveedor("GestorCae");
        var mediator = proveedor.GetRequiredService<IMediator>();
        var emisionLeida = new DateOnly(2026, 3, 1);

        var resultado = await mediator.Send(NuevoCommand(emisionLeida, new PropuestaIaDocumento(_trabajadorId, _tipoDocumentoId, emisionLeida, null, 97)));

        resultado.EsExitoso.Should().BeTrue();
        var documento = await _dbContext.Documentos.AsNoTracking().SingleAsync(d => d.TrabajadorId == _trabajadorId);
        documento.FechaEmision.Should().Be(emisionLeida, "la fecha leída y confirmada, nunca hoy");
        documento.Comentarios.Should().Be("Creado desde subida múltiple. Propuesta de la IA (97 % de confianza) confirmada por una persona.");
    }

    [Fact]
    public async Task Consulta_no_puede_confirmar_y_no_deja_ningun_Documento()
    {
        await using var proveedor = ConstruirProveedor("Consulta");
        var mediator = proveedor.GetRequiredService<IMediator>();

        var resultado = await mediator.Send(NuevoCommand(new DateOnly(2026, 3, 1), PropuestaIaDocumento.Vacia));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        (await _dbContext.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId)).Should().Be(0);
    }

    [Fact]
    public async Task Un_servicio_de_fondo_con_permiso_de_escritura_no_puede_confirmar_y_no_deja_ningun_Documento()
    {
        await using var proveedor = ConstruirProveedor("GestorCae");
        var mediator = proveedor.GetRequiredService<IMediator>();

        // Control positivo: la misma composición y el mismo comando SÍ crean el Documento con una persona (primer test).
        using var ambito = AmbitoActorAuditoria.EstablecerSistema();
        var resultado = await mediator.Send(NuevoCommand(new DateOnly(2026, 3, 1), new PropuestaIaDocumento(_trabajadorId, _tipoDocumentoId, new DateOnly(2026, 3, 1), null, 99)));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ConfirmacionHumanaRequerida");
        (await _dbContext.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId)).Should().Be(0,
            "una propuesta de la IA ejecutada por la plataforma no puede convertirse en Documento");
    }

    private ConfirmarDocumentoPropuestoPorIaCommand NuevoCommand(DateOnly fechaEmision, PropuestaIaDocumento propuesta) => new(
        _trabajadorId, _tipoDocumentoId, fechaEmision, FechaVencimientoManual: null, ArchivoUrl: "documentos/prueba.pdf", propuesta);

    private ServiceProvider ConstruirProveedor(string rol)
    {
        var usuarioId = Guid.NewGuid();
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(usuarioId, rol));
        servicios.AddSingleton<IActorAuditoria>(new ActorAuditoriaDePersona(usuarioId));
        servicios.AddSingleton<ITenantActual>(_tenantActual);

        servicios.AddSingleton(_dbContext);
        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEmpresasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITrabajadoresQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITiposDocumentoQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVehiculosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProyectosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IAsignacionesQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ICentrosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVisitasQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IDocumentosQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IConfiguracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IVisitaRepository, VisitaRepository>();

        servicios.AddSingleton<IDocumentoRepository, DocumentoRepository>();
        servicios.AddSingleton<ITrabajoAnalisisDocumentoRepository, TrabajoAnalisisDocumentoRepository>();
        servicios.AddSingleton<IAcreditacionDocumentoPlataformaRepository, AcreditacionDocumentoPlataformaRepository>();
        // Alcance sin restricción de cartera: este test prueba el rol, no la
        // cartera (esa la prueba CrearDocumentoCommandAlcanceCarteraTests).
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());

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

    /// <summary>Identidad de sesión resuelta y vía normal: lo que produce una persona autenticada.</summary>
    private sealed class ActorAuditoriaDePersona(Guid usuarioId) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(usuarioId));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(usuarioId);
    }
}
