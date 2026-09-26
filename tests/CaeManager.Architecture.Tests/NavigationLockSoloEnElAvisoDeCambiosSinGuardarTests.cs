using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P1-E2: salir de un formulario con cambios sin guardar pregunta antes. Todo el
/// comportamiento vive en un solo componente, <c>AvisoCambiosSinGuardar</c>, y cada
/// formulario aporta solo su <c>HayCambios</c>. Dos reglas lo sostienen:
///
/// <list type="number">
/// <item>Ningún otro fichero monta un <c>NavigationLock</c> ni registra a mano un
/// manejador de <c>LocationChanging</c>: un segundo aviso con otros textos y otras
/// reglas (¿se repite la navegación?, ¿se pregunta otra vez?) es lo que el componente
/// común evita.</item>
/// <item>Los formularios ya protegidos siguen protegidos: quitar el aviso de uno de
/// ellos pone esto en rojo. La lista solo crece.</item>
/// </list>
/// </summary>
public class NavigationLockSoloEnElAvisoDeCambiosSinGuardarTests
{
    private const string Componente = "src/CaeManager.Web/Components/DesignSystem/AvisoCambiosSinGuardar.razor";

    private static readonly Regex PatronBloqueoPropio = new(
        @"<NavigationLock\b|OpenComponent<NavigationLock>|\bRegisterLocationChangingHandler\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PatronUsoDelAviso = new(@"<AvisoCambiosSinGuardar\b", RegexOptions.Compiled);

    /// <summary>Un Drawer o un Modal que recibe HayCambios: cerrarlo con la X, Escape o el fondo pregunta.</summary>
    private static readonly Regex PatronContenedorQuePregunta = new(@"<(Drawer|Modal)\b[^>]*\bHayCambios=", RegexOptions.Compiled);

    /// <summary>
    /// Formularios con el aviso puesto. Solo crece: un formulario nuevo con estado que se
    /// pueda perder se añade aquí al protegerlo.
    /// </summary>
    private static readonly string[] FormulariosProtegidos =
    [
        "src/CaeManager.Web/Components/DesignSystem/RedactarMensajeDrawer.razor",
        "src/CaeManager.Web/Components/Workspace/ModalContactoAgenda.razor",
        "src/CaeManager.Web/Features/Bandeja/Components/DrawerReclamacionLote.razor",
        "src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Buzon.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Macros.razor",
        "src/CaeManager.Web/Features/Documentos/Components/DrawerGestionDocumento.razor",
        "src/CaeManager.Web/Features/Documentos/Components/FirmaEnCampoTab.razor",
        "src/CaeManager.Web/Features/Documentos/Components/PlantillasTab.razor",
        "src/CaeManager.Web/Features/Documentos/Components/PlataformaTab.razor",
        "src/CaeManager.Web/Features/Documentos/Components/RevisionIaTab.razor",
        "src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor",
        "src/CaeManager.Web/Features/Empresas/Pages/Empresas.razor",
        "src/CaeManager.Web/Features/Incidencias/Pages/Incidencias.razor",
        "src/CaeManager.Web/Features/Proyectos/Pages/Proyectos.razor",
        "src/CaeManager.Web/Features/Retencion/Pages/Retencion.razor",
        "src/CaeManager.Web/Features/Subcontratas/Pages/Subcontratas.razor",
        "src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor",
        "src/CaeManager.Web/Features/Trabajadores/Pages/Trabajadores.razor",
        "src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor",
        "src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor",
    ];

    /// <summary>
    /// P1-E2b (decisión del propietario, 2026-09-26): en estos ficheros el Drawer o Modal del
    /// formulario recibe su HayCambios, así que cerrarlo con la X, Escape o un clic en el
    /// fondo con cambios pregunta «¿Descartar cambios?». Solo crece.
    /// </summary>
    private static readonly string[] ContenedoresQuePreguntanAlCerrar =
    [
        "src/CaeManager.Web/Components/DesignSystem/RedactarMensajeDrawer.razor",
        "src/CaeManager.Web/Components/Workspace/ModalContactoAgenda.razor",
        "src/CaeManager.Web/Features/Bandeja/Components/DrawerReclamacionLote.razor",
        "src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Buzon.razor",
        "src/CaeManager.Web/Features/Comunicaciones/Pages/Macros.razor",
        "src/CaeManager.Web/Features/Documentos/Components/DrawerGestionDocumento.razor",
        "src/CaeManager.Web/Features/Documentos/Components/PlantillasTab.razor",
        "src/CaeManager.Web/Features/Documentos/Components/PlataformaTab.razor",
        "src/CaeManager.Web/Features/Documentos/Components/RevisionIaTab.razor",
        "src/CaeManager.Web/Features/Empresas/Pages/Empresas.razor",
        "src/CaeManager.Web/Features/Incidencias/Pages/Incidencias.razor",
        "src/CaeManager.Web/Features/Proyectos/Pages/Proyectos.razor",
        "src/CaeManager.Web/Features/Retencion/Pages/Retencion.razor",
        "src/CaeManager.Web/Features/Subcontratas/Pages/Subcontratas.razor",
        "src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor",
        "src/CaeManager.Web/Features/Trabajadores/Pages/Trabajadores.razor",
        "src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor",
        "src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor",
    ];

    [Fact]
    public void Los_drawers_y_modales_protegidos_preguntan_al_cerrar()
    {
        var raiz = RaizDelRepositorio();

        var sinPregunta = ContenedoresQuePreguntanAlCerrar
            .Where(ruta => !File.Exists(Path.Combine(raiz, ruta))
                || !PatronContenedorQuePregunta.IsMatch(File.ReadAllText(Path.Combine(raiz, ruta))))
            .ToList();

        sinPregunta.Should().BeEmpty(
            "el Drawer o Modal de estos formularios recibía HayCambios; sin él, la X, Escape o el clic en el fondo " +
            "vuelven a tirar lo escrito sin preguntar (o el fichero se movió sin actualizar la lista)");
    }

    [Fact]
    public void Solo_el_aviso_comun_monta_un_NavigationLock()
    {
        var raiz = RaizDelRepositorio();

        var infractores = FicherosDeLaWeb(raiz, "*.razor", "*.cs")
            .Where(x => x.Ruta != Componente)
            .Where(x => PatronBloqueoPropio.IsMatch(File.ReadAllText(x.Archivo)))
            .Select(x => x.Ruta)
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "el aviso de cambios sin guardar es AvisoCambiosSinGuardar: el formulario le pasa HayCambios " +
            "(y, si hace falta, sus textos o AlDescartar) en vez de montar su propio NavigationLock");
    }

    [Fact]
    public void Los_formularios_protegidos_siguen_llevando_el_aviso()
    {
        var raiz = RaizDelRepositorio();

        var sinAviso = FormulariosProtegidos
            .Where(ruta => !File.Exists(Path.Combine(raiz, ruta))
                || !PatronUsoDelAviso.IsMatch(File.ReadAllText(Path.Combine(raiz, ruta))))
            .ToList();

        sinAviso.Should().BeEmpty(
            "estos formularios ya avisaban antes de perder lo escrito al salir; quitarles AvisoCambiosSinGuardar " +
            "(o mover el fichero sin actualizar la lista) vuelve a perderlo sin aviso");
    }

    /// <summary>Control positivo de los dos patrones, por separado de las listas que vigilan.</summary>
    [Fact]
    public void Los_patrones_detectan_lo_que_vigilan_y_no_lo_parecido()
    {
        PatronBloqueoPropio.IsMatch("<NavigationLock OnBeforeInternalNavigation=\"AntesDeSalirAsync\" />").Should().BeTrue();
        PatronBloqueoPropio.IsMatch("builder.OpenComponent<NavigationLock>(0);").Should().BeTrue();
        PatronBloqueoPropio.IsMatch("_registro = Navegacion.RegisterLocationChangingHandler(AntesDeSalirAsync);").Should().BeTrue();
        PatronBloqueoPropio.IsMatch("// el único sitio que monta un NavigationLock").Should().BeFalse(
            "mencionarlo en un comentario no es montarlo");

        PatronUsoDelAviso.IsMatch("<AvisoCambiosSinGuardar HayCambios=\"() => HayCambiosSinGuardar\" />").Should().BeTrue();
        PatronUsoDelAviso.IsMatch("<AvisoCambiosSinGuardarOtro />").Should().BeFalse();

        PatronContenedorQuePregunta.IsMatch("<Drawer HayCambios=\"() => HayCambiosSinGuardar\" Visible=\"_drawerVisible\">").Should().BeTrue();
        PatronContenedorQuePregunta.IsMatch("<Modal Visible=\"_v\" HayCambios=\"() => X\">").Should().BeTrue();
        PatronContenedorQuePregunta.IsMatch("<Drawer Visible=\"_drawerVisible\">").Should().BeFalse();
        PatronContenedorQuePregunta.IsMatch("<AvisoCambiosSinGuardar HayCambios=\"X\" />").Should().BeFalse(
            "el aviso de navegación no es el Drawer ni el Modal");
    }

    /// <summary>Guarda: el componente existe y el recorrido ve el árbol real; si no, las reglas de arriba pasarían en vacío.</summary>
    [Fact]
    public void El_recorrido_ve_el_arbol_y_el_componente_comun()
    {
        var raiz = RaizDelRepositorio();
        var ficheros = FicherosDeLaWeb(raiz, "*.razor", "*.cs").ToList();

        ficheros.Count.Should().BeGreaterThan(300, "si esto es bajo, el recorrido dejó de ver el árbol real");
        ficheros.Should().Contain(x => x.Ruta == Componente);
        PatronBloqueoPropio.IsMatch(File.ReadAllText(Path.Combine(raiz, Componente))).Should().BeTrue(
            "el componente común es precisamente quien monta el NavigationLock");
    }

    private static IEnumerable<(string Ruta, string Archivo)> FicherosDeLaWeb(string raiz, params string[] patrones)
    {
        var directorio = Path.Combine(raiz, "src", "CaeManager.Web");
        return patrones
            .SelectMany(patron => Directory.EnumerateFiles(directorio, patron, SearchOption.AllDirectories))
            .Where(archivo => !archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(archivo => (Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/'), archivo));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
