using CaeManager.Application.Centros.Queries.ObtenerDocumentacionVehiculosDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Auditoria.Queries;
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
using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
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
public partial class Centro360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js; MenuAcciones no usa interop.</summary>
    public Centro360Gen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([]);
        this.ConRolDeEscritura();
    }

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id del centro, el de
    /// la visita—, no por tipo: una consulta con el Id equivocado recibe lo que
    /// le corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, List<DocumentacionEmpresaCentroDto>> DocumentacionEmpresas { get; } = [];
        public Dictionary<Guid, List<DocumentacionRequeridaCentroDto>> Requisitos { get; } = [];
        public Dictionary<Guid, CentroDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, List<RegistroAuditoriaListaDto>> Auditoria { get; } = [];
        public Dictionary<Guid, CentroListaDto> Resumenes { get; } = [];
        public Dictionary<Guid, List<VisitaResumenDto>> Visitas { get; } = [];
        public Dictionary<Guid, List<CanalGestionResumenDto>> Canales { get; } = [];
        /// <summary>Credencial por (Centro, Canal): responde solo al par exacto, como la consulta real.</summary>
        public Dictionary<(Guid CentroId, Guid CanalId), CredencialCanalGestionDto> Credenciales { get; } = [];
        public Dictionary<Guid, List<TrabajadorAsignacionDocumentacionDto>> Asignaciones { get; } = [];
        public Dictionary<Guid, List<TrabajadorSinAsignacionDto>> SinAsignacion { get; } = [];

        /// <summary>Empresa empleadora por Trabajador asignado (la fila de Centro 360 la enseña junto al nombre).</summary>
        public Dictionary<Guid, List<TrabajadorAsignadoDto>> Empleadores { get; } = [];
        public Dictionary<Guid, List<DocumentoReclamableDto>> PorReclamar { get; } = [];

        /// <summary>La vista «Vehículos» de Centro 360, por Centro.</summary>
        public Dictionary<Guid, List<VehiculoDocumentacionCentroDto>> Vehiculos { get; } = [];

        /// <summary>Los bloqueos de acceso por Centro: responde a la consulta con SU Centro, como la real.</summary>
        public Dictionary<Guid, List<DocumentacionBloqueantePendienteDto>> Bloqueos { get; } = [];

        public Result<ResultadoBajaLoteDto> ResultadoBaja { get; set; } = Result.Exito(new ResultadoBajaLoteDto(0, []));
        public Result<Guid> ResultadoCrearAsignacion { get; set; } = Result.Exito(Guid.NewGuid());
        public Result ResultadoFicha { get; set; } = Result.Exito();
        public CentroDetalleDto? DetalleTrasGuardarFicha { get; set; }

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>El token con el que llegó cada petición, en el mismo orden que <see cref="Enviadas"/>.</summary>
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        /// <summary>Como AutorizacionSecretosDeTenantBehavior con un rol que lee secretos y sin 2FA (P1-I1).</summary>
        public bool SinDobleFactor { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (SinDobleFactor && request is IConsultaDeSecretosDeTenant or IConsultaDeDatosDeCredencial)
                throw new SegundoFactorRequeridoParaCredencialesException();
            if (Retener?.Invoke(request) is { } retenida)
                await retenida;
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerDocumentacionEmpresasDeCentroQuery q => DocumentacionEmpresas.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerDocumentacionRequeridaDeCentroQuery q => Requisitos.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerCentroPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerAuditoriaQuery q => new ResultadoPaginado<RegistroAuditoriaListaDto>(
                q.EntidadTipo == "Centro" && q.EntidadId is { } id ? Auditoria.GetValueOrDefault(id) ?? [] : [],
                q.EntidadId is { } centro ? Auditoria.GetValueOrDefault(centro)?.Count ?? 0 : 0, q.Pagina, q.TamanoPagina),
            ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                q.CentroId is { } id && Resumenes.TryGetValue(id, out var r) ? [r] : [], 1, 1, 1),
            ObtenerProximaVisitaPorCentroQuery q => q.CentroIds.ToDictionary(
                c => c,
                c => (IReadOnlyList<VisitaResumenDto>)(Visitas.GetValueOrDefault(c) ?? [])),
            ObtenerCanalesGestionDeCentroQuery q => Canales.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerCredencialCanalGestionQuery q => Credenciales.GetValueOrDefault((q.CentroId, q.CanalId)),
            ObtenerLoteReclamacionQuery q => q.CentroId is { } id && PorReclamar.TryGetValue(id, out var docs)
                ? new List<LoteReclamacionClienteDto> { new(Guid.NewGuid(), "Refrielectric S.A.", null, docs) }
                : new List<LoteReclamacionClienteDto>(),
            ObtenerAsignacionesDocumentacionPorCentroQuery q => Asignaciones.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerDocumentacionBloqueantePendienteQuery q => (IReadOnlyList<DocumentacionBloqueantePendienteDto>)
                (q.CentroId is { } id ? Bloqueos.GetValueOrDefault(id) ?? [] : Bloqueos.Values.SelectMany(b => b).ToList()),
            ObtenerTrabajadoresVisitaSinAsignacionQuery q => SinAsignacion.GetValueOrDefault(q.CentroId) ?? [],
            ObtenerTrabajadoresAsignadosDeCentroQuery q => (IReadOnlyList<TrabajadorAsignadoDto>)(Empleadores.GetValueOrDefault(q.CentroId) ?? []),
            ObtenerDocumentacionVehiculosDeCentroQuery q => (IReadOnlyList<VehiculoDocumentacionCentroDto>)(Vehiculos.GetValueOrDefault(q.CentroId) ?? []),
            ObtenerDocumentosFaltantesParaAsignacionQuery => new List<DocumentoFaltanteDto>(),
            CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro.ObtenerCambiosEstadoPlataformaDeCentroQuery =>
                (IReadOnlyList<CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro.CambioEstadoPlataformaCentroDto>)[],
            DarDeBajaAsignacionesCommand => ResultadoBaja,
            CrearAsignacionCommand => ResultadoCrearAsignacion,
            EliminarCentroCommand => Result.Exito(),
            RestaurarCentroCommand => Result.Exito(),
            CaeManager.Application.Centros.Commands.EditarFichaCentro.EditarFichaCentroCommand c => GuardarFicha(c),
            CaeManager.Application.Centros.Commands.GuardarNotaInternaCentro.GuardarNotaInternaCentroCommand c => GuardarNota(c.Id, c.Notas),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        private Result GuardarFicha(CaeManager.Application.Centros.Commands.EditarFichaCentro.EditarFichaCentroCommand comando)
        {
            if (ResultadoFicha.EsExitoso && DetalleTrasGuardarFicha is { } detalle) Detalles[comando.Id] = detalle;
            return ResultadoFicha;
        }

        /// <summary>Lo que responde el servidor al guardar la nota interna; con éxito, la relectura trae la nota y otra Version.</summary>
        public Result ResultadoNota { get; set; } = Result.Exito();

        private Result GuardarNota(Guid id, string? notas)
        {
            if (ResultadoNota.EsFallido) return ResultadoNota;
            Detalles[id] = Detalles[id] with { Notas = notas, Version = Guid.NewGuid() };
            return ResultadoNota;
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
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
        Services.AddLocalization();
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
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, new DateOnly(2026, 1, 9), peor, documentos,
            CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));

    private static IElement Boton(IRenderedComponent<CentroDetalle> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    private static IElement BotonAcordeon(IRenderedComponent<AcordeonAsignacionesCentro> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    // ── Canal de plataforma: la dirección se abre, no se copia ────────────

    private static CanalGestionResumenDto CanalPlataforma(string? url) =>
        new(Guid.NewGuid(), TipoCanalGestion.Plataforma, "Gestión general", true, Guid.NewGuid(), "Nalanda",
            url, null, null, null, true, Guid.NewGuid());

    /// <summary>
    /// El Gestor CAE abre el portal en pestaña nueva y pega allí las
    /// credenciales: la dirección es un enlace que se abre, no un texto con
    /// «Copiar». Sin <c>noopener</c> la pestaña abierta tendría acceso a
    /// <c>window.opener</c>, la de TALVEG.
    /// </summary>
    /// <summary>
    /// P1-X2: un Centro sin gestión CAE se rotula como tal, en tono neutro, sin
    /// anillo de cumplimiento (un 0 % o un 100 % afirmaría algo que no se
    /// mide) y con la explicación en lugar de los canales de gestión.
    /// </summary>
    [Fact]
    public void Las_pestanas_disponibles_respetan_el_orden_de_la_maqueta_y_cuentan_Trabajadores_distintos()
    {
        var id = Guid.NewGuid();
        var trabajador = Guid.NewGuid();
        var otroTrabajador = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte",
            vencidas: [
                Incidencia("PRL") with { TrabajadorId = trabajador },
                Incidencia("Aptitud") with { TrabajadorId = trabajador },
                Incidencia("Seguro", AmbitoCausa.Empresa) with { TrabajadorId = Guid.NewGuid() },
                Incidencia("Sin trabajador") with { TrabajadorId = null }
            ],
            proximas: [
                Incidencia("Renovación") with { TrabajadorId = trabajador },
                Incidencia("Formación") with { TrabajadorId = otroTrabajador }
            ]);

        var cut = Renderizar(id);

        cut.FindAll("[role=tab]")
            .Select(t => t.ChildNodes.OfType<IText>().First(n => !string.IsNullOrWhiteSpace(n.Data)).Data.Trim())
            .Should().Equal("Trabajadores", "Documentación Empresa", "Requisitos", "Visitas", "Ficha", "Historial");
        var primera = cut.FindAll("[role=tab]")[0];
        primera.GetAttribute("aria-selected").Should().Be("true");
        primera.QuerySelector(".pestanas-contador")!.TextContent.Trim().Should().Be("2 trabajadores con incidencias");
    }

    [Theory]
    [InlineData(0, "0 trabajadores con incidencias", false)]
    [InlineData(1, "1 trabajador con incidencias", true)]
    public void El_contador_de_Trabajadores_conserva_la_glosa_en_cero_y_singular(int cantidad, string texto, bool enAlerta)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte",
            vencidas: cantidad == 0 ? [] : [Incidencia("PRL") with { TrabajadorId = Guid.NewGuid() }]);

        var cut = Renderizar(id);

        var contador = cut.FindAll("[role=tab]")[0].QuerySelector(".pestanas-contador")!;
        contador.TextContent.Trim().Should().Be(texto);
        contador.ClassList.Contains("pestanas-contador-alerta").Should().Be(enAlerta);
    }

    [Fact]
    public async Task Cambiar_de_pestana_conserva_la_busqueda_y_el_estado_en_la_URL()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"/centros/{id}?q=Marta&estado=Vencido");
        var cut = Renderizar(id);

        await cut.Find("[role=tab][aria-controls$='-visitas']").ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().Contain("pestana=visitas").And.Contain("q=Marta").And.Contain("estado=Vencido");
        await cut.FindAll("[role=tab]")[0].ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().NotContain("pestana=").And.Contain("q=Marta").And.Contain("estado=Vencido");
    }

    [Fact]
    public void La_url_visitas_abre_Visitas_y_muestra_todas_las_visitas_por_fecha_de_inicio()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Visitas[id] = [
            new(Guid.NewGuid(), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), 1),
            new(Guid.NewGuid(), new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 22), 3)
        ];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=visitas");

        var cut = Renderizar(id);

        cut.Find("[role=tab][aria-controls$='-visitas']").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[role=tabpanel] .elemento-lista-lateral-etiqueta").Select(v => v.TextContent.Trim())
            .Should().Equal("10/10/2026 – 22/10/2026", "12/10/2026 – 14/10/2026");
        cut.FindAll("[role=tabpanel] .elemento-lista-lateral-detalles").Select(v => v.TextContent.Trim())
            .Should().Equal("3 trabajadores", "1 trabajador");
        cut.FindAll(".centro-visitas-lista .boton-360").Select(v => v.GetAttribute("href"))
            .Should().Equal(mediador.Visitas[id].OrderBy(v => v.FechaInicio).Select(v => $"/visitas/{v.VisitaId}"));
        cut.Find("[role=tabpanel] [hidden]").Should().NotBeNull();
    }

    [Fact]
    public void Un_valor_desconocido_de_pestana_abre_Trabajadores()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=desconocida");

        var cut = Renderizar(id);

        cut.FindAll("[role=tab]")[0].GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[role=tabpanel] > div").HasAttribute("hidden").Should().BeFalse();
    }

    [Theory]
    [InlineData("trabajadores", false)]
    [InlineData("visitas", true)]
    public void La_barra_de_filtros_solo_se_ve_en_Trabajadores(string pestana, bool oculta)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana={pestana}");

        var cut = Renderizar(id);

        cut.Find(".barra-filtros").HasAttribute("hidden").Should().Be(oculta);
    }

    [Theory]
    [InlineData("trabajadores")]
    [InlineData("visitas")]
    public void La_tarjeta_de_canales_pertenece_a_Ficha_y_no_al_lateral(string pestana)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana={pestana}");

        var cut = Renderizar(id);

        cut.Find("aside[data-pieza=lateral]").TextContent.Should()
            .NotContain("Canales de gestión").And.NotContain("Próxima visita");
        cut.Find(".centro-ficha-canales").HasAttribute("hidden").Should().BeTrue();
        cut.FindAll("[role=tab]").Single(t => t.GetAttribute("aria-selected") == "true")
            .TextContent.Trim().Should().StartWith(pestana == "visitas" ? "Visitas" : "Trabajadores");
    }

    [Fact]
    public void Visitas_sin_datos_muestra_EstadoVacio()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=visitas");

        var cut = Renderizar(id);

        cut.Find("[role=tabpanel] > .estado-vacio h3").TextContent.Should().Be("Sin visitas programadas");
        cut.Find("[role=tabpanel]").TextContent.Should().Contain("Este centro no tiene visitas en curso ni futuras.");
    }

    [Fact]
    public async Task Cambiar_de_pestana_actualiza_la_url_sin_recargar_la_lista_de_Trabajadores()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var cut = Renderizar(id);
        var lista = cut.FindComponent<AcordeonAsignacionesCentro>().Instance;
        var consultas = mediador.Enviadas.Count;
        var navegador = Services.GetRequiredService<NavigationManager>();

        await cut.Find("[role=tab][aria-controls$='-visitas']").ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().Contain("pestana=visitas");
        cut.Find("[role=tab][aria-controls$='-visitas']").GetAttribute("aria-selected").Should().Be("true");
        await cut.FindAll("[role=tab]")[0].ClickAsync(new MouseEventArgs());

        navegador.Uri.Should().NotContain("pestana=");
        cut.FindComponent<AcordeonAsignacionesCentro>().Instance.Should().BeSameAs(lista);
        mediador.Enviadas.Should().HaveCount(consultas);
        cut.Find("[role=tabpanel] > div").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void Un_centro_sin_gestion_cae_dice_que_no_la_requiere_y_no_muestra_cumplimiento_ni_verde()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Almacén Sur") with { GestionCae = ModalidadGestionCae.SinGestionCae };
        mediador.Resumenes[id] = Resumen(id, "Almacén Sur", porcentaje: null, estado: EstadoCentro.SinGestionCae);

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.Find(".centro360-indicadores").TextContent.Should().Contain("No requiere gestión CAE"));
        cut.Find(".centro360-indicadores .badge").ClassList.Should().Contain("badge-neutro").And.NotContain("badge-exito");
        // Por componente, no por clase: con porcentaje null el anillo pinta «—»
        // sin su svg, y la clase no distinguiría si la página lo monta o no.
        cut.FindComponents<AnilloCumplimiento>().Should().BeEmpty();
        cut.FindAll(".cabecera-identidad-anillo").Should().BeEmpty("sin anillo la tarjeta no reserva su hueco");
        cut.Find(".centro360-sin-gestion-cae").TextContent.Should().Contain("no requiere gestión CAE");
    }

    /// <summary>
    /// Centro 360 usa la maquetación compartida de las fichas 360: la cabecera
    /// de identidad en tarjeta (con qué es, «Centro», sobre el nombre), el
    /// cuerpo con lateral y las dos cajas del lateral como tarjetas compactas,
    /// con «Gestionar →» en la cabecera de «Canales de gestión». Observa la
    /// estructura del DOM, no el estilo: cómo se ve se comprueba en el navegador.
    /// </summary>
    [Fact]
    public void La_ficha_usa_la_cabecera_en_tarjeta_y_el_cuerpo_con_lateral_de_las_fichas_360()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: 61);

        var cut = Renderizar(id);

        var cabecera = cut.Find("header.cabecera-identidad[data-pieza='cabecera-identidad']");
        cabecera.QuerySelector(".cabecera-identidad-kicker").Should().BeNull("Centro 360 del 17-10 no lleva rótulo sobre el nombre");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Centro Norte");
        // Bajo el nombre solo el Cliente (nombre = consulta rápida, icono 360 = su página), los Trabajadores y la
        // pastilla de estado: ni la Empresa ni las pastillas de motivos.
        var datos = cabecera.QuerySelector(".cabecera-identidad-datos")!;
        datos.TextContent.Should().Contain("Refrielectric S.A.").And.NotContain("Ibertec GmbH").And.NotContain("Empresa:");
        datos.QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        datos.QuerySelectorAll("a").Should().ContainSingle("el nombre ya no es un enlace (abre la consulta rápida): solo el icono 360 lo es")
            .Which.ClassList.Should().Contain("boton-360");
        datos.QuerySelectorAll(".badge").Should().HaveCount(1, "una sola pastilla: el estado del Centro");
        cabecera.QuerySelector(".cabecera-identidad-anillo [role=img]").Should().NotBeNull();
        cut.FindAll("h1").Should().ContainSingle("la ficha tiene un solo título de página");

        var lateral = cut.Find(".cuerpo-con-lateral > aside[data-pieza='lateral']");
        lateral.GetAttribute("aria-label").Should().Be("Contexto del centro");
        var tarjetas = lateral.QuerySelectorAll(":scope > .tarjeta-compacta[data-pieza='tarjeta']");
        tarjetas.Select(t => t.QuerySelector(".tarjeta-titulo")!.TextContent.Trim())
            .Should().Equal("Información");
        tarjetas[0].TextContent.Should().Contain("Refrielectric S.A.").And.Contain("CN-01").And.Contain("Pol. Norte 4");
        tarjetas[0].QuerySelector(".tarjeta-acciones button")!.TextContent.Trim().Should().Be("Editar →");
        cut.Find(".centro-ficha-canales .tarjeta-acciones button").TextContent.Trim().Should().Be("Gestionar →");

        cut.Find(".cuerpo-con-lateral-principal").QuerySelector("aside").Should().BeNull();
        cut.FindAll(".centro360-cuerpo, .centro360-lateral, .centro360-tarjeta-lateral, .cabecera-pagina")
            .Should().BeEmpty("la maquetación propia de la ficha se sustituyó por las piezas compartidas");
    }

    /// <summary>
    /// Por rol: la tarjeta «Información» y los datos de la cabecera los ve todo rol (el Cliente empresarial y su
    /// consulta rápida también); «Editar →» y «Programar visita» son escritura y a Consulta no se le pintan.
    /// </summary>
    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("Consulta", false)]
    public void La_tarjeta_Informacion_y_la_cabecera_respetan_el_rol_de_escritura(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");

        var cut = Renderizar(id);

        var informacion = cut.FindAll("aside[data-pieza=lateral] .tarjeta-compacta")
            .Single(t => t.QuerySelector(".tarjeta-titulo")!.TextContent.Trim() == "Información");
        informacion.TextContent.Should().Contain("Refrielectric S.A.").And.Contain("CN-01");
        informacion.QuerySelectorAll(".tarjeta-acciones button").Any(b => b.TextContent.Trim() == "Editar →").Should().Be(escribe);
        cut.Find(".cabecera-identidad-datos button.enlace-nombre-fila").TextContent.Trim().Should().Be("Refrielectric S.A.");
        cut.FindAll(".cabecera-identidad-acciones button").Any(b => b.TextContent.Trim() == "Programar visita").Should().Be(escribe);
    }

    /// <summary>
    /// Maqueta del 20-10, lista de Trabajadores: la Empresa empleadora acompaña al nombre, los filtros son «Todos» y «Bloquea acceso»
    /// y la baja en lote no se ofrece. El lateral lleva «Alta» y ya no «Empresa»; «Visitas» sin visitas no enseña un «0».
    /// </summary>
    [Fact]
    public void La_ficha_sigue_la_maqueta_del_20_10_empleadora_filtros_alta_y_sin_cero_en_Visitas()
    {
        this.ConRolDeEscritura();
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte") with { CreadoEnUtc = new DateTime(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc) };
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        var juan = Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido));
        mediador.Asignaciones[id] = [juan];
        mediador.Empleadores[id] = [new(juan.TrabajadorId, "Juan Pérez", "Montajes Orvelta S.L.", juan.FechaAlta)];

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.Find("[data-empleador]").TextContent.Should().Be("Montajes Orvelta S.L."));
        cut.FindAll(".franja-estado [data-estado]").Select(e => e.GetAttribute("data-estado")).Should().Equal("Bloqueado");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Dar de baja seleccionados");
        var lateral = cut.Find(".centro360-lateral, aside").TextContent;
        lateral.Should().Contain("Alta").And.Contain("05/03/2026").And.NotContain("Ibertec GmbH");
        cut.FindAll("[role=tab]").Single(p => p.TextContent.StartsWith("Visitas")).TextContent.Trim().Should().Be("Visitas");
    }

    /// <summary>
    /// Maqueta del 20-10: los documentos de Empresa con incidencia no abren «Trabajadores» en un bloque «Empresa»; su cifra va en la
    /// pestaña «Documentación Empresa» (también la causa de plataforma con Estado null) y las causas de Trabajador no cuentan.
    /// </summary>
    [Fact]
    public void Las_causas_de_ambito_Empresa_van_en_el_contador_de_Documentacion_Empresa_y_no_en_el_acordeon()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", vencidas:
        [
            Incidencia("Seguro de responsabilidad civil", AmbitoCausa.Empresa) with { TrabajadorId = null },
            Incidencia("Rechazo en plataforma: falta la firma", AmbitoCausa.Empresa) with { Estado = null, TrabajadorId = null },
            Incidencia("Formación de Juan", AmbitoCausa.Trabajador)
        ]);

        var cut = Renderizar(id);

        var pestana = cut.WaitForElements("[role=tab]").Single(p => p.TextContent.Contains("Documentación Empresa"));
        pestana.TextContent.Should().Contain("2", "los dos documentos de Empresa con incidencia, no el de Trabajador");
        cut.FindAll("[data-bloque-empresa]").Should().BeEmpty("el bloque «Empresa» ya no abre la pestaña Trabajadores");
    }

    [Fact]
    public void Un_centro_con_gestion_cae_conserva_el_anillo_y_no_se_rotula_sin_gestion()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.FindComponents<AnilloCumplimiento>().Should().NotBeEmpty());
        cut.Markup.Should().NotContain("No requiere gestión CAE");
        cut.FindAll(".centro360-sin-gestion-cae").Should().BeEmpty();
    }

    [Fact]
    public void La_direccion_del_portal_es_un_enlace_que_abre_pestana_nueva_y_no_un_boton_de_copiar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.Canales[id] = [CanalPlataforma("app.twind.io/login")];

        var cut = Renderizar(id);

        var enlace = cut.FindAll("a").Where(a => a.TextContent.Contains("Abrir portal"))
            .Should().ContainSingle("hay un canal de plataforma con dirección").Subject;
        enlace.GetAttribute("class").Should().Contain("boton-secundario");
        enlace.GetAttribute("href").Should().Be("https://app.twind.io/login");
        enlace.GetAttribute("target").Should().Be("_blank");
        enlace.GetAttribute("rel").Should().Contain("noopener").And.Contain("noreferrer");
        cut.FindAll("[data-caja=canales-centro] .boton-copiar").Select(b => b.TextContent.Trim()).Should().BeEquivalentTo(
            ["Copiar usuario", "Copiar contraseña"], "la dirección del portal ya no se copia: solo las credenciales");
    }

    /// <summary>La URL es texto libre: un esquema que no sea http(s) no puede llegar a un href.</summary>
    [Fact]
    public void Una_direccion_que_no_es_http_s_se_ensena_como_texto_y_no_como_enlace()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.Canales[id] = [CanalPlataforma("javascript:alert(1)")];

        var cut = Renderizar(id);

        cut.Markup.Should().Contain("javascript:alert(1)", "el dato se sigue viendo, para poder corregirlo");
        cut.FindAll("a").Should().NotContain(a => (a.GetAttribute("href") ?? "").StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
        cut.FindAll("a").Should().NotContain(a => a.TextContent.Contains("Abrir portal"));
    }

    // ── Canal de plataforma: copiar usuario y contraseña (clic explícito) ──

    private (Guid CentroId, CanalGestionResumenDto Canal, MediatorFalso Mediador) CentroConCanal(CanalGestionResumenDto canal)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.Canales[id] = [canal];
        return (id, canal, mediador);
    }

    /// <summary>DEC-53/DEC-62: abrir la pantalla no pide ni pinta la credencial; solo el clic.</summary>
    [Fact]
    public void Abrir_la_pantalla_no_pide_la_credencial_ni_la_pinta()
    {
        var (id, canal, mediador) = CentroConCanal(CanalPlataforma("app.twind.io"));
        mediador.Credenciales[(id, canal.Id)] = new("usuario.secreto", "clave-secreta-123");

        var cut = Renderizar(id);

        mediador.Enviadas.OfType<ObtenerCredencialCanalGestionQuery>().Should().BeEmpty();
        cut.Markup.Should().NotContain("usuario.secreto").And.NotContain("clave-secreta-123");
    }

    [Fact]
    public void Un_canal_de_plataforma_sin_credenciales_no_ofrece_copiar_nada()
    {
        var (id, _, _) = CentroConCanal(CanalPlataforma("app.twind.io") with { TieneCredenciales = false });

        Renderizar(id).FindAll("[data-caja=canales-centro] .boton-copiar").Should().BeEmpty("sin credenciales no hay nada que copiar");
    }

    [Fact]
    public void Un_canal_de_correo_no_ofrece_copiar_usuario_ni_contrasena()
    {
        var correo = new CanalGestionResumenDto(Guid.NewGuid(), TipoCanalGestion.Email, "Gestión general", true, null, null,
            null, "gestion@ejemplo.test", null, null, true, Guid.NewGuid());
        var (id, _, _) = CentroConCanal(correo);

        Renderizar(id).FindAll("[data-caja=canales-centro] .boton-copiar").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Copiar usuario", "Copiar contraseña"]);
    }

    /// <summary>
    /// Decisión del propietario (2026-09-23): los secretos del Tenant solo los
    /// leen los roles con escritura. A Consulta el servidor le devuelve null, así
    /// que ofrecerle «Copiar contraseña» sería un botón que no copia nada. El
    /// control positivo es el mismo canal con un rol de gestión.
    /// </summary>
    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("GestorCae", true)]
    public void Copiar_usuario_y_contrasena_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var (id, _, _) = CentroConCanal(CanalPlataforma("app.twind.io"));

        var botones = Renderizar(id).FindAll("[data-caja=canales-centro] .boton-copiar").Select(b => b.TextContent.Trim()).ToList();

        if (seOfrece)
            botones.Should().Contain(["Copiar usuario", "Copiar contraseña"]);
        else
            botones.Should().NotContain(["Copiar usuario", "Copiar contraseña"]);
    }

    [Theory]
    [InlineData("Copiar usuario", "usuario.secreto")]
    [InlineData("Copiar contraseña", "clave-secreta-123")]
    public async Task El_clic_pide_la_credencial_del_canal_y_del_centro_y_la_manda_al_portapapeles(string boton, string esperado)
    {
        var (id, canal, mediador) = CentroConCanal(CanalPlataforma("app.twind.io"));
        mediador.Credenciales[(id, canal.Id)] = new("usuario.secreto", "clave-secreta-123");
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(id);

        await cut.FindAll("[data-caja=canales-centro] .boton-copiar").Single(b => b.TextContent.Trim() == boton).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerCredencialCanalGestionQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerCredencialCanalGestionQuery(id, canal.Id));
        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal(esperado);
        cut.Markup.Should().NotContain(esperado, "el valor va al portapapeles, no a la pantalla");
    }

    [Fact]
    public async Task Sin_credencial_guardada_el_clic_avisa_y_no_copia()
    {
        var (id, _, mediador) = CentroConCanal(CanalPlataforma("app.twind.io"));
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(id);

        await cut.FindAll("[data-caja=canales-centro] .boton-copiar").Single(b => b.TextContent.Trim() == "Copiar contraseña").ClickAsync(new MouseEventArgs());

        modulo.Invocations.Should().BeEmpty("no había nada que copiar");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje.Contains("No hay ninguna contraseña"));
    }

    // P1-I1: sin 2FA, Application no entrega la credencial y la pantalla lleva
    // a activarlo, con el motivo para que la página de 2FA explique por qué.
    [Theory]
    [InlineData("Copiar usuario")]
    [InlineData("Copiar contraseña")]
    public async Task Sin_2FA_el_clic_lleva_a_configurar_el_2FA_y_no_copia(string boton)
    {
        var (id, canal, mediador) = CentroConCanal(CanalPlataforma("app.twind.io"));
        mediador.Credenciales[(id, canal.Id)] = new("usuario.secreto", "clave-secreta-123");
        mediador.SinDobleFactor = true;
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(id);

        await cut.FindAll("[data-caja=canales-centro] .boton-copiar").Single(b => b.TextContent.Trim() == boton).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerCredencialCanalGestionQuery>().Should().ContainSingle();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/cuenta/configurar-2fa?motivo=credenciales&returnUrl=%2F",
            "vuelve a la ruta en la que estaba la ficha (en bUnit, la raíz)");
        modulo.Invocations.Should().BeEmpty("sin 2FA no hay nada que copiar");
    }

    // ── El mockup ─────────────────────────────────────────────────────────

    /// <summary>
    /// La cabecera del mockup: nombre, las dos contrapartes, la ventana de la
    /// próxima visita y el anillo con el porcentaje que devuelve la consulta.
    /// El Cliente empresarial se rotula «Cliente» en pantalla (CONTRATO_TERMINOLOGIA § 3.2).
    /// </summary>
    [Fact]
    public void La_cabecera_nombra_el_centro_sus_contrapartes_y_los_Trabajadores_asignados()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: 61);
        mediador.Visitas[id] = [new(Guid.NewGuid(), new DateOnly(2026, 8, 21), new DateOnly(2026, 8, 23), 6)];

        var cut = Renderizar(id);

        cut.Find("h1").TextContent.Trim().Should().Be("Centro Norte");
        var entradilla = cut.Find(".cabecera-identidad-datos").TextContent;
        entradilla.Should().Contain("Refrielectric S.A.")
            .And.Contain("0 trabajadores asignados").And.NotContain("Empresa").And.NotContain("Ibertec GmbH").And.NotContain("21/08–23/08");
        // Decisión de rótulo del 2026-10-09 (CONTRATO_TERMINOLOGIA § 3.2): el Cliente empresarial se rotula
        // «Cliente» en todas las pantallas; el rótulo largo no vuelve.
        entradilla.Should().NotContain("Cliente empresarial", "la pantalla dice «Cliente», no el rótulo largo");
        cut.FindComponent<AnilloCumplimiento>().Instance.Detalle.Should().BeNull();
        // El anillo va a la izquierda de la identidad, como en las cuatro páginas 360
        // (decisión del propietario 2026-09-24), no entre las acciones.
        cut.FindComponent<AnilloCumplimiento>().Instance.Porcentaje.Should().Be(61);
        cut.FindComponent<AnilloCumplimiento>().Instance.Etiqueta.Should().Be("61% de cumplimiento — documentos al día / requeridos entre los trabajadores de este centro");
        cut.Find(".cabecera-identidad-acciones").QuerySelector("[role=img]").Should().BeNull();
    }

    /// <summary>
    /// Cada contraparte de la cabecera tiene dos caminos (decisión del
    /// propietario 2026-09-22): el nombre es un enlace real a su página 360
    /// —se puede abrir en otra pestaña— y el botón 360 de al lado abre su
    /// panel de 520 px sin salir de Centro 360, que es lo que antes hacía el
    /// nombre.
    /// </summary>
    [Fact]
    public void Las_contrapartes_de_la_cabecera_enlazan_a_sus_paginas_360_y_sus_botones_abren_el_panel()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var detalle = Detalle(id, "Centro Norte");
        mediador.Detalles[id] = detalle;
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: 61);

        var cut = Renderizar(id);
        var entradilla = cut.Find(".cabecera-identidad-datos");

        // Centro 360 del 17-10 invierte los dos caminos: el NOMBRE abre la consulta rápida y el icono 360 es el enlace a la
        // página. La Empresa ya no está en la cabecera (la Empresa del Centro vive en «Documentación del Centro»).
        entradilla.QuerySelectorAll("a").Select(a => (a.GetAttribute("aria-label"), a.GetAttribute("href"))).Should().Equal(
            ("Abrir la ficha 360 de Refrielectric S.A.", $"/clientes/{detalle.ClienteId}"));

        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        entradilla.QuerySelector("button.enlace-nombre-fila")!.Click();
        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Cliente, detalle.ClienteId, "Refrielectric S.A.", "informacion"));
        entradilla.QuerySelectorAll("button").Should().ContainSingle("solo el nombre del Cliente es botón; el icono 360 es un enlace");
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
    /// Lo que la ficha ofrece para escribir acaba en un ICommand que AutorizacionEscrituraBehavior deniega al rol
    /// Consulta (CrearVisita, EnviarReclamacion, CrearAsignaciones, DarDeBajaAsignaciones, CrearDocumento): a Consulta
    /// no se le pinta, en vez de dejarle pulsar y fallar al guardar. Lo que es lectura se conserva, y eso hace de
    /// barrera: la ausencia se afirma sobre una ficha que cargó entera. El control positivo es la misma ficha con un
    /// rol de escritura.
    /// </summary>
    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("GestorCae", true)]
    public async Task Las_escrituras_de_la_ficha_solo_se_ofrecen_a_los_roles_con_escritura(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", estado: EstadoCentro.Faltante);
        var bloqueado = Asignado("Juan Pérez", EstadoDocumento.Faltante, Documento("Formación PRL", EstadoDocumento.Faltante));
        mediador.Asignaciones[id] = [bloqueado];
        mediador.Bloqueos[id] =
        [
            new(id, "Centro Norte", bloqueado.TrabajadorId, "Juan Pérez", Guid.NewGuid(), "Formación PRL")
        ];
        mediador.PorReclamar[id] =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Formación PRL", new DateOnly(2026, 8, 2), EstadoDocumento.Vencido)
        ];

        var cut = Renderizar(id);
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.Find(".fila-relacion-desplegar").ClickAsync(new MouseEventArgs());

        // Barrera: la lectura sigue ahí para los dos roles.
        var menu = cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim()).ToList();
        menu.Should().Contain("Generar informe del centro");
        cut.Find(".motivo-bloqueo-texto").TextContent.Should().Contain("Formación PRL");
        cut.Find(".identidad-documento-plataforma").TextContent.Should().Contain("Formación PRL");

        var botones = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        var esperados = seOfrecen ? 1 : 0;
        botones.Count(b => b == "Programar visita").Should().Be(esperados);
        botones.Count(b => b == "+ Asignar trabajador").Should().Be(esperados);
        botones.Should().NotContain("Dar de baja seleccionados", "la maqueta del 20-10 lo quita en Centro 360 («no decidido»), también al rol con escritura");
        menu.Count(i => i == "Reclamar documentación (1)").Should().Be(esperados);
        cut.FindAll(".motivo-bloqueo-pedir").Should().HaveCount(esperados, "«Gestionar» (antes «Pedir») abre el flujo que acaba en un envío de reclamación");
        cut.FindAll(".identidad-documento-plataforma button").Should().BeEmpty("el desglose de plataforma es de lectura");
    }

    /// <summary>
    /// Las otras dos escrituras del acordeón, que dependen de datos que la ficha no siempre trae: «Asignar» a quien
    /// viene en la próxima Visita sin asignación (CrearAsignacionCommand) y el nombre de una incidencia de la Empresa,
    /// que abre el mismo Drawer de gestión que su «Gestionar».
    /// </summary>
    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("GestorCae", true)]
    public void Asignar_desde_la_visita_y_el_nombre_de_la_incidencia_de_Empresa_solo_actuan_con_escritura(string rol, bool seOfrecen)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] = [];
        mediador.SinAsignacion[id] = [new(Guid.NewGuid(), "Bea Alonso Ruiz")];

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, id)
            .Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.VisitaId, Guid.NewGuid())
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Ibertec GmbH")
            .Add(a => a.IncidenciasEmpresa, [Incidencia("Seguro de responsabilidad civil", AmbitoCausa.Empresa)]));

        // Barrera: el aviso de la Visita y la incidencia de la Empresa se leen con cualquier rol.
        cut.Find(".aviso-visita-sin-asignacion").TextContent.Should().Contain("Bea Alonso Ruiz");
        cut.Find(".celda-documento-nombre").TextContent.Trim().Should().Be("Seguro de responsabilidad civil");

        var esperados = seOfrecen ? 1 : 0;
        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Asignar").Should().Be(esperados);
        cut.FindAll(".celda-documento-nombre button").Should().HaveCount(esperados);
        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Gestionar").Should().Be(esperados);
    }

    /// <summary>
    /// «Bloqueado» es un estado del Trabajador (2026-10-03): el Centro 360 dice cuántos Trabajadores están bloqueados EN ESE Centro y
    /// cuál es, pero no rotula al Centro «Acceso bloqueado» por un documento. La consulta lleva el Centro de la página.
    /// </summary>
    [Fact]
    public void Un_Trabajador_bloqueado_se_dice_en_la_cabecera_y_en_su_fila_y_el_Centro_no_se_rotula_bloqueado()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", estado: EstadoCentro.Faltante);
        var bloqueado = Asignado("Juan Pérez", EstadoDocumento.Faltante, Documento("Formación PRL", EstadoDocumento.Faltante));
        var libre = Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente, new DateOnly(2027, 4, 17)));
        mediador.Asignaciones[id] = [bloqueado, libre];
        mediador.Bloqueos[id] =
        [
            new(id, "Centro Norte", bloqueado.TrabajadorId, "Juan Pérez", Guid.NewGuid(), "Formación PRL")
        ];

        var cut = Renderizar(id);

        mediador.Enviadas.OfType<ObtenerDocumentacionBloqueantePendienteQuery>().Should().ContainSingle()
            .Which.CentroId.Should().Be(id, "el detalle por Trabajador es el de ESTE Centro, no el de todos los visibles");
        // Centro 360 del 17-10: la cabecera ya no lleva la pastilla «N trabajadores bloqueados»; el bloqueo se dice en la fila.
        cut.Find(".cabecera-identidad-datos").TextContent.Should().NotContain("bloqueado");
        cut.Markup.Should().NotContain("Acceso bloqueado").And.NotContain("Bloqueo de la plataforma CAE",
            "un documento de Trabajador no bloquea al Centro: el Centro está en Faltante");
        var filaBloqueada = cut.FindAll(".lista-trabajadores-acordeon .fila-relacion").Single(b => b.TextContent.Contains("Juan Pérez"));
        filaBloqueada.TextContent.Should().Contain("Bloqueado");
        cut.FindAll(".lista-trabajadores-acordeon .fila-relacion").Single(b => b.TextContent.Contains("Marco Vila"))
            .TextContent.Should().NotContain("Bloqueado", "el otro Trabajador no tiene ningún bloqueo en este Centro");
    }

    /// <summary>
    /// El acordeón de la lista de Centros no recibe los bloqueos: los carga él, con el Centro de su fila, y la fila del
    /// Trabajador dice lo mismo que en Centro 360. Si la lectura falla, la fila se pinta sin el distintivo.
    /// </summary>
    [Fact]
    public void El_acordeon_sin_Bloqueos_los_carga_para_su_Centro_y_marca_al_Trabajador()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var bloqueado = Asignado("Juan Pérez", EstadoDocumento.Faltante, Documento("Formación PRL", EstadoDocumento.Faltante));
        var libre = Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente, new DateOnly(2027, 4, 17)));
        mediador.Asignaciones[id] = [bloqueado, libre];
        mediador.Bloqueos[id] =
        [
            new(id, "Centro Norte", bloqueado.TrabajadorId, "Juan Pérez", Guid.NewGuid(), "Formación PRL")
        ];

        var cut = RenderizarAcordeon(id);

        cut.WaitForAssertion(() =>
            cut.FindAll(".lista-trabajadores-acordeon .fila-relacion").Single(b => b.TextContent.Contains("Juan Pérez"))
                .TextContent.Should().Contain("Bloqueado"));
        cut.FindAll(".lista-trabajadores-acordeon .fila-relacion").Single(b => b.TextContent.Contains("Marco Vila"))
            .TextContent.Should().NotContain("Bloqueado");
        mediador.Enviadas.OfType<ObtenerDocumentacionBloqueantePendienteQuery>().Should().ContainSingle()
            .Which.CentroId.Should().Be(id);
    }

    /// <summary>
    /// Lo único que aún marca «Bloqueado» al Centro es la plataforma del Cliente empresarial (D-7), y se rotula así, no como
    /// «Acceso bloqueado», que sugeriría el bloqueo de un Trabajador.
    /// </summary>
    [Fact]
    public void El_bloqueo_de_la_plataforma_del_Cliente_empresarial_se_rotula_como_tal()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", estado: EstadoCentro.Bloqueado);

        var cut = Renderizar(id);

        cut.Markup.Should().Contain("Bloqueo de la plataforma CAE").And.NotContain("Acceso bloqueado");
        cut.Markup.Should().NotContain("trabajador bloqueado", "sin ningún Trabajador bloqueado la cabecera no dice nada de Trabajadores");
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
    /// Urgente y Próximo se rotulan igual («Por vencer»): el filtro ofrece una sola opción con ese texto, y
    /// elegirla deja a la vista al Trabajador cuyo peor estado sea uno de los dos.
    /// </summary>
    [Fact]
    public void El_filtro_de_estado_ofrece_un_solo_Por_vencer_que_trae_lo_urgente_y_lo_proximo()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Urgente, Documento("Formación PRL", EstadoDocumento.Urgente, new DateOnly(2026, 10, 12))),
            Asignado("Ana Ruiz", EstadoDocumento.Proximo, Documento("Formación PRL", EstadoDocumento.Proximo, new DateOnly(2026, 11, 2))),
            Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente, new DateOnly(2027, 4, 17)))
        ];

        var cut = RenderizarAcordeon(id);
        var opciones = cut.FindComponent<FranjaEstado>().Instance.Opciones;
        opciones.Select(o => o.Texto).Should().OnlyHaveUniqueItems();
        var porVencer = opciones.Single(o => o.Texto == "Por vencer");
        cut.Render(p => p
            .Add(a => a.CentroId, id)
            .Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.EmpresaId, Guid.NewGuid())
            .Add(a => a.EmpresaNombre, "Ibertec GmbH")
            .Add(a => a.SeleccionMultiple, true)
            .Add(a => a.MostrarTotales, true)
            .Add(a => a.FiltroEstado, string.Join(",", porVencer.Valores)));

        cut.Markup.Should().Contain("Juan Pérez").And.Contain("Ana Ruiz").And.NotContain("Marco Vila");
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
        totales.Should().Contain("4 exigidos").And.Contain("2 al día").And.Contain("1 vencido").And.Contain("1 pendiente")
            .And.Contain("trabajadores de este centro", "la documentación de la Empresa no entra en esta suma");
    }

    /// <summary>
    /// Revisión 1:1 del 2026-10-21 (Centro ILL-01): «40 exigidos · 10 al día · 0 vencidos · 28 pendientes» no sumaba. «Al día»
    /// incluye los por vencer y deja fuera los sin confirmar: el pie dice las dos cosas para que las partes cuadren.
    /// </summary>
    [Fact]
    public void Los_totales_suman_el_total_con_por_vencer_y_sin_confirmar()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Proximo,
                Documento("Formación PRL", EstadoDocumento.Proximo, new DateOnly(2026, 11, 2)),
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar)),
            Asignado("Nuria Salas", EstadoDocumento.Faltante,
                Documento("Formación específica de centro", EstadoDocumento.Faltante))
        ];

        var cut = RenderizarAcordeon(id);

        var totales = System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-totales-documentales]").TextContent, @"\s+", " ");
        totales.Should().Contain("3 exigidos · 1 al día (de ellos, 1 por vencer) · 0 vencidos · 1 pendiente")
            .And.Contain("sin confirmar o sin validar en la plataforma: 1");
    }

    /// <summary>El listado enseña el vencido que ya tiene renovación; el pie cuenta pares, así que ese vencido no suma.</summary>
    [Fact]
    public void Un_vencido_con_renovacion_vigente_no_cuenta_como_vencido_en_los_totales()
    {
        var id = Guid.NewGuid();
        var vencido = Documento("Formación PRL", EstadoDocumento.Vencido, new DateOnly(2026, 8, 2));
        var renovacion = vencido with { DocumentoId = Guid.NewGuid(), Estado = EstadoDocumento.Vigente, FechaVencimiento = new DateOnly(2027, 8, 2) };
        Registrar(new MediatorFalso()).Asignaciones[id] =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", new DateOnly(2026, 1, 9), EstadoDocumento.Vigente, [vencido, renovacion],
                new FraccionCumplimiento(1, 1))
        ];

        var cut = RenderizarAcordeon(id);

        System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-totales-documentales]").TextContent, @"\s+", " ")
            .Should().Contain("1 exigidos · 1 al día · 0 vencidos · 0 pendientes").And.NotContain("sin validar");
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
                nameof(ObtenerCanalesGestionDeCentroQuery), nameof(ObtenerDocumentacionBloqueantePendienteQuery),
                // Las del acordeón: es hijo de la página y su token es el suyo propio.
                nameof(ObtenerAsignacionesDocumentacionPorCentroQuery),
                nameof(ObtenerTrabajadoresVisitaSinAsignacionQuery),
                // La Empresa empleadora de cada fila (Centro 360 del 20-10).
                nameof(ObtenerTrabajadoresAsignadosDeCentroQuery), nameof(ObtenerDocumentacionVehiculosDeCentroQuery)
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

        // Sin await: el comando está retenido. Se busca por su clase y no por su texto: mientras guarda, el botón del kit
        // (BotonConEspera) cambia su rótulo.
        var primero = cut.Find(".modal-pie .boton-destructivo").ClickAsync(new MouseEventArgs());
        var segundo = cut.Find(".modal-pie .boton-destructivo").ClickAsync(new MouseEventArgs());

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
        // Dos seleccionadas, dos dadas de baja. Antes este caso pedía dos y
        // recibía una, y aun así esperaba éxito: no observaba lo que su nombre
        // afirma. Lo señaló la revisión de Codex.
        var toasts = await EjecutarBajaAsync(new ResultadoBajaLoteDto(2, []));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().Be(TonoToast.Exito);
        toast.Mensaje.Should().Contain("2 trabajador(es) dado(s) de baja");
    }

    /// <summary>
    /// Menos bajas que asignaciones pedidas, y sin errores que lo expliquen. El
    /// handler de hoy añade un error por cada una que no da de baja, así que
    /// este DTO no llega a producirse — pero esa garantía es suya, no del
    /// contrato de <c>ResultadoBajaLoteDto</c>, y mientras el recuento pueda
    /// quedarse corto decir «hecho» afirma un efecto que no consta.
    /// </summary>
    [Fact]
    public async Task Una_baja_que_solo_hizo_parte_no_se_presenta_como_exito_aunque_no_traiga_errores()
    {
        var toasts = await EjecutarBajaAsync(new ResultadoBajaLoteDto(1, []));

        var toast = toasts.Should().ContainSingle().Subject;
        toast.Tono.Should().Be(TonoToast.Advertencia);
        toast.Mensaje.Should().Contain("1 de 2");
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

    /// <summary>
    /// La bandera de «baja en curso» es una sola para el componente, y cambiar
    /// de centro la baja. Si la baja del centro anterior termina DESPUÉS de que
    /// el centro nuevo haya arrancado la suya, su <c>finally</c> apagaba la
    /// bandera del nuevo y dejaba su modal abierta a un segundo envío. Lo
    /// señaló la revisión de Codex: el caso de doble clic no cruzaba de centro,
    /// así que no ejercitaba esta reentrada.
    /// </summary>
    [Fact]
    public async Task La_baja_del_centro_anterior_al_terminar_no_reabre_la_del_centro_nuevo_a_un_segundo_envio()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var deA = Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido));
        var deB = Asignado("Marco Vila", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido));
        var bajaDeA = new TaskCompletionSource();
        var bajaDeB = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is DarDeBajaAsignacionesCommand c
                ? (c.Ids.Contains(deA.AsignacionId) ? bajaDeA.Task : bajaDeB.Task)
                : null,
            ResultadoBaja = Result.Exito(new ResultadoBajaLoteDto(1, []))
        });
        mediador.Asignaciones[a] = [deA];
        mediador.Asignaciones[b] = [deB];

        var cut = RenderizarAcordeon(a);
        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await BotonAcordeon(cut, "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        // Sin await: el comando de A queda retenido.
        var enA = cut.FindAll("button").Single(x => x.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        cut.Render(p => p
            .Add(x => x.CentroId, b)
            .Add(x => x.CentroNombre, "Planta Zaragoza")
            .Add(x => x.EmpresaId, Guid.NewGuid())
            .Add(x => x.EmpresaNombre, "Ibertec GmbH")
            .Add(x => x.SeleccionMultiple, true)
            .Add(x => x.MostrarTotales, true));

        await cut.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await BotonAcordeon(cut, "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        var enB = cut.FindAll("button").Single(x => x.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        // Ahora termina la de A, con la de B todavía en vuelo.
        await cut.InvokeAsync(bajaDeA.SetResult);
        await enA;

        var otroEnB = cut.FindAll("button").Single(x => x.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>()
            .Count(c => c.Ids.Contains(deB.AsignacionId)).Should().Be(1,
                "la baja del centro anterior no puede reabrir la del nuevo a un segundo envío");

        await cut.InvokeAsync(bajaDeB.SetResult);
        await enB;
        await otroEnB;
    }

    /// <summary>
    /// Los dos filtros de la barra de trabajo viajan en la URL para que un
    /// centro filtrado se pueda enlazar, y quitarlos también los quita de ahí:
    /// una pantalla sin filtro cuya URL siga llevándolos los repone en cuanto
    /// alguien recarga o comparte el enlace. El autor lo dejó declarado como
    /// hueco —comportamiento nuevo sin test—, así que esto lo cierra.
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_viaja_a_la_URL_y_limpiar_filtros_lo_quita()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Asignaciones[id] =
        [
            Asignado("Juan Pérez", EstadoDocumento.Vencido, Documento("Formación PRL", EstadoDocumento.Vencido)),
            Asignado("Marco Vila", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))
        ];
        mediador.Bloqueos[id] =
        [
            new(id, "Centro Norte", mediador.Asignaciones[id][0].TrabajadorId, "Juan Pérez", Guid.NewGuid(), "Formación PRL")
        ];

        var cut = Renderizar(id);
        var navegador = Services.GetRequiredService<NavigationManager>();

        // Maqueta del 20-10: «Todos» y «Bloquea acceso» (el estado «Bloqueado» del Trabajador).
        await cut.Find(".franja-estado [data-estado=Bloqueado]").ClickAsync(new MouseEventArgs());

        navegador.Uri.Should().Contain("estado=Bloqueado",
            "un centro filtrado tiene que poder enlazarse");

        await Boton(cut, "Limpiar filtros").ClickAsync(new MouseEventArgs());

        navegador.Uri.Should().NotContain("estado=",
            "una pantalla sin filtro cuya URL siga llevándolo lo repone al recargar");
        cut.FindAll(".barra-filtros button").Where(b => b.TextContent.Trim() == "Limpiar filtros")
            .Should().BeEmpty("sin filtros puestos no hay nada que limpiar");
    }

    /// <summary>D-16: la ficha del Centro ofrece «Eliminar centro» con confirmación y «Deshacer».</summary>
    [Fact]
    public async Task La_ficha_elimina_el_centro_con_confirmacion_y_ofrece_deshacer()
    {
        this.ConRolDeEscritura();
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Almacén Sur");
        mediador.Resumenes[id] = Resumen(id, "Almacén Sur", porcentaje: 100, estado: EstadoCentro.Vigente);
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Almacén Sur"));

        cut.Find(".menu-acciones-disparador").Click();
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar el centro «Almacén Sur»?");
        mediador.Enviadas.OfType<EliminarCentroCommand>().Should().BeEmpty("hasta confirmar no se elimina nada");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarCentroCommand>().Single().Id.Should().Be(id);
        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);
        mediador.Enviadas.OfType<RestaurarCentroCommand>().Single().Id.Should().Be(id);
    }
}
