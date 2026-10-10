using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo listado con <c>&lt;BarraFiltros&gt;</c> recuerda su vista (decisión del 2026-10-08): monta
/// <c>&lt;VistaRecordadaDeListado&gt;</c> con su lista blanca y pasa la conexión a la barra, que es la que
/// pinta «Restablecer vista». Un listado nuevo con barra de filtros y sin la pieza se queda en rojo.
///
/// <para>
/// Visitas no tiene <c>&lt;BarraFiltros&gt;</c> todavía (su cabecera se está reescribiendo): cuando la
/// tenga entrará sola en la medida. Los nueve listados con barra montan ya la pieza: no queda
/// ninguno congelado.
/// </para>
/// </summary>
public class ListadosConBarraFiltrosRecuerdanLaVistaTests
{
    private const string Paginas = "src/CaeManager.Web/Features";

    private static Dictionary<string, string> ListadosConBarra()
    {
        var raiz = FuentesDeSrc.RaizDelRepositorio();
        return Directory.EnumerateFiles(Path.Combine(raiz, Paginas), "*.razor", SearchOption.AllDirectories)
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Texto: File.ReadAllText(f)))
            .Where(f => f.Texto.Contains("<BarraFiltros ", StringComparison.Ordinal))
            .ToDictionary(f => f.Ruta, f => f.Texto);
    }

    private static bool RecuerdaLaVista(string marcado) =>
        marcado.Contains("<VistaRecordadaDeListado ", StringComparison.Ordinal)
        && marcado.Contains("VistaRecordada=\"_vistaRecordada\"", StringComparison.Ordinal)
        && marcado.Contains("Conexion=\"_vistaRecordada\"", StringComparison.Ordinal)
        && marcado.Contains("ParametrosDeVista=\"ParametrosDeVista\"", StringComparison.Ordinal);

    [Fact]
    public void El_recorrido_ve_los_nueve_listados_con_barra_de_filtros()
    {
        // Control positivo: si el recorrido no viera las páginas, «ninguno sin la pieza» valdría por vacío.
        ListadosConBarra().Keys.Select(Path.GetFileNameWithoutExtension).Should().BeEquivalentTo(
            ["Trabajadores", "Empresas", "Clientes", "Documentos", "Centros", "Subcontratas", "Vehiculos", "Proyectos", "Gestiones"]);
    }

    [Fact]
    public void Todo_listado_con_barra_de_filtros_monta_la_vista_recordada()
    {
        // No vale por vacío: el control positivo de arriba fija que el recorrido ve los nueve.
        var sinPieza = ListadosConBarra().Where(l => !RecuerdaLaVista(l.Value)).Select(l => l.Key).ToList();

        sinPieza.Should().BeEmpty(
            "un listado con <BarraFiltros> monta <VistaRecordadaDeListado Conexion=\"_vistaRecordada\" ParametrosDeVista=\"ParametrosDeVista\" …> "
            + "y pasa VistaRecordada=\"_vistaRecordada\" a la barra");
    }

    [Fact]
    public void Quitar_la_pieza_o_la_conexion_de_la_barra_se_detecta()
    {
        var vehiculos = ListadosConBarra()["src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor"];

        RecuerdaLaVista(vehiculos).Should().BeTrue("control: la página real cumple");
        RecuerdaLaVista(vehiculos.Replace("<VistaRecordadaDeListado ", "<Otra ")).Should().BeFalse();
        RecuerdaLaVista(vehiculos.Replace("VistaRecordada=\"_vistaRecordada\"", string.Empty)).Should().BeFalse();
    }
}
