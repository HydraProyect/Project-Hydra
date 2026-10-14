using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tipo de documento 360 (<see cref="TipoDocumentoDetalle"/>, <c>/documentos/tipos/{id}</c>): la vista viaja en la URL
/// (<c>?pestana=</c>, <c>?estado=</c> separado por comas, <c>?q=</c>) como en el resto de fichas 360. Prueban efectos:
/// qué consulta sale, qué filas se ven y qué queda en la barra de direcciones.
///
/// <para>
/// El mediador falso filtra con la MISMA semántica que el handler real (grupo del peor estado y nombre sin distinguir
/// mayúsculas; los recuentos y el total cuentan todas las filas): un falso que no filtrara daría verde con una página
/// que no pasa el filtro a la consulta.
/// </para>
/// </summary>
public class TipoDocumento360UrlTests : BunitContext
{
    public TipoDocumento360UrlTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([]);
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<FilaTrabajadorTipoDocumentoDto> Filas { get; } = [];
        public List<CentroExigenciaTipoDocumentoDto> CentrosExigencia { get; } = [];
        public List<EmpresaExigenciaTipoDocumentoDto> EmpresasEmpleadoras { get; } = [];
        public string? Descripcion { get; set; }
        public string? Criterios { get; set; }
        public string? SeSolicitaA { get; set; }
        public string? Observaciones { get; set; }

        /// <summary><c>false</c>: como el handler real con el rol Consulta o el rol Cliente, responde <c>null</c>.</summary>
        public bool PuedeLeer { get; set; } = true;

        public List<ObtenerEstadoTipoDocumentoQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerEstadoTipoDocumentoQuery consulta)
                throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

            Consultas.Add(consulta);
            return Task.FromResult((TResponse)(object?)Responder(consulta)!);
        }

        private EstadoTipoDocumentoDto? Responder(ObtenerEstadoTipoDocumentoQuery q)
        {
            if (!PuedeLeer)
                return null;

            IEnumerable<FilaTrabajadorTipoDocumentoDto> filtradas = Filas;
            if (q.Estados is { Count: > 0 } estados)
                filtradas = filtradas.Where(f => estados.Contains(EstadoTipoDocumentoCalculo.Grupo(f.PeorEstado)));
            if (!string.IsNullOrWhiteSpace(q.Busqueda))
                filtradas = filtradas.Where(f => f.Nombre.Contains(q.Busqueda.Trim(), StringComparison.OrdinalIgnoreCase));
            var lista = filtradas.ToList();

            return new EstadoTipoDocumentoDto(
                q.TipoDocumentoId, "Formación PRL", 12, true, default, default, SeSolicitaA, [],
                new FraccionCumplimiento(Filas.Count(f => f.CentrosAlDia > 0), Filas.Count),
                Centros: 1, Trabajadores: Filas.Count, EstadoTipoDocumentoCalculo.Recuentos(Filas),
                lista.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), lista.Count, q.Pagina, q.TamanoPagina,
                Notas: null, CentrosExigencia: CentrosExigencia, EmpresasEmpleadoras: EmpresasEmpleadoras, Descripcion: Descripcion,
                CriteriosValidacion: Criterios, Observaciones: Observaciones);
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

    /// <summary>La página monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    // ── Montaje ───────────────────────────────────────────────────────────

    private static readonly Guid TipoId = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();

    private static FilaTrabajadorTipoDocumentoDto Fila(string nombre, EstadoDocumento estado) =>
        new(Guid.NewGuid(), nombre, null, null, null, null, null, null, estado,
            CumplimientoDocumental.EsConforme(estado) ? 1 : 0,
            [new EstadoEnCentroDto(CentroId, "Almacén Vigo", Guid.NewGuid(), estado, null)]);

    /// <summary>Cinco filas, una por grupo: Vencido, Pendiente, Por vencer (dos estados de código) y Vigente.</summary>
    private MediatorFalso Montar(string rol = Roles.GestorCae)
    {
        var mediador = new MediatorFalso();
        mediador.Filas.AddRange([
            Fila("Marta Vencida", EstadoDocumento.Vencido),
            Fila("Pedro Pendiente", EstadoDocumento.Faltante),
            Fila("Martín Urgente", EstadoDocumento.Urgente),
            Fila("Paula Próxima", EstadoDocumento.Proximo),
            Fila("Vera Vigente", EstadoDocumento.Vigente)]);
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<TipoDocumentoDetalle> Renderizar(string consulta = "")
    {
        Navegacion.NavigateTo($"documentos/tipos/{TipoId}{consulta}");
        var cut = Render<TipoDocumentoDetalle>(p => p.Add(x => x.TipoDocumentoId, TipoId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public void Centros_exigidos_usan_agregado_completo_y_configuracion_sin_borrar_la_URL()
    {
        var mediador = Montar();
        mediador.CentrosExigencia.Add(new(CentroId, "Centro Norte", true, 7, new(1, 2), EstadoDocumento.Faltante));
        var cut = Renderizar("?q=ninguno&estado=Vigente&extra=conservar");
        cut.FindAll("[role=tab]").Single(t => t.TextContent == "Centros que lo exigen").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));
        var fila = cut.Find($"a[href='/centros/{CentroId}']").Closest("li")!;
        fila.TextContent.Should().Contain("Bloquea").And.Contain("Tolerancia: 7 días").And.Contain("1 de 2 al día");
        fila.GetAttribute("data-tono").Should().Be("peligro");
        Consulta.Should().Contain("pestana=centros").And.Contain("extra=conservar").And.Contain("q=ninguno");
    }

    [Fact]
    public void Empresas_empleadoras_usan_fraccion_de_pares_y_no_inventan_identidad()
    {
        var mediador = Montar();
        var id = Guid.NewGuid();
        mediador.EmpresasEmpleadoras.Add(new(id, "Empresa empleadora", 2, 3, new(4, 6), EstadoDocumento.Vencido));
        mediador.EmpresasEmpleadoras.Add(new(null, null, 1, 1, new(0, 1), EstadoDocumento.Faltante));
        var cut = Renderizar("?pestana=empresas");
        cut.Markup.Should().Contain("Sin Empresa empleadora");
        var fila = cut.Find($"a[href='/empresas/{id}']").Closest("li")!;
        fila.TextContent.Should().Contain("4 de 6 al día").And.Contain("2 Trabajadores · 3 Centros");
        fila.GetAttribute("data-tono").Should().Be("peligro");
        cut.FindAll(".fila-relacion-nombre").Where(n => n.TextContent == "Sin Empresa empleadora").Should().ContainSingle().Which.TagName.Should().Be("SPAN");
    }

    [Fact]
    public void Ficha_solo_con_cajas_reales_y_enlace_profundo_funcional()
    {
        var mediador = Montar();
        mediador.Descripcion = "Descripción real";
        mediador.Criterios = "Criterios reales";
        mediador.SeSolicitaA = "A la Empresa empleadora";
        mediador.Observaciones = "Observaciones reales";
        var cut = Renderizar("?pestana=ficha");
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().Be("Ficha");
        cut.FindAll("[data-caja]").Should().HaveCount(4);
        cut.Find("[data-pieza=cajas-ficha]").TextContent.Should().Contain("Descripción real").And.Contain("Criterios reales").And.Contain("A la Empresa empleadora").And.Contain("Observaciones reales");
    }

    [Fact]
    public void Ficha_sin_datos_se_oculta_y_la_URL_vuelve_a_Trabajadores()
    {
        Montar();
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[role=tab]").Should().NotContain(t => t.TextContent == "Ficha");
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().StartWith("Trabajadores");
    }

    [Theory]
    [InlineData("centros", "Ningún Centro visible lo exige a un Trabajador")]
    [InlineData("empresas", "Sin Trabajadores con este requisito en Centros visibles")]
    public void Agregados_vacios_no_inventan_filas(string pestana, string titulo)
    {
        Montar();
        var cut = Renderizar($"?pestana={pestana}");
        cut.Markup.Should().Contain(titulo);
        cut.FindAll("li.fila-relacion").Should().BeEmpty();
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private string Consulta => new Uri(Navegacion.Uri).Query;

    private static List<string> Nombres(IRenderedComponent<TipoDocumentoDetalle> cut) =>
        cut.FindAll(".fila-relacion .fila-relacion-nombre").Select(n => n.TextContent.Trim()).ToList();

    private static List<string> ContadoresMarcados(IRenderedComponent<TipoDocumentoDetalle> cut) =>
        cut.FindAll(".filtro-estados-opcion[aria-pressed=true]").Select(b => b.TextContent.Trim()).ToList();

    private static IElement Contador(IRenderedComponent<TipoDocumentoDetalle> cut, string rotulo) =>
        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.Trim().StartsWith(rotulo, StringComparison.Ordinal));

    private static IElement Buscador(IRenderedComponent<TipoDocumentoDetalle> cut) => cut.Find("input[type=search]");

    [Fact]
    public async Task Sin_Empresa_empleadora_filtra_la_pagina_sin_alterar_el_recuento_global()
    {
        var mediador = Montar();
        mediador.Filas[0] = mediador.Filas[0] with { EmpresaId = Guid.NewGuid(), EmpresaNombre = "Empresa empleadora A" };
        var cut = Renderizar();
        var global = cut.Find(".filtro-estados").TextContent;
        var selector = cut.FindAll(".tipo360-filtros-pagina select")[0];
        selector.QuerySelector("option[value=sin-empresa]")!.TextContent.Should().Be("Sin Empresa empleadora");
        cut.FindAll(".tipo360-filtros-pagina label")[0].TextContent.Should().Be("Empresa empleadora de esta página");
        cut.Find(".tipo360-filtros-pagina").TextContent.Should().Contain("Empresa empleadora").And.Contain("Por Empresa empleadora");
        await selector.ChangeAsync(new ChangeEventArgs { Value = "sin-empresa" });
        Nombres(cut).Should().HaveCount(4).And.NotContain("Marta Vencida");
        cut.Find(".filtro-estados").TextContent.Should().Be(global);
        mediador.Consultas.Should().ContainSingle();
    }

    [Fact]
    public void Sin_Empresa_empleadora_no_se_ofrece_si_todas_las_filas_la_tienen()
    {
        var mediador = Montar();
        for (var i = 0; i < mediador.Filas.Count; i++)
            mediador.Filas[i] = mediador.Filas[i] with { EmpresaId = Guid.NewGuid(), EmpresaNombre = "Empresa empleadora" };
        var cut = Renderizar();
        cut.FindAll(".tipo360-filtros-pagina option[value=sin-empresa]").Should().BeEmpty();
    }

    [Fact]
    public async Task Empresa_y_Centro_filtran_solo_la_pagina_sin_consultas_ni_recuentos_globales_falsos()
    {
        var mediador = Montar();
        var empresaA = Guid.NewGuid();
        var empresaB = Guid.NewGuid();
        var otroCentro = Guid.NewGuid();
        mediador.Filas[0] = mediador.Filas[0] with { EmpresaId = empresaA, EmpresaNombre = "Empresa A" };
        mediador.Filas[1] = mediador.Filas[1] with { EmpresaId = empresaA, EmpresaNombre = "Empresa A", Centros = [new(otroCentro, "Centro Sur", Guid.NewGuid(), EstadoDocumento.Faltante, null)] };
        mediador.Filas[2] = mediador.Filas[2] with { EmpresaId = empresaB, EmpresaNombre = "Empresa B" };
        var cut = Renderizar();
        var contadores = cut.Find(".filtro-estados").TextContent;
        var cabecera = cut.Find(".cabecera-identidad").TextContent;

        cut.Find(".tipo360-filtros-pagina legend").TextContent.Should().Be("Filtrar esta página");
        await cut.FindAll(".tipo360-filtros-pagina select")[0].ChangeAsync(new ChangeEventArgs { Value = empresaA.ToString() });
        Nombres(cut).Should().Equal("Marta Vencida", "Pedro Pendiente");
        cut.FindAll(".tipo360-filtros-pagina select")[0].QuerySelectorAll("option").Select(o => o.TextContent)
            .Should().Contain("Empresa B", "las opciones salen de la página completa, antes de los filtros locales");
        await cut.FindAll(".tipo360-filtros-pagina select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroId.ToString() });
        Nombres(cut).Should().Equal("Marta Vencida");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("Mostrando 1 de 5 de esta página").And.Contain("5 Trabajadores en el filtro global");
        cut.Find(".filtro-estados").TextContent.Should().Be(contadores);
        cut.Find(".cabecera-identidad").TextContent.Should().Be(cabecera);
        Consulta.Should().BeEmpty();
        mediador.Consultas.Should().ContainSingle();
    }

    [Fact]
    public async Task Agrupar_por_Empresa_conserva_una_fila_por_Trabajador_y_el_orden_dentro_del_grupo()
    {
        var mediador = Montar();
        var empresaA = Guid.NewGuid();
        var empresaB = Guid.NewGuid();
        mediador.Filas[0] = mediador.Filas[0] with { EmpresaId = empresaA, EmpresaNombre = "Empresa A" };
        mediador.Filas[1] = mediador.Filas[1] with { EmpresaId = empresaB, EmpresaNombre = "Empresa B" };
        mediador.Filas[2] = mediador.Filas[2] with { EmpresaId = empresaA, EmpresaNombre = "Empresa A" };
        var cut = Renderizar();

        await cut.FindAll(".tipo360-filtros-pagina select")[2].ChangeAsync(new ChangeEventArgs { Value = "empresa" });
        Nombres(cut).Should().Equal("Marta Vencida", "Martín Urgente", "Pedro Pendiente", "Paula Próxima", "Vera Vigente");
        cut.FindAll(".tipo360-grupo-empresa").Select(e => e.TextContent.Trim())
            .Should().Equal("Empresa A", "Empresa B", "Nombre de Empresa empleadora no disponible");
        cut.FindAll(".lista-relaciones-tarjeta").Should().ContainSingle();
        mediador.Consultas.Should().ContainSingle();
    }

    [Fact]
    public void Una_pagina_parcial_no_ofrece_Empresas_ni_Centros_que_no_ha_cargado()
    {
        var mediador = Montar();
        for (var i = 0; i < 15; i++) mediador.Filas.Add(Fila($"Trabajador {i}", EstadoDocumento.Vigente));
        mediador.Filas.Add(Fila("Fuera de página", EstadoDocumento.Vigente) with
        {
            EmpresaId = Guid.NewGuid(),
            EmpresaNombre = "Empresa fuera de página",
            Centros = [new(Guid.NewGuid(), "Centro fuera de página", Guid.NewGuid(), EstadoDocumento.Vigente, null)]
        });
        var cut = Renderizar();
        Nombres(cut).Should().HaveCount(20).And.NotContain("Fuera de página");
        cut.Find(".tipo360-filtros-pagina").TextContent.Should().NotContain("Empresa fuera de página").And.NotContain("Centro fuera de página");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("Mostrando 20 de 20 de esta página").And.Contain("21 Trabajadores en el filtro global");
        cut.FindComponent<PaginadorSimple>().Instance.TotalElementos.Should().Be(21);
    }

    [Theory]
    [InlineData("pagina")]
    [InlineData("tamano")]
    [InlineData("estado")]
    [InlineData("tipo")]
    public async Task Al_cambiar_el_universo_cargado_se_restablecen_los_selectores_de_pagina(string cambio)
    {
        var mediador = Montar();
        var empresa = Guid.NewGuid();
        mediador.Filas[0] = mediador.Filas[0] with { EmpresaId = empresa, EmpresaNombre = "Empresa A" };
        for (var i = 0; i < 20; i++) mediador.Filas.Add(Fila($"Trabajador {i}", EstadoDocumento.Vigente));
        var cut = Renderizar();
        await cut.FindAll(".tipo360-filtros-pagina select")[0].ChangeAsync(new ChangeEventArgs { Value = empresa.ToString() });
        await cut.FindAll(".tipo360-filtros-pagina select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroId.ToString() });
        await cut.FindAll(".tipo360-filtros-pagina select")[2].ChangeAsync(new ChangeEventArgs { Value = "empresa" });

        if (cambio == "pagina")
            await cut.InvokeAsync(() => cut.FindComponent<PaginadorSimple>().Instance.PaginaActualChanged.InvokeAsync(2));
        else if (cambio == "tamano")
            await cut.InvokeAsync(() => cut.FindComponent<PaginadorSimple>().Instance.TamanoPaginaChanged.InvokeAsync(50));
        else if (cambio == "estado")
            await Contador(cut, "Vigente").ClickAsync(new MouseEventArgs());
        else
            cut.Render(p => p.Add(x => x.TipoDocumentoId, Guid.NewGuid()));

        cut.FindAll(".tipo360-filtros-pagina select").Select(s => s.GetAttribute("value")).Should().OnlyContain(v => v == string.Empty);
        cut.FindAll(".tipo360-grupo-empresa").Should().BeEmpty();
        Nombres(cut).Should().NotBeEmpty();
        mediador.Consultas.Should().HaveCount(2);
    }

    [Fact]
    public async Task La_pestana_por_defecto_limpia_su_parametro_sin_perder_los_filtros_ni_releer()
    {
        var mediador = Montar();
        var cut = Renderizar("?pestana=desconocida&estado=PorVencer&q=Mart&extra=conservar");
        await cut.InvokeAsync(() => cut.FindComponent<Pestanas>().Instance.PestanaActivaChanged.InvokeAsync("trabajadores"));
        Consulta.Should().Contain("estado=PorVencer").And.Contain("q=Mart").And.Contain("extra=conservar").And.NotContain("pestana=");
        Nombres(cut).Should().Equal("Martín Urgente");
        mediador.Consultas.Should().ContainSingle();
    }

    // ── ?estado= ──────────────────────────────────────────────────────────

    [Fact]
    public void Sin_parametros_se_ven_todos_y_la_URL_queda_limpia()
    {
        var mediador = Montar();

        var cut = Renderizar();

        Nombres(cut).Should().HaveCount(5);
        ContadoresMarcados(cut).Should().ContainSingle().Which.Should().StartWith("Todos");
        Consulta.Should().BeEmpty();
        mediador.Consultas.Should().ContainSingle("una sola consulta pinta la página: adoptar la URL no la repite");
    }

    [Fact]
    public async Task Marcar_contadores_los_deja_en_la_URL_separados_por_coma_y_filtra_en_la_consulta()
    {
        var mediador = Montar();
        var cut = Renderizar();

        await Contador(cut, "Por vencer").ClickAsync(new MouseEventArgs());
        await Contador(cut, "Vencido").ClickAsync(new MouseEventArgs());

        Consulta.Should().Be("?estado=Vencido%2CPorVencer", "en el orden de gravedad, no en el del clic");
        Nombres(cut).Should().Equal("Marta Vencida", "Martín Urgente", "Paula Próxima");
        mediador.Consultas[^1].Estados.Should().BeEquivalentTo([GrupoEstadoTipoDocumento.Vencido, GrupoEstadoTipoDocumento.PorVencer]);
    }

    [Fact]
    public void Un_enlace_con_varios_estados_marca_cada_uno_y_no_los_combina_en_otro()
    {
        // Enum.TryParse directo leería «Vencido,Pendiente» como combinación de bits (Vencido | Pendiente = Pendiente):
        // la lista enseñaría solo los pendientes.
        Montar();

        var cut = Renderizar("?estado=Vencido,Pendiente");

        Nombres(cut).Should().Equal("Marta Vencida", "Pedro Pendiente");
        ContadoresMarcados(cut).Select(c => c.Split(' ')[0]).Should().Equal("Vencido", "Pendiente");
    }

    [Theory]
    [InlineData("?estado=Inventado")]
    [InlineData("?estado=3")]
    [InlineData("?estado=,,")]
    [InlineData("?estado=vencido")]
    [InlineData("?pestana=inventada")]
    [InlineData("?pestana=documentacion&estado=Inventado")]
    public void Un_valor_desconocido_no_rompe_la_pagina_ni_filtra(string consulta)
    {
        var mediador = Montar();

        var cut = Renderizar(consulta);

        Nombres(cut).Should().HaveCount(5);
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().StartWith("Trabajadores");
        mediador.Consultas[^1].Estados.Should().BeEmpty();
    }

    [Fact]
    public void Lo_desconocido_se_descarta_y_lo_conocido_del_mismo_enlace_si_filtra()
    {
        Montar();

        var cut = Renderizar("?estado=Inventado,Vigente,7");

        Nombres(cut).Should().Equal("Vera Vigente");
    }

    // ── ?q= ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_buscador_filtra_por_nombre_lo_deja_en_la_URL_y_no_toca_los_contadores()
    {
        var mediador = Montar();
        var cut = Renderizar();
        var contadoresAntes = cut.FindAll(".filtro-estados-opcion").Select(b => b.TextContent.Trim()).ToList();

        await Buscador(cut).InputAsync("mart");

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Marta Vencida", "Martín Urgente"));
        Consulta.Should().Be("?q=mart");
        mediador.Consultas[^1].Busqueda.Should().Be("mart");
        cut.FindAll(".filtro-estados-opcion").Select(b => b.TextContent.Trim()).Should().Equal(contadoresAntes);
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().Contain("5", "la pestaña cuenta a todos, no a los buscados");
    }

    [Fact]
    public void Un_enlace_con_q_y_estado_abre_la_misma_vista_con_una_sola_consulta()
    {
        var mediador = Montar();

        var cut = Renderizar("?estado=PorVencer&q=mart");

        Nombres(cut).Should().Equal("Martín Urgente");
        Buscador(cut).GetAttribute("value").Should().Be("mart");
        ContadoresMarcados(cut).Should().ContainSingle().Which.Should().StartWith("Por vencer");
        mediador.Consultas.Should().ContainSingle();
    }

    [Fact]
    public async Task Buscar_vuelve_a_la_primera_pagina_y_conserva_los_estados_marcados_en_la_URL()
    {
        var mediador = Montar();
        var cut = Renderizar("?estado=Vencido,PorVencer");

        await Buscador(cut).InputAsync("paula");

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Paula Próxima"));
        Consulta.Should().Contain("estado=Vencido,PorVencer").And.Contain("q=paula");
        mediador.Consultas[^1].Pagina.Should().Be(1);
    }

    // ── Quitar filtros y navegación ───────────────────────────────────────

    [Fact]
    public async Task Sin_coincidencias_Quitar_filtros_limpia_el_texto_y_los_estados_tambien_de_la_URL()
    {
        var mediador = Montar();
        var cut = Renderizar("?estado=Vigente&q=mart");
        Nombres(cut).Should().BeEmpty();
        cut.Markup.Should().Contain("Ninguno con ese filtro.");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(5));
        Consulta.Should().BeEmpty("si la URL conservara q o estado, recargar los repondría");
        Buscador(cut).GetAttribute("value").Should().BeEmpty();
        ContadoresMarcados(cut).Should().ContainSingle().Which.Should().StartWith("Todos");
        mediador.Consultas[^1].Should().Match<ObtenerEstadoTipoDocumentoQuery>(q => q.Estados!.Count == 0 && string.IsNullOrEmpty(q.Busqueda));
    }

    [Fact]
    public void Volver_atras_a_otra_URL_de_la_misma_ficha_recupera_esa_vista()
    {
        var mediador = Montar();
        var cut = Renderizar("?estado=Vencido");
        Nombres(cut).Should().Equal("Marta Vencida");

        Navegacion.NavigateTo($"documentos/tipos/{TipoId}?q=vera");
        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Vera Vigente"));
        ContadoresMarcados(cut).Should().ContainSingle().Which.Should().StartWith("Todos");

        Navegacion.NavigateTo($"documentos/tipos/{TipoId}");
        cut.WaitForAssertion(() => Nombres(cut).Should().HaveCount(5));
        Buscador(cut).GetAttribute("value").Should().BeEmpty();
        mediador.Consultas.Should().HaveCount(3, "una consulta por vista; adoptar la URL que la propia página escribió no añade ninguna");
    }

    // ── La URL no abre la puerta ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("?pestana=trabajadores&estado=Vencido&q=marta")]
    public void A_quien_la_consulta_no_deja_leer_ningun_parametro_le_ensena_la_lista(string consulta)
    {
        // El handler real responde null al rol Consulta y al rol Cliente (ObtenerEstadoTipoDocumentoQueryTests); la ruta
        // los deja fuera por atributo (TipoDocumento360SoloRolesDeGestionTests). Aquí: los parámetros no cambian eso.
        var mediador = Montar(Roles.Consulta);
        mediador.PuedeLeer = false;

        var cut = Renderizar(consulta);

        Nombres(cut).Should().BeEmpty();
        cut.FindAll("input[type=search], .filtro-estados-opcion").Should().BeEmpty();
        cut.Markup.Should().NotContain("Marta Vencida");
        cut.FindAll(".estado-vacio").Should().NotBeEmpty();
    }
}
