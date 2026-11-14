using CaeManager.Application.Plantillas.Queries.ObtenerLoteGeneracionDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Common;
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

        var handler = Handler(contexto, trabajadoresContext, new AlcanceDatosServiceFalso());

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
        var handler = Handler(new PlantillasQueryContextFalso(), new TrabajadoresQueryContextFalso(), new AlcanceDatosServiceFalso());

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

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_no_alcanza_a_un_Trabajador_del_lote_no_lo_ve()
    {
        var (contexto, lote, uno, _) = ConstruirLote();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_ve_el_lote_de_una_plantilla_atada_a_un_Cliente_empresarial_fuera_de_su_Cartera()
    {
        var (contexto, lote, uno, dos) = ConstruirLote(clienteDePlantilla: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    [Fact]
    public async Task Sin_acceso_total_el_lote_de_una_version_que_ya_no_se_lee_no_se_ve()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        contexto.ListaPlantillasDocumentoVersion.Clear();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    private readonly DocumentosQueryContextFalso _documentos = new();

    private ObtenerLoteGeneracionDocumentosQueryHandler Handler(
        PlantillasQueryContextFalso contexto, TrabajadoresQueryContextFalso trabajadoresContext, AlcanceDatosServiceFalso alcance) =>
        new(contexto, trabajadoresContext, _documentos, new ProyectosQueryContextFalso(), alcance);

    /// <summary>
    /// Completa <paramref name="item"/> con un documento generado de su Trabajador en <paramref name="centroId"/> (y
    /// <paramref name="empresaId"/>), con su Documento real y legible, para que solo decidan las coordenadas del registro.
    /// </summary>
    private DocumentoGenerado CompletarConGenerado(
        PlantillasQueryContextFalso contexto, LoteGeneracionDocumento lote, ItemGeneracionDocumento item, Guid? centroId = null, Guid? empresaId = null,
        bool conAvisos = false)
    {
        var documento = Documento.DeTrabajador(item.TrabajadorId, Guid.NewGuid(), DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "generado.pdf");
        _documentos.ListaDocumentos.Add(documento);
        var generado = new DocumentoGenerado(lote.PlantillaDocumentoVersionId, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow,
            trabajadorId: item.TrabajadorId, empresaId: empresaId, centroId: centroId, conAvisos: conAvisos);
        contexto.ListaDocumentosGenerados.Add(generado);
        if (conAvisos)
            item.MarcarCompletadoConAvisos(generado.Id, ["Mutua"], []);
        else
            item.MarcarCompletado(generado.Id);
        lote.RegistrarItemCompletado(DateTime.UtcNow);
        return generado;
    }

    /// <summary>
    /// Hallazgo de Codex (ronda 2, ASVS 4.0.3 V4.2.1): un Gestor CAE alcanza a los dos Trabajadores del lote por
    /// estructura propia y la plantilla es general, así que pasa la puerta del lote; pero uno de los documentos se generó
    /// para un Centro fuera de su Cartera. La lista de trazabilidad lo oculta por su Centro, y el progreso no puede
    /// enseñar ese ítem ni contarlo.
    /// </summary>
    [Fact]
    public async Task El_progreso_no_ensena_ni_cuenta_el_item_cuyo_generado_es_de_un_Centro_fuera_de_la_Cartera()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var centroDeCartera = Guid.NewGuid();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        var itemDos = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos);
        CompletarConGenerado(contexto, lote, itemUno, centroId: centroDeCartera);
        CompletarConGenerado(contexto, lote, itemDos, centroId: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Items.Should().ContainSingle().Which.ItemId.Should().Be(itemUno.Id);
        resultado.Valor.TotalItems.Should().Be(1);
        resultado.Valor.ItemsCompletados.Should().Be(1);
        resultado.Valor.ItemsFallidos.Should().Be(0);
    }

    [Fact]
    public async Task El_progreso_no_ensena_el_item_cuyo_generado_es_de_una_Empresa_fuera_de_la_Cartera()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var empresaDeCartera = Guid.NewGuid();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        var itemDos = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos);
        CompletarConGenerado(contexto, lote, itemUno, empresaId: empresaDeCartera);
        CompletarConGenerado(contexto, lote, itemDos, empresaId: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], empresaIdsVisibles: [empresaDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.Valor.Items.Should().ContainSingle().Which.ItemId.Should().Be(itemUno.Id);
        resultado.Valor.ItemsCompletados.Should().Be(1);
    }

    [Fact]
    public async Task Sin_acceso_total_el_item_que_apunta_a_un_generado_que_no_se_lee_no_se_ensena_y_el_pendiente_si()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        itemUno.MarcarCompletado(Guid.NewGuid());
        lote.RegistrarItemCompletado(DateTime.UtcNow);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.Valor.Items.Should().ContainSingle().Which.TrabajadorId.Should().Be(dos);
        resultado.Valor.TotalItems.Should().Be(1);
        resultado.Valor.ItemsCompletados.Should().Be(0);
    }

    [Fact]
    public async Task Los_recuentos_salen_de_los_items_que_se_ensenan_con_completados_con_avisos_y_fallidos()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        var itemDos = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos);
        CompletarConGenerado(contexto, lote, itemUno, conAvisos: true);
        itemDos.MarcarFallido("No encontramos al trabajador.");
        lote.RegistrarItemFallido(DateTime.UtcNow);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.Valor.TotalItems.Should().Be(2);
        resultado.Valor.ItemsCompletados.Should().Be(1);
        resultado.Valor.ItemsFallidos.Should().Be(1);
    }
}