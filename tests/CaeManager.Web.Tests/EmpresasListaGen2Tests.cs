using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Empresas contra su mockup Gen 2 («Empresas TALVEG.dc.html»). El
/// vacío por filtro, los chips y la rejilla de cabecera, que ya existían y se
/// conservan, los sigue probando <see cref="EmpresasVacioPorFiltroTests"/>; la
/// casilla de «los de esta página», <see cref="SeleccionarTodosDiceQueEsLaPaginaTests"/>.
///
/// <para>
/// La fila desplegada son los Clientes empresariales a los que la Empresa
/// presta servicio en una Relación Empresarial (la Empresa es la proveedora).
/// La entidad Empresa no lleva ningún rol CAE: nada de esto es un «tipo».
/// </para>
///
/// <para>
/// El doble del mediador guarda las empresas y APLICA lo que recibe, igual que
/// <c>ObtenerEmpresasQueryHandler</c>: búsqueda solo por razón social, estado
/// documental con <see cref="EstadoDocumentalFiltro.Coincide"/> y paginación
/// con <c>Pagina</c>/<c>TamanoPagina</c>. Un doble que ignorase cualquiera de
/// ellos dejaría en verde una pantalla que no los envía.
/// </para>
/// </summary>
public class EmpresasListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa un módulo JS.</summary>
    public EmpresasListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<EmpresaListaDto> Almacen { get; } = [];
        public Dictionary<Guid, List<ClienteDeEmpresaDto>> ClientesDe { get; } = [];
        public HashSet<Guid> ClientesQueFallan { get; } = [];
        public PerfilVocabularioTenant Perfil { get; set; } = PerfilVocabularioTenant.Consultora;
        public List<object> Enviadas { get; } = [];

        /// <summary>
        /// Si devuelve una tarea para la petición, esa es la respuesta: permite
        /// retenerla con un <see cref="TaskCompletionSource{TResult}"/> y
        /// resolverla fuera de orden.
        /// </summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            return (TResponse)Responder(request);
        }

        private object Responder(object request) => request switch
        {
            ObtenerPerfilVocabularioActualQuery => Perfil,
            ObtenerEmpresasQuery q => Filtrar(q),
            ObtenerClientesDeEmpresaQuery c => ClientesQueFallan.Contains(c.EmpresaId)
                ? throw new InvalidOperationException("Fallo simulado de la consulta de clientes de la empresa.")
                : (IReadOnlyList<ClienteDeEmpresaDto>)(ClientesDe.GetValueOrDefault(c.EmpresaId) ?? []).ToList(),
            ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)Array.Empty<ClienteSelectorDto>(),
            CrearEmpresaCommand => Result.Exito(Guid.NewGuid()),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        /// <summary>Mismo orden de pasos que el handler: filtra, ordena por razón social y desempata por Id, pagina.</summary>
        public ResultadoPaginado<EmpresaListaDto> Filtrar(ObtenerEmpresasQuery q)
        {
            var coincidentes = Almacen
                .Where(e => string.IsNullOrWhiteSpace(q.Busqueda)
                    || e.RazonSocial.ToUpperInvariant().Contains(q.Busqueda.ToUpperInvariant()))
                .Where(e => EstadoDocumentalFiltro.Coincide(e.EstadoDocumental, q.EstadoDocumental))
                .OrderBy(e => e.RazonSocial, StringComparer.Ordinal).ThenBy(e => e.Id)
                .ToList();

            var pagina = coincidentes.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<EmpresaListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
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
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private void Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
    }

    /// <param name="url">Ruta relativa con la que se abre la página: los filtros son [SupplyParameterFromQuery].</param>
    private IRenderedComponent<Empresas> Renderizar(MediatorFalso mediador, string url = "empresas")
    {
        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<Empresas>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static EmpresaListaDto Empresa(
        string razonSocial, string? cif = "B-48.220.917", EstadoDocumento? estado = null,
        int? cumplimiento = null, int detecciones = 0) =>
        new(Guid.NewGuid(), razonSocial, cif, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            estado, cumplimiento, detecciones);

    private static ClienteDeEmpresaDto ClienteEmpresarial(string razonSocial, string cif) => new(Guid.NewGuid(), razonSocial, cif);

    private static ObtenerEmpresasQuery UltimaConsulta(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Last();

    private static int ConsultasDeLista(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Count();

    private static int ConsultasDeClientes(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerClientesDeEmpresaQuery>().Count();

    private static List<string> TextosDeLosChips(IRenderedComponent<Empresas> cut) =>
        cut.FindAll(".barra-filtros .chip-filtro").Select(c => c.TextContent.Trim()).ToList();

    // ------------------------------------------------------------------ Cabecera

    [Fact]
    public void La_cabecera_es_la_Gen_2_con_su_kicker_y_sus_dos_acciones()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A.") } });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Negocio");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Empresas");

        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelector("a")!.GetAttribute("href").Should().Be("/empresas/exportar.xlsx");
        acciones.QuerySelector("button")!.TextContent.Trim().Should().Be("+ Nueva empresa");
    }

    /// <summary>El título lo sigue decidiendo el perfil de vocabulario del tenant, como antes de Gen 2.</summary>
    [Fact]
    public void Con_el_perfil_ClienteDirecto_el_titulo_de_la_cabecera_es_Mi_empresa()
    {
        var cut = Renderizar(new MediatorFalso { Perfil = PerfilVocabularioTenant.ClienteDirecto, Almacen = { Empresa("Refrielectric S.A.") } });

        cut.Find("header.cabecera-pagina h1.titulo-pagina").TextContent.Trim().Should().Be("Mi empresa");
    }

    [Fact]
    public async Task Nueva_empresa_de_la_cabecera_abre_el_drawer_de_alta()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A.") } });
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida: el drawer está cerrado");

        await cut.Find("header.cabecera-pagina .acciones-cabecera button").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nueva empresa");
    }

    // ------------------------------------------------------- Tarjeta de filtros

    [Fact]
    public async Task El_filtro_documental_dice_Todas_viaja_en_la_consulta_y_su_chip_nombra_la_documentacion()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Refrielectric S.A.", estado: EstadoDocumento.Vencido), Empresa("Montajes Ebro S.L.", estado: EstadoDocumento.Vigente) }
        };
        var cut = Renderizar(mediador);
        var select = cut.Find(".barra-filtros select");
        select.QuerySelector("option")!.TextContent.Trim().Should().Be("Todas");

        await select.ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });

        UltimaConsulta(mediador).EstadoDocumental.Should().Be(nameof(EstadoDocumento.Vencido));
        Navegacion.Uri.Should().Contain("estado=Vencido");
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Equal(["Documentación: Vencido"]));
        cut.FindAll(".enlace-nombre-fila").Select(b => b.TextContent.Trim()).Should().Equal(["Refrielectric S.A."]);
    }

    [Fact]
    public void Los_filtros_activos_y_Limpiar_todo_viven_dentro_de_la_tarjeta_de_filtros()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A.", estado: EstadoDocumento.Vencido) } },
            "empresas?q=Refri&estado=Vencido");

        var tarjeta = cut.Find(".barra-filtros");
        tarjeta.QuerySelectorAll(".chip-filtro").Should().HaveCount(2);
        tarjeta.QuerySelector("button.limpiar-filtros-barra")!.TextContent.Trim().Should().Be("Limpiar todo");
    }

    /// <summary>
    /// «Limpiar todo» quita los dos filtros de la consulta y de la URL con UNA
    /// sola navegación y UNA sola consulta. Escribir la URL en dos pasos
    /// dejaba entre ellos una URL con <c>estado</c> todavía puesto, que
    /// OnParametersSetAsync leía como un cambio de fuera: reponía el filtro y
    /// lanzaba consultas de más.
    /// </summary>
    [Fact]
    public async Task Limpiar_todo_quita_los_dos_filtros_de_la_consulta_y_de_la_url_con_una_sola_consulta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Refrielectric S.A.", estado: EstadoDocumento.Vencido), Empresa("Montajes Ebro S.L.") }
        };
        var cut = Renderizar(mediador, "empresas?q=Refri&estado=Vencido");
        Navegacion.Uri.Should().Contain("estado=Vencido", "es el punto de partida de este caso");
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.Find(".barra-filtros button.limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("q=").And.NotContain("estado=");
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1, "quitar los dos filtros es una sola pregunta nueva");
        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().BeNull();
        consulta.EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().BeEmpty());
        cut.Find(".conteo-empresas").TextContent.Trim().Should().Be("2 de 2 empresas");
    }

    // ------------------------------------------------------------------ Conteo

    [Fact]
    public void Sin_filtros_el_conteo_dice_cuantas_se_ven_de_cuantas_hay_sin_hablar_de_filtros()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A."), Empresa("Montajes Ebro S.L.") } });

        cut.Find(".barra-herramientas-lista .conteo-empresas").TextContent.Trim().Should().Be("2 de 2 empresas");
    }

    /// <summary>25 empresas con 20 por página: se ven 20 de 25. Si el conteo usara el total en los dos lados diría «25 de 25».</summary>
    [Fact]
    public void Con_mas_empresas_que_la_pagina_el_conteo_distingue_las_que_se_ven_del_total()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Empresa($"Empresa {i:00}"));
        var cut = Renderizar(mediador);

        cut.Find(".conteo-empresas").TextContent.Trim().Should().Be("20 de 25 empresas");
    }

    [Fact]
    public void Con_filtros_el_conteo_dice_que_el_total_es_el_de_las_coincidencias()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A."), Empresa("Montajes Ebro S.L.") } },
            "empresas?q=Refri");

        cut.Find(".conteo-empresas").TextContent.Trim().Should().Be("1 de 1 empresa con estos filtros");
    }

    // ------------------------------------------------------------------- Filas

    [Fact]
    public async Task El_chevron_dice_si_muestra_u_oculta_a_quien_presta_servicio_la_empresa()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A.") } });
        var chevron = cut.Find(".boton-expandir-fila");
        chevron.GetAttribute("aria-label").Should().Be("Ver los clientes de Refrielectric S.A.");
        chevron.GetAttribute("aria-expanded").Should().Be("false");

        await chevron.ClickAsync(new MouseEventArgs());

        var abierto = cut.Find(".boton-expandir-fila");
        abierto.GetAttribute("aria-label").Should().Be("Ocultar los clientes de Refrielectric S.A.");
        abierto.GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public async Task Con_seleccion_multiple_la_casilla_de_cada_fila_dice_a_que_empresa_marca()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Aislamientos Nervión S.L."), Empresa("Refrielectric S.A.") } });

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());

        cut.FindAll(".tarjeta-fila-acordeon-cabecera input[type=checkbox]").Select(c => c.GetAttribute("aria-label"))
            .Should().Equal(["Seleccionar Aislamientos Nervión S.L.", "Seleccionar Refrielectric S.A."]);
    }

    /// <summary>
    /// Sin cumplimiento calculable el anillo pinta «—», y su nombre accesible
    /// no puede anunciar un porcentaje: antes decía «% de cumplimiento…» sin
    /// número.
    /// </summary>
    [Fact]
    public void El_anillo_de_cumplimiento_no_anuncia_un_porcentaje_que_no_existe()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Empresa("Aislamientos Nervión S.L.", cumplimiento: 72), Empresa("Talleres Berriz S. Coop.", cumplimiento: null) }
        });

        cut.FindAll(".tarjeta-fila-acordeon-cabecera [role=img]").Select(a => a.GetAttribute("aria-label")).Should().Equal(
            [
                "72% de cumplimiento acumulado en los centros donde esta empresa tiene actividad",
                "Sin actividad en ningún centro con requisitos aplicables"
            ]);
    }

    [Fact]
    public void El_estado_documental_explica_de_donde_sale_sin_inventar_plazos()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Empresa("Aislamientos Nervión S.L.", estado: EstadoDocumento.Vencido), Empresa("Talleres Berriz S. Coop.") }
        });

        var badges = cut.FindAll(".tarjeta-fila-acordeon-cabecera .badge");
        badges.Select(b => b.TextContent.Trim()).Should().Equal(["Vencido", "Sin documentos"]);
        badges.Select(b => b.GetAttribute("title")).Should().Equal(
            ["Peor estado de vigencia entre sus documentos: Vencido", "Todavía no tiene ningún documento"]);
        badges.Select(b => b.GetAttribute("title")).Should().NotContain(t => t!.Contains("días"),
            "los umbrales de urgente y próximo son configurables: el mockup escribe «7 días» y «30 días», y aquí no se copian");
    }

    [Fact]
    public async Task La_fila_con_la_vista_previa_abierta_se_marca_y_solo_esa()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Aislamientos Nervión S.L."), Empresa("Montajes Ebro S.L.") },
            // El panel de vista previa pide su detalle al abrirse; aquí se deja en
            // vuelo: lo que se observa es la fila, no el panel.
            Retener = p => p is ObtenerEmpresaPorIdQuery ? new TaskCompletionSource<object>().Task : null
        };
        var cut = Renderizar(mediador);
        cut.FindAll(".fila-empresa-en-vista-previa").Should().BeEmpty("sin vista previa abierta no se marca ninguna");

        await cut.FindAll(".enlace-nombre-fila")[1].ClickAsync(new MouseEventArgs());

        var marcadas = cut.FindAll(".fila-empresa-en-vista-previa");
        marcadas.Should().ContainSingle();
        marcadas[0].QuerySelector(".enlace-nombre-fila")!.TextContent.Trim().Should().Be("Montajes Ebro S.L.");
    }

    // ------------------------------------------------------- Fila desplegada

    [Fact]
    public async Task La_fila_desplegada_dice_a_cuantos_clientes_presta_servicio_y_los_lista()
    {
        var refrielectric = Empresa("Refrielectric S.A.");
        var montajes = Empresa("Montajes Ebro S.L.");
        var mediador = new MediatorFalso { Almacen = { refrielectric, montajes } };
        mediador.ClientesDe[refrielectric.Id] = [ClienteEmpresarial("Grupo Arbeko", "A-95.117.220"), ClienteEmpresarial("Petronor Servicios", "B-48.902.331")];
        mediador.ClientesDe[montajes.Id] = [ClienteEmpresarial("Grupo Arbeko", "A-95.117.220")];
        var cut = Renderizar(mediador);

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Trim() == "Expandir todos").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".titulo-clientes-empresa").Select(t => t.TextContent.Trim())
            .Should().Equal(["Presta servicio a 1 cliente", "Presta servicio a 2 clientes"]));
        var contenidoRefrielectric = cut.FindAll(".tarjeta-fila-acordeon-contenido")[1];
        contenidoRefrielectric.TextContent.Should().Contain("Grupo Arbeko").And.Contain("Petronor Servicios");
    }

    [Fact]
    public async Task Sin_ninguna_relacion_la_fila_desplegada_lo_dice_sin_rotulo_de_recuento()
    {
        var mediador = new MediatorFalso { Almacen = { Empresa("Talleres Berriz S. Coop.") } };
        var cut = Renderizar(mediador);

        await cut.Find(".boton-expandir-fila").ClickAsync(new MouseEventArgs());

        cut.Find(".tarjeta-fila-acordeon-contenido").TextContent.Trim()
            .Should().Be("Esta empresa todavía no tiene ningún Cliente asociado.");
        cut.FindAll(".titulo-clientes-empresa").Should().BeEmpty();
    }

    /// <summary>
    /// Un fallo de la consulta no puede pintarse como «no tiene ningún Cliente
    /// asociado»: eso es afirmar algo que no se sabe. Se dice que falló y se
    /// ofrece reintentar, y el reintento pinta lo que llega.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_consulta_de_la_fila_desplegada_lo_dice_y_se_puede_reintentar()
    {
        var empresa = Empresa("Refrielectric S.A.");
        var mediador = new MediatorFalso { Almacen = { empresa }, ClientesQueFallan = { empresa.Id } };
        var cut = Renderizar(mediador);

        await cut.Find(".boton-expandir-fila").ClickAsync(new MouseEventArgs());

        var contenido = cut.Find(".tarjeta-fila-acordeon-contenido");
        contenido.QuerySelectorAll("[role=alert]").Select(a => a.TextContent.Trim()).Should().Equal(["No pudimos cargar a quién presta servicio esta empresa."], "un fallo de la consulta se dice como fallo");
        contenido.TextContent.Should().NotContain("todavía no tiene", "no se sabe si tiene o no: la consulta falló");

        mediador.ClientesQueFallan.Clear();
        mediador.ClientesDe[empresa.Id] = [ClienteEmpresarial("Grupo Arbeko", "A-95.117.220")];
        await cut.Find(".tarjeta-fila-acordeon-contenido button").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".titulo-clientes-empresa").TextContent.Trim().Should().Be("Presta servicio a 1 cliente"));
        cut.FindAll(".tarjeta-fila-acordeon-contenido [role=alert]").Should().BeEmpty();
    }

    /// <summary>
    /// La consulta de la fila desplegada de Refrielectric tarda; mientras
    /// tanto se cambia el filtro y la lista se recarga (y se pliega). Cuando la
    /// respuesta vieja llega, no puede quedarse guardada como si fuera de la
    /// lista nueva: al volver a desplegar la fila se pregunta otra vez.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_de_la_fila_desplegada_de_antes_de_recargar_no_se_queda_como_si_fuera_nueva()
    {
        var empresa = Empresa("Refrielectric S.A.", estado: EstadoDocumento.Vencido);
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso { Almacen = { empresa } };
        mediador.ClientesDe[empresa.Id] = [ClienteEmpresarial("Grupo Arbeko", "A-95.117.220")];
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerClientesDeEmpresaQuery) return null;
            retenida = true;
            return respuestaVieja.Task;
        };
        var cut = Renderizar(mediador);

        // Sin await: su manejador espera a la consulta retenida.
        var expansion = cut.Find(".boton-expandir-fila").ClickAsync(new MouseEventArgs());
        await cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });
        cut.WaitForAssertion(() => cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty(
            "recargar la lista pliega las filas"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            (IReadOnlyList<ClienteDeEmpresaDto>)[ClienteEmpresarial("Grupo Arbeko", "A-95.117.220")]));
        await expansion;
        var consultasAntes = ConsultasDeClientes(mediador);

        await cut.Find(".boton-expandir-fila").ClickAsync(new MouseEventArgs());

        ConsultasDeClientes(mediador).Should().Be(consultasAntes + 1,
            "la respuesta vieja era de antes de recargar la lista: no puede servir de caché a la fila");
        cut.WaitForAssertion(() => cut.Find(".titulo-clientes-empresa").TextContent.Trim().Should().Be("Presta servicio a 1 cliente"));
    }

    // ------------------------------------------------------------- Carreras

    /// <summary>
    /// La primera carga (sin filtros) tarda; mientras tanto se filtra por
    /// «Vencido», que responde en seguida sin ninguna. Cuando la vieja llega
    /// con tres empresas, no puede pisar el resultado del filtro vigente.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_de_la_carga_anterior_no_pisa_el_resultado_del_filtro_nuevo()
    {
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso
        {
            Almacen = { Empresa("Refrielectric S.A."), Empresa("Montajes Ebro S.L."), Empresa("Grúas Aldapa S.L.") }
        };
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerEmpresasQuery) return null;
            retenida = true;
            return respuestaVieja.Task;
        };
        Registrar(mediador);
        Navegacion.NavigateTo("empresas");
        var cut = Render<Empresas>();

        await cut.Find(".barra-filtros select").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ninguna empresa con este filtro"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(mediador.Filtrar(new ObtenerEmpresasQuery(null))));

        // Cualquier repintado posterior enseña el estado que dejó la respuesta
        // vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ninguna empresa con este filtro",
            "la respuesta vieja era de la lista sin filtrar, no de la pregunta vigente");
        cut.FindAll(".tarjeta-fila-acordeon").Should().BeEmpty();
    }

    /// <summary>Mientras viaja el alta, un segundo «Guardar» no manda otra: crearía la misma Empresa dos veces.</summary>
    [Fact]
    public async Task Un_segundo_Guardar_mientras_viaja_el_alta_no_manda_otra()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = p => p is CrearEmpresaCommand ? respuesta.Task : null };
        var cut = Renderizar(mediador);

        await cut.Find("header.cabecera-pagina .acciones-cabecera button").ClickAsync(new MouseEventArgs());
        var primero = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearEmpresaCommand>().Should().HaveCount(1, "el segundo clic llega con el alta en vuelo");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito(Guid.NewGuid())));
        await Task.WhenAll(primero, segundo);
    }

    // ------------------------------------------------------ Acción por URL

    /// <summary>
    /// El atajo global «n» navega a <c>/empresas?accion=crear</c> ESTANDO ya en
    /// /empresas: el componente no se recrea. Antes la acción solo se leía al
    /// montar y «n» cambiaba la URL sin abrir nada. Y al cerrar se quita de la
    /// URL, para que un segundo «n» vuelva a abrir el alta.
    /// </summary>
    [Fact]
    public async Task Pedir_crear_por_url_estando_ya_en_la_lista_abre_el_alta_y_se_puede_repetir()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Empresa("Refrielectric S.A.") } });
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida");

        await cut.InvokeAsync(() => Navegacion.NavigateTo("empresas?accion=crear"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nueva empresa"));

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
        Navegacion.Uri.Should().NotContain("accion=", "si se quedara, el siguiente «n» navegaría a la misma URL y no abriría nada");

        await cut.InvokeAsync(() => Navegacion.NavigateTo("empresas?accion=crear"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nueva empresa"));
    }

    /// <summary>«Crear empresa «nombre»» del buscador global (P3-31) sigue llegando con la razón social puesta.</summary>
    [Fact]
    public void Abrir_la_lista_con_accion_crear_y_nombre_abre_el_alta_con_la_razon_social_precargada()
    {
        var cut = Renderizar(new MediatorFalso(), "empresas?accion=crear&nombre=Refrielectric");

        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nueva empresa"));
        cut.Find(".drawer-panel input").GetAttribute("value").Should().Be("Refrielectric");
    }
}
