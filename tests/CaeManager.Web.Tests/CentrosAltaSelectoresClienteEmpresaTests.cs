using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Panel «Nuevo centro»: los selectores de Cliente empresarial y de Empresa son campos del panel como Nombre o Dirección
/// (mismo envoltorio <c>.campo</c>, mismo control <c>.campo-input</c> y, en la hoja de SelectorEntidad, las mismas
/// declaraciones que CampoTexto: el CSS aislado de CampoTexto no llega a otro componente). La Empresa depende del
/// Cliente empresarial: deshabilitada hasta elegirlo, recortada a las suyas al elegirlo y, si el Cliente empresarial
/// tiene una sola Empresa, esa queda elegida y es la que se envía en el alta. Con varias no se elige ninguna. El Cliente
/// empresarial no se preselecciona aunque haya uno solo, como en el alta de Proyecto.
/// </summary>
public class CentrosAltaSelectoresClienteEmpresaTests : BunitContext
{
    private static readonly Guid ClienteConUnaEmpresa = Guid.NewGuid();
    private static readonly Guid ClienteConVariasEmpresas = Guid.NewGuid();
    private static readonly Guid EmpresaUnica = Guid.NewGuid();
    private static readonly Guid EmpresaNorte = Guid.NewGuid();
    private static readonly Guid EmpresaSur = Guid.NewGuid();

    private const string RazonClienteUno = "Refrielectric S.A.";
    private const string RazonClienteVarias = "Bebidas del Norte S.A.";
    private const string RazonEmpresaUnica = "Montajes Ebro S.L.";
    private const string RazonEmpresaNorte = "Instalaciones Norte S.L.";
    private const string RazonEmpresaSur = "Instalaciones Sur S.L.";

    public CentrosAltaSelectoresClienteEmpresaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public IReadOnlyList<ClienteSelectorDto> Clientes { get; set; } =
            [new(ClienteConUnaEmpresa, RazonClienteUno), new(ClienteConVariasEmpresas, RazonClienteVarias)];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new ClienteAutorizadoDto(Guid.NewGuid(), "Propia", EsOrigen: true)],
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                ObtenerClientesParaSelectorQuery => Clientes,
                ObtenerEmpresasParaSelectorQuery q => EmpresasDe(q.ClienteId),
                CrearCentroCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
        }

        private static IReadOnlyList<EmpresaSelectorDto> EmpresasDe(Guid? clienteId)
        {
            EmpresaSelectorDto unica = new(EmpresaUnica, RazonEmpresaUnica);
            EmpresaSelectorDto norte = new(EmpresaNorte, RazonEmpresaNorte);
            EmpresaSelectorDto sur = new(EmpresaSur, RazonEmpresaSur);
            if (clienteId == ClienteConUnaEmpresa) return [unica];
            if (clienteId == ClienteConVariasEmpresas) return [norte, sur];
            return [unica, norte, sur];
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

    private readonly MediatorFalso _mediador = new();

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Centros> Renderizar(string ruta = "centros")
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        Navegacion.NavigateTo(ruta);
        return Render<Centros>();
    }

    private static async Task<IRenderedComponent<Centros>> AbrirAltaAsync(IRenderedComponent<Centros> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "+ Nuevo centro"));
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo centro").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return cut;
    }

    private static IElement Etiqueta(IRenderedComponent<Centros> cut, string texto) =>
        cut.Find(".drawer-panel").QuerySelectorAll("label").Single(l => l.TextContent.Trim() == texto);

    private static IElement Control(IRenderedComponent<Centros> cut, string etiqueta) =>
        cut.Find("#" + Etiqueta(cut, etiqueta).GetAttribute("for"));

    private static async Task ElegirAsync(IRenderedComponent<Centros> cut, string etiqueta, string opcion)
    {
        await Control(cut, etiqueta).InputAsync(new ChangeEventArgs { Value = opcion[..4] });
        await cut.FindAll("li.selector-entidad-opcion").Single(li => li.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private static Task GuardarAsync(IRenderedComponent<Centros> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Cliente_y_Empresa_van_en_el_mismo_envoltorio_y_con_el_mismo_control_que_los_campos_vecinos()
    {
        var cut = await AbrirAltaAsync(Renderizar());

        var campoNombre = Etiqueta(cut, "Nombre").ParentElement!;
        foreach (var etiqueta in new[] { "Cliente", "Empresa" })
        {
            var campo = Etiqueta(cut, etiqueta).ParentElement!;
            campo.ClassList.Should().Contain("campo", $"«{etiqueta}» es un campo del panel como «Nombre»");
            campo.ParentElement.Should().BeSameAs(campoNombre.ParentElement, $"«{etiqueta}» va en la misma columna que «Nombre», a todo el ancho");
            Control(cut, etiqueta).ClassList.Should().Contain("campo-input");
        }

        Control(cut, "Nombre").ClassList.Should().Contain("campo-input", "barrera: el campo vecino usa ese mismo control");
    }

    [Fact]
    public void La_hoja_de_SelectorEntidad_da_a_su_control_las_mismas_declaraciones_que_CampoTexto_y_todo_el_ancho()
    {
        var propia = Regla(HojaDelKit("SelectorEntidad.razor.css"), ".campo-input");
        var vecina = Regla(HojaDelKit("CampoTexto.razor.css"), ".campo-input");

        vecina.Should().NotBeEmpty("barrera: CampoTexto declara el aspecto del control del sistema de diseño");
        propia.Should().Contain(vecina, "el CSS aislado de CampoTexto no llega a SelectorEntidad: tiene que declarar lo mismo");
        propia.Should().Contain("width: 100%", "el control va dentro de un envoltorio propio y no se estira solo");

        Regla(HojaDelKit("SelectorEntidad.razor.css"), ".campo-input:focus")
            .Should().Contain(Regla(HojaDelKit("CampoTexto.razor.css"), ".campo-input:focus"), "el foco se ve igual que en los vecinos");
        Regla(HojaDelKit("SelectorEntidad.razor.css"), ".campo-input:disabled")
            .Should().Contain(Regla(HojaDelKit("CampoTexto.razor.css"), ".campo-input:disabled"), "la Empresa deshabilitada se ve igual que un campo deshabilitado");
    }

    [Fact]
    public async Task Sin_Cliente_la_Empresa_esta_deshabilitada_y_un_Cliente_unico_no_se_elige_solo()
    {
        _mediador.Clientes = [new(ClienteConUnaEmpresa, RazonClienteUno)];
        var cut = await AbrirAltaAsync(Renderizar());

        Control(cut, "Cliente").GetAttribute("value").Should().BeNullOrEmpty("el alta de Proyecto tampoco elige el Cliente empresarial único");
        Control(cut, "Empresa").HasAttribute("disabled").Should().BeTrue();
        Control(cut, "Empresa").GetAttribute("placeholder").Should().Be("Elige primero un Cliente…");
        Control(cut, "Empresa").GetAttribute("value").Should().BeNullOrEmpty("sin Cliente empresarial no hay Empresa que elegir");
    }

    [Fact]
    public async Task Con_una_sola_Empresa_del_Cliente_esa_queda_elegida_y_se_envia_en_el_alta()
    {
        var cut = await AbrirAltaAsync(Renderizar());

        await ElegirAsync(cut, "Cliente", RazonClienteUno);

        _mediador.Enviadas.OfType<ObtenerEmpresasParaSelectorQuery>().Should().Contain(q => q.ClienteId == ClienteConUnaEmpresa,
            "las Empresas se piden acotadas al Cliente empresarial elegido");
        Control(cut, "Empresa").HasAttribute("disabled").Should().BeFalse();
        Control(cut, "Empresa").GetAttribute("value").Should().Be(RazonEmpresaUnica);

        await Control(cut, "Nombre").InputAsync(new ChangeEventArgs { Value = "Planta Zaragoza" });
        await GuardarAsync(cut);

        var alta = _mediador.Enviadas.OfType<CrearCentroCommand>().Should().ContainSingle().Subject;
        alta.ClienteId.Should().Be(ClienteConUnaEmpresa);
        alta.EmpresaId.Should().Be(EmpresaUnica);
    }

    [Fact]
    public async Task Con_varias_Empresas_del_Cliente_no_se_elige_ninguna()
    {
        var cut = await AbrirAltaAsync(Renderizar());

        await ElegirAsync(cut, "Cliente", RazonClienteVarias);

        Control(cut, "Empresa").GetAttribute("value").Should().BeNullOrEmpty();
        await Control(cut, "Nombre").InputAsync(new ChangeEventArgs { Value = "Planta Zaragoza" });
        await GuardarAsync(cut);

        cut.Find(".alerta-formulario").TextContent.Trim().Should().Be("Selecciona una empresa.");
        _mediador.Enviadas.OfType<CrearCentroCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cambiar_de_Cliente_recorta_la_Empresa_y_solo_elige_sola_la_unica()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await ElegirAsync(cut, "Cliente", RazonClienteVarias);
        await ElegirAsync(cut, "Empresa", RazonEmpresaSur);
        Control(cut, "Empresa").GetAttribute("value").Should().Be(RazonEmpresaSur, "barrera: elegida a mano entre varias");

        await ElegirAsync(cut, "Cliente", RazonClienteUno);
        Control(cut, "Empresa").GetAttribute("value").Should().Be(RazonEmpresaUnica,
            "la Empresa del Cliente empresarial anterior no es de este, y este tiene una sola");

        await ElegirAsync(cut, "Cliente", RazonClienteVarias);
        Control(cut, "Empresa").GetAttribute("value").Should().BeNullOrEmpty(
            "la Empresa única del otro Cliente empresarial no es de este, y entre varias no se elige ninguna");
    }

    [Fact]
    public async Task Un_aviso_de_Empresa_sin_elegir_se_quita_cuando_la_Empresa_unica_queda_elegida()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await ElegirAsync(cut, "Cliente", RazonClienteVarias);
        await GuardarAsync(cut);
        cut.Find(".alerta-formulario").TextContent.Trim().Should().Be("Selecciona una empresa.", "barrera");

        await ElegirAsync(cut, "Cliente", RazonClienteUno);

        cut.FindAll(".alerta-formulario").Should().BeEmpty("la Empresa ya está elegida");
        _mediador.Enviadas.OfType<CrearCentroCommand>().Should().BeEmpty("elegirla no guarda por sí solo");
    }

    [Fact]
    public async Task El_Cliente_que_trae_el_encadenado_con_una_sola_Empresa_la_deja_elegida_y_eso_no_es_un_cambio()
    {
        var cut = Renderizar($"centros?accion=crear&clienteId={ClienteConUnaEmpresa}");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());

        cut.WaitForAssertion(() => Control(cut, "Empresa").GetAttribute("value").Should().Be(RazonEmpresaUnica));
        Control(cut, "Empresa").HasAttribute("disabled").Should().BeFalse("sin Empresa en la URL los padres no quedan fijados");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo que elige la pantalla al abrir no es un cambio de quien edita");
    }

    private static string HojaDelKit(string fichero)
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        directorio.Should().NotBeNull("hay que encontrar la raíz del repositorio para leer las hojas del kit");
        return File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Components", "DesignSystem", fichero));
    }

    /// <summary>Declaraciones normalizadas del primer bloque cuyo selector es exactamente <paramref name="selector"/>.</summary>
    private static IReadOnlyList<string> Regla(string css, string selector)
    {
        var sinComentarios = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var bloque = Regex.Match(sinComentarios, @"(?:^|[}])\s*" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        if (!bloque.Success) return [];

        return bloque.Groups[1].Value
            .Split(';')
            .Select(d => Regex.Replace(d.Trim(), @"\s+", " "))
            .Where(d => d.Length > 0)
            .ToList();
    }
}
