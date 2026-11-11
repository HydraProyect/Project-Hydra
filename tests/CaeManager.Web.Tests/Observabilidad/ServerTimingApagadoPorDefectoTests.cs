using System.Text.Json;
using CaeManager.Web.Services;
using FluentAssertions;

namespace CaeManager.Web.Tests.Observabilidad;

/// <summary>
/// La cabecera Server-Timing divulga a un usuario autenticado cuánto costó su página
/// (OWASP ASVS 4.0.3 V14.3): por eso va apagada salvo que el despliegue la encienda
/// con una variable de entorno. Estos tests fijan ese valor por defecto, que ningún
/// appsettings del proyecto Web lo encienda, y que el middleware siga cableado en
/// Program.cs detrás de las cabeceras de seguridad.
/// </summary>
public class ServerTimingApagadoPorDefectoTests
{
    [Fact]
    public void La_opcion_nace_apagada()
    {
        new OpcionesServerTiming().Habilitado.Should().BeFalse();
    }

    [Fact]
    public void Ningun_appsettings_del_proyecto_Web_la_enciende()
    {
        var ficheros = Directory.GetFiles(Path.Combine(Raiz(), "src", "CaeManager.Web"), "appsettings*.json");
        ficheros.Should().Contain(f => Path.GetFileName(f) == "appsettings.json");

        foreach (var fichero in ficheros)
        {
            using var documento = JsonDocument.Parse(File.ReadAllText(fichero),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var habilitado = Leer(documento.RootElement, "Observabilidad", "ServerTiming", "Habilitado");

            if (Path.GetFileName(fichero) == "appsettings.json")
                habilitado.Should().NotBeNull("appsettings.json declara el valor por defecto de forma explícita");

            if (habilitado is { } valor)
                valor.ValueKind.Should().Be(JsonValueKind.False, $"{Path.GetFileName(fichero)} no puede encender Server-Timing");
        }
    }

    [Fact]
    public void Program_usa_el_middleware_detras_de_las_cabeceras_de_seguridad()
    {
        var programa = File.ReadAllText(Path.Combine(Raiz(), "src", "CaeManager.Web", "Program.cs"));

        var cabeceras = programa.IndexOf("app.UseCabecerasSeguridad();", StringComparison.Ordinal);
        var serverTiming = programa.IndexOf("app.UseServerTimingCosteCarga();", StringComparison.Ordinal);

        cabeceras.Should().BeGreaterThanOrEqualTo(0);
        serverTiming.Should().BeGreaterThan(cabeceras,
            "la cabecera se registra después de UseCabecerasSeguridad, que tiene que cubrir también sus respuestas");
    }

    private static JsonElement? Leer(JsonElement raiz, params string[] camino)
    {
        var actual = raiz;
        foreach (var paso in camino)
        {
            if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty(paso, out actual))
                return null;
        }

        return actual;
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
