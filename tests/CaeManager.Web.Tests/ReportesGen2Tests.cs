using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Reportes.Commands.RegistrarHistorialInforme;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Application.Reportes.Queries.ObtenerHistorialInformes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ReportesPagina = CaeManager.Web.Features.Reportes.Pages.Reportes;

namespace CaeManager.Web.Tests;

/// <summary>
/// Reportes contra su mockup Gen 2 («Reportes TALVEG.dc.html»).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador
/// y con qué parámetros —el doble responde según ellos: filtra filas por
/// cliente empresarial y por «incluir vigentes», y rotula el alcance con el
/// cliente o centro pedido, así que una pantalla que no los enviara recibiría
/// otra cosa—, qué se pinta con lo que vuelve, la URL que queda, y qué pasa
/// cuando las respuestas llegan fuera de orden (mediador controlado por
/// <see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización de la ruta (el
/// <c>[Authorize]</c> lo fija <c>PaginasInternasExcluyenAlRolClienteTests</c>)
/// ni el acotado por cartera de las consultas (<c>AlcanceDeCarteraEnReportesTests</c>,
/// en Application); el contenido de los PDF/Excel; ni el aspecto (CSS).
/// </para>
/// </summary>
public class ReportesGen2Tests : BunitContext
{
    /// <summary><see cref="TextoFechaCopiable"/> importa clipboard.js al pintarse.</summary>
    public ReportesGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid CentroA1 = Guid.Parse("a1a1a1a1-0000-0000-0000-0000000000c1");
    private static readonly Guid CentroB1 = Guid.Parse("b2b2b2b2-0000-0000-0000-0000000000c1");
    private static readonly Guid UsuarioMarta = Guid.Parse("99999999-0000-0000-0000-000000000001");

    private const string NombreA = "Refrielectric S.A.";
    private const string NombreB = "Montajes Ebro S.L.";

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Solo responde a <c>FindByIdAsync</c>, que es lo único que la página pide.</summary>
    private sealed class AlmacenUsuarios(Dictionary<string, ApplicationUser> usuarios) : IUserStore<ApplicationUser>
    {
        private static Exception NoPrevisto() => new NotSupportedException("La página solo busca usuarios por Id.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(usuarios.GetValueOrDefault(userId));

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }

    private sealed record FilaDe(Guid ClienteId, FilaReporteDocumentoDto Fila);

    /// <summary>
    /// Datos que ve el mediador. Todo se responde <b>según los parámetros de
    /// la consulta</b>: un doble que los ignorase dejaría en verde una
    /// pantalla que no los envía.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } = [new(ClienteA, NombreA), new(ClienteB, NombreB)];

        public Dictionary<Guid, List<CentroSelectorDto>> Centros { get; } = new()
        {
            [ClienteA] = [new(CentroA1, "Nave Norte", NombreA, NombreA)],
            [ClienteB] = [new(CentroB1, "Planta Zaragoza", NombreB, NombreB)]
        };

        public List<FilaDe> Documentos { get; } =
        [
            new(ClienteA, Documento("Juan Pérez", "Formación PRL — 20 h", new DateOnly(2026, 8, 2), EstadoDocumento.Vencido)),
            new(ClienteA, Documento("Nuria Salas", "Certificado de aptitud", new DateOnly(2026, 9, 21), EstadoDocumento.Urgente)),
            new(ClienteA, Documento("Iker Mena", "Formación trabajos en altura", new DateOnly(2026, 11, 14), EstadoDocumento.Vigente)),
            new(ClienteA, Documento("David Rey", "Alta en Seguridad Social", null, EstadoDocumento.SinCaducidad)),
            new(ClienteB, Documento("Laura Ortiz", "Reconocimiento médico", new DateOnly(2026, 7, 30), EstadoDocumento.Vencido)),
        ];

        public List<HistorialInformeDto> Historial { get; } = [];

        /// <summary>Si devuelve null, la consulta de vigencia responde con <see cref="Documentos"/> filtrados.</summary>
        public Func<GenerarInformeVigenciaQuery, InformeVigenciaDto?>? Vigencia { get; set; }

        public Func<ObtenerHistorialInformesQuery, IReadOnlyList<HistorialInformeDto>>? HistorialConsulta { get; set; }

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes,
                ObtenerCentrosParaSelectorQuery q => q.ClienteId is { } id && Centros.TryGetValue(id, out var centros)
                    ? centros.ToList()
                    : new List<CentroSelectorDto>(),
                GenerarInformeVigenciaQuery q => Vigencia is null ? VigenciaSegun(q) : Vigencia(q),
                GenerarInformeAsignacionesQuery q => AsignacionesSegun(q),
                ObtenerHistorialInformesQuery q => HistorialConsulta is null ? Historial.ToList() : HistorialConsulta(q),
                RegistrarHistorialInformeCommand c => Registrar(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        public InformeVigenciaDto VigenciaSegun(GenerarInformeVigenciaQuery q) => new(
            Alcance(q.ClienteId, q.CentroId),
            Documentos
                .Where(d => q.ClienteId is null || d.ClienteId == q.ClienteId)
                .Select(d => d.Fila)
                .Where(f => q.IncluirVigentes || f.Estado is EstadoDocumento.Vencido or EstadoDocumento.Urgente)
                .ToList());

        private InformeAsignacionesDto AsignacionesSegun(GenerarInformeAsignacionesQuery q) => new(
            Alcance(q.ClienteId, q.CentroId),
            q.ClienteId == ClienteB
                ? [new FilaInformeAsignacionDto("Laura Ortiz", "Planta Zaragoza", new DateOnly(2026, 3, 1))]
                : [new FilaInformeAsignacionDto("Juan Pérez", "Nave Norte", new DateOnly(2026, 1, 15))]);

        private string Alcance(Guid? clienteId, Guid? centroId) =>
            centroId is { } c ? $"Solo centro: {Centros.Values.SelectMany(l => l).Single(x => x.Id == c).Nombre}"
            : clienteId is { } id ? $"{Clientes.Single(x => x.Id == id).RazonSocial} · todo el cliente"
            : "Toda la cartera (sin filtrar por cliente)";

        private Result Registrar(RegistrarHistorialInformeCommand c)
        {
            Historial.Insert(0, new HistorialInformeDto(Guid.NewGuid(), c.TipoInforme, c.ClienteNombre, DateTime.UtcNow, UsuarioMarta));
            return Result.Exito();
        }
    }

    private static FilaReporteDocumentoDto Documento(string trabajador, string documento, DateOnly? vence, EstadoDocumento estado) =>
        new(Guid.NewGuid(), trabajador, "Instalaciones Vega S.L.", documento, vence, estado);

    // ---------------------------------------------------------------- arnés

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private (IRenderedComponent<ReportesPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario, string ruta = "reportes")
    {
        // La aplicación fija es-ES en Program.cs; aquí se fija en el flujo del
        // propio test, que es donde renderiza bUnit.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorControlado(escenario.Responder);
        var usuarios = new Dictionary<string, ApplicationUser>
        {
            [UsuarioMarta.ToString()] = new ApplicationUser { NombreCompleto = "Marta Rodríguez" }
        };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuarios(usuarios), null!, null!, null!, null!, null!, null!, null!, null!));

        // [SupplyParameterFromQuery]: se llega navegando, igual que en el producto.
        Navegacion.NavigateTo(ruta);

        return (Render<ReportesPagina>(), mediador);
    }

    private static Task Generar(IRenderedComponent<ReportesPagina> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Generar vista previa").ClickAsync(new MouseEventArgs());

    private static Task ElegirInforme(IRenderedComponent<ReportesPagina> cut, string titulo) =>
        cut.FindAll("[role=radio]").Single(b => b.QuerySelector(".titulo-item-informe")!.TextContent == titulo).ClickAsync(new MouseEventArgs());

    /// <summary>Escribir la razón social exacta es lo que hace casar la opción del datalist.</summary>
    private static Task EscribirCliente(IRenderedComponent<ReportesPagina> cut, string razonSocial) =>
        cut.Find(".campo-cliente-informe input").InputAsync(new ChangeEventArgs { Value = razonSocial });

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    private static IReadOnlyList<string> Celdas(IElement fila) => fila.QuerySelectorAll("td").Select(Texto).ToList();

    private static IReadOnlyList<IElement> FilasHoja(IRenderedComponent<ReportesPagina> cut) =>
        cut.FindAll(".tabla-hoja-informe tbody tr");

    private static IReadOnlyList<string> Descargas(IRenderedComponent<ReportesPagina> cut) =>
        cut.FindAll("a.boton-exportar-informe").Select(a => a.GetAttribute("href")!).ToList();

    private static IElement EnviarPorComunicaciones(IRenderedComponent<ReportesPagina> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Enviar por Comunicaciones…");

    // ---------------------------------------------------------------- generar

    [Fact]
    public async Task Generar_vigencia_envia_el_cliente_y_el_centro_del_enlace_y_pinta_lo_que_devuelve()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}&centroId={CentroA1}");

        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Should().ContainSingle()
            .Which.Should().Be(new GenerarInformeVigenciaQuery(ClienteA, CentroA1, true),
                "el enlace profundo desde Centro 360 preselecciona cliente empresarial y centro");
        Texto(cut.Find(".titulo-hoja-informe")).Should().Be($"Informe de vigencia documental — {NombreA}");
        cut.FindAll(".metadato-hoja-informe").Select(Texto).Last()
            .Should().Be("Abarca: Solo centro: Nave Norte · incluye los vigentes · 4 documentos");

        var filas = FilasHoja(cut);
        filas.Should().HaveCount(4);
        Celdas(filas[0]).Should().Equal("Juan Pérez", "Instalaciones Vega S.L.", "Formación PRL — 20 h", "02/08/2026", "Vencido");
        Celdas(filas[3]).Should().Equal("David Rey", "Instalaciones Vega S.L.", "Alta en Seguridad Social", "No caduca", "Sin caducidad");
    }

    [Fact]
    public async Task Incidencias_pide_solo_vencidos_y_urgentes_y_no_ofrece_la_casilla_de_vigentes()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}");

        await ElegirInforme(cut, "Incidencias");

        cut.FindAll("[role=radio]").Single(b => b.GetAttribute("aria-checked") == "true")
            .QuerySelector(".titulo-item-informe")!.TextContent.Should().Be("Incidencias");
        cut.FindAll(".opcion-checkbox-informe input").Should().BeEmpty();

        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Single().IncluirVigentes.Should().BeFalse();
        Texto(cut.Find(".titulo-hoja-informe")).Should().Be($"Informe de incidencias — {NombreA}");
        FilasHoja(cut).Select(f => Celdas(f)[4]).Should().Equal("Vencido", "Urgente");
    }

    [Fact]
    public async Task Desmarcar_vigentes_en_vigencia_se_envia_y_la_hoja_lo_dice()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}");

        await cut.Find(".opcion-checkbox-informe input").ChangeAsync(new ChangeEventArgs { Value = false });
        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Single()
            .Should().Be(new GenerarInformeVigenciaQuery(ClienteA, null, false));
        cut.FindAll(".metadato-hoja-informe").Select(Texto).Last()
            .Should().Be($"Abarca: {NombreA} · todo el cliente · solo vencidos y urgentes · 2 documentos");
        Descargas(cut).Should().Contain($"/reportes/vigencia.pdf?clienteId={ClienteA}&incluirVigentes=false");
    }

    [Fact]
    public async Task Asignaciones_pide_su_propia_consulta_y_descarga_sin_incluirVigentes()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteB}");

        await ElegirInforme(cut, "Asignaciones activas");
        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeAsignacionesQuery>().Should().ContainSingle()
            .Which.Should().Be(new GenerarInformeAsignacionesQuery(ClienteB, null));
        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Should().BeEmpty();
        Celdas(FilasHoja(cut).Single()).Should().Equal("Laura Ortiz", "Planta Zaragoza", "01/03/2026");
        Descargas(cut).Should().Equal(
            $"/reportes/asignaciones.pdf?clienteId={ClienteB}",
            $"/reportes/asignaciones.xlsx?clienteId={ClienteB}");
    }

    // ---------------------------------------------------------------- descargas y filtros

    [Fact]
    public async Task Sin_vista_previa_no_hay_nada_que_descargar_ni_enviar()
    {
        var (cut, _) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}");

        cut.FindAll("a.boton-exportar-informe").Should().BeEmpty(
            "antes eran enlaces con href atenuados solo por CSS: con el teclado se descargaban igual");
        cut.FindAll(".boton-exportar-informe[aria-disabled=true]").Should().HaveCount(2);
        EnviarPorComunicaciones(cut).HasAttribute("disabled").Should().BeTrue();

        await Generar(cut);

        Descargas(cut).Should().Equal(
            $"/reportes/vigencia.pdf?clienteId={ClienteA}&incluirVigentes=true",
            $"/reportes/vigencia.xlsx?clienteId={ClienteA}&incluirVigentes=true");
        EnviarPorComunicaciones(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Cambiar_de_cliente_tras_generar_retira_la_hoja_y_sus_descargas_y_lo_escribe_en_la_url()
    {
        var (cut, _) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}");
        await Generar(cut);
        cut.FindAll(".hoja-informe").Should().ContainSingle();

        await EscribirCliente(cut, NombreB);

        cut.FindAll(".hoja-informe").Should().BeEmpty(
            "la hoja era de Refrielectric y el filtro dice ahora Montajes Ebro: antes seguía en pantalla y «Descargar PDF» bajaba otro informe");
        cut.FindAll("a.boton-exportar-informe").Should().BeEmpty();
        EnviarPorComunicaciones(cut).HasAttribute("disabled").Should().BeTrue();
        Texto(cut.Find(".estado-vacio h3")).Should().Be("Genera una vista previa");
        Navegacion.Uri.Should().Contain($"clienteId={ClienteB}").And.NotContain(ClienteA.ToString());
    }

    [Fact]
    public async Task Quitar_el_filtro_de_cliente_limpia_la_url_y_avisa_de_que_abarca_toda_la_cartera()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}&centroId={CentroA1}");
        cut.FindAll(".aviso-sin-cliente-informe").Should().BeEmpty();
        cut.Find(".campo-centro-informe select").GetAttribute("value").Should().Be(CentroA1.ToString());

        await cut.Find("button.quitar-filtro-cliente").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("clienteId").And.NotContain("centroId",
            "con el clienteId viejo en la URL, recargar la página volvía a filtrar por el cliente quitado");
        Texto(cut.Find(".aviso-sin-cliente-informe")).Should().Be("Sin filtrar por cliente empresarial: la vista previa abarca toda tu cartera");
        cut.Find(".campo-cliente-informe input").GetAttribute("value").Should().BeEmpty();
        cut.Find(".campo-centro-informe select").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button.quitar-filtro-cliente").Should().BeEmpty();

        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Single()
            .Should().Be(new GenerarInformeVigenciaQuery(null, null, true));
        Texto(cut.Find(".titulo-hoja-informe")).Should().Be("Informe de vigencia documental");
    }

    [Fact]
    public async Task Elegir_un_centro_lo_envia_y_lo_escribe_en_la_url()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteB}");

        cut.FindAll(".campo-centro-informe option").Select(Texto)
            .Should().Equal("Todos los centros del cliente empresarial", "Solo centro: Planta Zaragoza");
        await cut.Find(".campo-centro-informe select").ChangeAsync(new ChangeEventArgs { Value = CentroB1.ToString() });
        Navegacion.Uri.Should().Contain($"centroId={CentroB1}").And.Contain($"clienteId={ClienteB}");

        await Generar(cut);

        mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Single()
            .Should().Be(new GenerarInformeVigenciaQuery(ClienteB, CentroB1, true));
    }

    // ---------------------------------------------------------------- carreras

    [Fact]
    public async Task Una_vista_previa_pedida_con_el_cliente_anterior_no_se_pinta_bajo_el_nuevo()
    {
        var vigenciaA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Interceptar = p => p is GenerarInformeVigenciaQuery q && q.ClienteId == ClienteA ? vigenciaA.Task : null;
        var (cut, mediador) = Renderizar(escenario, $"reportes?clienteId={ClienteA}");

        var primera = Generar(cut);
        cut.WaitForAssertion(() => mediador.Enviados.OfType<GenerarInformeVigenciaQuery>().Should().ContainSingle());

        await EscribirCliente(cut, NombreB);
        await Generar(cut);
        FilasHoja(cut).Select(f => Celdas(f)[0]).Should().Equal("Laura Ortiz");

        // La de Refrielectric llega ahora, tarde.
        await cut.InvokeAsync(() => vigenciaA.SetResult(escenario.VigenciaSegun(new GenerarInformeVigenciaQuery(ClienteA, null, true))));
        await primera;

        Texto(cut.Find(".titulo-hoja-informe")).Should().Be($"Informe de vigencia documental — {NombreB}");
        FilasHoja(cut).Select(f => Celdas(f)[0]).Should().Equal(["Laura Ortiz"],
            "la respuesta de Refrielectric llegó después de pedir la de Montajes Ebro y ya no es la vigente");
        mediador.Enviados.OfType<RegistrarHistorialInformeCommand>().Should().ContainSingle()
            .Which.ClienteId.Should().Be(ClienteB, "un informe descartado no se anota en el historial");
    }

    [Fact]
    public async Task Quitar_el_filtro_mientras_genera_descarta_lo_que_llegue_despues()
    {
        var vigenciaA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Interceptar = p => p is GenerarInformeVigenciaQuery ? vigenciaA.Task : null;
        var (cut, mediador) = Renderizar(escenario, $"reportes?clienteId={ClienteA}");

        var generacion = Generar(cut);
        cut.WaitForAssertion(() => cut.FindAll(".progreso-carga").Should().ContainSingle());

        await cut.Find("button.quitar-filtro-cliente").ClickAsync(new MouseEventArgs());
        cut.FindAll(".progreso-carga").Should().BeEmpty("cambiar el filtro deja de esperar la vista previa anterior");

        await cut.InvokeAsync(() => vigenciaA.SetResult(escenario.VigenciaSegun(new GenerarInformeVigenciaQuery(ClienteA, null, true))));
        await generacion;

        cut.FindAll(".hoja-informe").Should().BeEmpty();
        cut.FindAll("a.boton-exportar-informe").Should().BeEmpty();
        mediador.Enviados.OfType<RegistrarHistorialInformeCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Los_centros_del_cliente_anterior_que_llegan_tarde_se_descartan()
    {
        var centrosA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Interceptar = p => p is ObtenerCentrosParaSelectorQuery { ClienteId: var id } && id == ClienteA ? centrosA.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        var eleccionA = EscribirCliente(cut, NombreA);
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerCentrosParaSelectorQuery>().Should().ContainSingle());
        await EscribirCliente(cut, NombreB);

        await cut.InvokeAsync(() => centrosA.SetResult(escenario.Centros[ClienteA].ToList()));
        await eleccionA;

        cut.FindAll(".campo-centro-informe option").Select(Texto)
            .Should().Equal(["Todos los centros del cliente empresarial", "Solo centro: Planta Zaragoza"],
                "los centros de Refrielectric llegaron tarde y el cliente elegido es Montajes Ebro");
    }

    // ---------------------------------------------------------------- estados

    [Fact]
    public async Task Si_la_generacion_revienta_lo_dice_y_reintentar_la_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Vigencia = q =>
        {
            if (++llamadas == 1) throw new InvalidOperationException("Base de datos caída (simulada).");
            return escenario.VigenciaSegun(q);
        };
        var (cut, _) = Renderizar(escenario, $"reportes?clienteId={ClienteA}");

        await Generar(cut);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("No pudimos generar la vista previa");
        cut.FindAll(".progreso-carga").Should().BeEmpty();

        await cut.Find(".estado-vacio-accion button").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        FilasHoja(cut).Should().HaveCount(4);
    }

    [Fact]
    public async Task Un_filtro_fuera_de_cartera_no_pinta_informe_ni_se_anota_en_el_historial()
    {
        var escenario = new Escenario { Vigencia = _ => null };
        var (cut, mediador) = Renderizar(escenario, $"reportes?clienteId={ClienteA}");

        await Generar(cut);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("No hay informe que mostrar para ese filtro");
        cut.FindAll("a.boton-exportar-informe").Should().BeEmpty();
        mediador.Enviados.OfType<RegistrarHistorialInformeCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_filas_la_hoja_lo_dice_en_vez_de_pintar_una_tabla_vacia()
    {
        var escenario = new Escenario();
        escenario.Documentos.RemoveAll(d => d.Fila.Estado is EstadoDocumento.Vencido or EstadoDocumento.Urgente);
        var (cut, _) = Renderizar(escenario, $"reportes?clienteId={ClienteA}");
        await ElegirInforme(cut, "Incidencias");

        await Generar(cut);

        cut.FindAll(".tabla-hoja-informe").Should().BeEmpty();
        Texto(cut.Find(".vacio-hoja-informe")).Should().Be("Ningún documento vencido ni urgente con estos filtros.");
        cut.FindAll(".metadato-hoja-informe").Select(Texto).Last().Should().EndWith("· 0 documentos");
    }

    // ---------------------------------------------------------------- historial

    [Fact]
    public async Task Generar_anota_el_informe_en_el_historial_y_lo_recarga_con_el_nombre_de_quien_lo_genero()
    {
        var (cut, mediador) = Renderizar(new Escenario(), $"reportes?clienteId={ClienteA}");
        Texto(cut.Find(".vacio-historial-informes")).Should().Be("Todavía no se ha generado ningún informe.");

        await Generar(cut);

        mediador.Enviados.OfType<RegistrarHistorialInformeCommand>().Should().ContainSingle()
            .Which.Should().Be(new RegistrarHistorialInformeCommand("Vigencia documental", ClienteA, NombreA));
        var item = cut.Find(".item-historial-informes");
        Texto(item.QuerySelector(".nombre-item-historial-informes")!).Should().Be("Vigencia documental");
        Texto(item.QuerySelector(".detalle-item-historial-informes")!).Should().Be($"{NombreA} · Marta Rodríguez");
    }

    [Fact]
    public async Task Si_el_historial_no_carga_no_dice_que_no_hay_informes_y_reintentar_lo_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Historial.Add(new HistorialInformeDto(Guid.NewGuid(), "Incidencias", null, DateTime.UtcNow, UsuarioMarta));
        escenario.HistorialConsulta = _ =>
        {
            if (++llamadas == 1) throw new InvalidOperationException("Base de datos caída (simulada).");
            return escenario.Historial.ToList();
        };
        var (cut, _) = Renderizar(escenario);

        cut.Markup.Should().NotContain("Todavía no se ha generado ningún informe",
            "antes el fallo se tragaba y el panel afirmaba que no había informes");
        Texto(cut.Find(".error-historial-informes")).Should().StartWith("No pudimos cargar el historial.");

        await cut.Find(".error-historial-informes button").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        Texto(cut.Find(".detalle-item-historial-informes")).Should().Be("Toda la cartera · Marta Rodríguez");
    }
}
