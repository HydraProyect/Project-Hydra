using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Cobertura mínima explícita de P0-1 (docs/business/MATURITY_REVIEW.md):
/// "verificar Ids referenciados en todos los Commands de creación/
/// vinculación (mínimo CrearDocumentoCommandHandler, CrearAsignacionCommandHandler)".
/// Antes de este fix, un Guid inventado se persistía sin error; ahora el
/// handler lo rechaza con un Result.Fallo legible ANTES de llegar a la base
/// de datos — AislamientoPorAgregadoTests ya prueba que la propia base de
/// datos también lo rechazaría (la FK real), pero este test cubre la capa de
/// encima: el mensaje de error que ve el usuario.
/// </summary>
public class VerificacionIdsAjenosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    [Fact]
    public async Task CrearAsignacion_rechaza_un_TrabajadorId_inexistente()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearAsignacionCommandHandler(new AsignacionRepository(contexto), contexto, new AutoridadAsignacionesServiceFalso(contexto), contexto);

        var resultado = await handler.Handle(
            new CrearAsignacionCommand(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Asignacion.TrabajadorNoEncontrado");
    }

    [Fact]
    public async Task CrearDocumento_rechaza_un_TrabajadorId_inexistente_aunque_el_TipoDocumento_sea_real()
    {
        await using var contexto = CrearContexto();

        var tipoDocumento = new TipoDocumento("Apto médico de prueba", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var handler = new CrearDocumentoCommandHandler(
            new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto,
            contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
            new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
            new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(
                TrabajadorId: Guid.NewGuid(), ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                TipoDocumentoId: tipoDocumento.Id, FechaEmision: new DateOnly(2026, 1, 1),
                FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.PropietarioNoEncontrado");
    }

    /// <summary>
    /// Hallazgo de la auditoría independiente de PR #48 sobre este mismo P0-1:
    /// CrearTipoDocumentoCommand/EditarTipoDocumentoCommand quedaron fuera del
    /// barrido original pese a que TipoDocumentoCentro recibió FK real en el
    /// mismo commit — un CentroId ajeno habría producido un DbUpdateException
    /// sin capturar (500) en vez de un Result.Fallo legible.
    /// </summary>
    [Fact]
    public async Task CrearTipoDocumento_rechaza_un_CentroId_inexistente()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearTipoDocumentoCommandHandler(
            new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto, contexto);

        var resultado = await handler.Handle(
            new CrearTipoDocumentoCommand(
                Nombre: "Tipo de prueba", VigenciaMeses: 12, AplicaVencimientoAutomatico: true, Orden: 1,
                AmbitoAplicacion: AmbitoAplicacion.Trabajador, Requerido: RequisitoDocumental.No, Naturaleza: NaturalezaJuridica.RequisitoCliente, Notas: null, Descripcion: null,
                CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null, CentroIds: [Guid.NewGuid()]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TipoDocumento.CentroNoEncontrado");
    }

    [Fact]
    public async Task EditarTipoDocumento_rechaza_un_CentroId_nuevo_inexistente()
    {
        await using var contexto = CrearContexto();

        var tipoDocumento = new TipoDocumento("Tipo de prueba", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var handler = new EditarTipoDocumentoCommandHandler(
            new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto, contexto);

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                Id: tipoDocumento.Id, Nombre: "Tipo de prueba", VigenciaMeses: 12, AplicaVencimientoAutomatico: true,
                Orden: 1, Requerido: RequisitoDocumental.No, Naturaleza: NaturalezaJuridica.RequisitoCliente, Notas: null, Descripcion: null, CriteriosValidacion: null,
                SeSolicitaA: null, Observaciones: null, CentroIds: [Guid.NewGuid()]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("TipoDocumento.CentroNoEncontrado");
    }

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo) { }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class CurrentUserServiceFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
