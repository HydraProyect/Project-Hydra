using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P1-E1c: todo islote interactivo lleva el envoltorio común de errores. Un islote es una
/// raíz de circuito sin ruta, y hay dos formas de serlo:
/// <list type="bullet">
/// <item>llevar <c>@rendermode InteractiveServer</c> en su propia directiva (NavegacionMovil,
/// NotificacionesPopup…) — se ve por reflexión, en su <see cref="RenderModeAttribute"/>;</item>
/// <item>que otro .razor lo use con <c>@rendermode="InteractiveServer"</c> en el punto de uso
/// (los de MainLayout: SelectorTema, AnfitrionToasts…) — no deja rastro en el tipo, así que se
/// busca en el marcado de todos los .razor.</item>
/// </list>
/// Todas las raíces de una pantalla comparten circuito: una excepción no contenida en un
/// islote de cabecera tumba la página entera. Las páginas las vigila
/// <see cref="PaginasInteractivasConLimiteDeErroresTests"/>; entre los dos trinquetes cubren
/// toda raíz interactiva. Las dos mitades exigidas son las mismas que en las páginas:
/// heredar de <see cref="IsloteInteractivo"/> y envolver todo el marcado en
/// <c>&lt;LimiteDeErrores Islote="this"&gt;</c>.
/// </summary>
public class IslotesInteractivosConLimiteDeErroresTests
{
    private const string AperturaLimite = "<LimiteDeErrores Islote=\"this\">";
    private const string LimiteVacio = "<LimiteDeErrores Islote=\"this\" />";
    private const string CierreLimite = "</LimiteDeErrores>";

    /// <summary>
    /// Etiqueta de componente con <c>@rendermode=</c> en el punto de uso. La etiqueta puede
    /// ocupar varias líneas; su nombre puede venir cualificado con el espacio de nombres.
    /// </summary>
    private static readonly Regex UsoConRenderMode = new(
        @"<(?<nombre>[A-Z][\w.]*)\b[^<>]*?@rendermode\s*=",
        RegexOptions.Singleline);

    private static readonly Regex Directiva = new(
        @"^[﻿\s]*@(page|rendermode|attribute|using|inject|inherits|implements|layout|namespace|typeparam|preservewhitespace)\b.*$",
        RegexOptions.Multiline);

    private static readonly Regex InicioDeCodigo = new(@"^@(code|functions)\b", RegexOptions.Multiline);

    private static readonly Regex ComentarioRazor = new(@"@\*.*?\*@", RegexOptions.Singleline);

    [Fact]
    public void El_recorrido_encuentra_islotes_de_las_dos_formas()
    {
        var islotes = IslotesInteractivos().Select(t => t.Name).ToList();

        islotes.Should().Contain("SelectorTema",
            "control positivo del uso con @rendermode en MainLayout: si la búsqueda en el marcado se queda ciega, falta");
        islotes.Should().Contain("ContextWorkspace",
            "control positivo del uso cualificado con espacio de nombres (<CaeManager.Web.Components.Workspace.ContextWorkspace …>)");
        islotes.Should().Contain("NavegacionMovil",
            "control positivo de la directiva @rendermode propia: si la reflexión se queda ciega, falta");
        islotes.Should().HaveCountGreaterThanOrEqualTo(19,
            "los 13 islotes de MainLayout y los 6 componentes sin ruta con @rendermode propio medidos al crear el trinquete");
        IslotesInteractivos().Should().OnlyContain(t => File.Exists(RutaRazor(t)),
            "cada islote tiene que resolverse a su .razor; si no, la mitad de marcado no estaría mirando nada");
    }

    [Fact]
    public void Todo_uso_con_rendermode_en_el_marcado_se_resuelve_a_un_componente()
    {
        var sinResolver = UsosConRenderMode()
            .Where(uso => ResolverComponente(uso.Nombre) is null)
            .Select(uso => $"{uso.Nombre} ({Path.GetFileName(uso.Fichero)})")
            .ToList();

        sinResolver.Should().BeEmpty(
            "un uso con @rendermode que no se resuelve a un tipo es un islote que el trinquete no puede vigilar");
    }

    [Fact]
    public void Todo_islote_interactivo_hereda_de_IsloteInteractivo()
    {
        var infractores = IslotesInteractivos()
            .Where(t => !typeof(IsloteInteractivo).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        infractores.Should().BeEmpty(
            "un islote interactivo es raíz de un circuito que comparte con la página: sin IsloteInteractivo, una " +
            "excepción en su OnInitializedAsync o en un manejador suyo tumba la pantalla entera (P1-E1c)");
    }

    [Fact]
    public void Todo_islote_interactivo_envuelve_todo_su_marcado_en_LimiteDeErrores_con_el_islote()
    {
        var infractores = IslotesInteractivos()
            .Where(t => !MarcadoEnvuelto(File.ReadAllText(RutaRazor(t))))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        infractores.Should().BeEmpty(
            "todo el marcado del islote va dentro de <LimiteDeErrores Islote=\"this\">…</LimiteDeErrores> (o, si no " +
            "pinta nada, <LimiteDeErrores Islote=\"this\" />): es lo que muestra el aviso compacto cuando el islote " +
            "falla y lo que contiene los fallos al pintar (P1-E1c)");
    }

    [Fact]
    public void Ninguna_pagina_hereda_de_IsloteInteractivo()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var paginasConBaseDeIslote = ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .Where(t => typeof(IsloteInteractivo).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        paginasConBaseDeIslote.Should().BeEmpty(
            "una página lleva PaginaInteractiva y el aviso de región; el compacto es para islotes");
    }

    [Theory]
    [InlineData("@inherits X\n<LimiteDeErrores Islote=\"this\">\n@if (a)\n{\n<span/>\n}\n</LimiteDeErrores>\n\n@code {\n}\n", true)]
    [InlineData("@implements IDisposable\n@* sin marcado *@\n<LimiteDeErrores Islote=\"this\" />\n\n@code { }\n", true)]
    [InlineData("<span>fuera</span>\n<LimiteDeErrores Islote=\"this\">\n</LimiteDeErrores>\n", false)]
    [InlineData("<LimiteDeErrores Islote=\"this\">\n</LimiteDeErrores>\n<span>fuera</span>\n@code { }\n", false)]
    [InlineData("<LimiteDeErrores Pagina=\"this\">\n<span/>\n</LimiteDeErrores>\n", false)]
    [InlineData("<LimiteDeErrores>\n<span/>\n</LimiteDeErrores>\n", false)]
    [InlineData("<span/>\n", false)]
    public void El_analisis_del_marcado_distingue_envuelto_de_no_envuelto(string razor, bool esperado)
    {
        MarcadoEnvuelto(razor).Should().Be(esperado);
    }

    [Theory]
    [InlineData("<SelectorTema @rendermode=\"InteractiveServer\" />", "SelectorTema")]
    [InlineData("<A.B.Panel\n    Titulo=\"x\"\n    @rendermode=\"InteractiveServer\" />", "A.B.Panel")]
    [InlineData("<SelectorIdioma />\n<form method=\"post\">", null)]
    [InlineData("@* necesita su propio @rendermode por lo que explica *@", null)]
    public void La_busqueda_de_usos_con_rendermode_lee_la_etiqueta_y_no_los_comentarios(string razor, string? esperado)
    {
        var nombres = Usos(razor).ToList();

        if (esperado is null)
        {
            nombres.Should().BeEmpty();
        }
        else
        {
            nombres.Should().Equal(esperado);
        }
    }

    private static bool MarcadoEnvuelto(string razor)
    {
        var texto = razor.Replace("\r\n", "\n");
        var codigo = InicioDeCodigo.Match(texto);
        var marcado = codigo.Success ? texto[..codigo.Index] : texto;
        marcado = ComentarioRazor.Replace(marcado, string.Empty);
        marcado = Directiva.Replace(marcado, string.Empty);
        var lineas = marcado.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        if (lineas.Count == 1)
        {
            return lineas[0] == LimiteVacio;
        }

        return lineas.Count >= 2
            && lineas[0] == AperturaLimite
            && lineas[^1] == CierreLimite;
    }

    private static IEnumerable<string> Usos(string razor) =>
        UsoConRenderMode.Matches(ComentarioRazor.Replace(razor, string.Empty))
            .Select(m => m.Groups["nombre"].Value);

    private static List<(string Fichero, string Nombre)> UsosConRenderMode() =>
        Directory.EnumerateFiles(RaizWeb(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !EnSalidaDeCompilacion(f))
            .SelectMany(f => Usos(File.ReadAllText(f)).Select(n => (f, n)))
            .ToList();

    private static List<Type> IslotesInteractivos()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var porDirectiva = ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length == 0)
            .Where(t => t.GetCustomAttribute<RenderModeAttribute>(inherit: true)?.Mode is InteractiveServerRenderMode);
        var porUso = UsosConRenderMode()
            .Select(uso => ResolverComponente(uso.Nombre))
            .OfType<Type>()
            .Where(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length == 0);

        return porDirectiva.Concat(porUso)
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static Type? ResolverComponente(string nombre)
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var candidatos = ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
            .Where(t => nombre.Contains('.') ? t.FullName == nombre : t.Name == nombre)
            .ToList();
        return candidatos.Count == 1 ? candidatos[0] : null;
    }

    private static bool EnSalidaDeCompilacion(string ruta)
    {
        var partes = ruta.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return partes.Contains("bin") || partes.Contains("obj");
    }

    private static string RutaRazor(Type componente)
    {
        const string prefijo = "CaeManager.Web.";
        var relativa = componente.FullName!.StartsWith(prefijo, StringComparison.Ordinal)
            ? componente.FullName[prefijo.Length..]
            : componente.FullName;
        return Path.Combine(RaizWeb(), relativa.Replace('.', Path.DirectorySeparatorChar) + ".razor");
    }

    private static string RaizWeb()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
        {
            actual = actual.Parent;
        }

        if (actual is null)
        {
            throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
        }

        return Path.Combine(actual.FullName, "src", "CaeManager.Web");
    }
}
