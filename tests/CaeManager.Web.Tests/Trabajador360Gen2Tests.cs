using CaeManager.Infrastructure.Identity;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Trabajador 360 (<see cref="TrabajadorDetalle"/>, ruta
/// <c>/trabajadores/{TrabajadorId:guid}</c>) contra su mockup Gen 2
/// («Trabajador 360 TALVEG.dc.html»). Es una PÁGINA con ruta propia, no el
/// panel de 520 px del Context Workspace: el panel sigue existiendo para el
/// contexto rápido desde Gestiones o Incidencias y no es lo que se prueba
/// aquí.
///
/// <para>
/// Prueban efectos observables: qué se ve, qué consultas salen y con qué
/// parámetros, y con qué token viajan. bUnit no evalúa CSS, así que ninguno
/// afirma un estilo — la banda de severidad se comprueba por la clase que la
/// enciende, no por su color.
/// </para>
///
/// <para>
/// Las esperas se retienen con <see cref="TaskCompletionSource"/> SIN
/// continuaciones asíncronas: al liberar dentro de <c>cut.InvokeAsync</c> la
/// continuación de la página corre en línea en el dispatcher del renderer y,
/// al volver del <c>await</c>, ya ha escrito (o descartado) lo que iba a
/// escribir. Sin eso, una comprobación negativa podría pasar solo por llegar
/// antes que la respuesta.
/// </para>
/// </summary>
public class Trabajador360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js.</summary>
    public Trabajador360Gen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id del trabajador—,
    /// no por tipo: una consulta con el Id equivocado recibe lo que le
    /// corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, TrabajadorDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, List<CentroDocumentacionTrabajadorDto>> Centros { get; } = [];
        public Dictionary<Guid, List<GestionListaDto>> Gestiones { get; } = [];
        public List<TipoDocumentoListaDto> Tipos { get; } = [];
        public List<TrabajadorSelectorDto> Selector { get; } = [];
        public Dictionary<Guid, List<DocumentoListaDto>> Documentos { get; } = [];

        /// <summary>Si es true, ObtenerDocumentosQuery lanza: el resto de la página tiene que seguir en pie.</summary>
        public bool FallarDocumentos { get; set; }

        /// <summary>El comando dice cuántas bajas hizo y qué errores hubo: un éxito pelado no distingue «dada de baja» de «no se dio de baja ninguna».</summary>
        public Result<ResultadoBajaLoteDto> ResultadoDarDeBajaAsignacion { get; set; } =
            Result.Exito(new ResultadoBajaLoteDto(1, []));

        public Result ResultadoReactivar { get; set; } = Result.Exito();

        /// <summary>Si tiene valor, el servidor rechaza el envío de la reclamación / la creación de la gestión con ese motivo.</summary>
        public string? FallaEnviarReclamacionCon { get; set; }

        /// <summary>Con <see cref="FallaEnviarReclamacionCon"/>, el envío a partir del cual falla (1 = todos; 2 = el primero sale y el segundo falla).</summary>
        public int FallaEnviarReclamacionDesdeElEnvio { get; set; } = 1;
        private int EnviosReclamacion;
        public string? FallaCrearGestionCon { get; set; }

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>El token con el que llegó cada petición, en el mismo orden que <see cref="Enviadas"/>.</summary>
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (Retener?.Invoke(request) is { } retenida)
                await retenida;
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerTrabajadorPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerDocumentacionPorCentroDeTrabajadorQuery q =>
                (IReadOnlyList<CentroDocumentacionTrabajadorDto>)(Centros.GetValueOrDefault(q.TrabajadorId) ?? []),
            ObtenerGestionesQuery q => Paginar(q),
            ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)Tipos,
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)Selector,
            ObtenerAgendaContactosQuery => (IReadOnlyList<ContactoAgendaDto>)[],
            ObtenerDocumentosQuery when FallarDocumentos => throw new InvalidOperationException("Fallo simulado de la consulta de documentos."),
            ObtenerDocumentosQuery q => PaginarDocumentos(q),
            DarDeBajaAsignacionesCommand => ResultadoDarDeBajaAsignacion,
            ReactivarAsignacionCommand => ResultadoReactivar,
            EnviarReclamacionCommand when FallaEnviarReclamacionCon is { } motivo && ++EnviosReclamacion >= FallaEnviarReclamacionDesdeElEnvio => Result.Fallo<EnvioReclamacionResultado>(Error.Crear("reclamacion.rechazada", motivo)),
            EnviarReclamacionCommand c => Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, ["cliente@ejemplo.com"])),
            CrearGestionesParaTrabajadorCommand when FallaCrearGestionCon is { } motivo => Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear("gestion.rechazada", motivo)),
            CrearGestionesParaTrabajadorCommand => Result.Exito(new ResultadoCrearGestionesDto(1)),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        private ResultadoPaginado<GestionListaDto> Paginar(ObtenerGestionesQuery q)
        {
            var todas = q.TrabajadorId is { } id ? Gestiones.GetValueOrDefault(id) ?? [] : [];
            return new ResultadoPaginado<GestionListaDto>(todas, todas.Count, q.Pagina, q.TamanoPagina);
        }

        private ResultadoPaginado<DocumentoListaDto> PaginarDocumentos(ObtenerDocumentosQuery q)
        {
            var todos = q.TrabajadorId is { } id ? Documentos.GetValueOrDefault(id) ?? [] : [];
            return new ResultadoPaginado<DocumentoListaDto>(todos, todos.Count, q.Pagina, q.TamanoPagina);
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

    private static TrabajadorDetalleDto Detalle(
        Guid id, string nombre, string apellidos, string? telefono = null, string? email = null, string? alias = null) => new(
        id, EmpresaId: Guid.NewGuid(), SubcontrataId: null, EmpleadorNombre: "Refrielectric S.L.",
        nombre, apellidos, Dni: "12345678Z", FechaNacimiento: null, email, Observaciones: null,
        alias, telefono, Puesto: "Oficial de 1.ª — montaje", Version: Guid.NewGuid());

    private static DocumentoRequeridoDto Documento(string tipo, EstadoDocumento estado, bool existe = true) =>
        new(existe ? Guid.NewGuid() : null, Guid.NewGuid(), tipo, estado,
            estado switch
            {
                EstadoDocumento.Vigente => new DateOnly(2027, 5, 3),
                // Con vencimiento dentro de la ventana de reclamación (VentanaReclamacion): Vencido, Urgente, Próximo.
                EstadoDocumento.Vencido or EstadoDocumento.Urgente or EstadoDocumento.Proximo => DiaDeNegocio.Hoy().AddDays(-10),
                _ => null
            });

    private static CentroDocumentacionTrabajadorDto Centro(
        string nombre, string clienteEmpresarial, EstadoDocumento peorEstado, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, Guid.NewGuid(), clienteEmpresarial,
            new DateOnly(2026, 7, 2), peorEstado, documentos,
            // Con «Sin caducidad» el listado no lo trae pero el par cuenta: un caso aparte construye el Dto con su fracción.
            CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));

    private static GestionListaDto Gestion(Guid trabajadorId, string tipoDocumento, string centro) =>
        new(Guid.NewGuid(), trabajadorId, "Javier Salas", Guid.NewGuid(), centro,
            Guid.NewGuid(), tipoDocumento, EstadoGestion.Pendiente, new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// La página monta DrawerGestionDocumento, que los inyecta. Ningún camino
    /// de estos tests los toca: si alguno salta, la página cambió de camino.
    /// </summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Trabajador 360 no abre archivos por sí sola en estos caminos.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Trabajador 360 no convierte nada en estos caminos.");
    }

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // Después de registrar, no antes: SetRendererInfo resuelve servicios y
        // congela el proveedor de bUnit. La página solo encadena la carga de
        // gestiones en la fase interactiva (ver el comentario de CargarAsync),
        // así que sin esto RendererInfo.IsInteractive sería false y ninguna
        // gestión llegaría a pedirse.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<TrabajadorDetalle> Renderizar(Guid id) =>
        Render<TrabajadorDetalle>(p => p.Add(x => x.TrabajadorId, id));

    /// <summary>El sangrado del .razor mete saltos de línea dentro del texto; la frase es lo que se lee, no su maquetación.</summary>
    private static string SinEspaciosDeMas(string texto) =>
        string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IElement Boton(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    /// <summary>Escena por defecto: tres centros, uno con incidencias, otro completo.</summary>
    private static (CentroDocumentacionTrabajadorDto Norte, CentroDocumentacionTrabajadorDto Berriz) Escena() => (
        Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido,
            Documento("Formación PRL — 20 h", EstadoDocumento.Vencido),
            Documento("Formación específica de centro", EstadoDocumento.Faltante, existe: false),
            Documento("Certificado de aptitud", EstadoDocumento.Vigente)),
        Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.Vigente,
            Documento("Formación PRL — 20 h", EstadoDocumento.Vigente),
            Documento("Reconocimiento médico", EstadoDocumento.Vigente)));

    [Fact]
    public void La_cabecera_dice_el_cumplimiento_agregado_quien_es_y_el_desglose_del_centro_mas_urgente()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().StartWith("Javier Salas Moreno");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Trabajador");
        cabecera.TextContent.Should().Contain("12345678Z").And.Contain("Refrielectric S.L.");
        // 3 de 5 documentos exigidos entre todos los centros están al día. El anillo va a la
        // izquierda de la identidad, como en las otras páginas 360, y no entre las acciones.
        cabecera.QuerySelector(".cabecera-pagina-inicio .anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("60");
        cabecera.QuerySelector(".acciones-cabecera .anillo-cumplimiento-texto").Should().BeNull();
        // El desglose literal del centro peor parado, no solo un número.
        cabecera.TextContent.Should().Contain("2 incidencias — Centro Norte")
            .And.Contain("Formación PRL — 20 h — Vencido")
            .And.Contain("Formación específica de centro — Falta");
    }

    [Fact]
    public void Son_tres_pestanas_y_solo_Operacion_lleva_el_recuento_de_documentos_con_incidencia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        var pestanas = cut.FindAll("[role=tab]");
        pestanas.Select(t => t.QuerySelector(".pestanas-contador") is null
            ? t.TextContent.Trim()
            : t.FirstChild!.TextContent.Trim()).Should().Equal(["Operación", "Documentación", "Historial"],
            "«Contactos» pasó al lateral y «Documentación» es la del trabajador a día de hoy (decisiones del propietario 2026-09-24); «Vehículos» nunca existió en esta página");

        var contadores = cut.FindAll(".pestanas-contador");
        contadores.Should().ContainSingle("solo Operación cuenta algo");
        SinEspaciosDeMas(contadores[0].TextContent).Should().Be("2 documentos con incidencia",
            "el número sin unidad no dice qué cuenta: la glosa existe para el nombre accesible del botón");
        contadores[0].ClassList.Should().Contain("pestanas-contador-alerta");
    }

    /// <summary>
    /// D-22 (recorrido de 2026-10-01): «Sin confirmar» no es incidencia —cuenta como al día, con
    /// aviso—, igual que en el panel Documentación base (#1015). Faltante, Vencido, Urgente y Próximo
    /// siguen contando.
    /// </summary>
    [Fact]
    public void Sin_confirmar_cuenta_como_al_dia_con_aviso_y_no_como_incidencia_roja()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar),
                Documento("Formación PRL — 20 h", EstadoDocumento.Vigente),
                Documento("Formación específica de centro", EstadoDocumento.Vencido))
        ];

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-pagina");
        // El porcentaje sigue la definición única de CumplimientoDocumental: «Sin confirmar» no cuenta como al día en un
        // porcentaje (1 de 3), aunque en las incidencias sí; la única incidencia es el vencido.
        cabecera.QuerySelector(".cabecera-pagina-inicio .anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("33");
        cabecera.TextContent.Should().Contain("1 incidencia — Centro Norte")
            .And.Contain("Formación específica de centro — Vencido")
            .And.NotContain("Reconocimiento médico — Sin confirmar");
        cabecera.TextContent.Should().Contain("Vigencia sin confirmar: 1", "al día no es «sin aviso»");
        SinEspaciosDeMas(cut.Find(".pestanas-contador").TextContent).Should().Be("1 documento con incidencia");
    }

    [Fact]
    public void Un_centro_con_solo_documentos_sin_confirmar_no_tiene_incidencias_y_lo_avisa()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar))
        ];

        var cut = Renderizar(id);

        cut.FindAll(".pestanas-contador").Should().BeEmpty("sin incidencias no hay píldora de alerta");
        cut.Find(".cabecera-pagina").TextContent.Should().NotContain("incidencia —",
            "el badge rojo de incidencias del centro no aparece");
        var tarjeta = cut.Find(".trabajador360-centro");
        tarjeta.ClassList.Should().NotContain("trabajador360-centro-con-incidencia");
        tarjeta.TextContent.Should().Contain("Sin incidencias · vigencia sin confirmar");
        cut.Find(".anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("0",
            "«Sin confirmar» no es al día en un porcentaje (decisión del propietario, 2026-10-03): solo lo es en incidencias y paneles");
    }

    /// <summary>
    /// Decisión del propietario, 2026-10-03: Próximo y Urgente siguen siendo válidos hoy y cuentan como al día en el
    /// porcentaje, aunque sigan siendo incidencias. El % es la suma de las fracciones de CumplimientoDocumental de cada Centro.
    /// </summary>
    [Fact]
    public void Proximo_y_urgente_cuentan_como_al_dia_en_el_porcentaje_aunque_sigan_siendo_incidencia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Urgente,
                Documento("Reconocimiento médico", EstadoDocumento.Proximo),
                Documento("Formación PRL — 20 h", EstadoDocumento.Urgente),
                Documento("Formación específica de centro", EstadoDocumento.Vencido))
        ];

        var cut = Renderizar(id);

        cut.Find(".cabecera-pagina .cabecera-pagina-inicio .anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("67",
            "dos de tres exigidos están al día (Próximo y Urgente); solo el vencido no");
        SinEspaciosDeMas(cut.Find(".pestanas-contador").TextContent).Should().Be("3 documentos con incidencia",
            "Próximo y Urgente al día en el porcentaje no deja de ser incidencia: son dos preguntas distintas");
    }

    /// <summary>
    /// «No caduca» confirmado es al día Y requerido. El listado del Centro no lo trae (no es un requisito pendiente), así
    /// que el porcentaje sale de la fracción que calcula la consulta, no de contar filas.
    /// </summary>
    [Fact]
    public void Un_documento_que_no_caduca_cuenta_como_al_dia_y_como_requerido_aunque_el_listado_no_lo_traiga()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        // Dos exigidos: uno «No caduca» (omitido del listado) y uno vencido. La fracción de la consulta es 1 de 2.
        mediador.Centros[id] =
        [
            new CentroDocumentacionTrabajadorDto(
                Guid.NewGuid(), Guid.NewGuid(), "Centro Norte", Guid.NewGuid(), "Refrielectric S.A.", new DateOnly(2026, 7, 2),
                EstadoDocumento.Vencido, [Documento("Formación específica de centro", EstadoDocumento.Vencido)],
                new FraccionCumplimiento(1, 2))
        ];

        var cut = Renderizar(id);

        cut.Find(".cabecera-pagina .cabecera-pagina-inicio .anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("50");
        cut.Find(".trabajador360-centro-resumen").TextContent.Should().Contain("1 de 2 exigidos correctos");
    }

    /// <summary>
    /// D-06/D-22: el botón primario no puede quedar deshabilitado y sin salida en un trabajador
    /// recién creado; abre el drawer de Nuevo documento con el trabajador elegido y el tipo por elegir allí.
    /// </summary>
    [Fact]
    public async Task Subir_documento_en_un_trabajador_sin_documentos_esta_habilitado_y_abre_Nuevo_documento_con_el_trabajador_elegido()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Selector.Add(new TrabajadorSelectorDto(id, "Salas Moreno, Javier", null, null));

        var cut = Renderizar(id);

        var boton = cut.Find(".cabecera-pagina .trabajador360-acciones > button");
        boton.TextContent.Trim().Should().Be("Subir documento");
        boton.HasAttribute("disabled").Should().BeFalse("sin documento al que apuntar abre el alta, no queda muerto");

        await boton.ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Nuevo documento");
        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle();
    }

    [Fact]
    public async Task La_pestana_Documentacion_vacia_ofrece_Subir_documento()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];

        var cut = Renderizar(id);
        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Documentación")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("todavía no tiene documentos");
        cut.Find(".texto-vacio-seccion + button, .texto-vacio-seccion ~ button").TextContent.Trim().Should().Be("Subir documento");
    }

    [Fact]
    public void Sin_ninguna_incidencia_la_pestana_de_Operacion_no_lleva_pildora()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        cut.FindAll(".pestanas-contador").Should().BeEmpty("un cero no se pinta como si fuera un dato que atender");
        cut.FindAll("[role=tab]").Should().HaveCount(3);
    }

    [Fact]
    public void Cada_centro_es_una_tarjeta_que_dice_su_cliente_empresarial_su_fraccion_y_lleva_al_Centro_360()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        cut.Markup.Should().Contain("La exigencia documental es por centro",
            "es lo primero que explica por qué el mismo trabajador sale completo en una fila y bloqueado en la de al lado");

        var tarjetas = cut.FindAll(".trabajador360-centro");
        tarjetas.Should().HaveCount(2);

        tarjetas[0].QuerySelector(".trabajador360-centro-nombre")!.TextContent.Trim().Should().Be("Centro Norte");
        tarjetas[0].QuerySelector(".trabajador360-centro-resumen")!.TextContent.Should()
            .Contain("Refrielectric S.A.").And.Contain("1 de 3 exigidos correctos");
        tarjetas[0].QuerySelector(".badge")!.TextContent.Trim().Should().Be("2 incidencias");
        tarjetas[0].ClassList.Should().Contain("trabajador360-centro-con-incidencia");
        tarjetas[0].QuerySelector(".trabajador360-centro-360")!.GetAttribute("href")
            .Should().Be($"/centros/{norte.CentroId}");
        tarjetas[0].QuerySelector(".trabajador360-centro-360")!.GetAttribute("aria-label")
            .Should().Be("Abrir el Centro 360 de Centro Norte");

        tarjetas[1].QuerySelector(".badge")!.TextContent.Trim().Should().Be("Completo");
        tarjetas[1].ClassList.Should().NotContain("trabajador360-centro-con-incidencia");
        tarjetas[1].QuerySelector(".trabajador360-centro-resumen")!.TextContent.Should()
            .Contain("2 de 2 exigidos correctos");
    }

    [Fact]
    public async Task Desplegar_un_centro_ensena_sus_documentos_exigidos_con_el_estado_y_la_accion_que_toca()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);
        cut.FindAll(".trabajador360-centro-detalle").Should().BeEmpty("los centros nacen plegados");

        var disparador = cut.FindAll(".trabajador360-centro-disparador")[0];
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        await disparador.ClickAsync(new MouseEventArgs());

        cut.FindAll(".trabajador360-centro-disparador")[0].GetAttribute("aria-expanded").Should().Be("true");
        var detalle = cut.Find(".trabajador360-centro-detalle");
        detalle.TextContent.Should().Contain("Formación PRL — 20 h").And.Contain("Certificado de aptitud");
        // Sin documento que renovar la acción es «Subir»; con uno vencido, «Renovar»; vigente, «Ver».
        detalle.QuerySelectorAll(".columna-accion button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Renovar", "Subir", "Ver"]);
        detalle.TextContent.Should().NotContain("Nave Berriz", "solo se despliega el centro sobre el que se pulsó");
    }

    private static DocumentoListaDto DocumentoDelTrabajador(
        string tipo, EstadoDocumento estado, DateOnly emision, DateOnly? vencimiento, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Javier Salas Moreno", tipo,
            emision, vencimiento, estado, ArchivoUrl: null, Acreditaciones: []);

    private static TipoDocumentoListaDto Tipo(Guid id, string nombre, RequisitoDocumental requerido) =>
        new(id, nombre, VigenciaMeses: 12, AplicaVencimientoAutomatico: true, Orden: 0,
            AmbitoAplicacion.Trabajador, requerido, Naturaleza: default, Descripcion: null,
            CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null,
            LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false,
            PerfilDocumentoOficial.Ninguno, Aliases: []);

    private static async Task AbrirPestanaAsync(IRenderedComponent<TrabajadorDetalle> cut, string nombre)
    {
        var pestana = cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim().StartsWith(nombre, StringComparison.Ordinal));
        await pestana.ClickAsync(new MouseEventArgs());
    }

    private static IReadOnlyList<string[]> Filas(IRenderedComponent<TrabajadorDetalle> cut, string tabla) =>
        cut.Find($"[role=table][aria-label='{tabla}']").QuerySelectorAll(".fila-documento-requerido")
            .Select(f => f.QuerySelectorAll("[role=cell]").Select(c => SinEspaciosDeMas(c.TextContent)).ToArray())
            .ToList();

    [Fact]
    public void Operacion_abre_con_la_franja_de_lo_que_esta_por_vencer_y_solo_con_eso()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1)),
            DocumentoDelTrabajador("Entrega de EPI", EstadoDocumento.Proximo, new(2025, 10, 20), new(2026, 10, 20)),
            DocumentoDelTrabajador("Formación PRL — 20 h", EstadoDocumento.Vigente, new(2025, 3, 1), new(2027, 3, 1)),
            DocumentoDelTrabajador("Formación PRL — 60 h", EstadoDocumento.Vencido, new(2024, 3, 1), new(2026, 3, 1)),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
        ];

        var cut = Renderizar(id);

        var franja = cut.Find("ul.trabajador360-por-vencer");
        franja.GetAttribute("aria-label").Should().Be("Por vencer");
        franja.QuerySelectorAll(".trabajador360-por-vencer-fila")
            .Select(f => SinEspaciosDeMas(f.TextContent))
            .Should().Equal(
                ["Reconocimiento médico Urgente Vence: 01/10/2026", "Entrega de EPI Próximo Vence: 20/10/2026"],
                "la franja usa el umbral ámbar del sistema (Q3): Próximo y Urgente, lo más cercano primero; lo vencido ya es incidencia de Centro y lo vigente no apremia");
    }

    [Fact]
    public void Sin_nada_por_vencer_no_se_pinta_la_franja()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];
        mediador.Documentos[id] = [DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null)];

        var cut = Renderizar(id);

        cut.FindAll("ul.trabajador360-por-vencer").Should().BeEmpty("una lista vacía con su rótulo diría que hay algo que mirar");
    }

    [Fact]
    public async Task La_documentacion_del_trabajador_dice_de_cada_documento_su_fecha_y_cuando_caduca()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Formación PRL — 20 h", EstadoDocumento.Vigente, new(2025, 3, 1), new(2027, 3, 1)),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Vencido, new(2024, 9, 1), new(2025, 9, 1)),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        Filas(cut, "Documentos del trabajador").Should().BeEquivalentTo(
            new[]
            {
                new[] { "Reconocimiento médico", "Vencido", "01/09/2024", "01/09/2025" },
                new[] { "Formación PRL — 20 h", "Vigente", "01/03/2025", "01/03/2027" },
                new[] { "DNI", "Sin caducidad", "01/01/2020", "Sin caducidad" },
            },
            o => o.WithStrictOrdering(),
            "lo más grave arriba; lo que no caduca lo dice (Q4) en vez de dejar la celda en blanco");

        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new { TrabajadorId = (Guid?)id, Ambito = (AmbitoAplicacion?)AmbitoAplicacion.Trabajador, OrdenarPor = "Estado", Descendente = false },
                "la misma consulta, alcance y RLS que la lista de Documentos, acotada a este trabajador; con lo más grave primero, " +
                "si el tope de la página corta la lista, lo que se pierde es lo vigente y no lo que alimenta «Por vencer»");
    }

    /// <summary>
    /// Mismo criterio en el tercer nivel de la pestaña Operación (Centro desplegado): la celda de vigencia de un
    /// documento exigido «Sin confirmar» dice «Sin confirmar» con aviso, y un hueco sigue en «—», sin aviso.
    /// </summary>
    [Fact]
    public async Task La_vigencia_de_un_exigido_en_el_centro_desplegado_dice_Sin_confirmar_con_aviso_y_un_hueco_sigue_en_raya()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar),
                Documento("Autorización de acceso", EstadoDocumento.Faltante, existe: false))
        ];

        var cut = Renderizar(id);
        await cut.FindAll(".trabajador360-centro-disparador")[0].ClickAsync(new MouseEventArgs());

        var filas = cut.Find(".trabajador360-centro-detalle").QuerySelectorAll(".fila-documento-requerido")
            .ToDictionary(f => SinEspaciosDeMas(f.QuerySelector(".celda-documento-nombre")!.TextContent),
                f => f.QuerySelector(".celda-documento-vigencia")!);

        SinEspaciosDeMas(filas["Reconocimiento médico"].TextContent).Should().Be("Sin confirmar");
        filas["Reconocimiento médico"].ClassList.Should().Contain("celda-documento-vigencia-sin-confirmar");
        SinEspaciosDeMas(filas["Autorización de acceso"].TextContent).Should().Be("—");
        filas["Autorización de acceso"].ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    /// <summary>
    /// «Sin confirmar» no tiene fecha de vencimiento y esa celda no puede quedar en «—»: «—» oculta un estado
    /// conocido (decisión del propietario, 2026-10-03). Dice «Sin confirmar» con el mismo aviso ámbar que Centro 360;
    /// un estado sin fecha que no es ni «Sin caducidad» ni «Sin confirmar» (un hueco) sigue en «—», sin aviso.
    /// </summary>
    [Fact]
    public async Task La_celda_Vence_de_un_documento_sin_confirmar_dice_Sin_confirmar_con_aviso_y_un_hueco_sigue_en_raya()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.SinConfirmar, new(2025, 5, 1), null),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
            DocumentoDelTrabajador("Formación de centro", EstadoDocumento.Faltante, new(2025, 6, 1), null),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        CeldaVence(cut, "Documentos del trabajador", "Reconocimiento médico").TextContent.Trim().Should().Be("Sin confirmar");
        CeldaVence(cut, "Documentos del trabajador", "Reconocimiento médico").ClassList
            .Should().Contain("celda-documento-vigencia-sin-confirmar", "lleva el mismo aviso ámbar que Centro 360");
        CeldaVence(cut, "Documentos del trabajador", "DNI").TextContent.Trim().Should().Be("Sin caducidad");
        CeldaVence(cut, "Documentos del trabajador", "DNI").ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
        CeldaVence(cut, "Documentos del trabajador", "Formación de centro").TextContent.Trim()
            .Should().Be("—", "un estado sin fecha que no es «Sin caducidad» ni «Sin confirmar» no tiene vigencia que rotular");
        CeldaVence(cut, "Documentos del trabajador", "Formación de centro").ClassList
            .Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public async Task La_celda_Renovar_antes_de_un_exigido_sin_confirmar_dice_Sin_confirmar_con_aviso_y_un_hueco_sigue_en_raya()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");

        var tipoGeneral = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoGeneral, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var tipoSinConfirmar = Guid.NewGuid();
        var tipoHueco = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoSinConfirmar, "Reconocimiento médico de centro", RequisitoDocumental.No));
        mediador.Tipos.Add(Tipo(tipoHueco, "Autorización de acceso", RequisitoDocumental.No));

        var documentoSinConfirmar = Guid.NewGuid();
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                new DocumentoRequeridoDto(documentoSinConfirmar, tipoSinConfirmar, "Reconocimiento médico de centro", EstadoDocumento.SinConfirmar, null),
                new DocumentoRequeridoDto(null, tipoHueco, "Autorización de acceso", EstadoDocumento.Faltante, null))
        ];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico de centro", EstadoDocumento.SinConfirmar, new(2025, 5, 1), null, documentoSinConfirmar),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        var tabla = "Exigidos solo en algunos Centros de trabajo";
        CeldaVence(cut, tabla, "Reconocimiento médico de centro").TextContent.Trim().Should().Be("Sin confirmar");
        CeldaVence(cut, tabla, "Reconocimiento médico de centro").ClassList.Should().Contain("celda-documento-vigencia-sin-confirmar");
        CeldaVence(cut, tabla, "Autorización de acceso").TextContent.Trim().Should().Be("—");
        CeldaVence(cut, tabla, "Autorización de acceso").ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    /// <summary>Última celda (la fecha de vencimiento o de renovación) de la fila de un documento, en la tabla indicada.</summary>
    private static AngleSharp.Dom.IElement CeldaVence(IRenderedComponent<TrabajadorDetalle> cut, string tabla, string documento) =>
        cut.Find($"[role=table][aria-label='{tabla}']").QuerySelectorAll(".fila-documento-requerido")
            .Single(f => SinEspaciosDeMas(f.QuerySelector("[role=cell]")!.TextContent) == documento)
            .QuerySelectorAll("[role=cell]").Last();

    [Fact]
    public async Task Los_exigidos_solo_en_algunos_Centros_dicen_en_que_Centro_de_que_Cliente_empresarial_y_cuando_renovar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");

        var tipoGeneral = Guid.NewGuid();
        var tipoDeCentro = Guid.NewGuid();
        var documentoDeCentro = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoGeneral, "Formación PRL — 20 h", RequisitoDocumental.Si));
        mediador.Tipos.Add(Tipo(tipoDeCentro, "Formación específica de centro", RequisitoDocumental.No));

        var norte = Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Proximo,
            new DocumentoRequeridoDto(Guid.NewGuid(), tipoGeneral, "Formación PRL — 20 h", EstadoDocumento.Vigente, new(2027, 3, 1)),
            new DocumentoRequeridoDto(documentoDeCentro, tipoDeCentro, "Formación específica de centro", EstadoDocumento.Proximo, new(2026, 10, 15)));
        mediador.Centros[id] = [norte];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Formación específica de centro", EstadoDocumento.Proximo, new(2025, 10, 15), new(2026, 10, 15), documentoDeCentro),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        Filas(cut, "Exigidos solo en algunos Centros de trabajo").Should().BeEquivalentTo(
            new[]
            {
                new[] { "Formación específica de centro", "Centro Norte del Cliente empresarial Refrielectric S.A.", "Próximo", "15/10/2025", "15/10/2026" },
            },
            o => o.WithStrictOrdering(),
            "el tipo exigido a todo trabajador (Requerido = Sí) no es propio de ningún Centro (Q2); la renovación es la caducidad del documento (Q1)");

        var centro = cut.Find(".trabajador360-exigencia-centro a");
        centro.GetAttribute("href").Should().Be($"/centros/{norte.CentroId}");
        cut.Find(".trabajador360-exigencias").TextContent.Should().NotContain("exigido por",
            "lo exige el Centro de trabajo; el Cliente empresarial solo lo sitúa");
    }

    [Fact]
    public async Task Si_la_documentacion_falla_el_resto_de_la_pagina_sigue_y_se_puede_reintentar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso { FallarDocumentos = true });
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];

        var cut = Renderizar(id);
        cut.FindAll(".trabajador360-centro").Should().ContainSingle("Operación no depende de la consulta de documentos");
        cut.FindAll("ul.trabajador360-por-vencer").Should().BeEmpty();

        await AbrirPestanaAsync(cut, "Documentación");
        cut.FindAll("[role=table][aria-label='Documentos del trabajador']").Should().BeEmpty(
            "un fallo no se pinta como «sin documentos»");

        mediador.FallarDocumentos = false;
        mediador.Documentos[id] = [DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null)];
        await cut.InvokeAsync(() => Boton(cut, "Reintentar").Click());

        cut.WaitForAssertion(() => Filas(cut, "Documentos del trabajador").Should().ContainSingle());
    }

    [Fact]
    public void Se_conservan_las_asignaciones_con_su_baja_y_las_gestiones_pendientes()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];

        var cut = Renderizar(id);

        var asignaciones = cut.Find("[aria-label='Asignaciones activas']");
        asignaciones.TextContent.Should().Contain("Cliente empresarial",
            "la contraparte de una Relación Empresarial no es «Cliente» a secas")
            .And.Contain("Centro Norte").And.Contain("02/07/2026");
        asignaciones.QuerySelectorAll(".columna-accion button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Dar de baja", "Dar de baja"]);

        var gestiones = cut.Find("[aria-label='Gestiones pendientes']");
        gestiones.TextContent.Should().Contain("Reconocimiento médico").And.Contain("Completar");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { TrabajadorId = id, Estado = EstadoGestion.Pendiente });
    }

    [Fact]
    public async Task Consulta_ve_documentos_asignaciones_y_gestiones_sin_que_se_le_ofrezca_subir_dar_de_baja_ni_completar()
    {
        // Subir/Renovar/Ver abre DrawerGestionDocumento (solo crea o renueva); dar de baja y
        // completar son DarDeBajaAsignacionesCommand y CompletarGestionCommand. Los tres acaban
        // en ICommand que AutorizacionEscrituraBehavior deniega a Consulta.
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];

        var cut = Renderizar(id);
        await cut.FindAll(".trabajador360-centro-disparador")[0].ClickAsync(new MouseEventArgs());

        cut.Find(".trabajador360-centro-detalle").TextContent.Should().Contain("Formación PRL — 20 h",
            "los documentos exigidos y su estado son lectura: se ven");
        cut.Find("[aria-label='Asignaciones activas']").TextContent.Should().Contain("Centro Norte");
        cut.Find("[aria-label='Gestiones pendientes']").TextContent.Should().Contain("Reconocimiento médico");
        cut.FindAll(".columna-accion button").Should().BeEmpty(
            "Subir, Renovar, Ver, Dar de baja y Completar son las únicas acciones de fila y todas escriben");
        cut.FindAll(".trabajador360-centro-detalle button.enlace-nombre-fila").Should().BeEmpty(
            "el nombre del documento abre el mismo drawer de gestión: a Consulta se le pinta como texto");
    }

    /// <summary>
    /// P41b (decisión del propietario, 2026-09-19): «Dar de baja» del trabajador
    /// solo vive en la lista. El menú de la ficha ya no la ofrece; las bajas de
    /// las asignaciones vigentes, que son otra cosa, se quedan en su tabla.
    /// </summary>
    [Fact]
    public async Task La_ficha_no_ofrece_dar_de_baja_al_trabajador()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        cut.FindAll(".menu-acciones-disparador").Should().ContainSingle(
            "la página tiene un único menú de acciones, el de la cabecera (lo da por hecho DeepLinksTests)");
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim())
            .Should().Equal(["Editar", "Reclamar faltantes", "Crear gestión"]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public void El_lateral_ensena_el_telefono_y_el_correo_del_trabajador_y_no_inventa_los_que_no_existen()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno",
            telefono: "644 902 118", email: "javier.salas@refrielectric.example");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        // Sin pulsar ninguna pestaña: lo que era «Contactos» vive en el lateral.
        var rejilla = cut.Find(".cuerpo-con-lateral-lateral .trabajador360-contactos");
        rejilla.TextContent.Should().Contain("644 902 118").And.Contain("javier.salas@refrielectric.example");
        rejilla.QuerySelectorAll("dt").Select(d => d.TextContent.Trim())
            .Should().Equal(["Teléfono", "Correo electrónico"],
                "ni la dirección ni el contacto de emergencia del mockup existen en TrabajadorDetalleDto");
        rejilla.QuerySelectorAll(".boton-copiar").Should().HaveCount(2, "los dos son datos para pegar en otro sitio");
    }

    [Fact]
    public void Sin_telefono_ni_correo_no_se_pinta_la_rejilla_de_contacto_vacia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        cut.FindAll(".trabajador360-contactos").Should().BeEmpty();
        cut.Find(".cuerpo-con-lateral-lateral").TextContent.Should().NotContain("Datos del trabajador",
            "sin teléfono ni correo no se pinta la tarjeta, ni siquiera su título");
    }

    [Fact]
    public void El_lateral_lleva_la_agenda_del_empleador_que_pide_por_su_Id_y_su_tipo()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        var detalle = Detalle(id, "Javier", "Salas Moreno");
        mediador.Detalles[id] = detalle;
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        cut.Find(".cuerpo-con-lateral-lateral").TextContent.Should().Contain("Agenda del empleador");
        mediador.Enviadas.OfType<ObtenerAgendaContactosQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Empresa, detalle.EmpresaId!.Value),
                "la agenda es la del empleador del trabajador, sin pulsar ninguna pestaña");
    }

    [Fact]
    public async Task Abrir_otro_trabajador_con_la_carga_del_primero_en_vuelo_no_pinta_el_primero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerTrabajadorPorIdQuery q && q.Id == a ? respuestaDeA.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];

        var cut = Renderizar(a);
        cut.FindAll(".cabecera-pagina").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.TrabajadorId, b));
        cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        await cut.InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta",
            "la respuesta de A llegó tarde y ya no es la vigente");
        cut.Markup.Should().NotContain("Centro Norte", "los centros de A no se piden: su cadena se corta al volver");
        mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>()
            .Select(q => q.TrabajadorId).Should().Equal([b]);
    }

    [Fact]
    public async Task Las_gestiones_pedidas_para_el_primero_no_se_pintan_en_el_segundo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var gestionesDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerGestionesQuery q && q.TrabajadorId == a ? gestionesDeA.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];
        mediador.Gestiones[a] = [Gestion(a, "Certificado de aptitud de A", "Centro Norte")];

        var cut = Renderizar(a);
        cut.Render(p => p.Add(x => x.TrabajadorId, b));
        await cut.InvokeAsync(() => gestionesDeA.SetResult());

        cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        // Un repintado que NO recarga nada (desplegar un centro es estado
        // local y síncrono). Sin él, este test solo observaría que la
        // respuesta superada no dispara StateHasChanged, no que además se
        // descarte: dos guardas distintas, y la del finally tapaba a la del
        // dato. Medido — con la guarda del dato debilitada, el test seguía en
        // verde hasta añadir este repintado.
        await cut.Find(".trabajador360-centro-disparador").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("Certificado de aptitud de A",
            "son las gestiones de otro trabajador");
        cut.FindAll("[aria-label='Gestiones pendientes']").Should().BeEmpty();
    }

    [Fact]
    public void Al_pasar_a_otro_trabajador_la_franja_Por_vencer_no_ensena_los_documentos_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var documentosDeB = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerDocumentosQuery q && q.TrabajadorId == b ? documentosDeB.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];
        mediador.Documentos[a] = [DocumentoDelTrabajador("Reconocimiento médico de A", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1))];

        var cut = Renderizar(a);
        cut.Find("ul.trabajador360-por-vencer").TextContent.Should().Contain("Reconocimiento médico de A");

        // Los documentos de B siguen en vuelo; el detalle y los centros de B ya han llegado.
        cut.Render(p => p.Add(x => x.TrabajadorId, b));

        cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");
        cut.Markup.Should().NotContain("Reconocimiento médico de A", "son los documentos de otro trabajador");
    }

    [Fact]
    public async Task Las_consultas_llevan_el_token_de_la_pagina_y_retirarla_las_cancela()
    {
        var id = Guid.NewGuid();
        var enVuelo = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerDocumentacionPorCentroDeTrabajadorQuery ? enVuelo.Task : null
        });
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];

        Renderizar(id);

        var consultas = mediador.Tokens
            .Where(t => t.Peticion.GetType().Name.EndsWith("Query", StringComparison.Ordinal)).ToList();
        consultas.Should().NotBeEmpty("sin consultas emitidas este test no observaría nada");
        consultas.Should().OnlyContain(t => t.Token.CanBeCanceled && !t.Token.IsCancellationRequested,
            "cada consulta tiene que llevar el token del ciclo de la página, no CancellationToken.None");

        await DisposeComponentsAsync();

        consultas.Should().OnlyContain(t => t.Token.IsCancellationRequested,
            "retirada la página no queda ninguna consulta suya trabajando para nadie");

        enVuelo.SetResult();
    }

    [Fact]
    public void Si_el_trabajador_no_existe_se_dice_y_se_ofrece_reintentar()
    {
        var id = Guid.NewGuid();
        Services.ConEnlaceProfundoOtraEmpresa();
        Registrar(new MediatorFalso());

        var cut = Renderizar(id);

        cut.Markup.Should().Contain("No pudimos cargar este trabajador").And.Contain("Reintentar");
        cut.FindAll(".cabecera-pagina").Should().BeEmpty();
    }

    // --------------- Hallazgos de la revisión de Codex sobre esta pantalla

    private ToastService Avisos => Services.GetRequiredService<ToastService>();

    private const string BotonBajaAsignacion = "[aria-label='Asignaciones activas'] .columna-accion button";

    private static IElement BotonDelDialogo(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == texto);

    private static async Task ConfirmarBajaAsignacionAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
    }

    // --------------- FS-13 (auditoría UX de flujos sin salida, 2026-09-24)

    /// <summary>
    /// La baja salía al primer clic y sin vuelta atrás. Ahora se pregunta
    /// antes, nombrando a quién y de qué centro, y cancelar no envía nada.
    /// </summary>
    [Fact]
    public async Task Dar_de_baja_una_asignacion_pide_confirmacion_y_cancelar_no_envia_nada()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);

        var cut = Renderizar(id);
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty("sin confirmar no se da de baja");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Javier Salas Moreno").And.Contain("Centro Norte")
            .And.Contain("Podrás deshacerlo desde el aviso");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmar_da_de_baja_esa_asignacion_y_cierra_el_dialogo()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var norte = mediador.Centros[id][0];

        var cut = Renderizar(id);
        await ConfirmarBajaAsignacionAsync(cut);

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle()
            .Which.Ids.Should().Equal([norte.AsignacionId]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// «Deshacer» reabre la MISMA asignación (no crea otra) y recarga la ficha.
    /// </summary>
    [Fact]
    public async Task Deshacer_del_aviso_reabre_la_asignacion_dada_de_baja()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var norte = mediador.Centros[id][0];

        var cut = Renderizar(id);
        await ConfirmarBajaAsignacionAsync(cut);

        var aviso = Avisos.Mensajes.Should().ContainSingle().Subject;
        aviso.TextoAccion.Should().Be("Deshacer");
        var cargasAntes = mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>().Count();

        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<ReactivarAsignacionCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(norte.AsignacionId);
        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Contain(
            ("Baja deshecha: la asignación vuelve a estar activa.", TonoToast.Exito));
        mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>().Count().Should().BeGreaterThan(cargasAntes,
            "la asignación reabierta tiene que volver a verse en la tabla");
    }

    /// <summary>Si el comando rechaza la reapertura, se dice por qué y no se anuncia como hecha.</summary>
    [Fact]
    public async Task Deshacer_rechazado_muestra_el_motivo_y_no_se_anuncia_como_hecho()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.ResultadoReactivar = Result.Fallo(Error.Crear(
            "Asignacion.SolapaConOtra", "Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir."));

        var cut = Renderizar(id);
        await ConfirmarBajaAsignacionAsync(cut);
        await cut.InvokeAsync(Avisos.Mensajes.Single().OnAccion!);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Contain(
            ("Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir.", TonoToast.Error));
        Avisos.Mensajes.Should().NotContain(m => m.Mensaje.StartsWith("Baja deshecha"));
    }

    private static async Task AbrirModalCrearGestionAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Crear gestión")
            .ClickAsync(new MouseEventArgs());
    }

    private MediatorFalso ConTrabajador(Guid id, string nombre = "Javier", string apellidos = "Salas Moreno")
    {
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, nombre, apellidos);
        mediador.Centros[id] = [norte, berriz];
        return mediador;
    }

    /// <summary>
    /// Las modales se pintan fuera del bloque de la página y su estado no se
    /// tocaba al cambiar de ruta: abrir una modal para un trabajador y abrir la
    /// ficha de otro la dejaba abierta sobre el segundo. Lo preparado para un
    /// trabajador no puede ejecutarse sobre otro.
    /// </summary>
    [Fact]
    public async Task Una_modal_abierta_para_un_trabajador_no_sobrevive_al_abrir_la_ficha_de_otro()
    {
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var mediador = ConTrabajador(primero);
        mediador.Detalles[segundo] = Detalle(segundo, "Eider", "Lasa Arrieta");
        mediador.Centros[segundo] = mediador.Centros[primero];

        var cut = Renderizar(primero);
        await AbrirModalCrearGestionAsync(cut);
        cut.FindAll(".modal-contenido").Should().ContainSingle("la modal está abierta para el primero");

        cut.Render(p => p.Add(x => x.TrabajadorId, segundo));
        cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        cut.FindAll(".modal-contenido").Should().BeEmpty(
            "la modal preguntaba por el primero y en pantalla ya está el segundo");
    }

    /// <summary>
    /// El comando devuelve éxito aunque no haya dado de baja ninguna
    /// asignación (ya cerrada, o fuera de alcance). Decir «Asignación dada de
    /// baja» entonces afirma un efecto que no ocurrió.
    /// </summary>
    [Fact]
    public async Task Una_baja_de_asignacion_que_no_dio_ninguna_no_se_anuncia_como_hecha()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.ResultadoDarDeBajaAsignacion = Result.Exito(
            new ResultadoBajaLoteDto(0, ["La asignación ya estaba cerrada."]));

        var cut = Renderizar(id);
        await ConfirmarBajaAsignacionAsync(cut);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Equal(
            [("La asignación ya estaba cerrada.", TonoToast.Error)]);
    }

    /// <summary>Control positivo del anterior: con una baja real, el aviso sí dice que se hizo.</summary>
    [Fact]
    public async Task Una_baja_de_asignacion_que_si_ocurrio_se_anuncia_como_hecha()
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);

        var cut = Renderizar(id);
        await ConfirmarBajaAsignacionAsync(cut);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Equal(
            [("Asignación dada de baja.", TonoToast.Exito)]);
    }

    /// <summary>
    /// El botón de la fila nunca se deshabilita, así que un segundo evento
    /// llegaba mientras el primero esperaba al servidor y mandaba otra baja.
    /// El clic NO se espera antes de soltar la puerta: su manejador está
    /// detenido en la respuesta retenida y esperarlo colgaría el caso.
    /// </summary>
    [Fact]
    public async Task Dos_eventos_seguidos_sobre_la_misma_asignacion_no_mandan_dos_bajas()
    {
        var id = Guid.NewGuid();
        var puerta = new TaskCompletionSource();
        var mediador = ConTrabajador(id);

        var cut = Renderizar(id);
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());
        // Se retiene después de abrir el diálogo: si la fila mandara la baja sin
        // confirmar, el caso tiene que fallar al buscar el diálogo, no colgarse.
        mediador.Retener = p => p is DarDeBajaAsignacionesCommand ? puerta.Task : null;
        var confirmar = BotonDelDialogo(cut, "Dar de baja");
        var primera = confirmar.ClickAsync(new MouseEventArgs());
        var segunda = confirmar.ClickAsync(new MouseEventArgs());

        mediador.Retener = null;
        await cut.InvokeAsync(puerta.SetResult);
        await primera;
        await segunda;

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle(
            "la guarda de reentrada impide que el segundo evento mande otra baja");
    }

    // --- P1-E2b: aviso de cambios sin guardar -------------------------------------------------

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    [Fact]
    public async Task Aviso_crear_gestion_con_un_tipo_elegido_pregunta_al_salir()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);

        await cut.Find(".modal-contenido select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_crear_gestion_recien_abierta_no_pregunta_al_salir()
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        cut.FindAll(".modal-contenido").Should().ContainSingle("el test necesita la modal abierta");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin tipo elegido no hay nada que perder");
    }

    /// <summary>
    /// Alinea la ficha con el envío (EnviarReclamacion rechaza lo que no tiene FechaVencimiento): un
    /// «Sin confirmar» sin fecha no se ofrece para reclamar y, si es lo único pendiente, se explica por qué.
    /// </summary>
    [Fact]
    public async Task Reclamar_faltantes_no_ofrece_un_sin_confirmar_sin_fecha_y_explica_por_que()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar))
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Reclamar faltantes")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("el envío rechazaría un documento sin fecha");
        Avisos.Mensajes.Select(m => m.Mensaje).Should().ContainSingle()
            .Which.Should().Contain("«Sin confirmar» no tienen fecha de vencimiento");
    }

    [Fact]
    public async Task Reclamar_faltantes_con_un_sin_confirmar_sin_fecha_y_un_vencido_solo_reclama_el_vencido()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var vencido = Documento("Formación PRL — 20 h", EstadoDocumento.Vencido);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar), vencido)
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Reclamar faltantes")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.DocumentoIds.Should().Equal([vencido.DocumentoId!.Value]);
    }

    [Fact]
    public async Task Reclamar_faltantes_no_ofrece_un_documento_con_vencimiento_mas_alla_de_la_ventana()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var lejano = new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL — 20 h", EstadoDocumento.Proximo,
            VentanaReclamacion.Limite(DiaDeNegocio.Hoy()).AddDays(1));
        var dentro = new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Proximo,
            VentanaReclamacion.Limite(DiaDeNegocio.Hoy()));
        mediador.Centros[id] = [Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Proximo, lejano, dentro)];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Reclamar faltantes")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.DocumentoIds.Should().Equal([dentro.DocumentoId!.Value],
                "el límite es inclusivo y un día más allá el envío lo rechazaría");
    }

    private MediatorFalso ConDosClientesEmpresarialesQueReclamar(Guid id)
    {
        var mediador = ConTrabajador(id);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido, Documento("Formación PRL — 20 h", EstadoDocumento.Vencido)),
            Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.Vencido, Documento("Reconocimiento médico", EstadoDocumento.Vencido)),
        ];
        return mediador;
    }

    private static async Task AbrirModalReclamarAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Reclamar faltantes")
            .ClickAsync(new MouseEventArgs());
        cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().HaveCount(2,
            "el test necesita la modal con dos Clientes empresariales");
    }

    [Fact]
    public async Task Aviso_reclamar_con_todos_los_Clientes_empresariales_marcados_de_partida_no_pregunta()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "las casillas marcadas de partida no son un cambio");
    }

    [Fact]
    public async Task Aviso_reclamar_con_una_casilla_desmarcada_pregunta_y_volver_a_marcarla_no()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "volver a marcarla deja la modal como se abrió");
    }

    // ---- S12 lote 3b: los dos modales de la ficha son ModalFormulario

    private static IElement BotonDelPie(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    private static IReadOnlyList<string> AlertasDelModal(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=dialog] [role=alert]").Select(a => a.TextContent.Trim()).ToList();

    [Fact]
    public async Task Crear_gestion_sin_tipo_deshabilita_Crear_con_su_motivo_y_con_tipo_lo_habilita()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);

        var crear = BotonDelPie(cut, "Crear");
        crear.HasAttribute("disabled").Should().BeTrue("sin tipo elegido no hay nada que crear");
        crear.GetAttribute("title").Should().Be("Elige un tipo de documento", "un primario deshabilitado dice por qué");

        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });

        BotonDelPie(cut, "Crear").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Crear_gestion_rechazada_por_el_servidor_sale_en_el_aviso_fijo_no_en_un_toast_y_elegir_otro_tipo_lo_quita()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.FallaCrearGestionCon = "Ya hay una gestión abierta de este tipo.";
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });
        AlertasDelModal(cut).Should().BeEmpty("control positivo: antes del intento no hay aviso");

        await BotonDelPie(cut, "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearGestionesParaTrabajadorCommand>().Should().ContainSingle("control positivo: se intentó");
        AlertasDelModal(cut).Should().Equal("Ya hay una gestión abierta de este tipo.");
        cut.FindAll("[role=dialog] .modal-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        Avisos.Mensajes.Should().BeEmpty("el motivo ya no es un toast que desaparece");

        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = string.Empty });
        AlertasDelModal(cut).Should().BeEmpty("el motivo era del intento anterior");
    }

    [Fact]
    public async Task Cancelar_en_crear_gestion_con_un_tipo_elegido_pregunta_y_sin_tipo_cierra()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        await BotonDelPie(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin tipo elegido «Cancelar» cierra sin preguntar");

        await AbrirModalCrearGestionAsync(cut);
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });
        await BotonDelPie(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios", "con un tipo elegido «Cancelar» pregunta como la X");
    }

    [Fact]
    public async Task Reclamar_sin_ningun_Cliente_empresarial_marcado_deshabilita_Reclamar_con_su_motivo()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);
        BotonDelPie(cut, "Reclamar").HasAttribute("disabled").Should().BeFalse("control positivo: con las dos marcadas se puede reclamar");

        for (var i = 0; i < 2; i++)
            await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[i].ChangeAsync(new ChangeEventArgs { Value = false });

        var reclamar = BotonDelPie(cut, "Reclamar");
        reclamar.HasAttribute("disabled").Should().BeTrue();
        reclamar.GetAttribute("title").Should().Be("Marca al menos un Cliente empresarial");
    }

    /// <summary>El modal cerraba antes de enviar: la selección se perdía y, si el envío fallaba, solo quedaban toasts.</summary>
    [Fact]
    public async Task Reclamar_mantiene_el_modal_abierto_mientras_envia_y_lo_cierra_al_terminar()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el primer envío está en curso");
            cut.FindAll("[role=dialog]").Should().NotBeEmpty("el modal sigue abierto mientras se envía");
            cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().HaveCount(2, "y conserva la selección");
            cut.Find(".reclamacion-destinatarios").HasAttribute("disabled").Should().BeTrue("las casillas no se tocan mientras se envía");
            BotonDelPie(cut, "Cancelar").HasAttribute("disabled").Should().BeTrue();
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "un envío por Cliente empresarial");
        cut.FindAll("[role=dialog]").Should().BeEmpty("al terminar bien, se cierra");
    }

    /// <summary>El kit bloquea Escape y el clic fuera mientras guarda, pero la X es del Modal: la pantalla ignora el cierre mientras envía.</summary>
    [Fact]
    public async Task La_X_no_cierra_el_modal_de_reclamar_ni_el_de_crear_gestion_mientras_se_envia()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el envío está en curso");
            await cut.Find("[role=dialog] .modal-cerrar").ClickAsync(new MouseEventArgs());
            cut.FindAll("[role=dialog]").Should().NotBeEmpty("cerrar con la X a mitad de un envío perdería su resultado");
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Reclamar_con_exito_parcial_cierra_y_avisa_en_toasts_del_envio_que_fallo()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        mediador.FallaEnviarReclamacionCon = "Sin contacto con email.";
        mediador.FallaEnviarReclamacionDesdeElEnvio = 2;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "control positivo: se intentó con los dos");
        cut.FindAll("[role=dialog]").Should().BeEmpty("alguno salió: se cierra");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("Sin contacto con email."), "el fallo parcial no se pierde");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Exito, "y el envío que salió también se anuncia");
    }

    /// <summary>Con el modal abierto durante el envío, la ficha puede cambiar de trabajador antes de que termine.</summary>
    [Fact]
    public async Task Un_envio_de_reclamacion_que_termina_tras_cambiar_de_trabajador_no_escribe_en_la_ficha_nueva_y_sus_fallos_salen_en_toasts()
    {
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(primero);
        mediador.Detalles[segundo] = Detalle(segundo, "Eider", "Lasa Arrieta");
        mediador.Centros[segundo] = mediador.Centros[primero];
        mediador.FallaEnviarReclamacionCon = "Sin contacto con email.";
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(primero);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el envío del primero está en curso");
            cut.Render(p => p.Add(x => x.TrabajadorId, segundo));
            cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // El segundo trabajador abre su propio modal: no hereda el aviso del envío del primero.
        await AbrirModalReclamarAsync(cut);
        AlertasDelModal(cut).Should().BeEmpty("el rechazo era del trabajador anterior");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("Sin contacto con email."), "el fallo del primero no se pierde: sale en toast");
    }

    [Fact]
    public async Task Reclamar_con_todos_los_envios_rechazados_deja_el_modal_con_la_seleccion_y_el_motivo_en_el_aviso_fijo()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        mediador.FallaEnviarReclamacionCon = "El Cliente empresarial no tiene contacto con email.";
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "control positivo: se intentó con los dos");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("si no se envió ninguno, el modal sigue para reintentar");
        AlertasDelModal(cut).Should().ContainSingle().Which.Should().Contain("El Cliente empresarial no tiene contacto con email.");
        cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("checked"), "la selección se conserva");
        Avisos.Mensajes.Should().BeEmpty("los motivos van en el aviso fijo, no en toasts");

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = false });
        AlertasDelModal(cut).Should().BeEmpty("el motivo era del intento anterior");
    }
}
