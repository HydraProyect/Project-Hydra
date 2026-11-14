using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// ADR-013 § 4.3 (test 1) y § 4.7 (mecanismo 3): en Domain y Application el namespace de cada fichero es su carpeta
/// (<c>src/CaeManager.Application/Documentos/Commands/X/Y.cs</c> declara <c>CaeManager.Application.Documentos.Commands.X</c>).
/// Sin esta regla el registro de módulos por prefijo no significaría nada: un tipo podría declararse en el namespace de
/// otro módulo sin moverse de carpeta, o al revés. La revisión de ADR-013 daba 0 desajustes; la medida de M0a encontró
/// 1, que queda en <c>Congelados/Namespace-distinto-de-carpeta.txt</c> con la mecánica de <see cref="ListaCongelada"/>:
/// solo baja, y cualquier desajuste nuevo da rojo.
///
/// <para>
/// Lee el texto: un fichero sin <c>namespace</c> cuenta como desajuste (sus tipos irían al namespace global). Los
/// ficheros que solo llevan directivas (<c>global using</c>, atributos de ensamblado) no declaran tipos y se omiten.
/// </para>
/// </summary>
public partial class NamespaceIgualACarpetaTests
{
    private static readonly string[] Proyectos = ["CaeManager.Domain", "CaeManager.Application"];

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][\w.]*)\s*[;{]?", RegexOptions.Multiline)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*(?:class|record|struct|interface|enum|delegate)\s", RegexOptions.Multiline)]
    private static partial Regex DeclaraTipo();

    [Fact]
    public void En_Domain_y_Application_el_namespace_es_la_carpeta()
    {
        var raiz = FuentesDeSrc.RaizDelRepositorio();
        var ficheros = Proyectos.SelectMany(p => FicherosDe(raiz, p).Select(f => (Proyecto: p, Ruta: f))).ToList();

        ficheros.Should().HaveCountGreaterThan(1000, "control del instrumento: tiene que leer los ficheros de Domain y Application");

        var medido = ficheros
            .SelectMany(f => Desajustes(f.Proyecto, Path.GetRelativePath(Path.Combine(raiz, "src", f.Proyecto), f.Ruta),
                File.ReadAllText(f.Ruta)))
            .Select(d => d.Split(": ", 2))
            .GroupBy(p => new Ubicacion(p[0], p[1]))
            .ToDictionary(g => g.Key, g => g.Count());

        var fallo = ListaCongelada.Verificar(NombreDeLista, medido,
            "El namespace de cada fichero de Domain y Application es su carpeta (ADR-013 § 4.3): mueve el fichero o " +
            "corrige el namespace. La lista solo baja; no se añaden líneas.");
        fallo.Should().BeNull();
    }

    /// <summary>
    /// Deuda medida al crear el test: ADR-013 daba 0 desajustes y hay 1. Se congela en lo medido para no tocar
    /// <c>src</c> en M0a; solo baja, y el objetivo es cero.
    /// </summary>
    private const string NombreDeLista = "Namespace-distinto-de-carpeta";

    [Fact]
    public void Control_un_namespace_que_no_es_su_carpeta_da_rojo()
    {
        Desajustes("CaeManager.Application", Path.Combine("Documentos", "Commands", "X.cs"),
                "namespace CaeManager.Application.Documentos.Commands;\npublic class X;")
            .Should().BeEmpty("control: el caso correcto no da falso positivo");

        Desajustes("CaeManager.Application", Path.Combine("Documentos", "Commands", "X.cs"),
                "namespace CaeManager.Application.Inbound.Documentos.Commands;\npublic class X;")
            .Should().ContainSingle().Which.Should().Contain("CaeManager.Application.Inbound.Documentos.Commands");

        Desajustes("CaeManager.Domain", Path.Combine("Empresas", "Y.cs"), "namespace CaeManager.Domain.Empresas\n{\n  public class Y {}\n}")
            .Should().BeEmpty("control: el namespace de bloque también se lee");

        Desajustes("CaeManager.Domain", Path.Combine("Empresas", "Z.cs"), "public sealed class Z;")
            .Should().ContainSingle("control: un tipo sin namespace va al global y es un desajuste");

        Desajustes("CaeManager.Domain", "GlobalUsings.cs", "global using System;")
            .Should().BeEmpty("control: un fichero sin tipos no declara namespace");

        Desajustes("CaeManager.Domain", Path.Combine("Empresas", "W.cs"),
                "namespace CaeManager.Domain.Empresas;\nnamespace CaeManager.Domain.Centros;\npublic class W;")
            .Should().ContainSingle("control: cada declaración de namespace del fichero cuenta");
    }

    internal static IEnumerable<string> Desajustes(string proyecto, string rutaRelativa, string texto)
    {
        var carpeta = Path.GetDirectoryName(rutaRelativa) ?? string.Empty;
        var esperado = carpeta.Length == 0
            ? proyecto
            : proyecto + "." + carpeta.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
        var sinComentarios = LimpiadorDeComentarios.Quitar(texto, razor: false);
        var declarados = Namespace().Matches(sinComentarios).Select(m => m.Groups[1].Value).ToList();
        var ruta = $"src/{proyecto}/{rutaRelativa.Replace('\\', '/')}";

        if (declarados.Count == 0)
        {
            if (DeclaraTipo().IsMatch(sinComentarios))
                yield return $"{ruta}: sin namespace (esperado {esperado})";
            yield break;
        }

        foreach (var declarado in declarados.Where(d => d != esperado))
            yield return $"{ruta}: {declarado} (esperado {esperado})";
    }

    private static IEnumerable<string> FicherosDe(string raiz, string proyecto)
    {
        var s = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(raiz, "src", proyecto), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}"));
    }
}
