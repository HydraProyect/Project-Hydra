using CaeManager.Application.Common;
using CaeManager.Application.Plantillas;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
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
        var mediator = new MediatorFalso { Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(documentoGeneradoId, Guid.NewGuid(), [])) };
        var handler = Handler(items, lotes, mediator);

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
        var handler = Handler(items, lotes, mediator);

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        item.Error.Should().Be("No encontramos al trabajador.");
        lote.ItemsFallidos.Should().Be(1);
        lote.Estado.Should().Be(EstadoLoteGeneracion.CompletadoConErrores);
    }

    /// <summary>
    /// S-C-3 (OWASP API4:2023): sin hueco en el presupuesto de memoria de documentos la generación no guardó
    /// nada; el elemento queda fallido con el motivo, no pendiente para siempre, y el lote puede terminar.
    /// </summary>
    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_de_memoria_marca_el_item_fallido_en_vez_de_dejarlo_pendiente()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var mediator = new MediatorFalso { Excepcion = new PresupuestoMemoriaAgotadoException() };
        var handler = Handler(items, lotes, mediator);

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        item.Error.Should().Be(PresupuestoMemoriaAgotadoException.MensajeUsuario);
        lote.ItemsFallidos.Should().Be(1);
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
        var mediator = new MediatorFalso { Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid(), [])) };
        var handler = Handler(items, lotes, mediator);

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
        var handler = Handler(items, lotes, new MediatorFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ItemYaProcesado");
    }

    [Fact]
    public async Task Devuelve_fallo_si_el_item_no_existe()
    {
        var handler = Handler(new ItemGeneracionDocumentoRepositorioFalso(), new LoteGeneracionDocumentoRepositorioFalso(), new MediatorFalso());

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
    }
    /// <summary>
    /// DEC-5 (propietario, 2026-09-02): un obligatorio vacio no falla el item
    /// - el documento existe - pero tampoco lo deja como limpio. Sin este
    /// tercer estado el aviso solo viviria en la respuesta sincrona y un lote
    /// procesado de noche llegaria a la manana sin rastro de el.
    /// </summary>
    [Fact]
    public async Task Marca_el_item_completado_con_avisos_cuando_la_generacion_deja_obligatorios_vacios()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var documentoGeneradoId = Guid.NewGuid();
        var mediator = new MediatorFalso
        {
            Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(
                documentoGeneradoId, Guid.NewGuid(), ["Numero de poliza", "Mutua"])),
        };
        var handler = Handler(items, lotes, mediator);

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.CompletadoConAvisos);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId, "con avisos SI hay documento");
        item.Error.Should().Be("Campos obligatorios sin dato: Numero de poliza, Mutua.");
        lote.ItemsCompletados.Should().Be(1, "un aviso no es un fallo del lote");
        lote.ItemsFallidos.Should().Be(0);
    }

    /// <summary>
    /// DEC-32 (REC-115): un valor no reconocido por el campo tambien deja el
    /// item con avisos, aunque no falte ningun obligatorio - misma tercera
    /// categoria de estado que un obligatorio vacio, motivo distinto.
    /// </summary>
    [Fact]
    public async Task Marca_el_item_completado_con_avisos_cuando_la_generacion_deja_un_valor_no_reconocido()
    {
        var lote = CrearLote();
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        var documentoGeneradoId = Guid.NewGuid();
        var mediator = new MediatorFalso
        {
            Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(
                documentoGeneradoId, Guid.NewGuid(), [],
                [new AvisoValorNoReconocidoDto("Resultado", "Regular", ["Apto", "No apto"])])),
        };
        var handler = Handler(items, lotes, mediator);

        var resultado = await handler.Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.CompletadoConAvisos);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId, "con avisos SI hay documento");
        item.Error.Should().Be("Valores no reconocidos: Resultado (recibido «Regular»).");
        lote.ItemsCompletados.Should().Be(1, "un aviso no es un fallo del lote");
        lote.ItemsFallidos.Should().Be(0);
    }

    /// <summary>
    /// Criterio 2 de HO-004-01: un lote con un item limpio, uno con aviso y uno
    /// fallido deja los tres estados distinguibles al consultarlo, y el lote
    /// termina igual - que es justo lo que DEC-5 (b) protege frente a (a).
    /// </summary>
    [Fact]
    public async Task Un_lote_con_item_limpio_item_con_aviso_e_item_fallido_los_distingue_y_termina()
    {
        var lote = new LoteGeneracionDocumento(Guid.NewGuid(), 3, Guid.NewGuid(), DateTime.UtcNow);
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);

        var limpio = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var conAviso = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var fallido = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(limpio);
        items.Agregar(conAviso);
        items.Agregar(fallido);

        var mediator = new MediatorFalso();
        var handler = Handler(items, lotes, mediator);

        mediator.Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid(), []));
        await handler.Handle(new ProcesarItemLoteGeneracionCommand(limpio.Id), CancellationToken.None);

        mediator.Respuesta = Result.Exito(new GenerarDocumentoIndividualResultadoDto(Guid.NewGuid(), Guid.NewGuid(), ["Mutua"]));
        await handler.Handle(new ProcesarItemLoteGeneracionCommand(conAviso.Id), CancellationToken.None);

        mediator.Respuesta = Result.Fallo<GenerarDocumentoIndividualResultadoDto>(
            Error.Crear("Plantilla.PropietarioNoEncontrado", "No encontramos al trabajador."));
        await handler.Handle(new ProcesarItemLoteGeneracionCommand(fallido.Id), CancellationToken.None);

        limpio.Estado.Should().Be(EstadoItemGeneracion.Completado);
        conAviso.Estado.Should().Be(EstadoItemGeneracion.CompletadoConAvisos);
        conAviso.Error.Should().Contain("Mutua");
        fallido.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        lote.Estado.Should().Be(EstadoLoteGeneracion.CompletadoConErrores, "el lote termina pese al fallido");
        lote.CompletadoEnUtc.Should().NotBeNull();
    }

    /// <summary>
    /// IDOR (OWASP ASVS 4.0.3 V4.2.1): un lote de un Gestor CAE sobre una plantilla atada a un Cliente empresarial, con
    /// su ítem pendiente, visible para el handler por los repositorios (que solo filtran por Tenant propietario) y por
    /// el contexto de lectura (de donde salen los Trabajadores del lote).
    /// </summary>
    private static (ItemGeneracionDocumentoRepositorioFalso Items, LoteGeneracionDocumentoRepositorioFalso Lotes, PlantillasQueryContextFalso Contexto,
        LoteGeneracionDocumento Lote, ItemGeneracionDocumento Item, Guid ClienteEmpresarial) LoteDeOtroGestor(string? contextoJson = null)
    {
        var contexto = new PlantillasQueryContextFalso();
        var clienteEmpresarial = Guid.NewGuid();
        var plantilla = new PlantillaDocumento(OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual,
            Guid.NewGuid(), clienteId: clienteEmpresarial);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "url.pdf", new string('a', 64));
        contexto.ListaPlantillasDocumento.Add(plantilla);
        contexto.ListaPlantillasDocumentoVersion.Add(version);

        var lote = new LoteGeneracionDocumento(version.Id, 1, Guid.NewGuid(), DateTime.UtcNow, contextoJson);
        var lotes = new LoteGeneracionDocumentoRepositorioFalso();
        lotes.Agregar(lote);
        var item = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        var items = new ItemGeneracionDocumentoRepositorioFalso();
        items.Agregar(item);
        contexto.ListaItemsGeneracionDocumento.Add(item);
        return (items, lotes, contexto, lote, item, clienteEmpresarial);
    }

    private static MediatorFalso GeneracionQueFalla() => new()
    {
        Respuesta = Result.Fallo<GenerarDocumentoIndividualResultadoDto>(Error.Crear("Plantilla.PropietarioNoEncontrado", "No encontramos al trabajador.")),
    };

    [Fact]
    public async Task Un_Gestor_CAE_fuera_de_la_Cartera_del_lote_no_puede_marcar_su_item_y_recibe_lo_mismo_que_un_Id_inexistente()
    {
        var (items, lotes, contexto, lote, item, _) = LoteDeOtroGestor();
        var mediator = GeneracionQueFalla();
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], trabajadorIdsVisibles: [item.TrabajadorId]);

        var resultado = await Handler(items, lotes, mediator, contexto, fueraDeCartera)
            .Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
        item.Estado.Should().Be(EstadoItemGeneracion.Pendiente);
        lote.ItemsFallidos.Should().Be(0);
        mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_no_alcanza_a_un_Trabajador_del_lote_no_puede_marcar_su_item()
    {
        var (items, lotes, contexto, lote, item, clienteEmpresarial) = LoteDeOtroGestor();
        var otroTrabajadorDelLote = new ItemGeneracionDocumento(lote.Id, Guid.NewGuid());
        contexto.ListaItemsGeneracionDocumento.Add(otroTrabajadorDelLote);
        var mediator = GeneracionQueFalla();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEmpresarial], trabajadorIdsVisibles: [item.TrabajadorId]);

        var resultado = await Handler(items, lotes, mediator, contexto, alcance)
            .Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
        item.Estado.Should().Be(EstadoItemGeneracion.Pendiente);
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_el_Centro_del_lote_en_su_Cartera_no_puede_marcar_su_item()
    {
        var contextoJson = System.Text.Json.JsonSerializer.Serialize(new ContextoLoteGeneracionDto(Guid.NewGuid(), []));
        var (items, lotes, contexto, _, item, clienteEmpresarial) = LoteDeOtroGestor(contextoJson);
        var mediator = GeneracionQueFalla();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEmpresarial],
            trabajadorIdsVisibles: [item.TrabajadorId], centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await Handler(items, lotes, mediator, contexto, alcance)
            .Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
        item.Estado.Should().Be(EstadoItemGeneracion.Pendiente);
        mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Fuera_de_la_Cartera_un_item_ya_procesado_tampoco_delata_su_estado()
    {
        var (items, lotes, contexto, _, item, _) = LoteDeOtroGestor();
        item.MarcarCompletado(Guid.NewGuid());
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], trabajadorIdsVisibles: [item.TrabajadorId]);

        var resultado = await Handler(items, lotes, new MediatorFalso(), contexto, fueraDeCartera)
            .Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Plantilla.ItemNoEncontrado");
    }

    [Fact]
    public async Task Un_Gestor_CAE_dentro_de_la_Cartera_del_lote_si_puede_marcar_su_item()
    {
        var centroId = Guid.NewGuid();
        var contextoJson = System.Text.Json.JsonSerializer.Serialize(new ContextoLoteGeneracionDto(centroId, []));
        var (items, lotes, contexto, lote, item, clienteEmpresarial) = LoteDeOtroGestor(contextoJson);
        var mediator = GeneracionQueFalla();
        var dentroDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteEmpresarial],
            trabajadorIdsVisibles: [item.TrabajadorId], centroIdsVisibles: [centroId]);

        var resultado = await Handler(items, lotes, mediator, contexto, dentroDeCartera)
            .Handle(new ProcesarItemLoteGeneracionCommand(item.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        lote.ItemsFallidos.Should().Be(1);
    }

    private static ProcesarItemLoteGeneracionCommandHandler Handler(
        ItemGeneracionDocumentoRepositorioFalso items, LoteGeneracionDocumentoRepositorioFalso lotes, MediatorFalso mediator,
        PlantillasQueryContextFalso? contexto = null, AlcanceDatosServiceFalso? alcance = null) =>
        new(items, lotes, contexto ?? new PlantillasQueryContextFalso(), alcance ?? new AlcanceDatosServiceFalso(), mediator, new UnitOfWorkFalso());
}
