using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarDocumentoPropuestoPorIa;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf;

namespace CaeManager.Web.Tests;

public sealed class SubidaMasivaGen2Tests : BunitContext
{
    private readonly Mediador _mediador = new();
    private readonly Almacenamiento _almacenamiento = new();
    private readonly Rasterizador _rasterizador = new();

    public SubidaMasivaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<IFileStorageService>(_almacenamiento);
        Services.AddSingleton<IConversorWordPdfService>(new Conversor());
        Services.AddSingleton<IRasterizadorPaginasPdfService>(_rasterizador);
        Services.AddSingleton<ICurrentUserService>(new UsuarioActualFalso(Roles.GestorCae));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<SubidaMasiva>), NullLogger<SubidaMasiva>.Instance);
        Services.AddLocalization();
        // Se resuelve en el primer Render: un test puede bajar el tope antes de pintar.
        Services.AddSingleton(_ => new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            MaximoBytesPorCircuito = _maximoPorCircuito,
            DirectorioTemporal = Path.Combine(Path.GetTempPath(), "talveg-subida-masiva-tests")
        })));
        // Igual que la cuota: se resuelve en el primer Render, y un test puede achicarlo antes.
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(_ => _presupuestoMemoria ??= new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));
    }

    private long _maximoPorCircuito = 600L * 1024 * 1024;
    private PresupuestoMemoriaDocumentos? _presupuestoMemoria;

    [Fact]
    public void El_mockup_se_pinta_con_miga_zona_de_subida_y_acciones_de_documentos()
    {
        var cut = Render<SubidaMasiva>();

        cut.FindAll(".miga-subida-masiva").Should().ContainSingle();
        cut.FindAll(".zona-subida-masiva").Should().ContainSingle();
        cut.FindAll(".zona-subida-masiva h2").Should().ContainSingle().Which.TextContent.Should().Be("Arrastra aquí los documentos de trabajador");
        cut.Find(".zona-subida-masiva p").TextContent.Should().Be("PDF · JPG · PNG · DOCX · ZIP — hasta 60 archivos, 10 MB cada uno",
            "la indicación sale del recurso con los límites que de verdad se aplican");
        cut.FindAll("a.enlace-exportar").Select(a => a.TextContent.Trim()).Should().HaveCount(2).And.Contain("Importar desde Excel").And.Contain("Revisión IA →");
    }

    [Fact]
    public async Task Los_filtros_del_lote_muestran_solo_el_estado_elegido_y_ver_todos_lo_restauran()
    {
        var cut = Render<SubidaMasiva>();
        AgregarItem(cut, "creado.pdf", "Creado");
        AgregarItem(cut, "error.pdf", "Error");
        cut.Render();

        cut.FindAll(".item-subida-masiva").Should().HaveCount(2, "el control positivo confirma que hay filas que filtrar");
        await cut.FindAll("button.filtro-lote").First(b => b.TextContent.Trim() == "Errores").ClickAsync(new MouseEventArgs());
        cut.FindAll(".item-subida-masiva").Should().HaveCount(1).And.OnlyContain(f => f.TextContent.Contains("error.pdf"));

        cut.Find("button.filtro-lote.activo").TextContent.Trim().Should().Be("Errores");
        await cut.FindAll("button.filtro-lote").First(b => b.TextContent.Trim() == "Todos").ClickAsync(new MouseEventArgs());
        cut.FindAll(".item-subida-masiva").Should().HaveCount(2, "Ver todos restaura la lista completa");
    }

    [Fact]
    public async Task Retirar_el_componente_cancela_el_token_que_recibieron_las_consultas_iniciales()
    {
        var cut = Render<SubidaMasiva>();
        var tokensConsultas = _mediador.Recibidas.Select(r => r.Token).ToList();

        tokensConsultas.Should().HaveCount(2, "las dos consultas iniciales deben observar el token del ciclo");
        tokensConsultas.Should().OnlyContain(token => !token.IsCancellationRequested);
        await CierreDePagina.CerrarPaginaAsync(cut, this);

        tokensConsultas.Should().OnlyContain(token => token.IsCancellationRequested,
            "DisposeComponentsAsync ejecuta Dispose del componente y cancela el token entregado al mediador");
    }

    [Fact]
    public void Las_consultas_iniciales_comparten_el_token_del_ciclo()
    {
        var cut = Render<SubidaMasiva>();
        var token = Campo<CancellationTokenSource>(cut.Instance, "_ciclo").Token;

        _mediador.Recibidas.Should().HaveCount(2, "el control positivo confirma las dos lecturas iniciales");
        _mediador.Recibidas.Select(r => r.Token).Should().OnlyContain(t => t == token);
    }

    /// <summary>
    /// R-1 (F-04, subida múltiple): antes, confianza >= 95 con Trabajador y tipo resueltos creaba el
    /// Documento sin que nadie mirase, con fecha de emisión "hoy". Ahora la IA solo propone.
    /// </summary>
    [Fact]
    public async Task Una_propuesta_completa_con_confianza_maxima_no_crea_ningun_Documento_hasta_que_una_persona_confirma()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();

        await ProcesarAsync(cut, "propuesta.pdf");

        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().ContainSingle(
            "control: la detección se ejecutó y devolvió una propuesta completa");
        _mediador.Recibidas.Select(r => r.Peticion).Should().NotContain(
            p => p is ConfirmarDocumentoPropuestoPorIaCommand || p is CrearDocumentoCommand,
            "sin confirmación no existe el Documento, sea cual sea la confianza");
        _almacenamiento.Guardados.Should().Be(0, "ni siquiera el archivo se guarda antes de confirmar");
        cut.Find(".item-subida-masiva").TextContent.Should()
            .Contain("Pendiente de confirmar").And.Contain("Persona CAE").And.Contain("Tipo CAE").And.Contain("01/03/2026");
        cut.FindAll(".item-subida-masiva-acciones button").Select(b => b.TextContent.Trim()).Should().Equal("Gestionar");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// P4 (2026-09-23): el selector de Trabajador de alcance Cartera tampoco enseña el DNI, ni en la
    /// propuesta ni en el buscador al corregirla. El fake siembra un DNI conocido en el origen
    /// (<see cref="TrabajadorSelectorFalso"/>).
    /// </summary>
    [Fact]
    public async Task El_selector_de_Trabajador_de_la_propuesta_no_muestra_el_DNI()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.Trabajadores = [TrabajadorSelectorFalso.Crear(Guid.NewGuid(), "Persona CAE", empleador: "Montajes Ebro S.L.")];
        var cut = Render<SubidaMasiva>();

        await ProcesarAsync(cut, "propuesta.pdf");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new MouseEventArgs());

        var buscador = cut.FindComponents<CampoBuscarSelect>().Single(c => c.Instance.Etiqueta == "Trabajador");
        buscador.Instance.Opciones.Select(o => o.Texto).Should().Equal("Persona CAE (Montajes Ebro S.L.)");
        cut.Markup.Should().Contain("Persona CAE").And.NotContain(TrabajadorSelectorFalso.DniSembrado);
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// FS-14 (auditoría UX de flujos sin salida, 2026-09-24): el lote vive solo en la
    /// memoria del circuito y salir lo perdía sin aviso. Con archivos sin confirmar,
    /// salir por la aplicación se detiene y pregunta; «Seguir con el lote» conserva la
    /// página y el lote, y «Salir y descartar» repite la navegación.
    /// </summary>
    [Fact]
    public async Task Salir_con_archivos_sin_confirmar_pregunta_y_deja_seguir_con_el_lote()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "propuesta.pdf");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));

        navegacion.Uri.Should().Be(origen, "con un archivo sin confirmar la navegación se detiene");
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Salir sin terminar el lote?").And.Contain("1 archivo(s)");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Seguir con el lote").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().BeEmpty();
        navegacion.Uri.Should().Be(origen);
        cut.Find(".item-subida-masiva").TextContent.Should().Contain("Pendiente de confirmar", "el lote sigue donde estaba");

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/documentos", "confirmar la salida repite la navegación sin volver a preguntar");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Sin_archivos_pendientes_salir_no_pregunta()
    {
        var cut = Render<SubidaMasiva>();
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/documentos"));

        navegacion.Uri.Should().EndWith("/documentos");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Tras_confirmar_existe_el_Documento_con_las_fechas_leidas_del_archivo()
    {
        var (trabajadorId, tipoId) = PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "propuesta.pdf");
        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().BeEmpty("control previo al clic");

        await GestionarYConfirmarAsync(cut);

        var comando = _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle().Subject;
        comando.TrabajadorId.Should().Be(trabajadorId);
        comando.TipoDocumentoId.Should().Be(tipoId);
        comando.FechaEmision.Should().Be(new DateOnly(2026, 3, 1), "la fecha leída del archivo, no hoy");
        comando.Propuesta.Should().Be(new PropuestaIaDocumento(trabajadorId, tipoId, new DateOnly(2026, 3, 1), null, 100));
        _mediador.Recibidas.Select(r => r.Peticion).OfType<CrearDocumentoCommand>().Should().BeEmpty("la página no crea Documentos por su cuenta");
        _almacenamiento.Guardados.Should().Be(1);
        cut.Find(".item-subida-masiva-badges").TextContent.Should().Contain("Creado tras confirmar");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Con_un_tipo_de_vencimiento_manual_la_fila_muestra_el_vencimiento_propuesto_y_el_comando_lo_lleva()
    {
        var (trabajadorId, tipoId) = PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.Tipos = [CrearTipo(tipoId) with { AplicaVencimientoAutomatico = false }];
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, trabajadorId, 100, FechaEmisionLeida: new DateOnly(2026, 3, 1), FechaVencimientoPropuesta: new DateOnly(2028, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "manual.pdf");

        cut.Find(".item-subida-masiva-propuesta").TextContent.Should().Contain("vencimiento: 01/03/2028", "el botón de un clic no puede persistir una fecha que la fila no muestra");
        await GestionarYConfirmarAsync(cut);

        var comando = _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle().Subject;
        comando.FechaVencimientoManual.Should().Be(new DateOnly(2028, 3, 1));
        comando.Propuesta.FechaVencimiento.Should().Be(new DateOnly(2028, 3, 1));
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Con_un_tipo_de_vencimiento_automatico_el_vencimiento_propuesto_no_cuenta_como_correccion()
    {
        var (trabajadorId, tipoId) = PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, trabajadorId, 100, FechaEmisionLeida: new DateOnly(2026, 3, 1), FechaVencimientoPropuesta: new DateOnly(2028, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "automatico.pdf");

        cut.Find(".item-subida-masiva-propuesta").TextContent.Should().NotContain("vencimiento");
        await GestionarYConfirmarAsync(cut);

        var comando = _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle().Subject;
        comando.FechaVencimientoManual.Should().BeNull();
        comando.Propuesta.FechaVencimiento.Should().BeNull("el campo no existe en este tipo: su ausencia no es una corrección de la persona");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Una_fila_corregida_por_la_persona_ya_no_se_presenta_como_propuesta_intacta_de_la_IA()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "corregida.pdf");
        cut.Find(".item-subida-masiva-propuesta").TextContent.Should().StartWith("Propuesta de la IA —", "control: sin tocar nada es la propuesta intacta");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new MouseEventArgs());
        var fecha = cut.FindAll("input[type=date]").First();
        await fecha.InputAsync(new ChangeEventArgs { Value = "2026-03-05" });
        await fecha.BlurAsync(new FocusEventArgs());

        cut.Find(".item-subida-masiva-propuesta").TextContent.Should().StartWith("Propuesta de la IA con tus correcciones");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Sin_fecha_leida_no_se_pone_hoy_no_se_ofrece_confirmar_de_un_clic_y_no_se_crea_nada()
    {
        PrepararPropuesta(confianza: 100, emision: null);
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "sin-fecha.pdf");

        cut.Find(".item-subida-masiva").TextContent.Should().Contain("sin leer en el archivo");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new MouseEventArgs());
        cut.Find(".item-subida-masiva-formulario input[type=date]").GetAttribute("value").Should().BeNullOrEmpty(
            "el campo no se rellena con la fecha de hoy");
        cut.Find(".ayuda-fecha-subida").TextContent.Should().Contain("no leyó una fecha de emisión");
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());

        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "Indica la fecha de emisión antes de confirmar.");
        _almacenamiento.Guardados.Should().Be(0);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task La_persona_corrige_la_fecha_y_el_Documento_nace_con_la_corregida_conservando_la_propuesta()
    {
        PrepararPropuesta(confianza: 96, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "corregir.pdf");
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new MouseEventArgs());
        cut.Find(".item-subida-masiva-formulario input[type=date]").GetAttribute("value").Should().Be("2026-03-01",
            "la fecha leída del archivo aparece en el campo para que la persona la vea");

        var campo = cut.Find(".item-subida-masiva-formulario input[type=date]");
        await campo.InputAsync(new ChangeEventArgs { Value = "2026-02-02" });
        await campo.BlurAsync(new FocusEventArgs());
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());

        var comando = _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle().Subject;
        comando.FechaEmision.Should().Be(new DateOnly(2026, 2, 2));
        comando.Propuesta.FechaEmision.Should().Be(new DateOnly(2026, 3, 1), "la propuesta viaja intacta para dejar constancia de la corrección");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Una_fecha_de_emision_futura_no_llega_a_crear_ni_a_guardar_nada()
    {
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        PrepararPropuesta(confianza: 100, emision: manana);
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "futura.pdf");

        await GestionarYConfirmarAsync(cut);

        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().BeEmpty();
        _almacenamiento.Guardados.Should().Be(0);
        cut.Find(".item-subida-masiva-badges").TextContent.Should().Contain("Pendiente de confirmar", "el archivo sigue disponible para corregir la fecha");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Limpiar_resueltos_conserva_el_total_y_los_creados_acumulados_del_lote()
    {
        var cut = Render<SubidaMasiva>();
        AgregarItem(cut, "creado-1.pdf", "Creado");
        AgregarItem(cut, "confirmado.pdf", "Creado");
        AgregarItem(cut, "error.pdf", "Error");
        cut.Render();

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Limpiar creados y descartados").ClickAsync(new MouseEventArgs());

        cut.Find(".progreso-subida-masiva-cabecera strong").TextContent.Trim().Should().Be("3 de 3 archivos leídos");
        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("2 creados");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Observa que el aviso del desenlace se emite aunque la pantalla ya no exista. NO observa que no se
    /// repinte: bUnit no lanza al repintar un componente retirado, y la mutacion que quita esa guarda
    /// sale verde (medido 2026-09-12). Esa propiedad queda sin demostrar.
    /// </summary>
    [Fact]
    public async Task El_desenlace_de_una_confirmacion_tras_retirar_el_componente_emite_su_aviso()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.CrearPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.ComandoCrearRecibido = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var toastService = Services.GetRequiredService<ToastService>();
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "confirmar.pdf");

        // Sin await: el mediador no responde hasta que el test lo libera, con el componente ya retirado.
        await GestionarAsync(cut);
        var clic = cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
        // Con limite: sin el, si el comando nunca llega el test se cuelga en vez de fallar (medido 2026-09-12).
        var primero = await Task.WhenAny(_mediador.ComandoCrearRecibido.Task, clic).WaitAsync(TimeSpan.FromSeconds(10));
        primero.Should().BeSameAs(_mediador.ComandoCrearRecibido.Task,
            "la confirmacion tiene que enviar ConfirmarDocumentoPropuestoPorIaCommand; excepcion: {0}", clic.Exception?.GetBaseException().Message);
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        _mediador.CrearPendiente.SetResult(Result.Exito(Guid.NewGuid()));

        await clic.WaitAsync(TimeSpan.FromSeconds(10));
        toastService.Mensajes.Should().ContainSingle(m => m.Mensaje == "Documento creado desde confirmar.pdf." && m.Tono == TonoToast.Exito);
    }

    /// <summary>
    /// La ruta real de creacion suma al total acumulado y limpiar no lo rebaja. El test de limpiar
    /// anade items ya creados por reflexion y no pasa por la transicion a Creado: la mutacion del
    /// incremento de esa transicion salia verde (medido 2026-09-12).
    /// </summary>
    [Fact]
    public async Task Un_documento_creado_por_la_ruta_real_cuenta_en_el_lote_y_limpiar_no_lo_resta()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "real.pdf");
        await GestionarYConfirmarAsync(cut);

        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle(
            "control: el documento se creo de verdad por la ruta de confirmacion");
        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("1 creados");
        // Un item en error mantiene la lista con filas tras limpiar: con la lista vacia el resumen entero no se pinta.
        AgregarItem(cut, "error.pdf", "Error");
        cut.Render();

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Limpiar creados y descartados").ClickAsync(new MouseEventArgs());

        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("1 creados", "limpiar quita filas, no historia del lote");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// #626: Consulta no escribe en almacenamiento. La pagina comprueba la capacidad ANTES de GuardarAsync,
    /// ademas de ocultar la zona de subida; los tests de #626 solo observan lo segundo, y la mutacion de
    /// esta guarda salia verde (medido 2026-09-12).
    /// </summary>
    [Fact]
    public async Task Consulta_no_llega_a_escribir_el_pdf_aunque_se_alcance_la_creacion()
    {
        Services.AddSingleton<ICurrentUserService>(new UsuarioActualFalso(Roles.Consulta));
        var cut = Render<SubidaMasiva>();
        var tipoItem = typeof(SubidaMasiva).GetNestedType("ItemLote", BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(tipoItem, nonPublic: true)!;
        tipoItem.GetProperty("NombreArchivo")!.SetValue(item, "consulta.pdf");
        tipoItem.GetProperty("Pdf")!.SetValue(item, await Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen").GuardarAsync(CrearPdf()));
        var crear = typeof(SubidaMasiva).GetMethod("CrearDocumentoDelItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)crear.Invoke(cut.Instance, [item, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), 1])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

        cut.Markup.Should().Contain("Solo lectura", "control: la pantalla se abrio de verdad con el rol Consulta");
        _almacenamiento.Guardados.Should().Be(0, "sin capacidad de crear, el PDF no se escribe en almacenamiento");
        _mediador.Recibidas.Select(r => r.Peticion).Should().NotContain(p => p is ConfirmarDocumentoPropuestoPorIaCommand || p is CrearDocumentoCommand);
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Defecto encontrado el 2026-09-22 (sesión de la PR #795): un archivo de más de 10 MB hacía que
    /// <c>OpenReadStream(maxAllowedSize)</c> lanzara una <see cref="IOException"/> que nadie capturaba.
    /// El archivo no aparecía en la lista, no había aviso, y el resto del lote se perdía con él porque
    /// todos los archivos se leen antes de procesar el primero. Se prueba en los dos órdenes: el
    /// grande antes y después del válido.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_archivo_de_mas_de_10_MB_se_avisa_con_su_nombre_y_el_limite_y_el_valido_sigue_adelante(bool grandePrimero)
    {
        var cut = Render<SubidaMasiva>();
        var valido = InputFileContent.CreateFromBinary(CrearPdf(), "valido.pdf", contentType: "application/pdf");
        var grande = InputFileContent.CreateFromBinary(new byte[(10 * 1024 * 1024) + 1], "grande.pdf", contentType: "application/pdf");

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(grandePrimero ? [grande, valido] : [valido, grande]));
        cut.WaitForAssertion(() => cut.FindAll(".item-subida-masiva").Should().HaveCount(2), TimeSpan.FromSeconds(10));

        var filaGrande = cut.FindAll(".item-subida-masiva").Single(f => f.QuerySelector(".item-subida-masiva-nombre")!.TextContent == "grande.pdf");
        filaGrande.QuerySelector(".item-subida-masiva-badges")!.TextContent.Should().Contain("Error");
        filaGrande.QuerySelector(".item-subida-masiva-error")!.TextContent.Should()
            .Be("«grande.pdf» supera el límite de 10 MB por archivo y no se ha subido.");

        var filaValida = cut.FindAll(".item-subida-masiva").Single(f => f.QuerySelector(".item-subida-masiva-nombre")!.TextContent == "valido.pdf");
        filaValida.TextContent.Should().Contain("Pendiente de confirmar", "el archivo válido del mismo lote se procesa igual");
        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().ContainSingle(
            "solo el válido llega a la detección; el grande no se lee");
        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("1 con error").And.Contain("1 por confirmar");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// P1-E1b: la fila de un .zip rechazado enseñaba el <c>ex.Message</c> de cualquier
    /// <see cref="InvalidDataException"/>, y <c>ZipArchive</c> lanza ese tipo con texto técnico
    /// del framework (en inglés) ante un .zip dañado. Ahora solo un límite superado enseña su
    /// mensaje, redactado para el usuario; el .zip dañado cae en el aviso genérico.
    /// </summary>
    [Fact]
    public async Task Un_zip_danado_se_avisa_con_el_texto_generico_y_un_zip_que_no_cabe_con_el_suyo()
    {
        var cut = Render<SubidaMasiva>();
        var danado = InputFileContent.CreateFromBinary(
            [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05], "danado.zip", contentType: "application/zip");
        var demasiados = InputFileContent.CreateFromBinary(CrearZipConEntradas(200), "demasiados.zip", contentType: "application/zip");

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles([danado, demasiados]));
        cut.WaitForAssertion(() => cut.FindAll(".item-subida-masiva").Should().HaveCount(2), TimeSpan.FromSeconds(10));

        string ErrorDe(string nombre) => cut.FindAll(".item-subida-masiva")
            .Single(f => f.QuerySelector(".item-subida-masiva-nombre")!.TextContent == nombre)
            .QuerySelector(".item-subida-masiva-error")!.TextContent;

        ErrorDe("danado.zip").Should().Be("No pudimos abrir este archivo .zip.",
            "el mensaje de ZipArchive es técnico y no se enseña");
        ErrorDe("demasiados.zip").Should().Be("El .zip contiene 200 archivos y no caben en esta subida.",
            "un límite superado es el caso previsto y su mensaje está redactado para el usuario");
        await DisposeComponentsAsync();
    }

    // ---- Maqueta «Subida Masiva TALVEG» (Design, 2026-10-18) ----------------------------------------------------
    // Inventario de lo que tenia main antes de rehacer la pantalla, y donde queda fijado:
    //   miga, kicker, titulo y dos enlaces ...... El_mockup_se_pinta_con_miga_zona_de_subida_y_acciones_de_documentos
    //   zona de soltar con limites ............... el mismo test
    //   filtros del lote y «Ver todos» ........... Los_filtros_del_lote_muestran_solo_el_estado_elegido...
    //   la IA solo propone (R-1) ................. Una_propuesta_completa_con_confianza_maxima_no_crea_ningun_Documento...
    //   Trabajador sin DNI ....................... El_selector_de_Trabajador_de_la_propuesta_no_muestra_el_DNI
    //   aviso al salir (FS-14) ................... Salir_con_archivos_sin_confirmar_pregunta_y_deja_seguir_con_el_lote
    //   fechas leidas, corregidas, futura ........ los tests de fecha de arriba
    //   vencimiento manual o automatico .......... Con_un_tipo_de_vencimiento_*
    //   progreso, resumen y limpiar .............. Limpiar_resueltos_* y Un_documento_creado_por_la_ruta_real_*
    //   solo lectura para Consulta ............... SubidaMasivaSoloLecturaTests
    //   limite de 10 MB y .zip danado o excesivo .. los dos Theory/Fact de mas abajo
    // Lo que cambia por la maqueta se fija en los tests de este bloque.

    [Fact]
    public void Sin_archivos_la_pantalla_ensena_el_estado_vacio_de_la_maqueta()
    {
        var cut = Render<SubidaMasiva>();

        cut.Find(".vacio-lote-subida-masiva h2").TextContent.Should().Be("Todavía no has soltado ningún archivo");
        cut.Find(".vacio-lote-subida-masiva p").TextContent.Should().Be("Cuando sueltes el primero aparecerá aquí la lista con su estado.");
        cut.FindAll(".progreso-subida-masiva").Should().BeEmpty("sin archivos no hay progreso que enseñar");
    }

    [Fact]
    public async Task Si_la_carga_inicial_falla_se_ofrece_Reintentar_y_al_reintentar_la_pantalla_carga()
    {
        _mediador.FallarCarga = true;
        var cut = Render<SubidaMasiva>();

        cut.Find("h3").TextContent.Should().Be("No pudimos cargar la subida");
        cut.FindAll(".zona-subida-masiva").Should().BeEmpty();

        _mediador.FallarCarga = false;
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".zona-subida-masiva").Should().ContainSingle();
        cut.FindAll("h3").Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    [Fact]
    public void Los_estados_en_cola_subiendo_y_leyendo_se_pintan_con_su_barra_y_cuentan_como_en_curso()
    {
        var cut = Render<SubidaMasiva>();
        AgregarItem(cut, "cola.pdf", "Cola");
        var subiendo = AgregarItem(cut, "subiendo.pdf", "Subiendo");
        Poner(subiendo, "TamanoBytes", 4L * 1048576);
        Poner(subiendo, "BytesSubidos", 2L * 1048576);
        AgregarItem(cut, "leyendo.pdf", "Procesando");
        cut.Render();

        FilaDe(cut, "cola.pdf").TextContent.Should().Contain("En cola").And.Contain("A la espera de que termine el anterior");
        var filaSubiendo = FilaDe(cut, "subiendo.pdf");
        filaSubiendo.TextContent.Should().Contain("Subiendo…").And.Contain("50 %").And.Contain("2,0 de 4,0 MB");
        filaSubiendo.QuerySelector("[role=progressbar]")!.GetAttribute("aria-valuenow").Should().Be("50");
        FilaDe(cut, "leyendo.pdf").TextContent.Should().Contain("Leyendo…").And.Contain("100 % subido · leyendo con IA");
        cut.Find(".progreso-subida-masiva-cabecera strong").TextContent.Trim().Should().Be("0 de 3 archivos leídos");
        cut.Find(".pista-lote-subida-masiva").TextContent.Should().Contain("3 en curso");
        cut.Find(".zona-soltar-archivo").ClassList.Should().Contain("zona-soltar-archivo-deshabilitada",
            "mientras hay archivos en curso no se pueden soltar mas archivos");
    }

    [Fact]
    public async Task La_unica_accion_de_la_fila_es_Gestionar_y_descartar_pide_confirmacion()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "propuesta.pdf");
        AgregarItem(cut, "error.pdf", "Error");
        cut.Render();

        static IEnumerable<string> Acciones(IElement fila) =>
            fila.QuerySelectorAll(".item-subida-masiva-acciones button").Select(b => b.TextContent.Trim());
        Acciones(FilaDe(cut, "propuesta.pdf")).Should().Equal("Gestionar");
        Acciones(FilaDe(cut, "error.pdf")).Should().Equal("Quitar de la lista");

        await GestionarAsync(cut);
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Descartar").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("Descartar este archivo").And.Contain("propuesta.pdf")
            .And.Contain("Habría que volver a soltarlo para recuperarlo. El resto del lote sigue igual.");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        FilaDe(cut, "propuesta.pdf").TextContent.Should().Contain("Pendiente de confirmar", "cancelar no descarta nada");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Descartar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Descartar").ClickAsync(new MouseEventArgs());
        FilaDe(cut, "propuesta.pdf").QuerySelector(".item-subida-masiva-badges")!.TextContent.Should().Contain("Descartado");
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .Contain(m => m.Mensaje == "Archivo descartado. El resto del lote sigue igual.");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task La_fila_ensena_la_confianza_como_dato_y_el_formulario_calcula_el_vencimiento_con_la_vigencia_del_tipo()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "propuesta.pdf");
        FilaDe(cut, "propuesta.pdf").TextContent.Should().Contain("100 % de confianza IA");

        await GestionarAsync(cut);

        var vencimiento = cut.Find(".vencimiento-subida-masiva");
        vencimiento.TextContent.Should().Contain("Vencimiento").And.Contain("01/03/2027 (emisión + 12 meses)")
            .And.Contain("Se calcula con la vigencia del tipo.");
        cut.Find(".item-subida-masiva-formulario").TextContent.Should()
            .Contain("Propuesto por la IA con el DNI leído en el documento").And.Contain("Solo tipos de ámbito Trabajador.");
        cut.Find(".item-subida-masiva-vista").TextContent.Should().Contain("Vista previa del PDF convertido, todavía sin guardar");
        cut.FindAll(".acciones-formulario button").Select(b => b.TextContent.Trim()).Should()
            .Equal("Confirmar y crear", "Cerrar sin confirmar", "Descartar");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Sin_trabajador_el_formulario_lo_dice_y_no_se_ofrece_como_propuesto_por_la_IA()
    {
        var (_, tipoId) = PrepararPropuesta(confianza: 70, emision: new DateOnly(2026, 3, 1));
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, null, 70, FechaEmisionLeida: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "sin-trabajador.pdf");

        await GestionarAsync(cut);

        cut.Find(".error-campo-subida").TextContent.Should().Be("Selecciona un trabajador antes de confirmar.");
        cut.Markup.Should().NotContain("Propuesto por la IA con el DNI");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Un_zip_se_abre_y_cada_archivo_de_dentro_sale_con_su_origen_en_el_lugar_del_zip()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var zip = InputFileContent.CreateFromBinary(CrearZipConPdfs("uno.pdf", "dos.pdf"), "lote-agosto.zip", contentType: "application/zip");

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles([zip]));
        cut.WaitForAssertion(() => cut.FindAll(".item-subida-masiva").Should().HaveCount(2), TimeSpan.FromSeconds(10));

        cut.FindAll(".item-subida-masiva-nombre").Select(n => n.TextContent).Should().Equal("uno.pdf", "dos.pdf");
        cut.FindAll(".item-subida-masiva").Should().OnlyContain(f =>
            f.TextContent.Contains("Salió de lote-agosto.zip") && f.TextContent.Contains("Pendiente de confirmar"));
        cut.Find(".progreso-subida-masiva-cabecera strong").TextContent.Trim().Should().Be("2 de 2 archivos leídos",
            "el .zip se cuenta por los archivos que trae, no por si mismo");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Varios_archivos_pasan_por_la_cola_y_acaban_todos_leidos_sin_crear_nada()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var archivos = new[] { "a.pdf", "b.pdf", "c.pdf" }
            .Select(n => InputFileContent.CreateFromBinary(CrearPdf(), n, contentType: "application/pdf")).ToArray();

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(archivos));
        cut.WaitForAssertion(() => cut.Find(".progreso-subida-masiva-cabecera strong").TextContent.Trim().Should().Be("3 de 3 archivos leídos"),
            TimeSpan.FromSeconds(10));

        cut.Find(".pista-lote-subida-masiva").TextContent.Should().Contain("Lote terminado");
        cut.FindAll(".item-subida-masiva-badges").Should().OnlyContain(b => b.TextContent.Contains("Pendiente de confirmar"));
        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().HaveCount(3);
        _mediador.Recibidas.Select(r => r.Peticion).Should().NotContain(p => p is ConfirmarDocumentoPropuestoPorIaCommand);
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Alcance de la Asignacion de Cartera. Esta pantalla no lo decide: el selector de Trabajador se pide con
    /// alcance Cartera y el alta (CrearDocumentoCommand, heredado por la confirmacion) responde «no existe» para
    /// un Trabajador fuera de ella (CrearDocumentoCommandAlcanceCarteraTests, de integracion). Aqui se fija que la
    /// pantalla pide solo la cartera y que, si el alta rechaza a un usuario fuera de cartera, no queda Documento
    /// ni archivo guardado y la fila lo cuenta como error.
    /// </summary>
    [Fact]
    public async Task Un_usuario_fuera_de_cartera_no_crea_documento_y_el_archivo_guardado_se_retira()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.CrearResultado = Result.Fallo<Guid>(Error.Crear("Documento.PropietarioNoEncontrado", "El trabajador no existe."));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "ajeno.pdf");

        _mediador.Recibidas.Select(r => r.Peticion).OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera),
                "el selector se pide con el alcance de la cartera del usuario, nunca con todo el Tenant");
        await GestionarYConfirmarAsync(cut);

        var fila = FilaDe(cut, "ajeno.pdf");
        fila.QuerySelector(".item-subida-masiva-badges")!.TextContent.Should().Contain("Error");
        fila.QuerySelector(".item-subida-masiva-error")!.TextContent.Should().Be("El trabajador no existe.");
        _almacenamiento.Guardados.Should().Be(1);
        _almacenamiento.Eliminados.Should().Be(1, "sin Documento que lo posea, el archivo guardado se retira");
        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("0 creados").And.Contain("1 con error");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Carrera descartar/confirmar: con la creacion en vuelo, «Descartar» esta deshabilitado y, aunque un
    /// dialogo de descarte ya abierto llegara a confirmar, el archivo no se descarta: el Documento se crea y
    /// la fila acaba Creada, no Descartada, con su contenido intacto hasta el final.
    /// </summary>
    [Fact]
    public async Task Descartar_mientras_se_confirma_no_descarta_y_la_fila_acaba_creada()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _mediador.CrearPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.ComandoCrearRecibido = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "carrera.pdf");
        await GestionarAsync(cut);

        var clic = cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
        var primero = await Task.WhenAny(_mediador.ComandoCrearRecibido.Task, clic).WaitAsync(TimeSpan.FromSeconds(10));
        primero.Should().BeSameAs(_mediador.ComandoCrearRecibido.Task, "la confirmacion esta en vuelo; excepcion: {0}", clic.Exception?.GetBaseException().Message);

        cut.FindAll(".acciones-formulario button").Single(b => b.TextContent.Trim() == "Descartar")
            .HasAttribute("disabled").Should().BeTrue("no se puede descartar lo que se esta confirmando");

        var item = ((System.Collections.IList)Campo<object>(cut.Instance, "_items"))[0]!;
        typeof(SubidaMasiva).GetField("_paraDescartar", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, item);
        var descartar = typeof(SubidaMasiva).GetMethod("ConfirmarDescarteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)descartar.Invoke(cut.Instance, [])!).WaitAsync(TimeSpan.FromSeconds(10));
        item.GetType().GetProperty("Estado")!.GetValue(item)!.ToString().Should().Be("PendienteConfirmar",
            "el descarte con la confirmacion en vuelo no hace nada");

        _mediador.CrearPendiente.SetResult(Result.Exito(Guid.NewGuid()));
        await clic.WaitAsync(TimeSpan.FromSeconds(10));

        FilaDe(cut, "carrera.pdf").QuerySelector(".item-subida-masiva-badges")!.TextContent.Should().Contain("Creado").And.NotContain("Descartado");
        cut.Find(".progreso-subida-masiva").TextContent.Should().Contain("1 creados").And.Contain("0 descartados");
        Services.GetRequiredService<ToastService>().Mensajes.Should().NotContain(m => m.Mensaje == "Archivo descartado. El resto del lote sigue igual.");
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Cota global: no entra un lote nuevo mientras haya archivos en cola, subiendose o leyendose. Se llama
    /// al manejador con la zona deshabilitada (el navegador no deberia dispararlo, pero es el punto de entrada real).
    /// </summary>
    [Fact]
    public async Task Mientras_hay_archivos_en_curso_no_se_acepta_un_lote_nuevo()
    {
        var cut = Render<SubidaMasiva>();
        AgregarItem(cut, "leyendo.pdf", "Procesando");
        cut.Render();
        var manejador = typeof(SubidaMasiva).GetMethod("ManejarArchivosSeleccionadosAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nuevos = new InputFileChangeEventArgs([new ArchivoFalso("nuevo.pdf"), new ArchivoFalso("otro.pdf")]);

        await cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [nuevos])!).WaitAsync(TimeSpan.FromSeconds(10));

        cut.FindAll(".item-subida-masiva").Should().ContainSingle("el lote nuevo se ignora mientras otro sigue en curso");
        cut.Find(".zona-soltar-archivo").ClassList.Should().Contain("zona-soltar-archivo-deshabilitada");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Consulta_no_ve_el_vacio_que_invita_a_soltar_archivos()
    {
        Services.AddSingleton<ICurrentUserService>(new UsuarioActualFalso(Roles.Consulta));
        var cut = Render<SubidaMasiva>();

        cut.Markup.Should().Contain("Solo lectura", "control: la pantalla se abrio de verdad con el rol Consulta");
        cut.FindAll(".vacio-lote-subida-masiva").Should().BeEmpty("a Consulta no se le invita a soltar archivos");
        cut.FindAll(".zona-subida-masiva").Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Mas_de_60_archivos_en_un_lote_se_rechazan_enteros_con_el_aviso_del_maximo()
    {
        var cut = Render<SubidaMasiva>();
        var archivos = Enumerable.Range(0, 61)
            .Select(i => InputFileContent.CreateFromBinary(CrearPdf(), $"f{i}.pdf", contentType: "application/pdf")).ToArray();

        await cut.InvokeAsync(() => cut.FindComponent<InputFile>().UploadFiles(archivos));

        var aviso = cut.Find(".error-lote-subida-masiva");
        aviso.TextContent.Should().Contain("El lote supera el máximo de 60 archivos por subida")
            .And.Contain("No se ha guardado ningún archivo de este lote. Suelta menos archivos cada vez.");
        cut.FindAll(".item-subida-masiva").Should().BeEmpty("ninguno de los 61 llega a la lista");
        _almacenamiento.Guardados.Should().Be(0);
        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task La_fila_creada_ofrece_Gestionar_como_enlace_a_su_documento()
    {
        var cut = Render<SubidaMasiva>();
        var item = AgregarItem(cut, "hecho.pdf", "Creado");
        var documentoId = Guid.NewGuid();
        Poner(item, "DocumentoId", documentoId);
        cut.Render();

        var enlace = FilaDe(cut, "hecho.pdf").QuerySelectorAll(".item-subida-masiva-acciones a").Should().ContainSingle().Subject;
        enlace.TextContent.Trim().Should().Be("Gestionar");
        enlace.GetAttribute("href").Should().Be($"/documentos?documentoId={documentoId}");
        FilaDe(cut, "hecho.pdf").QuerySelectorAll(".item-subida-masiva-acciones button").Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// S-C-3 (ASVS 4.0.3 V12.1.1): lo que no cabe en lo que la pantalla ya retiene se marca con error
    /// sin pedir un solo byte al navegador, y el archivo que sí cabe del mismo lote sigue adelante.
    /// </summary>
    [Fact]
    public async Task Un_archivo_que_no_cabe_en_lo_retenido_se_rechaza_sin_leerlo_y_el_que_cabe_sigue()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var pdf = CrearPdf();
        _maximoPorCircuito = pdf.Length * 3L;
        var cut = Render<SubidaMasiva>();
        var cabe = new ArchivoContado("cabe.pdf", pdf);
        var excede = new ArchivoContado("excede.pdf", new byte[pdf.Length * 3 + 1]);
        var manejador = typeof(SubidaMasiva).GetMethod("ManejarArchivosSeleccionadosAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([excede, cabe])])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => FilaDe(cut, "cabe.pdf").TextContent.Should().Contain("Pendiente de confirmar"), TimeSpan.FromSeconds(10));

        FilaDe(cut, "excede.pdf").QuerySelector(".item-subida-masiva-error")!.TextContent.Should()
            .Be("No cabe en esta subida: confirma o descarta los archivos pendientes y vuelve a soltarlo.");
        excede.Aperturas.Should().Be(0, "el excedente se rechaza antes de abrir su flujo");
        cabe.Aperturas.Should().Be(1, "control: el que cabe sí se lee");
        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().ContainSingle();
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Seguimiento de S-C-3 (ASVS 4.0.3 V12.1.1): sin hueco en el presupuesto de memoria del proceso,
    /// la lectura espera lo acotado y el archivo acaba en Error con el mismo mensaje que la cuota de
    /// #1835, sin cargarse ni llegar a la IA; su temporal se borra. Con hueco, el siguiente sigue.
    /// </summary>
    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_de_memoria_la_lectura_se_rechaza_con_el_mensaje_de_la_cuota()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _presupuestoMemoria = new PresupuestoMemoriaDocumentos(Options.Create(new OpcionesPresupuestoMemoriaDocumentos
        {
            MaximoBytes = LimitesArchivoSubido.TamanoMaximoBytes,
            EsperaMaxima = TimeSpan.FromMilliseconds(100)
        }), TimeProvider.System);
        var ocupado = await _presupuestoMemoria.ReservarAsync(LimitesArchivoSubido.TamanoMaximoBytes);
        var cut = Render<SubidaMasiva>();
        var almacen = Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen");
        var manejador = typeof(SubidaMasiva).GetMethod("ManejarArchivosSeleccionadosAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([new ArchivoContado("sin-hueco.pdf", CrearPdf())])])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => FilaDe(cut, "sin-hueco.pdf").QuerySelector(".item-subida-masiva-error")!.TextContent.Should()
            .Be("No cabe en esta subida: confirma o descarta los archivos pendientes y vuelve a soltarlo."), TimeSpan.FromSeconds(10));

        _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().BeEmpty("no se cargó para leerlo");
        almacen.Reservado.Should().Be(0, "su temporal se borró y la cuota de la pantalla volvió");
        _presupuestoMemoria.EnEspera.Should().Be(0, "la espera vencida sale de la cola");
        _presupuestoMemoria.Reservado.Should().Be(LimitesArchivoSubido.TamanoMaximoBytes, "solo queda la reserva que lo bloqueaba");

        ocupado.Dispose();
        await cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([new ArchivoContado("con-hueco.pdf", CrearPdf())])])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => FilaDe(cut, "con-hueco.pdf").TextContent.Should().Contain("Pendiente de confirmar"), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => _presupuestoMemoria.Reservado.Should().Be(0, "terminada la lectura, su reserva vuelve"), TimeSpan.FromSeconds(10));
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Revisión de Codex del 2026-11-01 (OWASP API4:2023, ASVS 4.0.3 V12.1.1 y V5.2.1): la lectura reserva cuatro veces el
    /// tamaño comprimido, que no acota lo que cuesta decodificar una imagen. Un PNG de 2000 x 1500 px pesa unos KB y
    /// decodificado cuesta 6 B por píxel: antes de convertirlo se reserva además ese coste. Con ese hueco entra;
    /// con un byte menos, el archivo acaba en Error con el mensaje de la cuota, sin llegar a la IA, y nada queda reservado.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Una_imagen_muy_comprimida_reserva_ademas_el_coste_de_decodificarla(long bytesOcupados, bool entra)
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var png = DimensionesImagenTests.CrearPngUniforme(2000, 1500);
        var necesario = 4L * png.Length + 2000L * 1500 * DimensionesImagen.BytesPorPixelDecodificado;
        _presupuestoMemoria = new PresupuestoMemoriaDocumentos(Options.Create(new OpcionesPresupuestoMemoriaDocumentos
        {
            MaximoBytes = necesario,
            EsperaMaxima = TimeSpan.FromMilliseconds(100)
        }), TimeProvider.System);
        using var ocupado = await _presupuestoMemoria.ReservarAsync(bytesOcupados);
        var cut = Render<SubidaMasiva>();
        var manejador = typeof(SubidaMasiva).GetMethod("ManejarArchivosSeleccionadosAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)manejador.Invoke(cut.Instance, [new InputFileChangeEventArgs([new ArchivoContado("foto.png", png)])])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

        if (entra)
        {
            cut.WaitForAssertion(() => FilaDe(cut, "foto.png").TextContent.Should().Contain("Pendiente de confirmar"), TimeSpan.FromSeconds(10));
        }
        else
        {
            cut.WaitForAssertion(() => FilaDe(cut, "foto.png").QuerySelector(".item-subida-masiva-error")!.TextContent.Should()
                .Be("No cabe en esta subida: confirma o descarta los archivos pendientes y vuelve a soltarlo."), TimeSpan.FromSeconds(10));
            _mediador.Recibidas.Select(r => r.Peticion).OfType<DetectarCamposDocumentoQuery>().Should().BeEmpty("no se convirtió ni llegó a la IA");
        }

        cut.WaitForAssertion(() => _presupuestoMemoria.Reservado.Should().Be(bytesOcupados, "ninguna de las dos reservas se pierde"), TimeSpan.FromSeconds(10));
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Seguimiento de S-C-3: si al confirmar el almacenamiento no tiene hueco en el presupuesto de memoria,
    /// el rechazo es pasajero: avisa con el mensaje de la cuota, la fila sigue pendiente con su PDF y se
    /// puede volver a confirmar sin repetir la subida ni la lectura de la IA. La vista previa abierta se
    /// suelta antes de guardar, para que el guardado no espere a una reserva de su misma pantalla.
    /// </summary>
    [Fact]
    public async Task Confirmar_sin_hueco_en_el_presupuesto_de_memoria_deja_la_fila_pendiente_con_su_pdf()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var almacen = Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen");
        var presupuesto = (PresupuestoMemoriaDocumentos)Services.GetRequiredService<IPresupuestoMemoriaDocumentos>();
        await ProcesarAsync(cut, "reintento.pdf");
        await FilaDe(cut, "reintento.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        presupuesto.Reservado.Should().BePositive("control: la vista previa está abierta");

        _almacenamiento.SinMemoria = true;
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t =>
            t.Mensaje == "No cabe en esta subida: confirma o descarta los archivos pendientes y vuelve a soltarlo.");
        FilaDe(cut, "reintento.pdf").TextContent.Should().Contain("Pendiente de confirmar", "el formulario sigue abierto para reintentar");
        Directory.GetFiles(almacen.Directorio).Should().ContainSingle("el PDF sigue en su temporal para reintentar");
        presupuesto.Reservado.Should().Be(0, "la vista previa soltó su reserva antes de guardar");

        _almacenamiento.SinMemoria = false;
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
        _almacenamiento.Guardados.Should().Be(1, "control: con hueco se confirma");
        Directory.GetFiles(almacen.Directorio).Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Seguimiento de S-C-3: la vista previa abierta descuenta del presupuesto de memoria del proceso el
    /// PDF y su base64, y cerrarla, confirmar o cerrar la pantalla lo devuelve.
    /// </summary>
    [Fact]
    public async Task La_vista_previa_descuenta_del_presupuesto_de_memoria_mientras_esta_abierta()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var presupuesto = (PresupuestoMemoriaDocumentos)Services.GetRequiredService<IPresupuestoMemoriaDocumentos>();
        await ProcesarAsync(cut, "abierto.pdf");
        presupuesto.Reservado.Should().Be(0, "control: con la fila cerrada no hay nada en memoria");

        await FilaDe(cut, "abierto.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        var dataUri = cut.Find("iframe").GetAttribute("src")!;
        presupuesto.Reservado.Should().BeGreaterThanOrEqualTo(dataUri.Length * (long)sizeof(char), "el base64 abierto descuenta");

        await FilaDe(cut, "abierto.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        cut.FindAll("iframe").Should().BeEmpty("control: la fila se cerró");
        presupuesto.Reservado.Should().Be(0);

        await FilaDe(cut, "abierto.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        presupuesto.Reservado.Should().BePositive("control: vuelve a abrirse");
        await CierreDePagina.CerrarPaginaAsync(cut, this);
        presupuesto.Reservado.Should().Be(0, "cerrar la pantalla la devuelve");
    }

    /// <summary>S-C-3: confirmar, descartar o cerrar la pantalla borra el temporal del PDF y devuelve su reserva.</summary>
    [Fact]
    public async Task Confirmar_y_descartar_borran_el_temporal_del_pdf_y_la_vista_previa_solo_existe_abierta()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var almacen = Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen");
        await ProcesarAsync(cut, "confirmar.pdf");
        await ProcesarAsync(cut, "descartar.pdf");
        Directory.GetFiles(almacen.Directorio).Should().HaveCount(2, "control: cada pendiente espera en su temporal");
        cut.FindAll("iframe").Should().BeEmpty("sin fila abierta no hay base64 del PDF");

        await FilaDe(cut, "confirmar.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        cut.Find("iframe").GetAttribute("src").Should().StartWith("data:application/pdf;base64,");
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
        Directory.GetFiles(almacen.Directorio).Should().ContainSingle("el PDF confirmado ya está en el almacenamiento");
        cut.FindAll("iframe").Should().BeEmpty();

        var item = ((System.Collections.IList)Campo<object>(cut.Instance, "_items")).Cast<object>()
            .Single(i => (string)i.GetType().GetProperty("NombreArchivo")!.GetValue(i)! == "descartar.pdf");
        typeof(SubidaMasiva).GetField("_paraDescartar", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, item);
        var descartar = typeof(SubidaMasiva).GetMethod("ConfirmarDescarteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)descartar.Invoke(cut.Instance, [])!).WaitAsync(TimeSpan.FromSeconds(10));

        Directory.GetFiles(almacen.Directorio).Should().BeEmpty();
        almacen.Reservado.Should().Be(0);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Cerrar_la_pantalla_borra_los_temporales_pendientes_y_devuelve_la_reserva()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        var almacen = Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen");
        var cuota = Services.GetRequiredService<CuotaSubidaMasiva>();
        await ProcesarAsync(cut, "pendiente.pdf");
        Directory.GetFiles(almacen.Directorio).Should().ContainSingle("control: el pendiente está en disco");
        cuota.ReservadoGlobal.Should().BePositive();

        await CierreDePagina.CerrarPaginaAsync(cut, this);

        Directory.Exists(almacen.Directorio).Should().BeFalse();
        cuota.ReservadoGlobal.Should().Be(0);
    }

    /// <summary>
    /// S-C-3: la miniatura se pide reducida (no la página entera a 150 ppp), su base64 en memoria
    /// descuenta del tope de la pantalla, y quitar o limpiar la fila lo devuelve. Si no cabe, la fila
    /// sigue sin miniatura.
    /// </summary>
    [Fact]
    public async Task La_miniatura_se_pide_reducida_descuenta_del_tope_y_quitar_o_limpiar_la_devuelve()
    {
        PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        _rasterizador.Miniatura = new byte[3000];
        // 1 MiB: caben el PDF y una miniatura pequeña, no una de 600 KB (1,6 MB de base64 en UTF-16).
        _maximoPorCircuito = 1024 * 1024;
        var cut = Render<SubidaMasiva>();
        var almacen = Campo<AlmacenTemporalSubidaMasiva>(cut.Instance, "_almacen");
        await ProcesarAsync(cut, "con-miniatura.pdf");

        _rasterizador.AnchoPedido.Should().Be(144);
        _rasterizador.PaginasEnteras.Should().Be(0, "la miniatura ya no rasteriza la página entera");
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(_rasterizador.Miniatura);
        FilaDe(cut, "con-miniatura.pdf").QuerySelector("img")!.GetAttribute("src").Should().Be(dataUri);
        var pdfEnDisco = new FileInfo(Directory.GetFiles(almacen.Directorio).Should().ContainSingle().Subject).Length;
        almacen.Reservado.Should().Be(pdfEnDisco + dataUri.Length * sizeof(char), "el PDF en disco y la miniatura en memoria");

        // El Documento creado sigue en la lista con su miniatura hasta limpiar los resueltos.
        await FilaDe(cut, "con-miniatura.pdf").QuerySelector(".item-subida-masiva-acciones button")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => almacen.Reservado.Should().Be(dataUri.Length * sizeof(char),
            "control: el temporal del PDF se borró y la miniatura sigue a la vista"), TimeSpan.FromSeconds(10));
        var limpiar = typeof(SubidaMasiva).GetMethod("LimpiarResueltos", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => limpiar.Invoke(cut.Instance, []));
        almacen.Reservado.Should().Be(0);

        // Sin sitio para la miniatura, la fila llega igual a Pendiente, sin imagen; descartarla lo devuelve todo.
        _rasterizador.Miniatura = new byte[600 * 1024];
        await ProcesarAsync(cut, "sin-sitio.pdf");
        FilaDe(cut, "sin-sitio.pdf").QuerySelector("img").Should().BeNull();
        almacen.Reservado.Should().Be(new FileInfo(Directory.GetFiles(almacen.Directorio).Should().ContainSingle().Subject).Length);

        var segundo = ((System.Collections.IList)Campo<object>(cut.Instance, "_items")).Cast<object>().Single();
        typeof(SubidaMasiva).GetField("_paraDescartar", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, segundo);
        var descartar = typeof(SubidaMasiva).GetMethod("ConfirmarDescarteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)descartar.Invoke(cut.Instance, [])!).WaitAsync(TimeSpan.FromSeconds(10));
        almacen.Reservado.Should().Be(0);
        Directory.GetFiles(almacen.Directorio).Should().BeEmpty();
        await DisposeComponentsAsync();
    }
    private sealed class ArchivoContado(string nombre, byte[] contenido) : IBrowserFile
    {
        public int Aperturas { get; private set; }
        public string Name => nombre;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => contenido.Length;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Aperturas++;
            return new MemoryStream(contenido);
        }
    }

    private sealed class ArchivoFalso(string nombre) : IBrowserFile
    {
        public string Name => nombre;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => 10;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static IElement FilaDe<T>(IRenderedComponent<T> cut, string nombre) where T : IComponent =>
        cut.FindAll(".item-subida-masiva").Single(f => f.QuerySelector(".item-subida-masiva-nombre")!.TextContent == nombre);

    private static void Poner(object item, string propiedad, object valor) =>
        item.GetType().GetProperty(propiedad)!.SetValue(item, valor);

    private static byte[] CrearZipConPdfs(params string[] nombres)
    {
        using var memoria = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memoria, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var nombre in nombres)
            {
                using var flujo = zip.CreateEntry(nombre).Open();
                flujo.Write(CrearPdf());
            }
        }

        return memoria.ToArray();
    }
    private static byte[] CrearZipConEntradas(int cuantas)
    {
        using var memoria = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memoria, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < cuantas; i++)
            {
                using var flujo = zip.CreateEntry($"doc{i}.pdf").Open();
                flujo.Write([0x25, 0x50, 0x44, 0x46]);
            }
        }

        return memoria.ToArray();
    }

    private (Guid TrabajadorId, Guid TipoId) PrepararPropuesta(int confianza, DateOnly? emision)
    {
        var trabajadorId = Guid.NewGuid();
        var tipoId = Guid.NewGuid();
        _mediador.Trabajadores = [TrabajadorSelectorFalso.Crear(trabajadorId, "Persona CAE")];
        _mediador.Tipos = [CrearTipo(tipoId)];
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, trabajadorId, confianza, FechaEmisionLeida: emision);
        return (trabajadorId, tipoId);
    }

    /// <summary>
    /// En el Dispatcher del renderer: el metodo llama a StateHasChanged, y fuera de el lanza (medido 2026-09-12).
    /// Lee un archivo que ya esta en memoria (el paso de «Leyendo…»), sin pasar por la subida de bytes.
    /// </summary>
    private static async Task ProcesarAsync(IRenderedComponent<SubidaMasiva> cut, string nombre)
    {
        var item = AgregarItem(cut, nombre, "Procesando");
        var metodo = typeof(SubidaMasiva).GetMethod("LeerItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // La reserva de la lectura (vacía aquí: el original ya está en memoria) que LeerItemAsync amplía si hay imágenes.
        using var reservaLectura = await cut.Services.GetRequiredService<IPresupuestoMemoriaDocumentos>().ReservarAsync(0);
        await cut.InvokeAsync(() => (Task)metodo.Invoke(cut.Instance, [item, CrearPdf(), reservaLectura, 1, CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>La unica accion de una fila por confirmar es «Gestionar»: abre el formulario con la propuesta prellenada.</summary>
    private static async Task GestionarAsync(IRenderedComponent<SubidaMasiva> cut) =>
        await cut.FindAll(".item-subida-masiva-acciones button").First(b => b.TextContent.Trim() == "Gestionar").ClickAsync(new MouseEventArgs());

    private static async Task GestionarYConfirmarAsync(IRenderedComponent<SubidaMasiva> cut)
    {
        await GestionarAsync(cut);
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar y crear").ClickAsync(new MouseEventArgs());
    }
    private static object AgregarItem<T>(IRenderedComponent<T> cut, string nombre, string estado)
        where T : IComponent
    {
        var tipoItem = typeof(SubidaMasiva).GetNestedType("ItemLote", BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(tipoItem, nonPublic: true)!;
        tipoItem.GetProperty("NombreArchivo")!.SetValue(item, nombre);
        var tipoEstado = typeof(SubidaMasiva).GetNestedType("EstadoItem", BindingFlags.NonPublic)!;
        tipoItem.GetProperty("Estado")!.SetValue(item, Enum.Parse(tipoEstado, estado));
        typeof(SubidaMasiva).GetMethod("AgregarItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cut.Instance, [item]);
        return item;
    }

    private static byte[] CrearPdf()
    {
        using var documento = new PdfDocument();
        documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    private static TipoDocumentoListaDto CrearTipo(Guid id) => new(
        id, "Tipo CAE", 12, true, 1, CaeManager.Domain.Documentos.AmbitoAplicacion.Trabajador,
        default, default, null, null, null, null, false, false, false, default, []);

    private static T Campo<T>(object instancia, string nombre) => (T)instancia.GetType()
        .GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instancia)!;

    private sealed class Mediador : IMediator
    {
        public List<(object Peticion, CancellationToken Token)> Recibidas { get; } = [];
        public IReadOnlyList<TrabajadorSelectorDto> Trabajadores { get; set; } = [];
        public IReadOnlyList<TipoDocumentoListaDto> Tipos { get; set; } = [];
        public DeteccionCamposDocumentoDto? Deteccion { get; set; }
        public TaskCompletionSource<Result<Guid>>? CrearPendiente { get; set; }
        public TaskCompletionSource? ComandoCrearRecibido { get; set; }
        public bool FallarCarga { get; set; }
        public Result<Guid>? CrearResultado { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add((request, cancellationToken));
            object? valor = request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => FallarCarga ? throw new InvalidOperationException("carga") : Trabajadores,
                ObtenerTiposDocumentoQuery => Tipos,
                DetectarCamposDocumentoQuery => Result.Exito(Deteccion ?? new DeteccionCamposDocumentoDto(null, null, 0)),
                ConfirmarDocumentoPropuestoPorIaCommand when CrearPendiente is not null => EsperarCreacion<TResponse>(),
                ConfirmarDocumentoPropuestoPorIaCommand => CrearResultado ?? Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            if (valor is Task<TResponse> tarea)
                return tarea;
            return Task.FromResult((TResponse)valor!);
        }

        private Task<TResponse> EsperarCreacion<TResponse>()
        {
            ComandoCrearRecibido?.SetResult();
            return (Task<TResponse>)(object)CrearPendiente!.Task;
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class Almacenamiento : IFileStorageService
    {
        public int Guardados { get; private set; }
        public int Eliminados { get; private set; }

        /// <summary>Como el almacenamiento real sin hueco en el presupuesto de memoria del proceso.</summary>
        public bool SinMemoria { get; set; }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            if (SinMemoria)
                return Task.FromException<string>(new PresupuestoMemoriaAgotadoException());
            Guardados++;
            return Task.FromResult("archivo");
        }
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) { Eliminados++; return Task.CompletedTask; }
    }
    private sealed class Conversor : IConversorWordPdfService { public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Rasterizador : IRasterizadorPaginasPdfService
    {
        /// <summary>Sin valor, no hay miniatura: es el caso de casi todos los tests.</summary>
        public byte[]? Miniatura { get; set; }
        public int? AnchoPedido { get; private set; }
        public int PaginasEnteras { get; private set; }

        public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default)
        {
            PaginasEnteras++;
            return Result.Fallo<byte[]>(Error.Crear("Pdf.SinMiniatura", "No hay miniatura."));
        }

        public Result<byte[]> RasterizarMiniatura(byte[] contenidoPdf, int indicePagina, int anchoPixeles, CancellationToken cancellationToken = default)
        {
            AnchoPedido = anchoPixeles;
            return Miniatura is { } miniatura
                ? Result.Exito(miniatura)
                : Result.Fallo<byte[]>(Error.Crear("Pdf.SinMiniatura", "No hay miniatura."));
        }
    }

    private sealed class UsuarioActualFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
