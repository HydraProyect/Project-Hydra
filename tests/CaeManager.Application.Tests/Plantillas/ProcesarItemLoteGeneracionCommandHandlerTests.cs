using CaeManager.Application.Plantillas;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ProcesarItemLoteGeneracionCommandHandlerTests
{
    private static LoteGeneracionDocumento CrearLote(string? contextoJson = null) =>
        new(Guid.NewGuid(), 1, Guid.NewGuid(), DateTime.UtcNow, contextoJson);

    [Fact]
    public async Task Marca_el_item_completado_y_actualiza_el_lote_cuando_la_generacion_tiene_exito()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var documentoGeneradoId = Guid.NewGuid();
        var mediator = new MediatorFalso { Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(documentoGeneradoId, Guid.NewGuid())) };
        var handler = new ProcesarItemLoteGeneracionCommandHandler(items, lotes, mediator, new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.Completado);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId);
        lote.ItemsCompletados.Should().Be(1);
        lote.Estado.Should().Be(EstadoLoteGeneracion.Completado);
    }

    [Fact]
    public async Task Marca_el_item_fallido_y_no_detiene_el_lote_cuando_la_generacion_falla()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var mediator = new MediatorFalso
        {
            Respuesta = Result.Fallo<GenerarDocumentoIndividualResultadoDto>(Error.Crear("Plantilla.PropietarioNoEncontrado", "No encontramos al trabajador."))
        };
        var handler = new ProcesarItemLoteGeneracionCommandHandler(items, lotes, mediator, new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        item.Error.Should().Be("No encontramos al trabajador.");
        lote.ItemsFallidos.Should().Be(1);
        lote.Estado.Should().Be(EstadoLoteGeneracion.CompletadoConErrores);
    }

    [Fact]
    public async Task Pasa_el_centro_y_los_valores_manuales_del_contexto_del_lote()
    {
        var centroId = Guid.NewGuid();
        var contextoJson = System.Text.Json.JsonSerializer.Serialize(
            new ContextoLoteGeneracionDto(centroId, new Dictionary<Guid, string> { [Guid.NewGuid()] = "Observación" }));
        var lote = CrearLote(contextoJson);
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var mediator = new MediatorFalso { Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid())) };
        var handler = new ProcesarItemLoteGeneracionCommandHandler(items, lotes, mediator, new UnitOfWorkFalso());

        await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        var enviado = mediator.Enviados.Should().ContainSingle().Subject.Should().BeOfType<GenerarDocumentoIndividualCommand>().Subject;
        enviado.CentroId.Should().Be(centroId);
        enviado.OwnerId.Should().Be(item.TrabajadorId);
        enviado.ValoresManuales.Should().NotBeNull();
    }

    [Fact]
    public async Task Rechaza_procesar_un_item_que_ya_se_proceso()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        item.MarcarCompletado(Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var handler = new ProcesarItemLoteGeneracionCommandHandler(items, lotes, new MediatorFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ItemYaProcesado");
    }

    [Fact]
    public async Task Devuelve_fallo_si_el_item_no_existe()
    {
        var handler = new ProcesarItemLoteGeneracionCommandHandler(
            new ItemGeneracionDocumentoRepositorioFalso(), new LoteGeneracionDocumentoRepositorioFalso(), new MediatorFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
    }
}
