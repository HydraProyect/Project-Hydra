using CaeManager.Application.Plantillas.Queries.ObtenerLoteGeneracionDocumentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class ObtenerLoteGeneracionDocumentosQueryHandlerTests
{
    [Fact]
    public async Task Devuelve_el_progreso_del_lote_con_el_nombre_de_cada_trabajador()
    {
        var contexto = new PlantillasQueryContextFalso();
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", "77189989B");
        trabajadoresContext.ListaTrabajadores.Add(trabajador);

        var lote = new LoteGeneracionDocumento(Guid.NewGuid(), 2, Guid.NewGuid(), DateTime.UtcNow);
        lote.RegistrarItemCompletado(DateTime.UtcNow);
        contexto.ListaLotesGeneracionDocumento.Add(lote);

        var itemCompletado = new ItemGeneracionDocumento(lote.Id, trabajador.Id);
        itemCompletado.MarcarCompletado(Guid.NewGuid());
        var itemPendiente = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        contexto.ListaItemsGeneracionDocumento.AddRange([itemCompletado, itemPendiente]);

        var handler = new ObtenerLoteGeneracionDocumentosQueryHandler(contexto, trabajadoresContext);

        var resultado = await handler.Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Items.Should().HaveCount(2);
        resultado.Valor.Items.Single(i => i.ItemId == itemCompletado.Id).TrabajadorNombreCompleto.Should().Be("Juan Pérez");
        resultado.Valor.Items.Single(i => i.ItemId == itemPendiente.Id).TrabajadorNombreCompleto.Should().Be("—");
        resultado.Valor.ItemsCompletados.Should().Be(1);
    }

    [Fact]
    public async Task Devuelve_fallo_si_el_lote_no_existe()
    {
        var handler = new ObtenerLoteGeneracionDocumentosQueryHandler(new PlantillasQueryContextFalso(), new TrabajadoresQueryContextFalso());

        var resultado = await handler.Handle(new ObtenerLoteGeneracionDocumentosQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }
}
