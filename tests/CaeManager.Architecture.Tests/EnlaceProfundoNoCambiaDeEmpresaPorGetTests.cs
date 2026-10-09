using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Contrato del selector de Tenant, § 4.5 e I15: el enlace profundo a una ficha que el Tenant activo no
/// devuelve nunca cambia de empresa por GET. La pista <c>?tenant</c> solo sirve para ofrecer un botón que
/// hace POST con antiforgery a <c>/cuenta/cliente-activo</c>. Aquí se fija por fuente que el componente
/// <c>EnlaceProfundoOtraEmpresa</c> no navega ni enlaza, que solo escribe por ese formulario POST, y que las
/// cuatro fichas 360 lo pintan en su estado de error (si una ficha nueva se olvida, el enlace profundo de esa
/// ficha volvería a callar el motivo).
/// </summary>
public class EnlaceProfundoNoCambiaDeEmpresaPorGetTests
{
    private const string Componente = "src/CaeManager.Web/Components/Layout/EnlaceProfundoOtraEmpresa.razor";

    /// <summary>Lo que cambiaría o ofrecería cambiar de empresa por GET: navegar por código, enlazar, o tocar la selección.</summary>
    private static readonly Regex CambioPorGet = new(
        @"NavigateTo\s*\(|<a\s|\bhref\s*=|IClienteActivoSeleccionado|ClienteActivoSeleccionado|Response\.Cookies|method\s*=\s*""get""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void El_componente_del_enlace_profundo_no_navega_ni_enlaza_ni_toca_la_seleccion()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(), Componente));

        CambioPorGet.Matches(fuente).Select(m => m.Value).Should().BeEmpty(
            "la pista ?tenant nunca cambia de empresa por GET (contrato del selector, § 4.5, I15)");
    }

    [Fact]
    public void El_componente_del_enlace_profundo_solo_escribe_por_un_formulario_POST_con_antiforgery_a_cliente_activo()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(), Componente));

        fuente.Should().Contain("method=\"post\"").And.Contain("action=\"/cuenta/cliente-activo\"")
            .And.Contain("FormFieldName", "el token antiforgery viaja en el formulario");
    }

    [Theory]
    [InlineData("<a href=\"/cuenta/cliente-activo?tenantId=x\">Abrir</a>")]
    [InlineData("NavigationManager.NavigateTo($\"/cuenta/cliente-activo?tenantId={t}\");")]
    [InlineData("<form method=\"get\" action=\"/cuenta/cliente-activo\">")]
    [InlineData("ClienteActivoSeleccionado.Seleccionar(id)")]
    public void Control_positivo_el_detector_reconoce_un_cambio_por_GET(string fuente) =>
        CambioPorGet.IsMatch(fuente).Should().BeTrue("si el detector no ve estos casos, la prueba de arriba no prueba nada");

    [Theory]
    [InlineData("src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Empresas/Pages/EmpresaDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Subcontratas/Pages/SubcontrataDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Vehiculos/Pages/VehiculoDetalle.razor")]
    public void Cada_ficha_360_pinta_el_enlace_profundo_en_su_estado_de_error(string ficha)
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(), ficha));

        fuente.Should().Contain("<EnlaceProfundoOtraEmpresa",
            "una ficha ausente y una ficha de otro Tenant son el mismo estado, y ese estado avisa (contrato § 4.5)");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
