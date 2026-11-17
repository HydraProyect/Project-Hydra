using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cableado de la validación Inbound en <c>Program.cs</c>: la clave <c>Inbound:Validacion:Habilitado</c> la enciende en
/// cualquier entorno (DECISION_LOG 2026-11-17, 2757bac y cc7e68e), así que <c>AddValidacionInbound</c> recibe la
/// configuración del host tal cual, una sola vez. El comportamiento de la opción lo prueba
/// <c>ValidacionInboundConBackendTests</c> en Web.Tests; aquí se vigila que nadie vuelva a filtrar la configuración por
/// entorno al componer, sobre el código sin comentarios (el proyecto no tiene un <c>WebApplicationFactory</c> que lo observe).
/// </summary>
public class ValidacionInboundRegistradaEnProgramTests
{
    [Fact]
    public void Program_registra_la_validacion_Inbound_con_la_configuracion_del_host_tal_cual()
    {
        var programa = ProgramSinComentarios();

        programa.Should().MatchRegex(
            @"[\w.]*\bAddValidacionInbound\(\s*builder\.Services\s*,\s*builder\.Configuration\s*\)\s*;",
            "la validación Inbound se compone con la configuración del host, sin una variante por entorno");
        Regex.Matches(programa, @"\bAddValidacionInbound\b").Count.Should().Be(1);
    }

    private static string ProgramSinComentarios()
    {
        var codigo = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        codigo = Regex.Replace(codigo, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(codigo, @"(?<!:)//[^\n]*", string.Empty);
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
