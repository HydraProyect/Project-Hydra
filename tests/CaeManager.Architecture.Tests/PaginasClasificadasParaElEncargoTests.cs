using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Toda página y todo endpoint de Administrador está clasificado para el Encargo de
/// administración</b> (decisión D-8, 2026-10-08).
///
/// <para>
/// Quien administra por encargo lleva el rol elevado en sus claims y cumple cualquier
/// <c>[Authorize(Roles = Administrador)]</c>. Las páginas que el encargo no abre se nombran en
/// <c>PaginasExcluidasDelEncargo</c> y los endpoints mínimos llevan
/// <c>.ExcluidoDelEncargoDeAdministracion()</c>. Este trinquete obliga a que una página nueva de
/// Administrador se declare excluida o permitida, y a que un endpoint nuevo de Administrador lleve la
/// marca.
/// </para>
///
/// <para>
/// <b>Lo que observa</b>, por texto: cada <c>.razor</c> de <c>src/CaeManager.Web</c> cuyo
/// <c>@attribute [Authorize(…)]</c> nombra <c>Roles.Administrador</c>, <c>Roles.DireccionCae</c> o
/// una <c>Policy</c> sin admitir <c>Roles.CoordinadorCae</c>; y cada <c>RequireAuthorization(…)</c> de
/// un <c>.cs</c> de Web que nombra <c>Roles.Administrador</c> sin <c>Roles.CoordinadorCae</c>.
/// <b>Lo que NO observa</b> (huecos declarados): una puerta escrita con <c>User.IsInRole(…)</c> en
/// línea (hoy solo <c>/cuenta/vista-vocabulario</c>, que escribe una cookie de vista previa); un panel
/// que el hub de Configuración embebe sin ruta, que cierra el propio hub; y si la clasificación es
/// acertada.
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

    [Fact]
    public void Todo_endpoint_de_Administrador_lleva_la_marca_de_excluido_del_encargo()
    {
        var sentencias = Ficheros("*.cs")
            .SelectMany(f => SentenciasConRequireAuthorization(f).Select(s => (Fichero: f, Sentencia: s)))
            .Where(x => x.Sentencia.Contains("Roles.Administrador", StringComparison.Ordinal)
                        && !x.Sentencia.Contains("Roles.CoordinadorCae", StringComparison.Ordinal))
            .ToList();

        sentencias.Should().HaveCountGreaterThanOrEqualTo(8,
            "control positivo: auditoría (2), Microsoft 365 (2), plantillas de importación (3) y facturación (1)");

        sentencias.Where(x => !x.Sentencia.Contains(".ExcluidoDelEncargoDeAdministracion()", StringComparison.Ordinal))
            .Select(x => x.Fichero)
            .Should().BeEmpty(
                "un endpoint que pide Administrador sin admitir Coordinador CAE lo abriría el rol elevado por el encargo: "
                + "lleva .ExcluidoDelEncargoDeAdministracion() salvo que se decida abrirlo, y entonces se declara aquí");
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
