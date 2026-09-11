using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Editor de plantilla (/plantillas/{id}/editar) contra su mockup Gen 2
/// («Editor Plantilla TALVEG.dc.html»): cabecera con el estado de la versión,
/// lienzo con sus cajas, panel de campos y ficha de origen.
///
/// Y lo que el mockup no pinta pero el editor tiene que cumplir por ser una
/// pantalla que escribe: los desenlaces del guardado (éxito, rechazo del
/// servidor, excepción) sin perder lo tecleado, que confirmar no siga adelante
/// sobre un guardado fallido, y las guardas de carga vigente y de cancelación.
/// </summary>
public class ConfigurarPlantillaEditorGen2Tests : BunitContext
{
    private const string Modulo = "./js/editorPlantilla.js";

    public ConfigurarPlantillaEditorGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule(Modulo);
    }

    // --------------------------------------------------------------- Dobles

    private sealed class MediatorFalso : IMediator
    {
        public Result<PlantillaDocumentoVersionDetalleDto> Version { get; set; } =
            Result.Fallo<PlantillaDocumentoVersionDetalleDto>(Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."));

        public Result Guardado { get; set; } = Result.Exito();
        public bool GuardadoLanza { get; set; }
        public Result Confirmacion { get; set; } = Result.Exito();

        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Si devuelve una tarea para la petición, esa es la respuesta: permite retenerla.</summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public int Veces<T>() => Enviadas.Count(p => p is T);

        public CancellationToken TokenDe<T>() => Tokens[Enviadas.FindIndex(p => p is T)];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            return (TResponse)Responder(request);
        }

        private object Responder(object request) => request switch
        {
            ObtenerCentrosParaSelectorQuery => (IReadOnlyList<CentroSelectorDto>)[],
            ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
            ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
            ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
            ObtenerPlantillaDocumentoVersionQuery => Version,
            DetectarCamposPlantillaQuery => Result.Exito((IReadOnlyList<PlantillaElementoCandidatoDto>)[]),
            GuardarElementosPlantillaCommand => GuardadoLanza
                ? throw new InvalidOperationException("Fallo simulado al guardar los elementos.")
                : Guardado,
            ConfirmarPlantillaDocumentoVersionCommand => Confirmacion,
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Devuelve siempre el mismo PDF de una página: es el fondo que el editor rasteriza.</summary>
    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("blob-plantilla");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(PdfDeUnaPagina()));

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RasterizadorFalso : IRasterizadorPaginasPdfService
    {
        public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default) =>
            Result.Exito(new byte[] { 0x89, 0x50 });
    }

    private static byte[] PdfDeUnaPagina()
    {
        using var documento = new PdfDocument();
        documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    // --------------------------------------------------------------- Ayudas

    private static PlantillaElementoDetalleDto Elemento(
        string etiqueta,
        TipoElementoPlantilla tipo = TipoElementoPlantilla.Texto,
        bool obligatorio = false,
        FuenteDatoPlantilla? fuente = null,
        RolFirmantePlantilla? rolFirmante = null) =>
        new(Guid.NewGuid(), tipo, 1, 48, 128, 200, 22, etiqueta, fuente, null, null, obligatorio, rolFirmante, "campo_acro");

    private static PlantillaDocumentoVersionDetalleDto Detalle(
        Guid versionId,
        string nombre = "Anexo II — Información de riesgos",
        EstadoConfiguracionPlantilla estado = EstadoConfiguracionPlantilla.PendienteRevision,
        AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        FormatoOrigenPlantilla formato = FormatoOrigenPlantilla.PdfConCampos,
        IReadOnlyList<PlantillaElementoDetalleDto>? elementos = null) =>
        new(versionId, Guid.NewGuid(), nombre, ambito, formato, estado, "blob-plantilla",
            elementos ?? [Elemento("Razón social", obligatorio: true, fuente: FuenteDatoPlantilla.EmpresaRazonSocial)]);

    private void Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenamientoFalso>();
        Services.AddScoped<IRasterizadorPaginasPdfService, RasterizadorFalso>();
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    private readonly Guid _versionId = Guid.NewGuid();

    private IRenderedComponent<ConfigurarPlantilla> Renderizar(MediatorFalso mediador, Guid? versionId = null)
    {
        Registrar(mediador);
        var cut = Render<ConfigurarPlantilla>(p => p.Add(c => c.PlantillaDocumentoVersionId, versionId ?? _versionId));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static IElement Boton(IRenderedComponent<ConfigurarPlantilla> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    private static IElement? BotonOpcional(IRenderedComponent<ConfigurarPlantilla> cut, string texto) =>
        cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == texto);

    private static IElement CajaDe(IRenderedComponent<ConfigurarPlantilla> cut, string etiqueta) =>
        cut.FindAll(".editor-plantilla-caja").Single(c => c.TextContent.Contains(etiqueta));

    // ------------------------------------------------------------- Cabecera

    [Fact]
    public void La_cabecera_es_la_Gen_2_con_kicker_estado_junto_al_titulo_y_vuelta_al_catalogo()
    {
        var cut = Renderizar(new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Documentos · Plantillas");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Anexo II — Información de riesgos");
        cabecera.QuerySelector(".cabecera-pagina-titular [data-testid='estado-version']")!.TextContent.Trim()
            .Should().Be("Pendiente de revisión", "el badge va en la misma línea que el título y no dice el nombre del enum");
        cabecera.QuerySelector(".cabecera-pagina-descripcion")!.TextContent.Trim()
            .Should().Be(ConfigurarPlantilla.TextoDescripcionEditable);
        cabecera.QuerySelector(".acciones-cabecera a")!.GetAttribute("href").Should().Be("/plantillas");
    }

    [Fact]
    public void Una_version_confirmada_se_ve_en_solo_lectura_y_su_panel_genera_documentos()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Confirmada))
        });

        cut.Find("[data-testid='estado-version']").TextContent.Trim().Should().Be("Confirmada");
        cut.Find(".cabecera-pagina-descripcion").TextContent.Trim().Should().Be(ConfigurarPlantilla.TextoDescripcionConfirmada);
        cut.Find(".editor-plantilla-lienzo-pista").TextContent.Trim().Should().Be("Solo lectura: la versión está confirmada");
        cut.FindAll(".tarjeta-titulo").Select(t => t.TextContent.Trim()).Should().Contain("Generar documento").And.NotContain("Campos");
        cut.FindAll(".editor-plantilla-handle").Should().BeEmpty("una versión confirmada no se recoloca");
    }

    [Fact]
    public void La_ficha_de_origen_dice_lo_que_el_DTO_trae_y_nada_mas()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, ambito: AmbitoAplicacion.Empresa, formato: FormatoOrigenPlantilla.PdfVisual))
        });

        var filas = cut.FindAll(".editor-plantilla-ficha-fila");
        filas.Select(f => f.QuerySelector("dt")!.TextContent.Trim()).Should().Equal(["Ámbito", "Formato del PDF"]);
        filas.Select(f => f.QuerySelector("dd")!.TextContent.Trim()).Should().Equal(["Empresa", "PDF visual (posición a mano)"]);
    }

    // --------------------------------------------------------------- Lienzo

    [Fact]
    public async Task Cada_caja_se_anuncia_con_su_tipo_marca_lo_obligatorio_y_dice_si_esta_seleccionada()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, elementos:
            [
                Elemento("Razón social", obligatorio: true),
                Elemento("Recibí la información", TipoElementoPlantilla.Checkbox)
            ]))
        });

        var obligatoria = CajaDe(cut, "Razón social");
        obligatoria.GetAttribute("aria-label").Should().Be("Campo Razón social, tipo Texto, obligatorio");
        obligatoria.QuerySelector(".editor-plantilla-caja-obligatorio")!.TextContent.Trim().Should().Be("*");
        obligatoria.GetAttribute("aria-pressed").Should().Be("false");

        var casilla = CajaDe(cut, "Recibí la información");
        casilla.GetAttribute("aria-label").Should().Be("Campo Recibí la información, tipo Casilla");
        casilla.QuerySelector(".editor-plantilla-caja-obligatorio").Should().BeNull();

        await obligatoria.ClickAsync(new MouseEventArgs());

        CajaDe(cut, "Razón social").GetAttribute("aria-pressed").Should().Be("true");
        CajaDe(cut, "Recibí la información").GetAttribute("aria-pressed").Should().Be("false");
    }

    // ---------------------------------------------------------------- Panel

    [Fact]
    public async Task Una_firma_sin_firmante_lo_avisa_y_deja_de_avisarlo_al_elegirlo()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, elementos: [Elemento("Firma del trabajador", TipoElementoPlantilla.Firma)]))
        });

        await CajaDe(cut, "Firma del trabajador").ClickAsync(new MouseEventArgs());

        cut.Find("[data-testid='aviso-firma-sin-firmante']").TextContent.Trim()
            .Should().Be("Un campo de firma sin firmante no sabe a quién pedirle el trazo.");

        var quienFirma = cut.FindAll("select").Single(s => s.PreviousElementSibling?.TextContent.Trim() == "Quién firma");
        await quienFirma.ChangeAsync(new ChangeEventArgs { Value = RolFirmantePlantilla.Trabajador.ToString() });

        cut.FindAll("[data-testid='aviso-firma-sin-firmante']").Should().BeEmpty();
    }

    [Fact]
    public async Task Eliminar_un_campo_pide_confirmacion_y_cancelar_lo_conserva()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, elementos: [Elemento("Razón social"), Elemento("DNI")]))
        });
        await CajaDe(cut, "Razón social").ClickAsync(new MouseEventArgs());

        await Boton(cut, "Eliminar campo").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Eliminar este campo");
        cut.FindAll(".editor-plantilla-caja").Should().HaveCount(2, "todavía no se ha confirmado nada");

        await Boton(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".editor-plantilla-caja").Should().HaveCount(2);

        await Boton(cut, "Eliminar campo").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Sí, eliminar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".editor-plantilla-caja").Select(c => c.TextContent.Trim()).Should().ContainSingle().Which.Should().Contain("DNI");
    }

    // ------------------------------------------------------- Guardar cambios

    [Fact]
    public async Task Guardar_envia_los_campos_lo_dice_y_la_version_pasa_a_pendiente_de_revision()
    {
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId, estado: EstadoConfiguracionPlantilla.Borrador))
        };
        var cut = Renderizar(mediador);
        cut.Find("[data-testid='estado-version']").TextContent.Trim().Should().Be("Borrador");

        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());

        mediador.Veces<GuardarElementosPlantillaCommand>().Should().Be(1);
        Toasts.Mensajes.Select(m => m.Mensaje).Should().Contain("Cambios guardados.");
        cut.Find("[data-testid='estado-version']").TextContent.Trim().Should().Be("Pendiente de revisión");
    }

    /// <summary>Rechazo del servidor: su mensaje, y lo tecleado sigue en pantalla.</summary>
    [Fact]
    public async Task Un_guardado_rechazado_lo_dice_con_su_mensaje_y_no_pierde_lo_tecleado()
    {
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId)),
            Guardado = Result.Fallo(Error.Crear("Plantilla.VersionYaConfirmada", "Esta versión ya está confirmada — crea una versión nueva para modificarla."))
        };
        var cut = Renderizar(mediador);
        await CajaDe(cut, "Razón social").ClickAsync(new MouseEventArgs());
        var etiqueta = cut.FindAll("input[type=text]").First();
        // CampoTexto rebota 300 ms y notifica en oninput/onblur, no en onchange:
        // el blur vuelca el valor ya mismo, sin esperar al temporizador.
        await etiqueta.InputAsync(new ChangeEventArgs { Value = "Razón social del contratista" });
        await etiqueta.BlurAsync(new FocusEventArgs());

        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());

        Toasts.Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Esta versión ya está confirmada — crea una versión nueva para modificarla.");
        CajaDe(cut, "Razón social del contratista").Should().NotBeNull("lo tecleado no se revierte por un guardado rechazado");
        Boton(cut, "Guardar cambios").Should().NotBeNull("el botón vuelve a estar disponible");
    }

    /// <summary>Antes la excepción subía sin capturar y se llevaba por delante el circuito, con lo tecleado dentro.</summary>
    [Fact]
    public async Task Un_guardado_que_revienta_se_dice_sin_tumbar_la_pantalla()
    {
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)), GuardadoLanza = true };
        var cut = Renderizar(mediador);
        await CajaDe(cut, "Razón social").ClickAsync(new MouseEventArgs());
        var etiqueta = cut.FindAll("input[type=text]").First();
        // CampoTexto rebota 300 ms y notifica en oninput/onblur, no en onchange:
        // el blur vuelca el valor ya mismo, sin esperar al temporizador.
        await etiqueta.InputAsync(new ChangeEventArgs { Value = "Razón social del contratista" });
        await etiqueta.BlurAsync(new FocusEventArgs());

        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());

        Toasts.Mensajes.Select(m => m.Mensaje).Should().Equal([ConfigurarPlantilla.MensajeFalloGuardado]);
        CajaDe(cut, "Razón social del contratista").Should().NotBeNull();

        mediador.GuardadoLanza = false;
        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());

        Toasts.Mensajes.Select(m => m.Mensaje).Should().Contain("Cambios guardados.", "tras el fallo se puede reintentar");
    }

    /// <summary>
    /// El clic se lanza sin esperarlo: su manejador se queda dentro del
    /// guardado retenido y esperarlo aquí colgaría el test.
    /// </summary>
    [Fact]
    public async Task Un_doble_clic_en_guardar_no_envia_dos_comandos()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        var cut = Renderizar(mediador);
        mediador.Retener = p => p is GuardarElementosPlantillaCommand ? respuesta.Task : null;

        var primero = Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll("button").First(b => b.TextContent.Trim() is "Guardar cambios" or "Guardando…").ClickAsync(new MouseEventArgs());

        mediador.Veces<GuardarElementosPlantillaCommand>().Should().Be(1, "el segundo clic encuentra el guardado en curso");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito()));
        await primero;
        await segundo;

        mediador.Retener = null;
        await Boton(cut, "Guardar cambios").ClickAsync(new MouseEventArgs());
        mediador.Veces<GuardarElementosPlantillaCommand>().Should().Be(2, "control: con el guardado terminado, un clic nuevo sí envía");
    }

    // ---------------------------------------------------- Confirmar plantilla

    [Fact]
    public async Task Confirmar_pide_confirmacion_antes_de_hacer_nada_y_luego_guarda_confirma_y_vuelve_al_catalogo()
    {
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar plantilla").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Confirmar la plantilla");
        mediador.Veces<ConfirmarPlantillaDocumentoVersionCommand>().Should().Be(0, "abrir el diálogo no confirma nada");

        await Boton(cut, "Sí, confirmar").ClickAsync(new MouseEventArgs());

        mediador.Veces<GuardarElementosPlantillaCommand>().Should().Be(1, "confirmar guarda primero lo que hay en pantalla");
        mediador.Veces<ConfirmarPlantillaDocumentoVersionCommand>().Should().Be(1);
        Navegacion.Uri.Should().EndWith("/plantillas");
    }

    /// <summary>
    /// El defecto que esto cierra: antes confirmaba igual tras un guardado
    /// fallido. La versión quedaba inmutable con los elementos anteriores y lo
    /// tecleado se perdía sin que nadie lo dijera.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_previo_falla_no_se_confirma_ni_se_sale_de_la_pantalla()
    {
        var mediador = new MediatorFalso
        {
            Version = Result.Exito(Detalle(_versionId)),
            Guardado = Result.Fallo(Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."))
        };
        var cut = Renderizar(mediador);
        var urlAntes = Navegacion.Uri;

        await Boton(cut, "Confirmar plantilla").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Sí, confirmar").ClickAsync(new MouseEventArgs());

        mediador.Veces<GuardarElementosPlantillaCommand>().Should().Be(1);
        mediador.Veces<ConfirmarPlantillaDocumentoVersionCommand>().Should().Be(0, "confirmar sobre un guardado fallido congela lo anterior");
        Navegacion.Uri.Should().Be(urlAntes);
        Toasts.Mensajes.Select(m => m.Mensaje).Should().Equal(["No encontramos esta versión de plantilla."]);
        CajaDe(cut, "Razón social").Should().NotBeNull("la pantalla sigue con sus campos");
    }

    // ------------------------------------------------------ Carga y carreras

    [Fact]
    public async Task Si_la_carga_falla_lo_dice_con_una_alerta_y_Reintentar_pinta_la_plantilla()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);

        var alerta = cut.Find("[role='alert']");
        alerta.TextContent.Should().Contain("No pudimos cargar esta plantilla");

        mediador.Version = Result.Exito(Detalle(_versionId));
        await Boton(cut, "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Anexo II — Información de riesgos"));
        cut.FindAll("[role='alert']").Should().BeEmpty();
    }

    /// <summary>
    /// Salir de la página cancela la carga en curso, y su respuesta tardía ya
    /// no toca un componente retirado. Que el token quede cancelado demuestra
    /// además que el Dispose se ejecutó: DisposeComponentsAsync lo llama,
    /// cut.Dispose() de bUnit no.
    /// </summary>
    [Fact]
    public async Task Salir_de_la_pagina_cancela_la_carga_en_curso()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId)) };
        mediador.Retener = p => p is ObtenerPlantillaDocumentoVersionQuery ? respuesta.Task : null;
        Registrar(mediador);
        Render<ConfigurarPlantilla>(p => p.Add(c => c.PlantillaDocumentoVersionId, _versionId));

        var token = mediador.TokenDe<ObtenerPlantillaDocumentoVersionQuery>();
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del ciclo de la página");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("salir de la página cancela la carga en curso");
        var llegaTarde = () => respuesta.SetResult(Result.Exito(Detalle(_versionId)));
        llegaTarde.Should().NotThrow("la respuesta tardía no repinta un componente retirado");
    }

    /// <summary>
    /// Cambiar de versión en la ruta con la carga anterior todavía en vuelo: la
    /// respuesta vieja llega después y no puede montar su plantilla encima de
    /// la que el usuario está viendo.
    /// </summary>
    [Fact]
    public async Task Una_carga_superada_que_llega_tarde_no_pisa_a_la_version_vigente()
    {
        var vieja = new TaskCompletionSource<object>();
        var otraVersionId = Guid.NewGuid();
        var mediador = new MediatorFalso { Version = Result.Exito(Detalle(_versionId, "Anexo II — Información de riesgos")) };
        mediador.Retener = p => p is ObtenerPlantillaDocumentoVersionQuery ? vieja.Task : null;
        Registrar(mediador);
        var cut = Render<ConfigurarPlantilla>(p => p.Add(c => c.PlantillaDocumentoVersionId, _versionId));

        mediador.Retener = null;
        mediador.Version = Result.Exito(Detalle(otraVersionId, "Acta de coordinación"));
        cut.Render(p => p.Add(c => c.PlantillaDocumentoVersionId, otraVersionId));
        cut.WaitForAssertion(() => cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Acta de coordinación"));

        await cut.InvokeAsync(() => vieja.SetResult(Result.Exito(Detalle(_versionId, "Anexo II — Información de riesgos"))));

        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Acta de coordinación");
        cut.FindAll("[role='alert']").Should().BeEmpty("la carga superada tampoco puede dejar la pantalla en error");
        BotonOpcional(cut, "Reintentar").Should().BeNull();
    }
}
