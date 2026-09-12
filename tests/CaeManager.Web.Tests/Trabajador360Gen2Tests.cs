using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
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
    public Trabajador360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

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
        public Result ResultadoEliminarTrabajador { get; set; } = Result.Exito();
        public Result ResultadoDarDeBajaAsignacion { get; set; } = Result.Exito();

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
            EliminarTrabajadorCommand => ResultadoEliminarTrabajador,
            DarDeBajaAsignacionesCommand => ResultadoDarDeBajaAsignacion,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        private ResultadoPaginado<GestionListaDto> Paginar(ObtenerGestionesQuery q)
        {
            var todas = q.TrabajadorId is { } id ? Gestiones.GetValueOrDefault(id) ?? [] : [];
            return new ResultadoPaginado<GestionListaDto>(todas, todas.Count, q.Pagina, q.TamanoPagina);
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
            estado == EstadoDocumento.Vigente ? new DateOnly(2027, 5, 3) : null);

    private static CentroDocumentacionTrabajadorDto Centro(
        string nombre, string clienteEmpresarial, EstadoDocumento peorEstado, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, Guid.NewGuid(), clienteEmpresarial,
            new DateOnly(2026, 7, 2), peorEstado, documentos);

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

        var cabecera = cut.Find(".trabajador360-cabecera");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().StartWith("Javier Salas Moreno");
        cabecera.TextContent.Should().Contain("12345678Z").And.Contain("Refrielectric S.L.");
        // 3 de 5 documentos exigidos entre todos los centros están al día.
        cabecera.QuerySelector(".anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("60");
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
            : t.FirstChild!.TextContent.Trim()).Should().Equal(["Operación", "Historial", "Contactos"],
            "el mockup marcaba una cuarta pestaña «Vehículos» que nunca existió en esta página");

        var contadores = cut.FindAll(".pestanas-contador");
        contadores.Should().ContainSingle("solo Operación cuenta algo");
        SinEspaciosDeMas(contadores[0].TextContent).Should().Be("2 documentos con incidencia",
            "el número sin unidad no dice qué cuenta: la glosa existe para el nombre accesible del botón");
        contadores[0].ClassList.Should().Contain("pestanas-contador-alerta");
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
    public async Task Dar_de_baja_al_trabajador_sigue_pidiendo_confirmacion_y_solo_despues_emite_el_comando()
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

        var itemsMenu = cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim()).ToList();
        itemsMenu.Should().Equal(["Editar", "Reclamar faltantes", "Crear gestión", "Dar de baja"]);
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Dar de baja")
            .ClickAsync(new MouseEventArgs());

        var dialogo = cut.Find(".modal-contenido");
        dialogo.TextContent.Should().Contain("¿Dar de baja a Javier Salas Moreno?")
            .And.Contain("Podrás deshacerlo desde el aviso que aparecerá");
        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().BeEmpty("todavía no se ha confirmado");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Dar de baja")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarTrabajadorCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(id);
    }

    [Fact]
    public void Contactos_ensena_el_telefono_y_el_correo_del_trabajador_y_no_inventa_los_que_no_existen()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno",
            telefono: "644 902 118", email: "javier.salas@refrielectric.example");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Contactos").Click();

        var rejilla = cut.Find(".trabajador360-contactos");
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
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Contactos").Click();

        cut.FindAll(".trabajador360-contactos").Should().BeEmpty();
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
        cut.FindAll(".trabajador360-cabecera").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.TrabajadorId, b));
        cut.Find(".trabajador360-cabecera h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        await cut.InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find(".trabajador360-cabecera h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta",
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

        cut.Find(".trabajador360-cabecera h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

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
        Registrar(new MediatorFalso());

        var cut = Renderizar(id);

        cut.Markup.Should().Contain("No pudimos cargar este trabajador").And.Contain("Reintentar");
        cut.FindAll(".trabajador360-cabecera").Should().BeEmpty();
    }
}
