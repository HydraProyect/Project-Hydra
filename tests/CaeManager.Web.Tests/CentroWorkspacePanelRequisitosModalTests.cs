using Bunit;
using CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Modal «Requisitos del Centro» rediseñado (maqueta aprobada 2026-11-09): solo lo configurado en el Centro, agrupado
/// por ámbito (Empresa / Trabajador), con lápiz «Editar ajustes» (panel bajo la fila) y papelera «Quitar requisito»;
/// los excluidos, plegados al final; «Añadir requisito» con buscador del catálogo; y los estados vacío y sin coincidencias.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué pinta el panel según lo que devuelve la consulta y qué Commands envía al añadir, ajustar,
/// excluir y quitar. <b>Lo que NO observa:</b> la regla del Command (Application.Tests), el aspecto (CSS) ni el foco real
/// del navegador.
/// </para>
/// </summary>
public class CentroWorkspacePanelRequisitosModalTests : BunitContext
{
    private static readonly Guid RlcId = Guid.NewGuid();
    private static readonly Guid MedicoId = Guid.NewGuid();
    private static readonly Guid SeguroId = Guid.NewGuid();
    private static readonly Guid ExcluidoId = Guid.NewGuid();
    private static readonly Guid CatalogoId = Guid.NewGuid();
    private static readonly Guid ArchivadoId = Guid.NewGuid();

    public CentroWorkspacePanelRequisitosModalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    /// <summary>Requisito que el catálogo pide siempre (Aplica sin fila propia).</summary>
    private static DocumentacionRequeridaCentroDto Global(Guid id, string nombre, AmbitoAplicacion ambito) =>
        new(id, nombre, ambito, EsObligatorioGlobal: true, Aplica: true, Incluido: null, PeriodicidadEspecialMeses: null,
            BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null);

    /// <summary>Requisito que solo exige este Centro (fila propia con Incluido = true).</summary>
    private static DocumentacionRequeridaCentroDto Propio(
        Guid id, string nombre, AmbitoAplicacion ambito, int? meses = null, bool bloquea = false,
        int? tolerancia = null, string? archivoUrl = null, string? archivoNombre = null) =>
        new(id, nombre, ambito, EsObligatorioGlobal: false, Aplica: true, Incluido: true, PeriodicidadEspecialMeses: meses,
            BloqueaAcceso: bloquea, ArchivoUrl: archivoUrl, NombreArchivoOriginal: archivoNombre, ToleranciaDias: tolerancia,
            ToleranciaHeredadaDias: 5);

    private static DocumentacionRequeridaCentroDto Excluido(Guid id, string nombre) =>
        new(id, nombre, AmbitoAplicacion.Trabajador, EsObligatorioGlobal: true, Aplica: false, Incluido: false,
            PeriodicidadEspecialMeses: null, BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null);

    private static DocumentacionRequeridaCentroDto NoConfigurado(Guid id, string nombre, AmbitoAplicacion ambito, bool archivado = false) =>
        new(id, nombre, ambito, EsObligatorioGlobal: false, Aplica: false, Incluido: null, PeriodicidadEspecialMeses: null,
            BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null, Archivado: archivado);

    private sealed class MediatorFalso(CentroDetalleDto detalle, List<DocumentacionRequeridaCentroDto> filas) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? valor = request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
                ObtenerEstadoCentroQuery => null,
                ObtenerCanalesGestionDeCentroQuery => (IReadOnlyList<CanalGestionResumenDto>)[],
                ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
                ObtenerDocumentacionRequeridaDeCentroQuery => filas.ToList(),
                EstablecerDocumentacionRequeridaCentroCommand c => Aplicar(c),
                EliminarDocumentacionRequeridaCentroCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)valor!);
        }

        /// <summary>Como la BD: lo guardado es lo que devuelve la siguiente lectura.</summary>
        private Result Aplicar(EstablecerDocumentacionRequeridaCentroCommand c)
        {
            var indice = filas.FindIndex(f => f.TipoDocumentoId == c.TipoDocumentoId);
            var f = filas[indice];
            filas[indice] = f with
            {
                Incluido = c.Incluido,
                Aplica = c.Incluido,
                PeriodicidadEspecialMeses = c.PeriodicidadEspecialMeses,
                BloqueaAcceso = c.BloqueaAcceso,
                ArchivoUrl = c.ArchivoUrl,
                NombreArchivoOriginal = c.NombreArchivoOriginal,
                ToleranciaDias = c.ToleranciaDias
            };
            return Result.Exito();
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
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no resuelve proveedores.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no sube archivos.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private MediatorFalso _mediador = default!;
    private readonly List<(EntidadWorkspace Tipo, Guid Id)> _avisos = [];

    private IRenderedComponent<CentroWorkspacePanel> Renderizar(params DocumentacionRequeridaCentroDto[] filas)
    {
        var centroId = Guid.NewGuid();
        _mediador = new MediatorFalso(new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid()), [.. filas]);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros?ficha=norte&pestana=requisitos");
        Services.GetRequiredService<ContextWorkspaceService>().OnEntidadGuardada += (tipo, id) => _avisos.Add((tipo, id));

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, "requisitos"));
        cut.WaitForAssertion(() => cut.FindAll(".requisitos-centro").Should().NotBeEmpty());
        return cut;
    }

    private IRenderedComponent<CentroWorkspacePanel> RenderizarCatalogoCompleto() => Renderizar(
        Global(MedicoId, "Reconocimiento médico", AmbitoAplicacion.Trabajador),
        Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa),
        Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa),
        Excluido(ExcluidoId, "Formación PRL específica"),
        NoConfigurado(CatalogoId, "Certificado de aptitud", AmbitoAplicacion.Trabajador),
        NoConfigurado(ArchivadoId, "Recibo de pago RLC/TC1", AmbitoAplicacion.Empresa, archivado: true));

    private static string Nombre(AngleSharp.Dom.IElement fila) => fila.QuerySelector(".requisitos-nombre")!.TextContent.Trim();

    private static AngleSharp.Dom.IElement Lapiz(IRenderedComponent<CentroWorkspacePanel> cut, string nombre) =>
        cut.Find($"button[aria-label='Editar ajustes de {nombre}']");

    private static AngleSharp.Dom.IElement Papelera(IRenderedComponent<CentroWorkspacePanel> cut, string nombre) =>
        cut.Find($"button[aria-label='Quitar requisito {nombre}']");

    private static async Task AbrirBuscadorAsync(IRenderedComponent<CentroWorkspacePanel> cut)
    {
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir requisito").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("#requisitos-busqueda").Should().ContainSingle());
    }

    private static Task BuscarAsync(IRenderedComponent<CentroWorkspacePanel> cut, string termino) =>
        cut.Find("#requisitos-busqueda").InputAsync(new ChangeEventArgs { Value = termino });

    // ------------------------------------------------ solo lo configurado, agrupado

    [Fact]
    public void Solo_se_ven_los_requisitos_configurados_y_los_excluidos_van_plegados_al_final()
    {
        var cut = RenderizarCatalogoCompleto();

        var visibles = cut.FindAll(".requisitos-grupos > .requisitos-grupo .requisitos-fila").Select(Nombre).ToList();
        visibles.Should().BeEquivalentTo(["Reconocimiento médico", "RLC (TC1)", "Seguro de responsabilidad civil"]);
        cut.Markup.Should().NotContain("Certificado de aptitud", "un tipo del catálogo que este Centro no configura solo se ve en el buscador");
        cut.Markup.Should().NotContain("Recibo de pago RLC/TC1", "un tipo archivado que el Centro no exige no se ofrece ni se lista");

        var excluidos = cut.Find("details.requisitos-excluidos");
        excluidos.HasAttribute("open").Should().BeFalse("los excluidos van plegados");
        excluidos.QuerySelector("summary")!.TextContent.Trim().Should().Be("Excluidos en este Centro (1)");
        excluidos.QuerySelectorAll(".requisitos-fila").Select(Nombre).Should().ContainSingle()
            .Which.Should().Be("Formación PRL específica · Trabajador");
        cut.FindAll(".requisitos-grupos > .requisitos-grupo").SelectMany(g => g.QuerySelectorAll(".requisitos-fila"))
            .Select(Nombre).Should().NotContain(n => n.StartsWith("Formación PRL", StringComparison.Ordinal));
    }

    [Fact]
    public void Los_requisitos_se_agrupan_en_Empresa_y_Trabajador_con_su_recuento()
    {
        var cut = RenderizarCatalogoCompleto();

        var grupos = cut.FindAll(".requisitos-grupos > .requisitos-grupo");
        grupos.Select(g => g.QuerySelector("h4")!.TextContent.Trim()).Should().Equal("Empresa", "Trabajador");
        grupos[0].QuerySelectorAll(".requisitos-fila").Select(Nombre).Should().Equal("RLC (TC1)", "Seguro de responsabilidad civil");
        grupos[1].QuerySelectorAll(".requisitos-fila").Select(Nombre).Should().Equal("Reconocimiento médico");
        grupos[0].QuerySelector(".requisitos-grupo-cabecera")!.TextContent.Should().Contain("2");
        grupos[1].QuerySelector(".requisitos-grupo-cabecera")!.TextContent.Should().Contain("1");
    }

    [Fact]
    public void Un_grupo_sin_requisitos_no_se_pinta()
    {
        var cut = Renderizar(Global(MedicoId, "Reconocimiento médico", AmbitoAplicacion.Trabajador));

        cut.FindAll(".requisitos-grupo h4").Select(h => h.TextContent.Trim()).Should().Equal("Trabajador");
    }

    [Fact]
    public void La_fila_resume_renovacion_tolerancia_y_bloqueo()
    {
        var cut = Renderizar(
            Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa, meses: 12, bloquea: true, tolerancia: 10),
            Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa, meses: 1));

        var metas = cut.FindAll(".requisitos-fila").ToDictionary(Nombre, f => f.QuerySelector(".requisitos-meta")!.TextContent.Trim());
        metas["Seguro de responsabilidad civil"].Should().Be("Renovación cada 12 meses · Tolerancia tras vencer: 10 día(s), fijada en este Centro · Bloquea el acceso");
        metas["RLC (TC1)"].Should().Be("Renovación cada mes");
    }

    [Fact]
    public void Los_iconos_son_botones_con_nombre_accesible_y_title()
    {
        var cut = RenderizarCatalogoCompleto();

        var iconos = cut.FindAll("button.requisitos-icono");
        iconos.Should().NotBeEmpty();
        iconos.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.GetAttribute("aria-label")) && b.GetAttribute("aria-label") == b.GetAttribute("title"));
        Lapiz(cut, "RLC (TC1)").GetAttribute("type").Should().Be("button");
        Papelera(cut, "RLC (TC1)").GetAttribute("type").Should().Be("button");
        cut.FindAll("details.requisitos-excluidos button[aria-label^='Quitar requisito']").Should().BeEmpty(
            "un requisito excluido ya está fuera: solo ofrece el lápiz para devolverlo");
    }

    // ------------------------------------------------ lápiz: panel de ajustes

    [Fact]
    public async Task El_lapiz_abre_y_cierra_el_panel_de_ajustes_de_esa_fila()
    {
        var cut = RenderizarCatalogoCompleto();
        var lapiz = Lapiz(cut, "RLC (TC1)");
        lapiz.GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".requisitos-edicion").Should().BeEmpty();

        await lapiz.ClickAsync(new MouseEventArgs());

        var panel = cut.FindAll(".requisitos-edicion").Should().ContainSingle().Subject;
        Lapiz(cut, "RLC (TC1)").GetAttribute("aria-expanded").Should().Be("true");
        Lapiz(cut, "RLC (TC1)").GetAttribute("aria-controls").Should().Be(panel.Id);
        panel.ParentElement!.QuerySelector(".requisitos-nombre")!.TextContent.Trim().Should().Be("RLC (TC1)", "el panel cuelga de su fila");

        await Lapiz(cut, "RLC (TC1)").ClickAsync(new MouseEventArgs());
        cut.FindAll(".requisitos-edicion").Should().BeEmpty();
    }

    [Fact]
    public async Task El_panel_conserva_los_ajustes_de_siempre()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa,
            meses: 12, bloquea: true, tolerancia: 10, archivoUrl: "plantillas/seguro.pdf", archivoNombre: "seguro.pdf"));

        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());

        var panel = cut.Find(".requisitos-edicion");
        panel.QuerySelectorAll("fieldset label").Select(l => l.TextContent.Trim()).Should().Equal("Se exige", "Se excluye");
        panel.QuerySelectorAll("label").Select(l => l.TextContent).Should().Contain(t => t.Contains("Periodicidad propia"));
        panel.QuerySelectorAll("select").Should().HaveCount(2, "periodicidad y, al bloquear, tolerancia en días");
        panel.QuerySelector("label.requisitos-casilla input[type=checkbox]")!.HasAttribute("checked").Should().BeTrue();
        panel.QuerySelector("label.requisitos-casilla")!.TextContent.Should().Contain("Bloquea el acceso");
        var plantilla = panel.QuerySelector("a.requisitos-archivo-nombre")!;
        plantilla.TextContent.Trim().Should().Be("seguro.pdf");
        plantilla.GetAttribute("href").Should().Be($"/requisitos-documentales/{SeguroId}/archivo");
        panel.QuerySelectorAll(".requisitos-archivo button").Select(b => b.TextContent.Trim()).Should().Equal("Sustituir", "Quitar plantilla");
        panel.QuerySelectorAll(".requisitos-edicion-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Guardar ajustes");
    }

    [Fact]
    public async Task La_tolerancia_solo_aparece_cuando_el_requisito_bloquea_el_acceso()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());
        cut.Find(".requisitos-edicion").QuerySelectorAll("select").Should().ContainSingle("sin bloqueo solo hay periodicidad");

        await cut.Find(".requisitos-casilla input").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Find(".requisitos-edicion").QuerySelectorAll("select").Should().HaveCount(2);
    }

    [Fact]
    public async Task Guardar_ajustes_envia_el_Command_con_lo_editado_y_avisa_a_la_ficha()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());

        await cut.Find(".requisitos-casilla input").ChangeAsync(new ChangeEventArgs { Value = true });
        var selects = cut.Find(".requisitos-edicion").QuerySelectorAll("select");
        await selects[0].ChangeAsync(new ChangeEventArgs { Value = "12" });
        await cut.Find(".requisitos-edicion").QuerySelectorAll("select")[1].ChangeAsync(new ChangeEventArgs { Value = "10" });
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar ajustes").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        envio.TipoDocumentoId.Should().Be(SeguroId);
        envio.Incluido.Should().BeTrue();
        envio.PeriodicidadEspecialMeses.Should().Be(12);
        envio.BloqueaAcceso.Should().BeTrue();
        envio.ToleranciaDias.Should().Be(10);
        _avisos.Should().ContainSingle().Which.Should().Be((EntidadWorkspace.Centro, envio.CentroId));
        cut.WaitForAssertion(() => cut.FindAll(".requisitos-edicion").Should().BeEmpty("al guardar se cierra el panel"));
        cut.WaitForAssertion(() => cut.Find(".requisitos-meta").TextContent.Should().Contain("Renovación cada 12 meses"));
    }

    [Fact]
    public async Task Pasar_un_requisito_a_Se_excluye_lo_manda_a_los_excluidos()
    {
        var cut = Renderizar(
            Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa),
            Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "RLC (TC1)").ClickAsync(new MouseEventArgs());

        await cut.FindAll(".requisitos-edicion input[type=radio]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar ajustes").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Which.Incluido.Should().BeFalse();
        cut.WaitForAssertion(() => cut.Find("details.requisitos-excluidos summary").TextContent.Trim().Should().Be("Excluidos en este Centro (1)"));
        cut.FindAll(".requisitos-grupos > .requisitos-grupo .requisitos-fila").Select(Nombre).Should().Equal("Seguro de responsabilidad civil");
    }

    // ------------------------------------------------ papelera

    [Fact]
    public async Task La_papelera_de_un_requisito_que_el_catalogo_pide_siempre_lo_excluye_sin_borrarlo()
    {
        var cut = Renderizar(
            Global(MedicoId, "Reconocimiento médico", AmbitoAplicacion.Trabajador),
            Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa));

        await Papelera(cut, "Reconocimiento médico").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        envio.TipoDocumentoId.Should().Be(MedicoId);
        envio.Incluido.Should().BeFalse("un requisito que se pide siempre no se borra: se excluye en este Centro");
        _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>().Should().BeEmpty();
        _avisos.Should().ContainSingle();
        cut.WaitForAssertion(() => cut.Find("details.requisitos-excluidos summary").TextContent.Trim().Should().Be("Excluidos en este Centro (1)"));
        cut.FindAll(".requisitos-grupos > .requisitos-grupo .requisitos-fila").Select(Nombre).Should().Equal("RLC (TC1)");
    }

    [Fact]
    public async Task La_papelera_de_un_requisito_propio_del_Centro_pide_confirmacion_y_quita_el_ajuste()
    {
        var cut = Renderizar(
            Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa),
            Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa));

        await Papelera(cut, "RLC (TC1)").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>().Should().BeEmpty("se confirma antes de quitar");
        cut.Markup.Should().Contain("¿Quitar el ajuste de «RLC (TC1)»?");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Quitar ajuste").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Which.TipoDocumentoId.Should().Be(RlcId);
        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().BeEmpty();
    }

    // ------------------------------------------------ Añadir requisito: buscador

    [Fact]
    public async Task El_buscador_ofrece_los_no_configurados_y_no_archivados_y_marca_los_que_ya_estan()
    {
        var cut = RenderizarCatalogoCompleto();
        await AbrirBuscadorAsync(cut);

        await BuscarAsync(cut, "e");

        var opciones = cut.FindAll(".requisitos-opcion").ToDictionary(
            o => o.QuerySelector(".requisitos-opcion-nombre")!.TextContent.Trim(), o => o);
        opciones.Keys.Should().BeEquivalentTo(
            ["Certificado de aptitud", "Reconocimiento médico", "Seguro de responsabilidad civil", "Formación PRL específica"],
            "los archivados no se ofrecen (\"Recibo de pago RLC/TC1\" no aparece aunque contiene la «e» buscada) y «RLC (TC1)» no contiene «e»");
        opciones["Certificado de aptitud"].HasAttribute("disabled").Should().BeFalse();
        opciones["Certificado de aptitud"].TextContent.Should().Contain("Añadir");
        opciones["Reconocimiento médico"].HasAttribute("disabled").Should().BeTrue();
        opciones["Reconocimiento médico"].TextContent.Should().Contain("Ya se exige");
        opciones["Formación PRL específica"].HasAttribute("disabled").Should().BeTrue();
        opciones["Formación PRL específica"].TextContent.Should().Contain("Excluido en este Centro");
        cut.Find(".requisitos-resultados-cabecera").TextContent.Should().Contain("4 tipos de documento coinciden");
        cut.Markup.Should().Contain("Los tipos de documento archivados no aparecen en el buscador.");
    }

    [Fact]
    public async Task Elegir_un_tipo_en_el_buscador_lo_anade_con_el_Command_de_siempre_y_abre_sus_ajustes()
    {
        var cut = RenderizarCatalogoCompleto();
        await AbrirBuscadorAsync(cut);
        await BuscarAsync(cut, "aptitud");

        await cut.Find(".requisitos-opcion").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        envio.TipoDocumentoId.Should().Be(CatalogoId);
        envio.Incluido.Should().BeTrue();
        envio.PeriodicidadEspecialMeses.Should().BeNull();
        envio.BloqueaAcceso.Should().BeFalse();
        _avisos.Should().ContainSingle().Which.Should().Be((EntidadWorkspace.Centro, envio.CentroId));
        cut.WaitForAssertion(() => cut.FindAll("#requisitos-busqueda").Should().BeEmpty("el buscador se cierra al añadir"));
        cut.WaitForAssertion(() => cut.FindAll(".requisitos-grupos > .requisitos-grupo .requisitos-fila").Select(Nombre)
            .Should().Contain("Certificado de aptitud"));
        Lapiz(cut, "Certificado de aptitud").GetAttribute("aria-expanded").Should().Be("true", "tras añadirlo se abren sus ajustes");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje.Contains("se exige ahora en este Centro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_tipo_que_ya_se_exige_no_se_puede_volver_a_anadir()
    {
        var cut = RenderizarCatalogoCompleto();
        await AbrirBuscadorAsync(cut);
        await BuscarAsync(cut, "RLC (TC1)");

        var opcion = cut.FindAll(".requisitos-opcion").Should().ContainSingle().Subject;
        opcion.HasAttribute("disabled").Should().BeTrue();
        await opcion.ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_coincidencias_el_buscador_lo_dice_y_remite_al_catalogo()
    {
        var cut = RenderizarCatalogoCompleto();
        await AbrirBuscadorAsync(cut);

        await BuscarAsync(cut, "zzzz");

        cut.FindAll(".requisitos-opcion").Should().BeEmpty();
        var vacio = cut.Find(".requisitos-sin-coincidencias");
        vacio.TextContent.Should().Contain("Ningún tipo de documento coincide con «zzzz»");
        var enlace = vacio.QuerySelector("a")!;
        enlace.GetAttribute("href").Should().Be("/tipos-documento");
        enlace.TextContent.Trim().Should().Be("Ver catálogo de tipos de documento");
        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cerrar_el_buscador_devuelve_la_barra_de_Anadir_requisito()
    {
        var cut = RenderizarCatalogoCompleto();
        await AbrirBuscadorAsync(cut);

        await cut.Find("button[aria-label='Cerrar el buscador']").ClickAsync(new MouseEventArgs());

        cut.FindAll("#requisitos-busqueda").Should().BeEmpty();
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Añadir requisito");
    }

    [Fact]
    public async Task El_buscador_no_ofrece_un_archivado_aunque_el_Centro_todavia_lo_exija()
    {
        var archivadoExigido = Propio(ArchivadoId, "Recibo de pago RLC/TC1", AmbitoAplicacion.Empresa) with { Archivado = true };
        var cut = Renderizar(archivadoExigido, Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa));
        await AbrirBuscadorAsync(cut);

        await BuscarAsync(cut, "Recibo");

        cut.FindAll(".requisitos-opcion").Should().BeEmpty("el filtro del buscador excluye los archivados por sí mismo, no solo la consulta");
        cut.FindAll(".requisitos-sin-coincidencias").Should().ContainSingle();
    }

    // ------------------------------------------------ cambios sin guardar en el panel de una fila

    private static async Task MarcarBloqueoAsync(IRenderedComponent<CentroWorkspacePanel> cut) =>
        await cut.Find(".requisitos-casilla input").ChangeAsync(new ChangeEventArgs { Value = true });

    [Fact]
    public async Task Cancelar_con_cambios_pregunta_antes_de_descartar_y_seguir_editando_los_conserva()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());
        await MarcarBloqueoAsync(cut);

        await cut.FindAll(".requisitos-edicion-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        cut.FindAll(".requisitos-edicion").Should().ContainSingle("hasta confirmar, el panel sigue abierto con lo editado");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".requisitos-edicion").Should().ContainSingle();
        cut.Find(".requisitos-casilla input").HasAttribute("checked").Should().BeTrue();

        await cut.FindAll(".requisitos-edicion-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());

        cut.FindAll(".requisitos-edicion").Should().BeEmpty();
        _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().BeEmpty("descartar no guarda nada");
    }

    [Fact]
    public async Task Saltar_a_otra_fila_con_cambios_pregunta_y_no_pisa_lo_editado_hasta_confirmar()
    {
        var cut = Renderizar(
            Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa),
            Propio(RlcId, "RLC (TC1)", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "RLC (TC1)").ClickAsync(new MouseEventArgs());
        await MarcarBloqueoAsync(cut);

        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        Lapiz(cut, "RLC (TC1)").GetAttribute("aria-expanded").Should().Be("true");
        Lapiz(cut, "Seguro de responsabilidad civil").GetAttribute("aria-expanded").Should().Be("false");

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());

        Lapiz(cut, "Seguro de responsabilidad civil").GetAttribute("aria-expanded").Should().Be("true");
        Lapiz(cut, "RLC (TC1)").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".requisitos-casilla input").HasAttribute("checked").Should().BeFalse("la fila nueva parte de sus propios valores");
    }

    [Fact]
    public async Task Cancelar_sin_cambios_cierra_el_panel_sin_preguntar()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa));
        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());

        await cut.FindAll(".requisitos-edicion-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".requisitos-edicion").Should().BeEmpty();
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Quitar_el_ajuste_desde_el_panel_cierra_el_panel()
    {
        var cut = Renderizar(Excluido(ExcluidoId, "Formación PRL específica"));
        await Lapiz(cut, "Formación PRL específica").ClickAsync(new MouseEventArgs());

        await cut.FindAll(".requisitos-edicion-pie button").Single(b => b.TextContent.Trim() == "Quitar ajuste").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Quitar ajuste").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Which.TipoDocumentoId.Should().Be(ExcluidoId);
        cut.FindAll(".requisitos-edicion").Should().BeEmpty("la fila vuelve al criterio del catálogo: su panel de ajustes no puede quedar abierto con «Se excluye»");
    }

    [Fact]
    public async Task La_plantilla_se_puede_sustituir_o_quitar_desde_el_panel()
    {
        var cut = Renderizar(Propio(SeguroId, "Seguro de responsabilidad civil", AmbitoAplicacion.Empresa,
            archivoUrl: "plantillas/seguro.pdf", archivoNombre: "seguro.pdf"));
        await Lapiz(cut, "Seguro de responsabilidad civil").ClickAsync(new MouseEventArgs());
        cut.FindAll(".requisitos-edicion .zona-soltar-archivo, .requisitos-edicion input[type=file]").Should().BeEmpty("con plantilla, la subida solo aparece al sustituir");

        await cut.FindAll(".requisitos-archivo button").Single(b => b.TextContent.Trim() == "Sustituir").ClickAsync(new MouseEventArgs());
        cut.FindAll(".requisitos-edicion input[type=file]").Should().NotBeEmpty();

        await cut.FindAll(".requisitos-archivo button").Single(b => b.TextContent.Trim() == "Quitar plantilla").ClickAsync(new MouseEventArgs());
        cut.FindAll(".requisitos-archivo").Should().BeEmpty();
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar ajustes").ClickAsync(new MouseEventArgs());

        var envio = _mediador.Enviadas.OfType<EstablecerDocumentacionRequeridaCentroCommand>().Should().ContainSingle().Subject;
        envio.ArchivoUrl.Should().BeNull();
        envio.NombreArchivoOriginal.Should().BeNull();
    }

    // ------------------------------------------------ estado vacío

    [Fact]
    public async Task Sin_requisitos_configurados_se_ve_el_estado_vacio_con_su_accion()
    {
        var cut = Renderizar(
            NoConfigurado(CatalogoId, "Certificado de aptitud", AmbitoAplicacion.Trabajador),
            NoConfigurado(ArchivadoId, "Recibo de pago RLC/TC1", AmbitoAplicacion.Empresa, archivado: true));

        var vacio = cut.Find(".requisitos-vacio");
        vacio.QuerySelector("h3")!.TextContent.Trim().Should().Be("Este Centro no tiene requisitos propios");
        cut.FindAll(".requisitos-grupo").Should().BeEmpty();
        cut.FindAll("details.requisitos-excluidos").Should().BeEmpty();
        cut.Markup.Should().NotContain("Certificado de aptitud", "el catálogo no listado solo se ve en el buscador");
        var accion = vacio.QuerySelectorAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Añadir requisito").Subject;

        await accion.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("#requisitos-busqueda").Should().ContainSingle());
        await BuscarAsync(cut, "aptitud");
        cut.FindAll(".requisitos-opcion").Should().ContainSingle().Which.TextContent.Should().Contain("Certificado de aptitud");
    }
}
