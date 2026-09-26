using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P1-E1b: toda página con <c>@rendermode InteractiveServer</c> lleva el envoltorio común
/// de errores, en sus dos mitades:
/// <list type="number">
/// <item>hereda de <see cref="PaginaInteractiva"/> (contiene lo que lanza la propia
/// página: ciclo de vida y manejadores) — comprobado por reflexión sobre el ensamblado
/// compilado;</item>
/// <item>envuelve TODO su marcado en <c>&lt;LimiteDeErrores Pagina="this"&gt;</c>
/// (contiene lo que falle al pintar ese marcado y en sus hijos, y muestra el aviso) —
/// comprobado sobre el .razor, porque el marcado no deja rastro en la reflexión.</item>
/// </list>
/// Sin las dos, una excepción en una página InteractiveServer tumba el circuito: la
/// página es la raíz de su circuito y el límite de MainLayout, estático, no la cubre.
/// </summary>
public class PaginasInteractivasConLimiteDeErroresTests
{
    /// <summary>
    /// TEMPORAL — se vacía en la PR siguiente de P1-E1b (Visitas, Documentos y Mi
    /// trabajo). Es solo una lista blanca: no interviene en el control
    /// positivo, que se mide sobre todas las páginas interactivas, exentas incluidas.
    /// </summary>
    private static readonly HashSet<string> ExentasTemporales = new(StringComparer.Ordinal)
    {
        "CaeManager.Web.Features.Visitas.Pages.Visitas",
        "CaeManager.Web.Features.Documentos.Pages.Documentos",
        "CaeManager.Web.Features.Documentos.Pages.ImportarDocumentos",
        "CaeManager.Web.Features.Documentos.Pages.RevisionIa",
        "CaeManager.Web.Features.Documentos.Pages.SubidaMasiva",
        "CaeManager.Web.Features.Bandeja.Pages.MiTrabajo",
    };

    private const string AperturaLimite = "<LimiteDeErrores Pagina=\"this\">";
    private const string LimiteVacio = "<LimiteDeErrores Pagina=\"this\" />";
    private const string CierreLimite = "</LimiteDeErrores>";

    private static readonly Regex Directiva = new(
        @"^[﻿\s]*@(page|rendermode|attribute|using|inject|inherits|implements|layout|namespace|typeparam|preservewhitespace)\b.*$",
        RegexOptions.Multiline);

    private static readonly Regex InicioDeCodigo = new(@"^@(code|functions)\b", RegexOptions.Multiline);

    private static readonly Regex ComentarioRazor = new(@"@\*.*?\*@", RegexOptions.Singleline);

    [Fact]
    public void El_recorrido_encuentra_paginas_interactivas_de_verdad()
    {
        var paginas = PaginasInteractivas();

        paginas.Should().HaveCountGreaterThan(0, "un recorrido vacío daría verde sin mirar nada");
        paginas.Select(p => p.FullName).Should().Contain(
            "CaeManager.Web.Features.Alertas.Pages.Alertas",
            "control positivo: una página InteractiveServer conocida tiene que aparecer en el recorrido");
        paginas.Should().OnlyContain(p => File.Exists(RutaRazor(p)),
            "cada página tiene que resolverse a su .razor; si no, la mitad de marcado no estaría mirando nada");
    }

    [Fact]
    public void Toda_pagina_InteractiveServer_hereda_de_PaginaInteractiva()
    {
        var infractoras = PaginasInteractivas()
            .Where(p => !ExentasTemporales.Contains(p.FullName!))
            .Where(p => !typeof(PaginaInteractiva).IsAssignableFrom(p))
            .Select(p => p.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        infractoras.Should().BeEmpty(
            "una página InteractiveServer es la raíz de su circuito: sin PaginaInteractiva, una excepción en su " +
            "OnInitializedAsync o en un manejador suyo no la contiene ningún límite y tumba el circuito (P1-E1b)");
    }

    [Fact]
    public void Toda_pagina_InteractiveServer_envuelve_todo_su_marcado_en_LimiteDeErrores_con_la_pagina()
    {
        var infractoras = PaginasInteractivas()
            .Where(p => !ExentasTemporales.Contains(p.FullName!))
            .Where(p => !MarcadoEnvuelto(File.ReadAllText(RutaRazor(p))))
            .Select(p => p.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        infractoras.Should().BeEmpty(
            "todo el marcado de la página va dentro de <LimiteDeErrores Pagina=\"this\">…</LimiteDeErrores> (o, si " +
            "la página no pinta nada, <LimiteDeErrores Pagina=\"this\" />): es lo que muestra el aviso cuando la " +
            "página falla y lo que contiene los fallos al pintar (P1-E1b)");
    }

    [Fact]
    public void Las_exentas_temporales_siguen_siendo_paginas_interactivas_sin_envoltorio()
    {
        var paginas = PaginasInteractivas().ToDictionary(p => p.FullName!, StringComparer.Ordinal);

        foreach (var exenta in ExentasTemporales)
        {
            paginas.Should().ContainKey(exenta, "una exenta que ya no es página interactiva sobra en la lista");
            var tipo = paginas[exenta];
            var cubierta = typeof(PaginaInteractiva).IsAssignableFrom(tipo)
                && MarcadoEnvuelto(File.ReadAllText(RutaRazor(tipo)));
            cubierta.Should().BeFalse($"{exenta} ya lleva el envoltorio: sácala de ExentasTemporales");
        }
    }

    [Theory]
    [InlineData("@page \"/x\"\n@rendermode InteractiveServer\n\n<LimiteDeErrores Pagina=\"this\">\n<h1>Hola</h1>\n</LimiteDeErrores>\n\n@code {\n}\n", true)]
    [InlineData("@page \"/x\"\n@* comentario *@\n@inject X Y\n<LimiteDeErrores Pagina=\"this\" />\n\n@code { }\n", true)]
    [InlineData("@page \"/x\"\n<h1>Hola</h1>\n<LimiteDeErrores Pagina=\"this\">\n</LimiteDeErrores>\n", false)]
    [InlineData("@page \"/x\"\n<LimiteDeErrores Pagina=\"this\">\n</LimiteDeErrores>\n<p>fuera</p>\n@code { }\n", false)]
    [InlineData("@page \"/x\"\n<LimiteDeErrores>\n<h1>Hola</h1>\n</LimiteDeErrores>\n", false)]
    [InlineData("@page \"/x\"\n<h1>Hola</h1>\n", false)]
    public void El_analisis_del_marcado_distingue_envuelto_de_no_envuelto(string razor, bool esperado)
    {
        MarcadoEnvuelto(razor).Should().Be(esperado);
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

    private static List<Type> PaginasInteractivas()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        return ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => !t.IsAbstract && t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .Where(t => t.GetCustomAttribute<RenderModeAttribute>(inherit: true)?.Mode is InteractiveServerRenderMode)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static string RutaRazor(Type pagina)
    {
        const string prefijo = "CaeManager.Web.";
        var relativa = pagina.FullName!.StartsWith(prefijo, StringComparison.Ordinal)
            ? pagina.FullName[prefijo.Length..]
            : pagina.FullName;
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
