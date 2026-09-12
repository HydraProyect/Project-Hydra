using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Centro 360 (<see cref="CentroDetalle"/> y su
/// <see cref="AcordeonAsignacionesCentro"/>) contra su mockup Gen 2
/// («Centro 360 TALVEG.dc.html»). Prueban efectos: qué se ve, qué consultas y
/// comandos salen y con qué parámetros, con qué token viajan, qué queda
/// abierto o cerrado. bUnit no evalúa CSS, así que ninguno afirma un estilo.
///
/// <para>
/// Las esperas se retienen con <see cref="TaskCompletionSource"/> SIN
/// continuaciones asíncronas: al liberar dentro de <c>cut.InvokeAsync</c>, la
/// continuación de la página corre en línea en el dispatcher del renderer, y
/// al volver del <c>await</c> ya ha escrito (o descartado) lo que iba a
/// escribir. Sin eso, una comprobación negativa podría pasar solo por llegar
/// antes que la respuesta.
/// </para>
/// </summary>
public class Centro360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js; MenuAcciones no usa interop.</summary>
    public Centro360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id del centro, el de
    /// la visita—, no por tipo: una consulta con el Id equivocado recibe lo que
    /// le corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, CentroDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, CentroListaDto> Resumenes { get; } = [];
        public Dictionary<Guid, List<VisitaResumenDto>> Visitas { get; } = [];
        public Dictionary<Guid, List<CanalGestionResumenDto>> Canales { get; } = [];
        public Dictionary<Guid, List<TrabajadorAsignacionDocumentacionDto>> Asignaciones { get; } = [];
        public Dictionary<Guid, List<TrabajadorSinAsignacionDto>> SinAsignacion { get; } = [];
        public Dictionary<Guid, List<DocumentoReclamableDto>> PorReclamar { get; } = [];

        public Result<ResultadoBajaLoteDto> ResultadoBaja { get; set; } = Result.Exito(new ResultadoBajaLoteDto(0, []));
        public Result<Guid> ResultadoCrearAsignacion { get; set; } = Result.Exito(Guid.NewGuid());

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
            ObtenerCentroPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                q.CentroId is { } id && Resumenes.TryGetValue(id, out var r) ? [r] : [], 1, 1, 1),
            ObtenerProximaVisitaPorCentroQuery q => q.CentroIds.ToDictionary(
                c => c,
                c => (IReadOnlyList<VisitaResumenDto>)(Visitas.GetValueOrDefault(c) ?? [])),
            ObtenerCanalesGestionDeCentroQuery q => Canales.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerLoteReclamacionQuery q => q.CentroId is { } id && PorReclamar.TryGetValue(id, out var docs)
                ? new List<LoteReclamacionClienteDto> { new(Guid.NewGuid(), "Refrielectric S.A.", null, docs) }
                : new List<LoteReclamacionClienteDto>(),
            ObtenerAsignacionesDocumentacionPorCentroQuery q => Asignaciones.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerTrabajadoresVisitaSinAsignacionQuery q => SinAsignacion.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerDocumentosFaltantesParaAsignacionQuery => new List<DocumentoFaltanteDto>(),
            DarDeBajaAsignacionesCommand => ResultadoBaja,
            CrearAsignacionCommand => ResultadoCrearAsignacion,
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>El acordeón monta DrawerGestionDocumento, que los inyecta; ningún camino de estos tests los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Este camino no abre archivos; si esto salta, la pantalla cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este camino no convierte nada; si esto salta, la pantalla cambió de camino.");
    }

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IValidator<CrearAsignacionCommand>>(_ => new InlineValidator<CrearAsignacionCommand>());

        // La página consulta RendererInfo.IsInteractive para decidir si lanza la
        // carga en segundo plano de «Canales de gestión» —así no deja una tarea
        // en vuelo cuando ASP.NET Core libera el scope de DI al terminar el
        // prerenderizado—. bUnit no declara ninguno por defecto y leerlo lanza,
        // así que el test declara el mismo modo que la página usa en
        // producción: InteractiveServer, ya interactivo.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<CentroDetalle> Renderizar(Guid centroId) =>
        Render<CentroDetalle>(p => p.Add(x => x.CentroId, centroId));

    private IRenderedComponent<AcordeonAsignacionesCentro> RenderizarAcordeon(
        Guid centroId, string centroNombre = "Centro Norte", Guid? visitaId = null, bool seleccionMultiple = true) =>
        Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, centroId)
            .Add(a => a.CentroNombre, centroNombre)
            .Add(a => a.VisitaId, visitaId)
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Ibertec GmbH")
            .Add(a => a.SeleccionMultiple, seleccionMultiple)
            .Add(a => a.MostrarTotales, true));

    private static CentroDetalleDto Detalle(Guid id, string nombre, string cliente = "Refrielectric S.A.", string empresa = "Ibertec GmbH") =>
        new(id, Guid.NewGuid(), cliente, Guid.NewGuid(), empresa, nombre, "CN-01", "Pol. Norte 4", "Marta R.", null, Guid.NewGuid());

    private static CentroListaDto Resumen(
        Guid id, string nombre, int? porcentaje = 61, EstadoCentro estado = EstadoCentro.Vencido,
        IReadOnlyList<IncidenciaCentroDto>? vencidas = null, IReadOnlyList<IncidenciaCentroDto>? proximas = null) =>
        new(id, nombre, "CN-01", Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Ibertec GmbH",
            estado, porcentaje, new RecuentosCentroDto(vencidas ?? [], proximas ?? []));

    private static IncidenciaCentroDto Incidencia(string descripcion, AmbitoCausa ambito = AmbitoCausa.Trabajador) =>
        new(descripcion, ambito, EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 7, 31));

    private static DocumentoRequeridoDto Documento(string nombre, EstadoDocumento estado, DateOnly? vence = null) =>
        new(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Guid.NewGuid(), nombre, estado, vence);

    private static TrabajadorAsignacionDocumentacionDto Asignado(
        string nombre, EstadoDocumento peor, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, new DateOnly(2026, 1, 9), peor, documentos);

    private static IElement Boton(IRenderedComponent<CentroDetalle> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    private static IElement BotonAcordeon(IRenderedComponent<AcordeonAsignacionesCentro> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    // ── El mockup ─────────────────────────────────────────────────────────

    /// <summary>
    /// La cabecera del mockup: nombre, las dos contrapartes con su apellido
    /// semántico —la contraparte de la Relación Empresarial es el Cliente
    /// empresarial, no «el cliente»—, la ventana de la próxima visita y el
    /// anillo con el porcentaje que devuelve la consulta.
    /// </summary>
    [Fact]
    public void La_cabecera_nombra_el_centro_su_cliente_empresarial_su_empresa_y_su_proxima_visita()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: 61);
        mediador.Visitas[id] = [new(Guid.NewGuid(), new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 23), 6)];

        var cut = Renderizar(id);

        cut.Find("h1").TextContent.Trim().Should().Be("Centro Norte");
        var texto = cut.Markup;
        texto.Should().Contain("Cliente empresarial:").And.Contain("Refrielectric S.A.")
            .And.Contain("Ibertec GmbH")
            .And.Contain("21/08–23/08");
        cut.Markup.Should().Contain("61");
    }

    /// <summary>
    /// El anillo no inventa la fracción «14 de 23 exigidos» que pinta el
    /// mockup: ninguna consulta la devuelve. Enseña el porcentaje que sí
    /// llega, y su nombre accesible dice de qué es.
    /// </summary>
    [Fact]
    public void Sin_porcentaje_el_anillo_no_finge_uno()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: null);

        var cut = Renderizar(id);

        cut.Markup.Should().NotContain("0%", "sin universo no hay cumplimiento que enseñar, y 0 % sería una afirmación");
    }

    /// <summary>
    /// El menú «⋯» del mockup. «Reclamar documentación» solo aparece con algo
    /// que reclamar: una acción sin objeto se ofrecería y no haría nada.
    /// </summary>
    [Fact]
    public async Task El_menu_de_acciones_solo_ofrece_reclamar_cuando_hay_algo_que_reclamar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");

        var cut = Renderizar(id);
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim()).ToList();
        items.Should().Contain("Editar centro").And.Contain("Generar informe del centro");
        items.Should().NotContain(i => i.StartsWith("Reclamar", StringComparison.Ordinal),
            "este centro no tiene ningún documento reclamable");
    }

    [Fact]
    public async Task Con_documentos_reclamables_el_menu_dice_cuantos()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.PorReclamar[id] =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Formación PRL", new DateOnly(2026, 8, 2), EstadoDocumento.Vencido),
            new(Guid.NewGuid(), Guid.NewGuid(), "Nuria Salas", Guid.NewGuid(), "Certificado de aptitud", new DateOnly(2026, 8, 9), EstadoDocumento.Vencido)
        ];

        var cut = Renderizar(id);
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim())
            .Should().Contain("Reclamar documentación (2)");
    }

    /// <summary>
    /// Barra de trabajo del mockup: el filtro de estado sale del mismo léxico
    /// cerrado que el resto de la aplicación y filtra la lista ya cargada, sin
    /// pedir nada nuevo.
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_recorta_la_lista_sin_lanzar_ninguna_consulta_nueva()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido, new DateOnly(2026, 8, 2))),
            Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente, new DateOnly(2027, 4, 17)))
        ];

        var cut = RenderizarAcordeon(id);
        cut.Markup.Should().Contain("Juan Pérez").And.Contain("Marco Vila");
        var consultasAntes = mediador.Enviadas.Count;

        cut.Render(p => p
            .Add(a => a.CentroId, id)
            .Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Ibertec GmbH")
            .Add(a => a.SeleccionMultiple, true)
            .Add(a => a.MostrarTotales, true)
            .Add(a => a.FiltroEstado, nameof(EstadoDocumento.Vencido)));

        cut.Markup.Should().Contain("Juan Pérez").And.NotContain("Marco Vila");
        mediador.Enviadas.Count.Should().Be(consultasAntes, "filtrar es client-side sobre lo ya cargado");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Vacío POR FILTRO ≠ vacío por no haber trabajadores. El primero nombra el
    /// filtro que no encontró nada; el segundo dice que no hay asignaciones.
    /// </summary>
    [Fact]
    public void El_vacio_por_filtro_dice_que_es_por_el_filtro_y_no_que_no_haya_trabajadores()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso())
            .Asignaciones[id] = [Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))];

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, id)
            .Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Ibertec GmbH")
            .Add(a => a.MostrarTotales, true)
            .Add(a => a.FiltroTexto, "Zutano"));

        cut.Markup.Should().Contain("Sin coincidencias").And.Contain("«Zutano»")
            .And.NotContain("Sin trabajadores asignados", "sí hay trabajadores: lo que no hay es coincidencias");
    }

    [Fact]
    public void Sin_ninguna_asignacion_el_vacio_dice_que_no_hay_trabajadores_no_que_falle_un_filtro()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] = [];

        var cut = RenderizarAcordeon(id);

        cut.Markup.Should().Contain("Sin trabajadores asignados").And.NotContain("Sin coincidencias");
    }

    /// <summary>
    /// «Totales del centro» del mockup, derivado de los documentos ya cargados
    /// —no hay consulta que los sume— y rotulado con su ámbito real: cuenta la
    /// documentación de los trabajadores, no la de la Empresa, que va aparte.
    /// </summary>
    [Fact]
    public void Los_totales_cuentan_la_lista_completa_y_dicen_de_quien_son()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Vencido,
                Documento("Formación PRL", EstadoDocumento.Vencido, new DateOnly(2026, 8, 2)),
                Documento("Reconocimiento médico", EstadoDocumento.Vigente, new DateOnly(2027, 8, 14))),
            Asignado("Nuria Salas", EstadoDocumento.Faltante,
                Documento("Formación específica de centro", EstadoDocumento.Faltante),
                Documento("Alta en Seguridad Social", EstadoDocumento.SinCaducidad))
        ];

        var cut = RenderizarAcordeon(id);

        var totales = cut.Find("[data-totales-documentales]").TextContent;
        totales.Should().Contain("4 exigidos").And.Contain("2 al día").And.Contain("1 vencidos").And.Contain("1 faltantes")
            .And.Contain("trabajadores de este centro", "la documentación de la Empresa no entra en esta suma");
    }

    // ── Contrato: concurrencia ────────────────────────────────────────────

    /// <summary>
    /// Abrir otro centro con la carga del primero en vuelo. La respuesta tardía
    /// del primero llega cuando ya se ve el segundo y no puede escribir nada.
    /// </summary>
    [Fact]
    public async Task Abrir_otro_centro_con_la_carga_en_vuelo_no_pinta_el_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerCentroPorIdQuery q && q.Id == a ? respuestaDeA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Centro Norte");
        mediador.Detalles[b] = Detalle(b, "Planta Zaragoza");
        mediador.Resumenes[a] = Resumen(a, "Centro Norte");
        mediador.Resumenes[b] = Resumen(b, "Planta Zaragoza");

        var cut = Renderizar(a);
        cut.FindAll("h1").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.CentroId, b));
        cut.Find("h1").TextContent.Trim().Should().Be("Planta Zaragoza");

        await cut.InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find("h1").TextContent.Trim().Should().Be("Planta Zaragoza",
            "la respuesta de A llegó tarde y ya no es la vigente");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Select(q => q.CentroId).Should().Equal([b],
            "la cadena de A se corta al llegar: no sigue pidiendo datos de un centro que ya no se ve");
    }

    /// <summary>
    /// La lista de trabajadores es del centro que se ve. El acordeón vive en
    /// una sola instancia y Blazor la reutiliza al cambiar de centro.
    /// </summary>
    [Fact]
    public void Cambiar_de_centro_recarga_la_lista_y_no_deja_la_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[a] = [Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido))];
        mediador.Asignaciones[b] = [Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))];

        var cut = RenderizarAcordeon(a);
        cut.Markup.Should().Contain("Juan Pérez");

        cut.Render(p => p
            .Add(x => x.CentroId, b)
            .Add(x => x.CentroNombre, "Planta Zaragoza")
            .Add(x => x.EmpresaId, Guid.NewGuid())
            .Add(x => x.EmpresaNombre, "Ibertec GmbH")
            .Add(x => x.SeleccionMultiple, true)
            .Add(x => x.MostrarTotales, true));

        cut.Markup.Should().Contain("Marco Vila").And.NotContain("Juan Pérez");
        mediador.Enviadas.OfType<ObtenerAsignacionesDocumentacionPorCentroQuery>().Select(q => q.CentroId)
            .Should().Equal([a, b]);
    }

    /// <summary>
    /// La respuesta del centro anterior, llegando tarde, no puede pintar su
    /// lista sobre el centro nuevo.
    /// </summary>
    [Fact]
    public async Task La_lista_del_centro_anterior_que_llega_tarde_no_se_pinta_sobre_el_nuevo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var listaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerAsignacionesDocumentacionPorCentroQuery q && q.CentroId == a ? listaDeA.Task : null
        });
        mediador.Asignaciones[a] = [Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido))];
        mediador.Asignaciones[b] = [Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))];

        var cut = RenderizarAcordeon(a);

        cut.Render(p => p
            .Add(x => x.CentroId, b)
            .Add(x => x.CentroNombre, "Planta Zaragoza")
            .Add(x => x.EmpresaId, Guid.NewGuid())
            .Add(x => x.EmpresaNombre, "Ibertec GmbH")
            .Add(x => x.SeleccionMultiple, true)
            .Add(x => x.MostrarTotales, true));
        cut.Markup.Should().Contain("Marco Vila");

        await cut.InvokeAsync(() => listaDeA.SetResult());

        cut.Markup.Should().Contain("Marco Vila").And.NotContain("Juan Pérez",
            "la lista de A llegó tarde y su contador dejó de ser el vigente");
    }

    /// <summary>
    /// Todas las consultas de carga llevan el token del ciclo de vida, y
    /// retirar la página las cancela. Los comandos NO lo llevan: navegar fuera
    /// no deshace una escritura que el usuario ya pidió.
    /// </summary>
    [Fact]
    public async Task Todas_las_consultas_de_la_pagina_llevan_su_token_y_ninguna_sobrevive_a_retirarla()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.Visitas[id] = [new(Guid.NewGuid(), new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 23), 6)];
        mediador.PorReclamar[id] = [new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Formación PRL", new DateOnly(2026, 8, 2), EstadoDocumento.Vencido)];

        var cut = Renderizar(id);

        var consultas = mediador.Tokens.Where(t => t.Peticion.GetType().Name.EndsWith("Query", StringComparison.Ordinal)).ToList();
        consultas.Select(t => t.Peticion.GetType().Name).Distinct().Should().BeEquivalentTo(
            [
                nameof(ObtenerCentroPorIdQuery), nameof(ObtenerCentrosQuery),
                nameof(ObtenerProximaVisitaPorCentroQuery), nameof(ObtenerLoteReclamacionQuery),
                nameof(ObtenerCanalesGestionDeCentroQuery),
                // Las del acordeón: es hijo de la página y su token es el suyo propio.
                nameof(ObtenerAsignacionesDocumentacionPorCentroQuery),
                nameof(ObtenerTrabajadoresVisitaSinAsignacionQuery)
            ],
            "el recorrido tiene que haber pasado por todas las consultas que lanza la pantalla, o la comprobación no las mira");
        consultas.Should().OnlyContain(t => t.Token.CanBeCanceled && !t.Token.IsCancellationRequested);

        await DisposeComponentsAsync();

        consultas.Should().OnlyContain(t => t.Token.IsCancellationRequested,
            "retirada la página no queda ninguna consulta suya viva");
    }

    /// <summary>
    /// Retirar la página con una consulta en vuelo la cancela sin dejar una
    /// excepción sin controlar, y corta la cadena que venía detrás.
    /// </summary>
    [Fact]
    public async Task Retirar_la_pagina_cancela_la_consulta_en_vuelo_y_corta_la_cadena()
    {
        var id = Guid.NewGuid();
        var visitas = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerProximaVisitaPorCentroQuery ? visitas.Task : null
        });
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");

        var cut = Renderizar(id);
        var instancia = cut.Instance;
        var campoGeneracion = typeof(CentroDetalle).GetField("_generacion", BindingFlags.Instance | BindingFlags.NonPublic);
        campoGeneracion.Should().NotBeNull("el test lee la generación para comprobar que Dispose se ejecutó");
        var generacionAntes = (int)campoGeneracion!.GetValue(instancia)!;

        var token = mediador.Tokens.Where(t => t.Peticion is ObtenerProximaVisitaPorCentroQuery)
            .Should().ContainSingle("la cadena está parada en las visitas").Subject.Token;
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token de la página, no CancellationToken.None");
        token.Register(() => visitas.TrySetCanceled(token));

        await DisposeComponentsAsync();

        ((int)campoGeneracion.GetValue(instancia)!).Should().BeGreaterThan(generacionAntes,
            "DisposeComponentsAsync tiene que haber llamado al Dispose de la página");
        token.IsCancellationRequested.Should().BeTrue();
        Renderer.UnhandledException.IsCompleted.Should().BeFalse("la cancelación la absorbe el catch de la propia carga");
        mediador.Enviadas.OfType<ObtenerLoteReclamacionQuery>().Should().BeEmpty("y la cadena no sigue");
    }

    // ── Contrato: nada preparado para una entidad se ejecuta sobre otra ───

    /// <summary>
    /// La modal de baja en lote queda preparada con asignaciones del centro A.
    /// Al cambiar a B se cierra y la selección se tira: confirmarla sobre B
    /// daría de baja asignaciones que no son suyas.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_centro_cierra_la_modal_de_baja_y_tira_lo_que_tenia_preparado()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[a] = [Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido))];
        mediador.Asignaciones[b] = [Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))];

        var cut = RenderizarAcordeon(a);
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await BotonAcordeon(cut, "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().ContainSingle("la modal quedó abierta sobre asignaciones de A");

        cut.Render(p => p
            .Add(x => x.CentroId, b)
            .Add(x => x.CentroNombre, "Planta Zaragoza")
            .Add(x => x.EmpresaId, Guid.NewGuid())
            .Add(x => x.EmpresaNombre, "Ibertec GmbH")
            .Add(x => x.SeleccionMultiple, true)
            .Add(x => x.MostrarTotales, true));

        // Se mira el diálogo, no su título: el título lleva el recuento de la
        // selección («Dar de baja a N trabajador(es)»), así que al vaciarse
        // pasa de 1 a 0 y una comprobación sobre ese texto pasaría con la modal
        // todavía abierta — medido: la mutación que quitaba el cierre no caía.
        cut.FindAll("[role=dialog]").Should().BeEmpty(
            "la modal estaba preparada para asignaciones del centro anterior");
        BotonAcordeon(cut, "Dar de baja seleccionados").HasAttribute("disabled").Should().BeTrue(
            "la selección del centro anterior se tiró");
        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty();
    }

    // ── Contrato: reentrada ───────────────────────────────────────────────

    /// <summary>
    /// Un doble clic en «Dar de baja» manda un solo comando. La guarda es de
    /// esta pantalla: el lote va en un <c>Modal</c>, no en
    /// <c>DialogoConfirmacion</c>, así que no hereda la guarda de ese diálogo;
    /// y <c>Boton</c> conserva su <c>@onclick</c> aunque esté deshabilitado.
    /// </summary>
    [Fact]
    public async Task Un_doble_clic_en_dar_de_baja_manda_un_solo_comando()
    {
        var id = Guid.NewGuid();
        var comando = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is DarDeBajaAsignacionesCommand ? comando.Task : null,
            ResultadoBaja = Result.Exito(new ResultadoBajaLoteDto(1, []))
        });
        mediador.Asignaciones[id] = [Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido))];

        var cut = RenderizarAcordeon(id);
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await BotonAcordeon(cut, "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());

        // Sin await: el comando está retenido.
        var primero = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => comando.SetResult());
        await primero;
        await segundo;

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle();
    }

    /// <summary>Un doble clic en «Asignar» de la asignación rápida desde visita manda un solo comando.</summary>
    [Fact]
    public async Task Un_doble_clic_en_asignar_desde_visita_manda_un_solo_comando()
    {
        var id = Guid.NewGuid();
        var visitaId = Guid.NewGuid();
        var comando = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is CrearAsignacionCommand ? comando.Task : null
        });
        mediador.Asignaciones[id] = [];
        mediador.SinAsignacion[id] = [new(Guid.NewGuid(), "Bea Alonso Ruiz")];

        var cut = RenderizarAcordeon(id, visitaId: visitaId);

        var primero = BotonAcordeon(cut, "Asignar").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Asignar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearAsignacionCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => comando.SetResult());
        await primero;
        await segundo;

        mediador.Enviadas.OfType<CrearAsignacionCommand>().Should().ContainSingle();
    }

    // ── Contrato: desenlaces honestos ─────────────────────────────────────

    /// <summary>
    /// «Hizo cero» no es un éxito. El comando no falló, pero no dio de baja a
    /// nadie: se cuenta como aviso y se dice cuántas se pidieron.
    /// </summary>
    [Fact]
    public async Task Una_baja_que_no_dio_de_baja_a_nadie_no_se_presenta_como_exito()
    {
        var toasts = await EjecutarBajaAsync(new ResultadoBajaLoteDto(0, []));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().NotBe(TonoToast.Exito, "no se hizo nada: un vacío no se presenta como logro");
        toast.Mensaje.Should().Contain("No se dio de baja a nadie");
    }

    /// <summary>Errores parciales: ni éxito ni fallo total, y la modal sigue abierta sobre lo que no se procesó.</summary>
    [Fact]
    public async Task Una_baja_con_errores_parciales_los_cuenta_y_deja_la_modal_abierta()
    {
        var (toasts, cut) = await EjecutarBajaConComponenteAsync(
            new ResultadoBajaLoteDto(1, ["La asignación de Nuria Salas ya estaba de baja."]));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().Be(TonoToast.Advertencia);
        toast.Mensaje.Should().Contain("1 de 2").And.Contain("1 no se pudieron procesar")
            .And.Contain("ya estaba de baja");
        cut.FindAll("[role=dialog]").Should().ContainSingle("queda algo que decidir sobre lo que no se procesó");
    }

    [Fact]
    public async Task Una_baja_que_hizo_todo_lo_pedido_si_es_un_exito()
    {
        var toasts = await EjecutarBajaAsync(new ResultadoBajaLoteDto(1, []));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().Be(TonoToast.Exito);
        toast.Mensaje.Should().Contain("1 trabajador(es) dado(s) de baja");
    }

    /// <summary>Un comando rechazado se enseña con su motivo y no se traga.</summary>
    [Fact]
    public async Task Si_el_comando_rechaza_la_baja_se_ensena_su_motivo()
    {
        var toasts = await EjecutarBajaAsync(
            Result.Fallo<ResultadoBajaLoteDto>(Error.Crear("Asignacion.NoEncontrada", "No encontramos esas asignaciones.")));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().Be(TonoToast.Error);
        toast.Mensaje.Should().Be("No encontramos esas asignaciones.");
    }

    private async Task<IReadOnlyList<ToastMensaje>> EjecutarBajaAsync(Result<ResultadoBajaLoteDto> resultado)
    {
        var (toasts, _) = await EjecutarBajaConComponenteAsync(resultado);
        return toasts;
    }

    private async Task<(IReadOnlyList<ToastMensaje> Toasts, IRenderedComponent<AcordeonAsignacionesCentro> Cut)>
        EjecutarBajaConComponenteAsync(Result<ResultadoBajaLoteDto> resultado)
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso { ResultadoBaja = resultado }).Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido)),
            Asignado("Nuria Salas", EstadoDocumento.Vencido, Documento("Certificado de aptitud", EstadoDocumento.Vencido))
        ];

        var cut = RenderizarAcordeon(id);
        // Re-buscadas en cada vuelta: marcar una re-renderiza el acordeón y los
        // manejadores de la búsqueda anterior dejan de existir.
        for (var i = 0; i < 2; i++)
            await cut.FindAll("input[type=checkbox]")[i].ChangeAsync(new ChangeEventArgs { Value = true });

        await BotonAcordeon(cut, "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        return (Services.GetRequiredService<ToastService>().Mensajes, cut);
    }
}
