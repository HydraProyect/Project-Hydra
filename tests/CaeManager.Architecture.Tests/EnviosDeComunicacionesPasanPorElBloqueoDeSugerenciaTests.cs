using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Una respuesta sugerida no debe poder enviarse por error, y esa garantía vive en servidor: un comando nuevo que llame al
/// canal sin pasar por <c>BloqueoRespuestaSugerida</c> abre una vía sin el bloqueo. Quien añada un envío o bien llama al
/// bloqueo o bien se declara aquí, diciendo por qué ese texto no puede ser una sugerencia.
/// </summary>
public partial class EnviosDeComunicacionesPasanPorElBloqueoDeSugerenciaTests
{
    private static readonly HashSet<string> EnviosSinTextoDeUnaPersona =
    [
        // El acuse automático de la ingesta: lo escribe la configuración de la línea, y nunca lleva una sugerencia.
        "Integraciones/IngestaWebhookWhatsAppService.cs",
    ];

    [GeneratedRegex(@"\.(EnviarRespuestaAsync|EnviarTextoAsync|EnviarNuevoMensajeAsync)\(")]
    private static partial Regex LlamadaAlCanal();

    [GeneratedRegex(@"\.(ComprobarEnvioAsync|ComprobarEnvioPorBuzonAsync)\(")]
    private static partial Regex LlamadaAlBloqueo();

    [Fact]
    public void Todo_envio_al_canal_comprueba_antes_la_respuesta_sugerida()
    {
        var raiz = Path.Combine(BuscarRaizDelRepositorio(), "src", "CaeManager.Application");

        var enviosEncontrados = new List<string>();
        var sinBloqueo = new List<string>();
        foreach (var fichero in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            var ruta = Path.GetRelativePath(raiz, fichero).Replace('\\', '/');
            var texto = File.ReadAllText(fichero);
            var envio = LlamadaAlCanal().Match(texto);
            if (!envio.Success || EnviosSinTextoDeUnaPersona.Contains(ruta))
                continue;

            enviosEncontrados.Add(ruta);
            var bloqueo = LlamadaAlBloqueo().Match(texto);
            if (!bloqueo.Success || bloqueo.Index > envio.Index)
                sinBloqueo.Add(ruta);
        }

        enviosEncontrados.Should().HaveCountGreaterThanOrEqualTo(4, "control del instrumento: los cuatro comandos de hoy envían");
        sinBloqueo.Should().BeEmpty("el bloqueo va antes de la llamada al canal; después ya salió");
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
