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
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;

namespace CaeManager.Web.Tests;

public sealed class SubidaMasivaGen2Tests : BunitContext
{
    private readonly Mediador _mediador = new();
    private readonly Almacenamiento _almacenamiento = new();

    public SubidaMasivaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<IFileStorageService>(_almacenamiento);
        Services.AddSingleton<IConversorWordPdfService>(new Conversor());
        Services.AddSingleton<IRasterizadorPaginasPdfService>(new Rasterizador());
        Services.AddSingleton<ICurrentUserService>(new UsuarioActualFalso(Roles.GestorCae));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<SubidaMasiva>), NullLogger<SubidaMasiva>.Instance);
        Services.AddLocalization();
    }

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
        await DisposeComponentsAsync();

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
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Confirmar propuesta");
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
        var cut = Render<SubidaMasiva>();

        await ProcesarAsync(cut, "propuesta.pdf");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Confirmar…").ClickAsync(new MouseEventArgs());

        var buscador = cut.FindComponents<CampoBuscarSelect>().Single(c => c.Instance.Etiqueta == "Trabajador");
        buscador.Instance.Opciones.Select(o => o.Texto).Should().Equal("Persona CAE");
        cut.Markup.Should().Contain("Persona CAE").And.NotContain(TrabajadorSelectorFalso.DniSembrado);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Tras_confirmar_existe_el_Documento_con_las_fechas_leidas_del_archivo()
    {
        var (trabajadorId, tipoId) = PrepararPropuesta(confianza: 100, emision: new DateOnly(2026, 3, 1));
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "propuesta.pdf");
        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().BeEmpty("control previo al clic");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());

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
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());

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
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());

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

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar…").ClickAsync(new MouseEventArgs());
        var fecha = cut.FindAll("input[type=date]").First();
        await fecha.InputAsync(new ChangeEventArgs { Value = "2026-03-05" });
        await fecha.BlurAsync(new FocusEventArgs());

        cut.Find(".item-subida-masiva-propuesta").TextContent.Should().StartWith("Propuesta de la IA con tus correcciones");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Confirmar con mis correcciones");
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Sin_fecha_leida_no_se_pone_hoy_no_se_ofrece_confirmar_de_un_clic_y_no_se_crea_nada()
    {
        PrepararPropuesta(confianza: 100, emision: null);
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "sin-fecha.pdf");

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Confirmar propuesta");
        cut.Find(".item-subida-masiva").TextContent.Should().Contain("sin leer en el archivo");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar…").ClickAsync(new MouseEventArgs());
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
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar…").ClickAsync(new MouseEventArgs());
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
        var manana = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        PrepararPropuesta(confianza: 100, emision: manana);
        var cut = Render<SubidaMasiva>();
        await ProcesarAsync(cut, "futura.pdf");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());

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
        cut.Find(".resumen-subida-masiva").TextContent.Should().Contain("2 creados");
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
        var clic = cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());
        // Con limite: sin el, si el comando nunca llega el test se cuelga en vez de fallar (medido 2026-09-12).
        var primero = await Task.WhenAny(_mediador.ComandoCrearRecibido.Task, clic).WaitAsync(TimeSpan.FromSeconds(10));
        primero.Should().BeSameAs(_mediador.ComandoCrearRecibido.Task,
            "la confirmacion tiene que enviar ConfirmarDocumentoPropuestoPorIaCommand; excepcion: {0}", clic.Exception?.GetBaseException().Message);
        await DisposeComponentsAsync();
        _mediador.CrearPendiente.SetResult(Result.Exito(Guid.NewGuid()));

        await clic.WaitAsync(TimeSpan.FromSeconds(10));
        toastService.Mensajes.Should().ContainSingle(m => m.Mensaje == "Documento creado correctamente." && m.Tono == TonoToast.Exito);
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
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Confirmar propuesta").ClickAsync(new MouseEventArgs());

        _mediador.Recibidas.Select(r => r.Peticion).OfType<ConfirmarDocumentoPropuestoPorIaCommand>().Should().ContainSingle(
            "control: el documento se creo de verdad por la ruta de confirmacion");
        cut.Find(".resumen-subida-masiva").TextContent.Should().Contain("1 creados");
        // Un item en error mantiene la lista con filas tras limpiar: con la lista vacia el resumen entero no se pinta.
        AgregarItem(cut, "error.pdf", "Error");
        cut.Render();

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Limpiar creados y descartados").ClickAsync(new MouseEventArgs());

        cut.Find(".resumen-subida-masiva").TextContent.Should().Contain("1 creados", "limpiar quita filas, no historia del lote");
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
        tipoItem.GetProperty("ContenidoPdf")!.SetValue(item, CrearPdf());
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
        cut.Find(".resumen-subida-masiva").TextContent.Should().Contain("1 con error").And.Contain("1 pendiente(s)");
        await DisposeComponentsAsync();
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

    /// <summary>En el Dispatcher del renderer: el metodo llama a StateHasChanged, y fuera de el lanza (medido 2026-09-12).</summary>
    private static Task ProcesarAsync(IRenderedComponent<SubidaMasiva> cut, string nombre)
    {
        var metodo = typeof(SubidaMasiva).GetMethod("ProcesarEntradaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return cut.InvokeAsync(() => (Task)metodo.Invoke(cut.Instance, [CrearPdf(), nombre, 1, CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
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

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add((request, cancellationToken));
            object? valor = request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => Trabajadores,
                ObtenerTiposDocumentoQuery => Tipos,
                DetectarCamposDocumentoQuery => Result.Exito(Deteccion ?? new DeteccionCamposDocumentoDto(null, null, 0)),
                ConfirmarDocumentoPropuestoPorIaCommand when CrearPendiente is not null => EsperarCreacion<TResponse>(),
                ConfirmarDocumentoPropuestoPorIaCommand => Result.Exito(Guid.NewGuid()),
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
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            Guardados++;
            return Task.FromResult("archivo");
        }
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Conversor : IConversorWordPdfService { public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Rasterizador : IRasterizadorPaginasPdfService
    {
        public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default) =>
            Result.Fallo<byte[]>(Error.Crear("Pdf.SinMiniatura", "No hay miniatura."));
    }

    private sealed class UsuarioActualFalso(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
