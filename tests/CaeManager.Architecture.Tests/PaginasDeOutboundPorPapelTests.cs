using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Services.Autorizacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Ninguna página de Outbound sin la condición de papel (cierre del hueco declarado de B-02b, PR #2293). Toda página
/// Blazor (<see cref="RouteAttribute"/>) que el registro de módulos de ADR-013 (M0a, <see cref="RegistroDeModulos"/>)
/// asigna a <see cref="Modulo.Outbound"/> lleva <see cref="PaginaDeOutboundAttribute"/>, salvo las de
/// <see cref="Exentas"/> con su motivo; ninguna de otro módulo la lleva; y el handler que la hace cumplir
/// (<see cref="PaginaDeOutboundPorPapelAuthorizationHandler"/>) está registrado. Así una página de Outbound nueva no se
/// abre con papel Inbound por olvido.
///
/// <para>
/// El criterio de qué es «de Outbound» es el registro, no una lista escrita aquí: una página nueva en un área de Outbound
/// queda cubierta sin tocar este test. Mide por reflexión sobre el ensamblado compilado. Lo que no demuestra: que el
/// handler niegue con papel Inbound (eso lo prueban <c>PaginaDeOutboundPorPapelAuthorizationHandlerTests</c>) ni que el
/// papel salga de la operación revalidada (los tests de <c>CurrentUserService</c>).
/// </para>
/// </summary>
public class PaginasDeOutboundPorPapelTests
{
    /// <summary>Páginas que el registro asigna a Outbound y no llevan la marca, con su motivo.</summary>
    private static readonly Dictionary<string, string> Exentas = new()
    {
        ["CaeManager.Web.Features.Dashboard.Pages.Inicio"] =
            "Es la raíz \"/\" de toda la aplicación y su área entra en Outbound como provisional (INFERENCE por nombre); " +
            "negarla con papel Inbound dejaría sin página de llegada a quien elige ese papel. Con papel Inbound sus datos " +
            "pasan por AlcanceDatosService y salen vacíos. Dónde aterriza el papel Inbound es una decisión pendiente.",
    };

    private static readonly Lazy<IReadOnlyList<Type>> Paginas = new(() =>
        ReflexionArquitecturaHelper.TiposDe(ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"))
            .Where(t => !t.IsAbstract && t.IsDefined(typeof(RouteAttribute), inherit: false))
            .ToList());

    private static Modulo? ModuloDe(Type pagina) => RegistroDeModulos.Versionado().Clasificar(pagina)?.Modulo;

    private static bool LlevaLaMarca(Type tipo) => tipo.IsDefined(typeof(PaginaDeOutboundAttribute), inherit: false);

    [Fact]
    public void El_inventario_de_paginas_de_Outbound_no_esta_vacio()
    {
        // Control positivo del instrumento: si la reflexión o el registro dejaran de ver las páginas, todo lo de abajo
        // pasaría vacío. 32 páginas de Outbound en origin/main al cerrar el hueco (31 marcadas y Inicio exenta).
        Paginas.Value.Count(p => ModuloDe(p) == Modulo.Outbound).Should().BeGreaterThanOrEqualTo(32);
        Paginas.Value.Count(LlevaLaMarca).Should().BeGreaterThanOrEqualTo(31);
    }

    [Fact]
    public void Toda_pagina_de_Outbound_lleva_la_condicion_de_papel_o_esta_exenta_con_motivo()
    {
        var sinMarca = Paginas.Value
            .Where(p => ModuloDe(p) == Modulo.Outbound && !LlevaLaMarca(p) && !Exentas.ContainsKey(p.FullName!))
            .Select(p => p.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        sinMarca.Should().BeEmpty(
            "una página de Outbound sin @attribute [PaginaDeOutbound] se abre con papel Inbound si el rol cumple su " +
            "[Authorize]; márcala o exímela con motivo");
    }

    [Fact]
    public void Las_exentas_existen_son_de_Outbound_no_llevan_la_marca_y_tienen_motivo()
    {
        var porNombre = Paginas.Value.ToDictionary(p => p.FullName!);

        foreach (var (nombre, motivo) in Exentas)
        {
            porNombre.Should().ContainKey(nombre, "una exención obsoleta se retira");
            ModuloDe(porNombre[nombre]).Should().Be(Modulo.Outbound, $"{nombre} solo necesita exención si es de Outbound");
            LlevaLaMarca(porNombre[nombre]).Should().BeFalse($"{nombre} no es exenta si lleva la marca");
            motivo.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Ninguna_pagina_de_otro_modulo_ni_ningun_otro_tipo_lleva_la_marca()
    {
        ReflexionArquitecturaHelper.TiposDe(ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"))
            .Where(t => LlevaLaMarca(t) && (!Paginas.Value.Contains(t) || ModuloDe(t) != Modulo.Outbound))
            .Select(t => t.FullName)
            .Should().BeEmpty("la marca dice que la página es de Outbound; las de Inbound y del Núcleo no se tocan");
    }

    private const string MotivoExtension =
        "La llama la extensión de navegador con su propio token (Policies.SoloExtension o SesionOExtension): " +
        "ContextoExtensionMiddleware solo resuelve Asignaciones de Operación de Outbound, y con papel Inbound los " +
        "comandos los corta GuardaPorServicioBehavior y los datos AlcanceDatosService. Marcarla negaría la vía de la " +
        "extensión, cuyo papel no se ha medido; queda declarado en la PR que cierra el hueco.";

    private const string MotivoExtensionYSesion =
        "Policies.SesionOExtension: la llama la extensión con su token y también la sesión web. Por la vía del token, " +
        "ContextoExtensionMiddleware solo resuelve Asignaciones de Operación de Outbound; por la sesión web, con papel " +
        "Inbound, AlcanceDatosService da alcance vacío y GuardaPorServicioBehavior corta la escritura, pero la ruta " +
        "responde. Marcarla negaría también la vía de la extensión, cuyo papel no se ha medido; hueco declarado en la PR.";

    /// <summary>Clases de endpoints de un área de Outbound que no llevan <c>.DeOutbound()</c>, con su motivo.</summary>
    private static readonly Dictionary<string, string> EndpointsExentos = new()
    {
        ["CaeManager.Web.Features.Contacto.SolicitudDemoEndpoints"] =
            "Formulario público de solicitud de demo: es anónimo, no hay sesión ni papel que mirar.",
        ["CaeManager.Web.Api.V1.ClientesApiEndpoints"] =
            "API pública v1: la autentica la Clave de Integración (RequiereAlcanceIntegracion), sin sesión web ni " +
            "selección de Asignación de Operación, así que no hay papel Inbound que negar.",
        ["CaeManager.Web.Features.Extension.AcreditacionesPendientesEndpoints"] = MotivoExtensionYSesion,
        ["CaeManager.Web.Features.Extension.EntregaArchivoExtensionEndpoints"] = MotivoExtension,
        ["CaeManager.Web.Features.Extension.ExtensionTokenEndpoints"] = MotivoExtension,
        ["CaeManager.Web.Features.Extension.MarcarAcreditacionSubidaEndpoints"] = MotivoExtensionYSesion,
        ["CaeManager.Web.Features.Extension.TenantsBeneficiariosExtensionEndpoints"] = MotivoExtension,
    };

    private static readonly Lazy<IReadOnlyList<Type>> ClasesDeEndpointsDeOutbound = new(() =>
        ReflexionArquitecturaHelper.TiposDe(ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"))
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Endpoints", StringComparison.Ordinal)
                        && ModuloDe(t) == Modulo.Outbound)
            .ToList());

    [Fact]
    public void Toda_clase_de_endpoints_de_Outbound_lleva_la_condicion_de_papel_o_esta_exenta_con_motivo()
    {
        // Quien descarga o exporta lo de una página de Outbound es quien la ve: se niega con el mismo papel. Mide por
        // fuente (un endpoint mínimo no tiene tipo que marcar) y por clase, no por cada Map*: una clase con una sola
        // llamada a .DeOutbound() pasa; el detalle de cada ruta lo revisa quien la añade.
        var fuentes = Directory
            .GetFiles(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web"), "*Endpoints.cs",
                SearchOption.AllDirectories)
            .ToLookup(Path.GetFileNameWithoutExtension, SinComentarios);

        // Control positivo del instrumento: las diez clases marcadas al cerrar el hueco y las siete exentas.
        ClasesDeEndpointsDeOutbound.Value.Should().HaveCountGreaterThanOrEqualTo(17);

        ClasesDeEndpointsDeOutbound.Value
            .Where(t => !EndpointsExentos.ContainsKey(t.FullName!)
                        && !fuentes[t.Name].Any(fuente => fuente.Contains(".DeOutbound()", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Should().BeEmpty(
                "un endpoint de Outbound sin .DeOutbound() descarga con papel Inbound lo que su página niega; " +
                "márcalo o exímelo con motivo");
    }

    /// <summary>La fuente sin las líneas de comentario, para que un «.DeOutbound()» citado en la documentación no cuente.</summary>
    private static string SinComentarios(string ruta) =>
        string.Join('\n', File.ReadAllLines(ruta).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void Los_endpoints_exentos_existen_son_de_Outbound_y_tienen_motivo()
    {
        var porNombre = ClasesDeEndpointsDeOutbound.Value.ToDictionary(t => t.FullName!);

        foreach (var (nombre, motivo) in EndpointsExentos)
        {
            porNombre.Should().ContainKey(nombre, "una exención obsoleta o fuera de Outbound se retira");
            motivo.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Toda_pagina_marcada_dispara_la_evaluacion_de_autorizacion()
    {
        // El handler solo actúa cuando hay una evaluación: AuthorizeRouteView la hace si la página declara [Authorize], y
        // [AllowAnonymous] la salta. Sin esto, la marca no cerraría nada en el Router, que evalúa la página en cada petición HTTP.
        Paginas.Value
            .Where(p => LlevaLaMarca(p)
                        && (!p.IsDefined(typeof(AuthorizeAttribute), inherit: false)
                            || p.IsDefined(typeof(AllowAnonymousAttribute), inherit: false)))
            .Select(p => p.FullName)
            .Should().BeEmpty();
    }

    [Fact]
    public void El_handler_de_papel_esta_registrado_con_ambito()
    {
        var programa = File.ReadAllText(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        programa.Should().MatchRegex(
            @"AddScoped<Microsoft\.AspNetCore\.Authorization\.IAuthorizationHandler,\s*CaeManager\.Web\.Services\.Autorizacion\.PaginaDeOutboundPorPapelAuthorizationHandler>\(\)",
            "sin el handler registrado la marca no niega nada; con ámbito porque el papel depende de la sesión de cada petición");
    }
}
