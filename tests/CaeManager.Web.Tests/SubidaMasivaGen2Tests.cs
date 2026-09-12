using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
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
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<SubidaMasiva>), NullLogger<SubidaMasiva>.Instance);
    }

    [Fact]
    public void El_mockup_se_pinta_con_miga_zona_de_subida_y_acciones_de_documentos()
    {
        var cut = Render<SubidaMasiva>();

        cut.FindAll(".miga-subida-masiva").Should().ContainSingle();
        cut.FindAll(".zona-subida-masiva").Should().ContainSingle();
        cut.FindAll(".zona-subida-masiva h2").Should().ContainSingle().Which.TextContent.Should().Be("Arrastra aquí los documentos de trabajador");
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

    [Fact]
    public async Task Los_items_creados_por_la_IA_y_confirmados_por_una_persona_muestran_badges_distintos()
    {
        var cut = Render<SubidaMasiva>();
        var ia = AgregarItem(cut, "ia.pdf", "Procesando");
        var confirmado = AgregarItem(cut, "confirmado.pdf", "Procesando");
        ia.GetType().GetProperty("ContenidoPdf")!.SetValue(ia, "pdf"u8.ToArray());
        confirmado.GetType().GetProperty("ContenidoPdf")!.SetValue(confirmado, "pdf"u8.ToArray());
        var crear = typeof(SubidaMasiva).GetMethod("CrearDocumentoDelItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)crear.Invoke(cut.Instance, [ia, Guid.NewGuid(), Guid.NewGuid(), 1, true])!;
        await (Task)crear.Invoke(cut.Instance, [confirmado, Guid.NewGuid(), Guid.NewGuid(), 1, false])!;
        cut.Render();

        var badges = cut.FindAll(".item-subida-masiva-badges").Select(b => b.TextContent.Trim()).ToList();
        badges.Should().Contain("Creado por la IA");
        badges.Should().Contain("Creado tras confirmar");
        cut.FindAll(".item-subida-masiva").Single(item => item.TextContent.Contains("ia.pdf")).TextContent.Should().Contain("Creado por la IA");
        cut.FindAll(".item-subida-masiva").Single(item => item.TextContent.Contains("confirmado.pdf")).TextContent.Should().Contain("Creado tras confirmar");

        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task Limpiar_resueltos_conserva_el_total_y_los_creados_acumulados_del_lote()
    {
        var cut = Render<SubidaMasiva>();
        AgregarItem(cut, "ia.pdf", "Creado", creadoAutomaticamente: true);
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
    public async Task El_desenlace_automatico_tras_retirar_el_componente_emite_su_aviso()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoId = Guid.NewGuid();
        _mediador.Trabajadores = [new TrabajadorSelectorDto(trabajadorId, "Persona CAE", "12345678Z", null)];
        _mediador.Tipos = [CrearTipo(tipoId)];
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, trabajadorId, 95);
        _mediador.CrearPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.ComandoCrearRecibido = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var toastService = Services.GetRequiredService<ToastService>();
        var cut = Render<SubidaMasiva>();
        var pdf = CrearPdf();

        // En el Dispatcher del renderer: el metodo llama a StateHasChanged, y fuera de el lanza y la
        // tarea queda Faulted sin llegar al comando (medido 2026-09-12: asi se colgaba el test).
        var metodo = typeof(SubidaMasiva).GetMethod("ProcesarEntradaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var procesamiento = cut.InvokeAsync(() => (Task)metodo.Invoke(cut.Instance, [pdf, "automatico.pdf", 1, CancellationToken.None])!);
        // Con limite: sin el, si el comando nunca llega el test se cuelga en vez de fallar (medido 2026-09-12).
        // Y si el procesamiento acaba antes que el comando, el item quedo en error: se ensena por que.
        var primero = await Task.WhenAny(_mediador.ComandoCrearRecibido.Task, procesamiento).WaitAsync(TimeSpan.FromSeconds(10));
        primero.Should().BeSameAs(_mediador.ComandoCrearRecibido.Task,
            "la ruta automatica tiene que enviar CrearDocumentoCommand; excepcion: {0}", procesamiento.Exception?.GetBaseException().Message);
        await DisposeComponentsAsync();
        _mediador.CrearPendiente.SetResult(Result.Exito(Guid.NewGuid()));

        await procesamiento.WaitAsync(TimeSpan.FromSeconds(10));
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
        var trabajadorId = Guid.NewGuid();
        var tipoId = Guid.NewGuid();
        _mediador.Trabajadores = [new TrabajadorSelectorDto(trabajadorId, "Persona CAE", "12345678Z", null)];
        _mediador.Tipos = [CrearTipo(tipoId)];
        _mediador.Deteccion = new DeteccionCamposDocumentoDto(tipoId, trabajadorId, 95);
        var cut = Render<SubidaMasiva>();
        var metodo = typeof(SubidaMasiva).GetMethod("ProcesarEntradaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)metodo.Invoke(cut.Instance, [CrearPdf(), "real.pdf", 1, CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

        _mediador.Recibidas.Select(r => r.Peticion).OfType<CrearDocumentoCommand>().Should().ContainSingle(
            "control: el documento se creo de verdad por la ruta automatica");
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

        await cut.InvokeAsync(() => (Task)crear.Invoke(cut.Instance, [item, Guid.NewGuid(), Guid.NewGuid(), 1, false])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

        cut.Markup.Should().Contain("Solo lectura", "control: la pantalla se abrio de verdad con el rol Consulta");
        _almacenamiento.Guardados.Should().Be(0, "sin capacidad de crear, el PDF no se escribe en almacenamiento");
        _mediador.Recibidas.Select(r => r.Peticion).OfType<CrearDocumentoCommand>().Should().BeEmpty();
        await DisposeComponentsAsync();
    }

    private static object AgregarItem<T>(IRenderedComponent<T> cut, string nombre, string estado, bool creadoAutomaticamente = false)
        where T : IComponent
    {
        var tipoItem = typeof(SubidaMasiva).GetNestedType("ItemLote", BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(tipoItem, nonPublic: true)!;
        tipoItem.GetProperty("NombreArchivo")!.SetValue(item, nombre);
        var tipoEstado = typeof(SubidaMasiva).GetNestedType("EstadoItem", BindingFlags.NonPublic)!;
        tipoItem.GetProperty("Estado")!.SetValue(item, Enum.Parse(tipoEstado, estado));
        tipoItem.GetProperty("CreadoAutomaticamente")!.SetValue(item, creadoAutomaticamente);
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
                CrearDocumentoCommand when CrearPendiente is not null => EsperarCreacion<TResponse>(),
                CrearDocumentoCommand => Result.Exito(Guid.NewGuid()),
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
