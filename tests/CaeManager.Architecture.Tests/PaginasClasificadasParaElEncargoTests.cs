using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Toda página y todo endpoint de Administrador o de Dirección CAE está clasificado para el
/// Encargo de administración</b> (decisión D-8, 2026-10-08).
///
/// <para>
/// Quien administra por encargo lleva el rol elevado en sus claims (Administrador o Dirección CAE,
/// el que tenga en su Tenant de origen) y cumple cualquier <c>[Authorize(Roles = …)]</c> que lo
/// nombre. Las páginas que el encargo no abre se nombran en <c>PaginasExcluidasDelEncargo</c> y los
/// endpoints mínimos llevan <c>.ExcluidoDelEncargoDeAdministracion()</c>. Este trinquete obliga a
/// que una página nueva de esos dos roles se declare excluida o permitida, y a que un endpoint
/// nuevo lleve la marca.
/// </para>
///
/// <para>
/// <b>Lo que observa</b>. Páginas, por dos caminos que tienen que coincidir: por texto, cada
/// <c>.razor</c> de <c>src/CaeManager.Web</c> cuyo <c>@attribute [Authorize(…)]</c> nombra
/// <c>Roles.Administrador</c>, <c>Roles.DireccionCae</c> o una <c>Policy</c> sin admitir
/// <c>Roles.CoordinadorCae</c>; y por reflexión, cada tipo del ensamblado de Web cuyo
/// <c>AuthorizeAttribute</c> ya compilado dice lo mismo, que es lo que ve el router y lo que
/// descubre una puerta escrita con una constante intermedia (<c>Roles = RolesDeLaPagina</c>) o en
/// el <c>.razor.cs</c>. Endpoints, por texto: cada <c>RequireAuthorization(…)</c> de un <c>.cs</c>
/// de Web que nombra <c>Roles.Administrador</c> o <c>Roles.DireccionCae</c> sin
/// <c>Roles.CoordinadorCae</c>, y además que ninguno pase sus roles por una constante que este
/// texto no puede leer sin estar declarada aquí.
/// <b>Lo que NO observa</b> (huecos declarados): una puerta escrita con <c>User.IsInRole(…)</c> en
/// línea (hoy solo <c>/cuenta/vista-vocabulario</c>, que escribe una cookie de vista previa); un
/// endpoint protegido con una política con nombre (<c>ApiPublica</c>, <c>SesionOExtension</c>: no
/// piden rol de Propiedad); un panel que el hub de Configuración embebe sin ruta, que cierra el
/// propio hub; y si la clasificación es acertada.
/// </para>
/// </summary>
public class PaginasClasificadasParaElEncargoTests
{
    private const string FicheroDeLaLista = "src/CaeManager.Web/Services/PaginasExcluidasDelEncargo.cs";

    /// <summary>
    /// Páginas de Administrador o Dirección CAE que el encargo SÍ abre, con el motivo. Las demás
    /// tienen que estar en <c>PaginasExcluidasDelEncargo</c>.
    /// </summary>
    private static readonly Dictionary<string, string> Permitidas = new(StringComparer.Ordinal)
    {
        ["src/CaeManager.Web/Features/Configuracion/Pages/Configuracion.razor"] =
            "el hub de Configuración: administrar el Tenant propietario es el objeto del encargo; sus entradas "
            + "excluidas las filtra el propio hub",
        ["src/CaeManager.Web/Features/TiposDocumento/Pages/TiposDocumento.razor"] =
            "catálogo de tipos de documento; sus cuatro ajustes globales de IA se excluyen en Application por tipo",
        ["src/CaeManager.Web/Features/Clientes/Pages/ConfiguracionIaCliente.razor"] =
            "lectura con IA por Cliente empresarial: permitida por § 9.7 del diseño",
        ["src/CaeManager.Web/Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor"] =
            "cuadro de mando: lectura agregada del Tenant propietario, permitida por § 9.7 del diseño",
    };

    private static readonly Regex AtributoAuthorize = new(@"@attribute\s+\[Authorize\((?<args>[^\]]*)\)\]", RegexOptions.Compiled);

    private static readonly Regex TipoDePagina = new(@"typeof\(Features\.(?<tipo>[\w.]+)\)", RegexOptions.Compiled);

    private static readonly Regex Comentarios = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static bool EsDeAdministradorSinCoordinador(string argumentos) =>
        (argumentos.Contains("Roles.Administrador", StringComparison.Ordinal)
         || argumentos.Contains("Roles.DireccionCae", StringComparison.Ordinal)
         || argumentos.Contains("Policy", StringComparison.Ordinal))
        && !argumentos.Contains("Roles.CoordinadorCae", StringComparison.Ordinal);

    [Fact]
    public void Toda_pagina_de_Administrador_esta_excluida_del_encargo_o_declarada_permitida()
    {
        var paginasDeAdministrador = Ficheros("*.razor")
            .Where(f => AtributoAuthorize.Matches(File.ReadAllText(Absoluta(f)))
                .Any(m => EsDeAdministradorSinCoordinador(m.Groups["args"].Value)))
            .ToList();

        var excluidas = PaginasExcluidasEnElCodigo();

        paginasDeAdministrador.Should().Contain("src/CaeManager.Web/Features/ApiKeys/Pages/ClavesApi.razor",
            "control positivo: ClavesApi pide Administrador; si no aparece, el detector ha dejado de observar");
        excluidas.Should().HaveCountGreaterThan(10, "control positivo: la lista de páginas excluidas se lee del código");
        excluidas.Should().NotIntersectWith(Permitidas.Keys);

        paginasDeAdministrador.Should().BeEquivalentTo(excluidas.Concat(Permitidas.Keys),
            "una página nueva que pida Administrador o Dirección CAE se añade a PaginasExcluidasDelEncargo o, si el "
            + "encargo debe abrirla, a Permitidas con su motivo; y una que ya no lo pida se retira de donde esté");
    }

    /// <summary>
    /// La misma pregunta que <see cref="Toda_pagina_de_Administrador_esta_excluida_del_encargo_o_declarada_permitida"/>,
    /// hecha al ensamblado y no al texto: el atributo ya compilado lleva los roles resueltos, así
    /// que una constante intermedia sin Coordinador CAE o un <c>[Authorize]</c> puesto en el
    /// <c>.razor.cs</c> no se le escapan. Si los dos caminos discrepan, el de texto ha dejado de ver
    /// una puerta y hay que clasificarla.
    /// </summary>
    [Fact]
    public void Las_puertas_compiladas_de_Administrador_o_Direccion_CAE_son_las_mismas_que_ve_el_detector_por_texto()
    {
        var porTexto = Ficheros("*.razor")
            .Where(f => AtributoAuthorize.Matches(File.ReadAllText(Absoluta(f)))
                .Any(m => EsDeAdministradorSinCoordinador(m.Groups["args"].Value)))
            .ToList();

        const string prefijo = "CaeManager.Web.";
        var compiladas = typeof(CurrentUserService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any(EsDePropiedadSinCoordinador))
            .Select(t => t.FullName!.StartsWith(prefijo, StringComparison.Ordinal)
                ? "src/CaeManager.Web/" + t.FullName[prefijo.Length..].Replace('.', '/') + ".razor"
                : t.FullName)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        compiladas.Should().Contain("src/CaeManager.Web/Features/ApiKeys/Pages/ClavesApi.razor",
            "control positivo: la reflexión encuentra las páginas y las traduce a la ruta de su .razor");
        compiladas.Where(r => !File.Exists(Absoluta(r))).Should().BeEmpty(
            "cada tipo con puerta de Propiedad tiene que corresponder a un .razor; si no, la traducción de tipo a ruta "
            + "ha dejado de valer y la comparación de abajo no significa nada");

        compiladas.Should().BeEquivalentTo(porTexto,
            "una puerta de Administrador o Dirección CAE que solo aparece compilada está escrita con una constante "
            + "intermedia o en el .razor.cs: el detector por texto no la ve y quedaría sin clasificar para el encargo");
    }

    [Fact]
    public void Todo_endpoint_de_Administrador_o_Direccion_CAE_lleva_la_marca_de_excluido_del_encargo()
    {
        var sentencias = Ficheros("*.cs")
            .SelectMany(f => SentenciasConRequireAuthorization(f).Select(s => (Fichero: f, Sentencia: s)))
            .Where(x => EsEndpointDePropiedadSinCoordinador(x.Sentencia))
            .ToList();

        sentencias.Should().HaveCountGreaterThanOrEqualTo(8,
            "control positivo: auditoría (2), Microsoft 365 (2), plantillas de importación (3) y facturación (1)");

        sentencias.Where(x => !x.Sentencia.Contains(".ExcluidoDelEncargoDeAdministracion()", StringComparison.Ordinal))
            .Select(x => x.Fichero)
            .Should().BeEmpty(
                "un endpoint que pide Administrador o Dirección CAE sin admitir Coordinador CAE lo abriría el rol elevado "
                + "por el encargo: lleva .ExcluidoDelEncargoDeAdministracion() salvo que se decida abrirlo, y entonces se "
                + "declara aquí");
    }

    /// <summary>
    /// Endpoints cuyo <c>RequireRole(…)</c> no nombra ningún <c>Roles.X</c>: pasan los roles por una
    /// constante que el detector por texto no puede leer. Cada uno se declara aquí con la definición
    /// que tiene que seguir teniendo; uno nuevo, o uno que deje de admitir Coordinador CAE, pone el
    /// test en rojo.
    /// </summary>
    private static readonly Dictionary<string, Regex> EndpointsConRolesEnUnaConstante = new(StringComparer.Ordinal)
    {
        ["src/CaeManager.Web/Reportes/ReportesEndpoints.cs"] = new Regex(
            @"RolesConAccesoAReportes\s*=\s*\[[^\]]*Roles\.CoordinadorCae[^\]]*\]", RegexOptions.Compiled),
    };

    [Fact]
    public void Ningun_endpoint_esconde_roles_de_Propiedad_tras_una_constante_sin_declarar()
    {
        var conConstante = Ficheros("*.cs")
            .Where(f => SentenciasConRequireAuthorization(f).Any(PasaLosRolesPorUnaConstante))
            .ToList();

        conConstante.Should().BeEquivalentTo(EndpointsConRolesEnUnaConstante.Keys,
            "un RequireRole(…) que no nombra Roles.X no lo puede clasificar el detector: se escribe con los roles a la "
            + "vista o se declara aquí con la definición de su constante");

        foreach (var (fichero, definicion) in EndpointsConRolesEnUnaConstante)
            definicion.IsMatch(File.ReadAllText(Absoluta(fichero))).Should().BeTrue(
                $"la constante de roles de {fichero} tiene que seguir admitiendo Coordinador CAE; si deja de hacerlo, sus "
                + "endpoints los abre el encargo y necesitan la marca");
    }

    [Fact]
    public void El_detector_distingue_las_puertas_de_Administrador_de_las_que_admiten_Coordinador()
    {
        EsDeAdministradorSinCoordinador("Roles = CaeManager.Infrastructure.Identity.Roles.Administrador").Should().BeTrue();
        EsDeAdministradorSinCoordinador("Roles = $\"{X.Roles.Administrador},{X.Roles.DireccionCae}\"").Should().BeTrue();
        EsDeAdministradorSinCoordinador("Policy = Policies.ConsultarAccesoDocumentosSensibles").Should().BeTrue();
        EsDeAdministradorSinCoordinador("Roles = $\"{X.Roles.Administrador},{X.Roles.CoordinadorCae}\"").Should().BeFalse(
            "lo que ya ve un Coordinador CAE no lo abre el encargo: lo tenía la cartera");
        EsDeAdministradorSinCoordinador("Roles = X.Roles.GestorCae").Should().BeFalse();

        var m = AtributoAuthorize.Match("@attribute [Authorize(Roles = CaeManager.Infrastructure.Identity.Roles.Administrador)]");
        m.Success.Should().BeTrue();
        m.Groups["args"].Value.Should().Contain("Roles.Administrador");
        AtributoAuthorize.IsMatch("@attribute [Authorize]").Should().BeFalse("sin argumentos no pide rol");
    }

    /// <summary>
    /// Corrección C3 (2026-10-09): el rol elevado puede ser Dirección CAE, no solo Administrador. Hoy
    /// ninguna página ni endpoint se pide solo a Dirección CAE (medido al ampliar el detector: los
    /// ocho endpoints detectados son los mismos), así que la sensibilidad se prueba aquí, sobre los
    /// tres detectores, con puertas que no existen todavía.
    /// </summary>
    [Fact]
    public void Los_tres_detectores_ven_una_puerta_pedida_solo_a_Direccion_CAE()
    {
        EsDeAdministradorSinCoordinador("Roles = Roles.DireccionCae").Should().BeTrue();

        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(p => p.RequireRole(Roles.DireccionCae))").Should().BeTrue();
        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(p => p.RequireRole(Roles.Administrador))").Should().BeTrue();
        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(p => p.RequireRole(Roles.Administrador, Roles.DireccionCae))")
            .Should().BeTrue();
        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(p => p.RequireRole(Roles.DireccionCae, Roles.CoordinadorCae))")
            .Should().BeFalse("lo que ya ve un Coordinador CAE no lo abre el encargo");
        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(p => p.RequireRole(Roles.GestorCae))").Should().BeFalse();
        EsEndpointDePropiedadSinCoordinador("RequireAuthorization(Policies.SesionOExtension)").Should().BeFalse();

        EsDePropiedadSinCoordinador(new AuthorizeAttribute { Roles = Roles.DireccionCae }).Should().BeTrue();
        EsDePropiedadSinCoordinador(new AuthorizeAttribute { Roles = Roles.Administrador }).Should().BeTrue();
        EsDePropiedadSinCoordinador(new AuthorizeAttribute { Roles = $"{Roles.Administrador}, {Roles.DireccionCae}" }).Should().BeTrue();
        EsDePropiedadSinCoordinador(new AuthorizeAttribute("UnaPolitica")).Should().BeTrue("una política se clasifica siempre");
        EsDePropiedadSinCoordinador(new AuthorizeAttribute { Roles = $"{Roles.DireccionCae},{Roles.CoordinadorCae}" }).Should().BeFalse();
        EsDePropiedadSinCoordinador(new AuthorizeAttribute { Roles = Roles.GestorCae }).Should().BeFalse();
        EsDePropiedadSinCoordinador(new AuthorizeAttribute()).Should().BeFalse("sin roles ni política no pide rol");

        PasaLosRolesPorUnaConstante("RequireAuthorization(p => p.RequireRole(RolesDeAlgo))").Should().BeTrue();
        PasaLosRolesPorUnaConstante("RequireAuthorization(p => p.RequireRole(Roles.DireccionCae))").Should().BeFalse();
        PasaLosRolesPorUnaConstante("RequireAuthorization(Policies.SesionOExtension)").Should().BeFalse();
    }

    private static bool EsEndpointDePropiedadSinCoordinador(string sentencia) =>
        (sentencia.Contains("Roles.Administrador", StringComparison.Ordinal)
         || sentencia.Contains("Roles.DireccionCae", StringComparison.Ordinal))
        && !sentencia.Contains("Roles.CoordinadorCae", StringComparison.Ordinal);

    private static bool PasaLosRolesPorUnaConstante(string sentencia) =>
        sentencia.Contains("RequireRole(", StringComparison.Ordinal)
        && !sentencia.Contains("Roles.", StringComparison.Ordinal);

    /// <summary>El atributo ya compilado: sus roles son el texto final, con las constantes resueltas.</summary>
    private static bool EsDePropiedadSinCoordinador(AuthorizeAttribute atributo)
    {
        if (!string.IsNullOrEmpty(atributo.Policy))
            return true;

        var roles = (atributo.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return (roles.Contains(Roles.Administrador) || roles.Contains(Roles.DireccionCae))
               && !roles.Contains(Roles.CoordinadorCae);
    }

    /// <summary>Las páginas que nombra <c>PaginasExcluidasDelEncargo</c>, como rutas de su <c>.razor</c>.</summary>
    private static List<string> PaginasExcluidasEnElCodigo() =>
        TipoDePagina.Matches(Comentarios.Replace(File.ReadAllText(Absoluta(FicheroDeLaLista)), " "))
            .Select(m => "src/CaeManager.Web/Features/" + m.Groups["tipo"].Value.Replace('.', '/') + ".razor")
            .ToList();

    /// <summary>Cada sentencia (hasta su <c>;</c>) que contiene un <c>RequireAuthorization(</c>.</summary>
    private static IEnumerable<string> SentenciasConRequireAuthorization(string fichero)
    {
        var codigo = Comentarios.Replace(File.ReadAllText(Absoluta(fichero)), " ");
        var desde = 0;

        while ((desde = codigo.IndexOf("RequireAuthorization(", desde, StringComparison.Ordinal)) >= 0)
        {
            var fin = codigo.IndexOf(';', desde);
            if (fin < 0) fin = codigo.Length;
            yield return codigo[desde..fin];
            desde = fin;
        }
    }

    private static List<string> Ficheros(string patron)
    {
        var raiz = Raiz();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "src", "CaeManager.Web"), patron, SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
    }

    private static string Absoluta(string relativo) =>
        Path.Combine(Raiz(), relativo.Replace('/', Path.DirectorySeparatorChar));

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
