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

        // La llamada es una sentencia suelta: la precede el fin de otra («;») o de un bloque («}»), no un «if (…)» ni un
        // «{» de una rama por entorno.
        programa.Should().MatchRegex(
            @"[;}]\s*[\w.]*\bAddValidacionInbound\(\s*builder\.Services\s*,\s*builder\.Configuration\s*\)\s*;",
            "la validación Inbound se compone con la configuración del host, sin una variante por entorno");
        Regex.Matches(programa, @"\bAddValidacionInbound\b").Count.Should().Be(1);
    }

    [Fact]
    public void Program_no_retoca_las_opciones_de_la_validacion_Inbound()
    {
        // Un Configure o PostConfigure de OpcionesValidacionInbound en Program.cs podría volver a atarlas al entorno.
        Regex.Matches(ProgramSinComentarios(), @"\bOpcionesValidacionInbound\b").Count.Should().Be(0);
    }

    [Fact]
    public void La_llamada_de_la_validacion_Inbound_no_esta_dentro_de_ningun_bloque()
    {
        // Una llamada metida tras otra sentencia dentro de un «if» por entorno también va precedida de «;»: lo que la delata es
        // que haya más «{» abiertas que cerradas antes de ella. Program.cs usa sentencias de nivel superior.
        var programa = ProgramSinComentarios();
        var llamada = Regex.Match(programa, @"\bAddValidacionInbound\(");
        llamada.Success.Should().BeTrue("Program.cs registra la validación Inbound");

        var antes = programa[..llamada.Index];
        antes.Count(c => c == '{').Should().Be(antes.Count(c => c == '}'),
            "la llamada va en el nivel superior de Program.cs, no dentro de una rama por entorno");
    }

    [Fact]
    public void Program_no_nombra_la_clave_de_la_validacion_Inbound()
    {
        // Reescribir Inbound:Validacion:Habilitado según el entorno antes de la llamada ataría la validación al entorno sin
        // tocar la llamada ni OpcionesValidacionInbound. La clave solo se lee dentro de AddValidacionInbound. Cualquier literal
        // de texto que empiece una palabra por «validacion» (sin distinguir mayúsculas, como las claves de configuración)
        // sobra en Program.cs: cubre Inbound:Validacion:Habilitado, una sección o la variable Inbound__Validacion__Habilitado,
        // y no confunde «revalidacion» ni el código que queda entre dos literales.
        var literales = Regex.Matches(ProgramSinComentarios(), @"""(?:[^""\\\n]|\\.)*""").Select(m => m.Value);

        literales.Where(l => Regex.IsMatch(l, @"(?<![a-z])validacion", RegexOptions.IgnoreCase)).Should().BeEmpty();
    }

    // Los comentarios de línea y de bloque se quitan de una sola pasada: gana el que empieza antes, así que un «/*» dentro de
    // un comentario de línea (rutas como /cuenta/*) no abre un bloque ni un «//» dentro de un bloque cierra nada de más. Quitar
    // primero un tipo y luego el otro se come código si el otro aparece dentro de uno del primero.
    private static string ProgramSinComentarios()
    {
        var codigo = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        return Regex.Replace(codigo, @"(?<!:)//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
