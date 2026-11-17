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

    [Fact]
    public void Toda_pagina_marcada_dispara_la_evaluacion_de_autorizacion()
    {
        // El handler solo actúa cuando hay una evaluación: AuthorizeRouteView la hace si la página declara [Authorize], y
        // [AllowAnonymous] la salta. Sin esto, la marca no cerraría nada en la navegación dentro del circuito.
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
            "sin el handler registrado la marca no niega nada; con ámbito porque el papel depende de la sesión del circuito");
    }
}
