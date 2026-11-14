using CaeManager.Application.Plantillas.Queries.ObtenerLoteGeneracionDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
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

        var handler = new ObtenerLoteGeneracionDocumentosQueryHandler(contexto, trabajadoresContext, new AlcanceDatosServiceFalso());

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
        var handler = new ObtenerLoteGeneracionDocumentosQueryHandler(new PlantillasQueryContextFalso(), new TrabajadoresQueryContextFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerLoteGeneracionDocumentosQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    /// <summary>Un lote de dos Trabajadores sobre una plantilla, opcionalmente atada a un Cliente empresarial.</summary>
    private static (PlantillasQueryContextFalso Contexto, LoteGeneracionDocumento Lote, Guid TrabajadorUno, Guid TrabajadorDos) ConstruirLote(Guid? clienteDePlantilla = null)
    {
        var contexto = new PlantillasQueryContextFalso();
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual,
            Guid.NewGuid(), clienteId: clienteDePlantilla);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);

        var lote = new LoteGeneracionDocumento(version.Id, 2, Guid.NewGuid(), DateTime.UtcNow);
        contexto.ListaLotesGeneracionDocumento.Add(lote);
        var (uno, dos) = (Guid.NewGuid(), Guid.NewGuid());
        contexto.ListaItemsGeneracionDocumento.AddRange([new ItemGeneracionDocumento(lote.Id, uno), new ItemGeneracionDocumento(lote.Id, dos)]);
        return (contexto, lote, uno, dos);
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_alcanza_la_plantilla_y_todos_los_Trabajadores_ve_el_lote()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await new ObtenerLoteGeneracionDocumentosQueryHandler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_no_alcanza_a_un_Trabajador_del_lote_no_lo_ve()
    {
        var (contexto, lote, uno, _) = ConstruirLote();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno]);

        var resultado = await new ObtenerLoteGeneracionDocumentosQueryHandler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_lote_de_una_plantilla_atada_a_un_Cliente_empresarial_fuera_de_su_Cartera()
    {
        var (contexto, lote, uno, dos) = ConstruirLote(clienteDePlantilla: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], trabajadorIdsVisibles: [uno, dos]);

        var resultado = await new ObtenerLoteGeneracionDocumentosQueryHandler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }
}