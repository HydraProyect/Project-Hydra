using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plantillas;

/// <summary>
/// Auditoría de seguridad del Módulo 4 (2026-08-30): la generación en lote se
/// ejecuta ítem a ítem dentro del mismo circuito Blazor síncrono (ADR-010
/// § 2.6), pero nada impedía que dos pestañas del mismo lote —o un reintento—
/// procesaran el mismo <see cref="ItemGeneracionDocumento"/> Pendiente a la
/// vez. <see cref="ItemGeneracionDocumento"/> ahora implementa
/// <see cref="IVersionable"/> (mismo token que el resto del sistema, ver
/// RenovarDocumentoConcurrenciaTests): dos guardados simultáneos sobre el
/// mismo ítem ya no pueden pisarse en silencio.
///
/// A diferencia de RenovarDocumentoConcurrenciaTests, ProcesarItemLoteGeneracionCommand
/// no lleva una versión "vista en pantalla" — el escenario aquí no es "dos
/// personas editando", es "dos guardados simultáneos" (dos pestañas, o un
/// reintento automático), así que la protección que importa es la de EF
/// (`IsConcurrencyToken` + `ConcurrenciaOptimistaInterceptor`), no
/// `ConcurrenciaOptimista.Verificar`. El handler se invoca directamente (sin
/// pasar por MediatR), así que `ConcurrenciaBehavior` no envuelve la llamada
/// y el conflicto llega como `DbUpdateConcurrencyException` cruda — igual que
/// llegaría a cualquier llamador que no pase por el pipeline.
/// </summary>
public class ProcesarItemLoteGeneracionConcurrenciaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private Guid _itemId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var tipoDocumento = new TipoDocumento(
            "Ficha de riesgos", null, false, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador,
            FormatoOrigenPlantilla.PdfVisual, tipoDocumento.Id);
        contexto.PlantillasDocumento.Add(plantilla);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "original.pdf", new string('a', 64));
        contexto.PlantillasDocumentoVersion.Add(version);
        await contexto.SaveChangesAsync();

        // TotalItems: 2 y no 1 a propósito: si el lote se cierra en cuanto A
        // completa el ítem bajo prueba, la relectura "fresca" del lote que
        // hace B (no forzada como la del ítem) chocaría con la guarda de
        // dominio "el lote ya terminó" antes de llegar siquiera al
        // SaveChangesAsync — un InvalidOperationException ajeno al hallazgo
        // que este test comprueba, no el DbUpdateConcurrencyException real.
        var lote = new LoteGeneracionDocumento(version.Id, 2, Guid.NewGuid(), DateTime.UtcNow);
        contexto.LotesGeneracionDocumento.Add(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        contexto.ItemsGeneracionDocumento.Add(item);
        await contexto.SaveChangesAsync();

        _itemId = item.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_guardados_simultaneos_sobre_el_mismo_item_no_se_pisan_en_silencio()
    {
        await using var contextoA = CrearContexto();
        await using var contextoB = CrearContexto();

        // Las dos "pestañas" cargan el ítem Pendiente antes de que ninguna
        // guarde — forzado explícitamente en vez de confiar en el orden real
        // de dos tareas asíncronas (no determinista): al precargar el
        // ChangeTracker de cada contexto, la llamada a ObtenerPorIdAsync que
        // hace el propio handler más abajo devuelve la MISMA instancia ya
        // rastreada (identity map de EF), con la Version de este momento.
        await contextoA.ItemsGeneracionDocumento.FirstAsync(i => i.Id == _itemId);
        await contextoB.ItemsGeneracionDocumento.FirstAsync(i => i.Id == _itemId);

        var handlerA = ConstruirHandler(contextoA, exito: true);
        var handlerB = ConstruirHandler(contextoB, exito: true);

        // A guarda primero: su UPDATE renueva la Version en la base de datos.
        var resultadoA = await handlerA.Handle(new ProcesarItemLoteGeneracionCommand(_itemId), CancellationToken.None);
        resultadoA.EsExitoso.Should().BeTrue();

        // B trae en memoria la Version anterior a la escritura de A: su
        // UPDATE ya no afecta ninguna fila y EF lanza DbUpdateConcurrencyException
        // en vez de sobrescribir el resultado de A.
        var accionB = async () => await handlerB.Handle(new ProcesarItemLoteGeneracionCommand(_itemId), CancellationToken.None);
        await accionB.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verificacion = CrearContexto();
        var almacenado = await verificacion.ItemsGeneracionDocumento.FirstAsync(i => i.Id == _itemId);
        almacenado.Estado.Should().Be(EstadoItemGeneracion.Completado);
    }

    private static ProcesarItemLoteGeneracionCommandHandler ConstruirHandler(CaeManagerDbContext contexto, bool exito) =>
        new(
            new ItemGeneracionDocumentoRepository(contexto), new LoteGeneracionDocumentoRepository(contexto),
            new MediatorFalso
            {
                Respuesta = exito
                    ? Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid(), []))
                    : Result.Fallo<GenerarDocumentoIndividualResultadoDto>(Error.Crear("x", "x")),
            },
            contexto);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    /// <summary>IMediator mínimo: solo el Send genérico que usa ProcesarItemLoteGeneracionCommandHandler.</summary>
    private sealed class MediatorFalso : IMediator
    {
        public object? Respuesta { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)Respuesta!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult(Respuesta);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediatorFalso no soporta streams.");

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("MediatorFalso no soporta streams.");

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
