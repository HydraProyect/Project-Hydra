using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fichas 360, Tanda 2: el formulario de documento abierto desde la lista de una ficha ofrece «Guardar y siguiente»
/// mientras la lista tenga más documentos con problema: guarda y carga el siguiente, en el orden de la lista, sin
/// cerrarse. En el último se comporta como «Guardar» y cierra.
/// </summary>
public class DrawerGestionDocumentoGuardarYSiguienteTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, DocumentoDetalleDto> Documentos { get; } = [];
        public List<object> Comandos { get; } = [];
        public HashSet<Guid> RenovacionesQueFallan { get; } = [];
        public List<TrabajadorSelectorDto> Trabajadores { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is RenovarDocumentoCommand or CrearDocumentoCommand)
                Comandos.Add(request);

            return Task.FromResult((TResponse)(object?)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)Trabajadores,
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerDocumentoPorIdQuery consulta => Documentos.GetValueOrDefault(consulta.Id),
                RenovarDocumentoCommand renovar => RenovacionesQueFallan.Contains(renovar.Id)
                    ? Result.Fallo<Guid>(Error.Crear("Documento.Prueba", "El documento no se pudo renovar."))
                    : Result.Exito(renovar.Id),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            })!);
        }

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

    private sealed class AlmacenQueNadieDebeTocar : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no sube nada.");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no abre nada.");

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no borra nada.");
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada.");
    }

    private readonly MediatorFalso _mediador = new();
    private int _guardados;
    private bool _laFichaFallaAlRecargar;

    public DrawerGestionDocumentoGuardarYSiguienteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<DrawerGestionDocumento> Renderizar()
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return Render<DrawerGestionDocumento>(p => p.Add(d => d.OnGuardado, () =>
        {
            _guardados++;
            if (_laFichaFallaAlRecargar) throw new InvalidOperationException("fallo transitorio al recargar la ficha");
        }));
    }

    private PasoSerieDocumento Documento(string propietario)
    {
        var id = Guid.NewGuid();
        _mediador.Documentos[id] = new DocumentoDetalleDto(
            Id: id,
            Ambito: AmbitoAplicacion.Trabajador,
            PropietarioNombre: propietario,
            TipoDocumentoNombre: "Reconocimiento médico",
            TipoDocumentoAplicaVencimientoAutomatico: true,
            FechaEmision: new DateOnly(2025, 1, 10),
            FechaVencimiento: new DateOnly(2026, 1, 10),
            EstadoVigencia: default,
            ArchivoUrl: null,
            Comentarios: null,
            TipoDocumentoDescripcion: null,
            TipoDocumentoCriteriosValidacion: null,
            TipoDocumentoSeSolicitaA: null,
            TipoDocumentoObservaciones: null,
            Version: Guid.NewGuid(),
            TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
            EmpresaId: null);
        return PasoSerieDocumento.Renovar(id);
    }

    private static IReadOnlyList<string> BotonesDelPie(IRenderedComponent<DrawerGestionDocumento> cut) =>
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).ToList();

    private static Task PulsarAsync(IRenderedComponent<DrawerGestionDocumento> cut, string rotulo) =>
        cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == rotulo).ClickAsync(new MouseEventArgs());

    private static string? CampoPrivado(DrawerGestionDocumento instancia, string nombre) =>
        typeof(DrawerGestionDocumento).GetField(nombre, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instancia) as string;

    private static bool EstaAbierto(IRenderedComponent<DrawerGestionDocumento> cut) => cut.FindAll(".drawer-panel").Count > 0;

    private IEnumerable<Guid> Renovados => _mediador.Comandos.OfType<RenovarDocumentoCommand>().Select(c => c.Id);

    [Fact]
    public async Task Con_mas_documentos_con_problema_guarda_y_carga_el_siguiente_sin_cerrar()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto"), Documento("Carla Vega")];

        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));

        cut.Markup.Should().Contain("Ana Ruiz");
        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar", "Guardar y siguiente");
        cut.FindAll(".drawer-pie button.boton-primario, .drawer-pie button.btn-primario, .drawer-pie button[class*=primario]")
            .Select(b => b.TextContent.Trim()).Should().Equal(["Guardar y siguiente"], "el pie tiene un solo primario");

        await PulsarAsync(cut, "Guardar y siguiente");

        Renovados.Should().Equal(serie[0].DocumentoId!.Value);
        _guardados.Should().Be(1, "la ficha recarga su lista tras cada guardado");
        EstaAbierto(cut).Should().BeTrue("«Guardar y siguiente» no cierra el formulario");
        cut.Markup.Should().Contain("Blas Soto").And.NotContain("Ana Ruiz");
    }

    [Fact]
    public async Task En_el_ultimo_se_comporta_como_guardar_y_cierra()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        await PulsarAsync(cut, "Guardar y siguiente");

        BotonesDelPie(cut).Should().Equal(["Cancelar", "Guardar"], "en el último no queda siguiente que ofrecer");

        await PulsarAsync(cut, "Guardar");

        Renovados.Should().Equal(serie[0].DocumentoId!.Value, serie[1].DocumentoId!.Value);
        _guardados.Should().Be(2);
        EstaAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Abierto_por_el_medio_sigue_por_los_de_detras_y_despues_por_los_de_delante()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto"), Documento("Carla Vega")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[1]));

        await PulsarAsync(cut, "Guardar y siguiente");
        cut.Markup.Should().Contain("Carla Vega");
        await PulsarAsync(cut, "Guardar y siguiente");
        cut.Markup.Should().Contain("Ana Ruiz", "los de delante también siguen con problema");
        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar");
        await PulsarAsync(cut, "Guardar");

        Renovados.Should().Equal(serie[1].DocumentoId!.Value, serie[2].DocumentoId!.Value, serie[0].DocumentoId!.Value);
        EstaAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Una_fila_sin_problema_sigue_por_el_primero_de_la_serie()
    {
        var cut = Renderizar();
        var vigente = Documento("Dora Gil");
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, vigente));

        await PulsarAsync(cut, "Guardar y siguiente");

        cut.Markup.Should().Contain("Ana Ruiz");
    }

    [Fact]
    public async Task Guardar_a_secas_con_siguiente_guarda_cierra_y_deja_la_serie()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));

        await PulsarAsync(cut, "Guardar");

        Renovados.Should().Equal(serie[0].DocumentoId!.Value);
        _guardados.Should().Be(1);
        EstaAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Abrir_de_uno_en_uno_no_ofrece_siguiente_ni_hereda_una_serie_anterior()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        BotonesDelPie(cut).Should().Contain("Guardar y siguiente");

        await cut.InvokeAsync(() => cut.Instance.AbrirEditarAsync(serie[1].DocumentoId!.Value));

        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar");
        await PulsarAsync(cut, "Guardar");
        EstaAbierto(cut).Should().BeFalse();

        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());

        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar");
    }

    [Fact]
    public async Task Si_el_guardado_falla_no_avanza_y_la_serie_sigue_en_pie()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        _mediador.RenovacionesQueFallan.Add(serie[0].DocumentoId!.Value);
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));

        await PulsarAsync(cut, "Guardar y siguiente");

        cut.Markup.Should().Contain("Ana Ruiz").And.Contain("El documento no se pudo renovar.");
        cut.Markup.Should().NotContain("Blas Soto");
        _guardados.Should().Be(0);
        BotonesDelPie(cut).Should().Contain("Guardar y siguiente");
    }

    [Fact]
    public async Task Un_siguiente_que_ya_no_existe_se_salta()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto"), Documento("Carla Vega")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        _mediador.Documentos.Remove(serie[1].DocumentoId!.Value);

        await PulsarAsync(cut, "Guardar y siguiente");

        cut.Markup.Should().Contain("Carla Vega");
        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar");
    }

    [Fact]
    public async Task Si_no_queda_ningun_siguiente_que_abrir_se_cierra()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        _mediador.Documentos.Remove(serie[1].DocumentoId!.Value);

        await PulsarAsync(cut, "Guardar y siguiente");

        Renovados.Should().Equal(serie[0].DocumentoId!.Value);
        EstaAbierto(cut).Should().BeFalse();
    }

    [Fact]
    public async Task El_siguiente_puede_ser_un_documento_que_falta_con_su_trabajador_y_su_tipo_ya_elegidos()
    {
        var trabajador = new TrabajadorSelectorDto(Guid.NewGuid(), "Soto Gil, Blas", null, null);
        _mediador.Trabajadores.Add(trabajador);
        var cut = Renderizar();
        var tipoDocumentoId = Guid.NewGuid();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), PasoSerieDocumento.SubirDeTrabajador(trabajador.Id, tipoDocumentoId)];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));

        await PulsarAsync(cut, "Guardar y siguiente");

        EstaAbierto(cut).Should().BeTrue();
        cut.Find(".drawer-header").TextContent.Should().Contain("Nuevo documento");
        CampoPrivado(cut.Instance, "_trabajadorId").Should().Be(trabajador.Id.ToString());
        CampoPrivado(cut.Instance, "_tipoDocumentoId").Should().Be(tipoDocumentoId.ToString());
        BotonesDelPie(cut).Should().Equal("Cancelar", "Guardar");
    }

    /// <summary>
    /// Un Trabajador fuera del catálogo con alcance (la cartera del Gestor CAE) no se puede preseleccionar: su alta no
    /// se podría guardar y, puesta delante al avanzar, cortaría el resto de la serie.
    /// </summary>
    [Fact]
    public async Task Al_avanzar_se_salta_el_que_falta_de_un_trabajador_que_no_se_puede_preseleccionar()
    {
        var cut = Renderizar();
        var fueraDeAlcance = PasoSerieDocumento.SubirDeTrabajador(Guid.NewGuid(), Guid.NewGuid());
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), fueraDeAlcance, Documento("Carla Vega")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));

        await PulsarAsync(cut, "Guardar y siguiente");

        cut.Markup.Should().Contain("Carla Vega");
        cut.FindAll("[role=alert]").Should().BeEmpty("el aviso del paso saltado no se arrastra al siguiente");
    }

    [Fact]
    public async Task Pulsado_en_su_fila_el_que_no_se_puede_preseleccionar_se_abre_con_su_aviso()
    {
        var cut = Renderizar();
        var fueraDeAlcance = PasoSerieDocumento.SubirDeTrabajador(Guid.NewGuid(), Guid.NewGuid());
        PasoSerieDocumento[] serie = [fueraDeAlcance, Documento("Carla Vega")];

        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, fueraDeAlcance));

        EstaAbierto(cut).Should().BeTrue();
        cut.Find("[role=alert]").TextContent.Should().Contain("No encontramos este trabajador");
    }

    /// <summary>
    /// El documento ya se guardó: un fallo al recargar la ficha o al abrir el siguiente no se enseña como fallo del
    /// guardado ni deja a la vista el formulario del documento guardado, cuya versión quedó atrás.
    /// </summary>
    [Fact]
    public async Task Si_falla_el_paso_al_siguiente_se_cierra_sin_dar_el_guardado_por_fallido()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        _laFichaFallaAlRecargar = true;

        await PulsarAsync(cut, "Guardar y siguiente");

        Renovados.Should().Equal(serie[0].DocumentoId!.Value);
        EstaAbierto(cut).Should().BeFalse();
        cut.Markup.Should().NotContain("No pudimos guardar los cambios");
    }

    [Fact]
    public async Task La_pregunta_de_vigencia_anterior_no_pierde_el_siguiente()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        var emision = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de emisión");
        await cut.InvokeAsync(() => emision.Instance.ValorChanged.InvokeAsync("2024-01-10"));

        await PulsarAsync(cut, "Guardar y siguiente");

        Renovados.Should().BeEmpty("una fecha de emisión anterior a la registrada pregunta antes de guardar");
        var confirmar = cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Continuar de todas formas");
        await confirmar.ClickAsync(new MouseEventArgs());

        Renovados.Should().Equal(serie[0].DocumentoId!.Value);
        EstaAbierto(cut).Should().BeTrue();
        cut.Markup.Should().Contain("Blas Soto");
    }

    [Fact]
    public async Task Tras_cargar_el_siguiente_cerrar_solo_pregunta_si_se_ha_tocado_algo()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto"), Documento("Carla Vega")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Renovado en la mutua" });
        await PulsarAsync(cut, "Guardar y siguiente");

        cut.FindComponents<CampoTextarea>().Single().Instance.Valor.Should().BeEmpty("el siguiente no hereda lo escrito en el anterior");

        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "A medias" });
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().NotBeEmpty("lo escrito en el siguiente se perdería: se pregunta antes de descartar");
        EstaAbierto(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Recien_cargado_el_siguiente_se_cierra_sin_preguntar()
    {
        var cut = Renderizar();
        PasoSerieDocumento[] serie = [Documento("Ana Ruiz"), Documento("Blas Soto")];
        await cut.InvokeAsync(() => cut.Instance.AbrirSerieAsync(serie, serie[0]));
        await PulsarAsync(cut, "Guardar y siguiente");

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        EstaAbierto(cut).Should().BeFalse();
    }
}
