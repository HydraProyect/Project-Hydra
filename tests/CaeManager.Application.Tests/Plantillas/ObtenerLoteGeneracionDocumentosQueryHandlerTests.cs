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

    /// <summary>Lo que responde la query por un lote que no existe, para comparar campo a campo con un lote oculto.</summary>
    private async Task<(bool EsFallido, string Codigo, string Mensaje)> RespuestaDeUnLoteInexistente(AlcanceDatosServiceFalso alcance)
    {
        var inexistente = await Handler(new PlantillasQueryContextFalso(), new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(Guid.NewGuid()), CancellationToken.None);
        return (inexistente.EsFallido, inexistente.Error.Codigo, inexistente.Error.Mensaje);
    }

    /// <summary>
    /// Hallazgo de Codex (ronda 2 de la PR #2222, alta): el estado procedía del lote completo y los ítems se filtraban,
    /// así que un lote de un solo ítem completado y oculto devolvía cero ítems con estado Completado y delataba que hubo
    /// una generación. Sin acceso total el lote es todo o nada: responde exactamente lo mismo que un lote que no existe.
    /// </summary>
    [Fact]
    public async Task Un_lote_de_un_solo_item_completado_y_oculto_responde_igual_que_un_lote_inexistente()
    {
        var contexto = new PlantillasQueryContextFalso();
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, Guid.NewGuid());
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);
        var lote = new LoteGeneracionDocumento(version.Id, 1, Guid.NewGuid(), DateTime.UtcNow);
        contexto.ListaLotesGeneracionDocumento.Add(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        contexto.ListaItemsGeneracionDocumento.Add(item);
        CompletarConGenerado(contexto, lote, item, centroId: Guid.NewGuid());
        lote.Estado.Should().Be(EstadoLoteGeneracion.Completado, "el caso es justo el de un lote terminado");
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [item.TrabajadorId], centroIdsVisibles: [Guid.NewGuid()]);

        var oculto = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        (oculto.EsFallido, oculto.Error.Codigo, oculto.Error.Mensaje).Should().Be(await RespuestaDeUnLoteInexistente(alcance));
    }

    /// <summary>
    /// Un Gestor CAE alcanza a los dos Trabajadores por estructura propia y la plantilla es general, pero uno de los
    /// documentos se generó para un Centro fuera de su Cartera: la lista de trazabilidad lo oculta, así que el lote
    /// entero no se ve, ni siquiera el ítem cuyo generado sí estaría dentro.
    /// </summary>
    [Fact]
    public async Task Un_lote_con_un_generado_de_un_Centro_fuera_de_la_Cartera_no_se_ve_entero()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var centroDeCartera = Guid.NewGuid();
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno), centroId: centroDeCartera);
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos), centroId: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        (resultado.EsFallido, resultado.Error.Codigo, resultado.Error.Mensaje).Should().Be(await RespuestaDeUnLoteInexistente(alcance));
    }

    [Fact]
    public async Task Un_lote_con_un_generado_de_una_Empresa_fuera_de_la_Cartera_no_se_ve_entero()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var empresaDeCartera = Guid.NewGuid();
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno), empresaId: empresaDeCartera);
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos), empresaId: Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], empresaIdsVisibles: [empresaDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    [Fact]
    public async Task Sin_acceso_total_un_lote_con_un_item_que_apunta_a_un_generado_que_no_se_lee_no_se_ve()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno).MarcarCompletado(Guid.NewGuid());
        lote.RegistrarItemCompletado(DateTime.UtcNow);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.LoteNoEncontrado");
    }

    /// <summary>
    /// Quien ve el lote entero lo ve con su estado y sus recuentos reales, los del lote, no un recuento de lo que
    /// enseña: ya no hay nada filtrado que recontar.
    /// </summary>
    [Fact]
    public async Task Un_lote_visible_entero_devuelve_su_estado_y_sus_recuentos_reales()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno), conAvisos: true);
        contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos).MarcarFallido("No encontramos al trabajador.");
        lote.RegistrarItemFallido(DateTime.UtcNow);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Estado.Should().Be(lote.Estado);
        resultado.Valor.Items.Should().HaveCount(2);
        resultado.Valor.TotalItems.Should().Be(2);
        resultado.Valor.ItemsCompletados.Should().Be(1);
        resultado.Valor.ItemsFallidos.Should().Be(1);
    }

    /// <summary>Con acceso total no cambia nada: el generado de un Centro cualquiera y el que no se lee no ocultan el lote.</summary>
    [Fact]
    public async Task Con_acceso_total_el_lote_se_ve_entero_aunque_sus_generados_no_se_lean()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        CompletarConGenerado(contexto, lote, contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno), centroId: Guid.NewGuid());
        contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == dos).MarcarCompletado(Guid.NewGuid());
        lote.RegistrarItemCompletado(DateTime.UtcNow);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), new AlcanceDatosServiceFalso(tieneAccesoTotal: true))
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Estado.Should().Be(EstadoLoteGeneracion.Completado);
        resultado.Valor.Items.Should().HaveCount(2);
        resultado.Valor.ItemsCompletados.Should().Be(2);
    }

    /// <summary>
    /// Hallazgo TOCTOU de Codex (ronda 3 de la PR #2222, alta): la puerta leía los ítems y después el handler los volvía
    /// a leer para devolverlos. Aquí otro circuito completa el ítem con un generado de un Centro fuera de la Cartera justo
    /// antes de la segunda lectura. Con la instantánea leída antes de la puerta, la puerta (segunda lectura) ve ese
    /// generado y niega; con el orden antiguo la puerta habría visto el ítem pendiente y la respuesta habría llevado el
    /// generado oculto sin comprobar.
    /// </summary>
    [Fact]
    public async Task Un_item_completado_con_un_generado_oculto_entre_la_instantanea_y_la_puerta_no_sale_en_la_respuesta()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var centroDeCartera = Guid.NewGuid();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        DocumentoGenerado? oculto = null;
        contexto.AntesDeLeerItems = lectura =>
        {
            if (lectura == 2)
                oculto = CompletarConGenerado(contexto, lote, itemUno, centroId: Guid.NewGuid());
        };
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        oculto.Should().NotBeNull("el intercalado tiene que haber ocurrido entre dos lecturas de los ítems");
        if (resultado.EsExitoso)
            resultado.Valor.Items.Select(i => i.DocumentoGeneradoId).Should().NotContain(oculto!.Id);
        (resultado.EsFallido, resultado.Error.Codigo, resultado.Error.Mensaje).Should().Be(await RespuestaDeUnLoteInexistente(alcance));
    }

    /// <summary>
    /// La defensa de la puerta: cada generado de la instantánea tiene que estar en lo que la puerta comprobó, aunque la
    /// lectura actual no lo traiga. Un ítem no puede volver a pendiente (solo avanza), así que este intercalado no ocurre
    /// en la base; si ocurriera, la puerta niega en vez de dejar salir un generado que no miró.
    /// </summary>
    [Fact]
    public async Task Un_generado_oculto_de_la_instantanea_que_la_lectura_actual_ya_no_trae_tambien_se_niega()
    {
        var (contexto, lote, uno, dos) = ConstruirLote();
        var centroDeCartera = Guid.NewGuid();
        var itemUno = contexto.ListaItemsGeneracionDocumento.Single(i => i.TrabajadorId == uno);
        CompletarConGenerado(contexto, lote, itemUno, centroId: Guid.NewGuid());
        contexto.AntesDeLeerItems = lectura =>
        {
            if (lectura != 2)
                return;
            contexto.ListaItemsGeneracionDocumento.Remove(itemUno);
            contexto.ListaItemsGeneracionDocumento.Add(new ItemGeneracionDocumento(lote.Id, uno));
        };
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [uno, dos], centroIdsVisibles: [centroDeCartera]);

        var resultado = await Handler(contexto, new TrabajadoresQueryContextFalso(), alcance)
            .Handle(new ObtenerLoteGeneracionDocumentosQuery(lote.Id), CancellationToken.None);

        (resultado.EsFallido, resultado.Error.Codigo, resultado.Error.Mensaje).Should().Be(await RespuestaDeUnLoteInexistente(alcance));
    }
}