using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Contrato del selector de Tenant, § 4.5 e I15: el enlace profundo a una ficha que el Tenant activo no
/// devuelve nunca cambia de empresa por GET. La pista <c>?tenant</c> solo sirve para ofrecer un botón que
/// hace POST con antiforgery a <c>/cuenta/cliente-activo</c>. Aquí se fija por fuente que el componente
/// <c>EnlaceProfundoOtraEmpresa</c> no navega ni enlaza, que solo escribe por ese formulario POST, que cada
/// ficha por Id pinta <c>FichaNoEncontrada</c> cuando su consulta devuelve null (si una ficha nueva se olvida,
/// el Gestor CAE vuelve a un «Reintentar» sin salida), y que <c>FichaNoEncontrada</c> solo navega a su lista
/// y conserva el botón «Abrir en…» de la pista.
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

    private const string FichaNoEncontrada = "src/CaeManager.Web/Components/Layout/FichaNoEncontrada.razor";

    [Theory]
    [InlineData("src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Clientes/Pages/ConfiguracionIaCliente.razor")]
    [InlineData("src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Empresas/Pages/EmpresaDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Empresas/Pages/DeteccionTrabajadores.razor")]
    [InlineData("src/CaeManager.Web/Features/Subcontratas/Pages/SubcontrataDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Vehiculos/Pages/VehiculoDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Proyectos/Pages/ProyectoDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Visitas/Pages/VisitaDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Documentos/Pages/TipoDocumentoDetalle.razor")]
    [InlineData("src/CaeManager.Web/Features/Plantillas/Pages/ConfigurarPlantilla.razor")]
    public void Cada_ficha_por_Id_pinta_FichaNoEncontrada_cuando_su_consulta_devuelve_null(string ficha)
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(), ficha));

        fuente.Should().Contain("<FichaNoEncontrada RutaLista=\"/",
            "una ficha ausente y una ficha de otro Tenant son el mismo estado, y ese estado tiene salida a la lista (contrato § 4.5)");
    }

    [Fact]
    public void FichaNoEncontrada_conserva_el_boton_Abrir_en_de_la_pista_y_solo_navega_a_su_lista()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(), FichaNoEncontrada));

        fuente.Should().Contain("<EnlaceProfundoOtraEmpresa",
            "con una pista ?tenant autorizada, el cambio de empresa sigue siendo el POST del enlace profundo");
        Regex.Matches(fuente, @"NavigateTo\s*\(([^)]*)\)").Select(m => m.Groups[1].Value.Trim())
            .Should().Equal(["RutaLista, replace: true"],
                "la única navegación es volver a la lista del mismo árbol, sin cambiar de empresa por GET");
        Regex.IsMatch(fuente, @"IClienteActivoSeleccionado|ClienteActivoSeleccionado|Response\.Cookies|cliente-activo")
            .Should().BeFalse("la ficha ausente nunca toca la selección de Tenant");
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
