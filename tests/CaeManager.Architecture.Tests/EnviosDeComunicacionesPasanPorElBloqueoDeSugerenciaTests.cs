using System.Text.RegularExpressions;
using CaeManager.Application.Integraciones;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Una respuesta sugerida no debe poder enviarse por error, y esa garantía vive en servidor: un comando nuevo que llame al
/// canal sin pasar por <c>BloqueoRespuestaSugerida</c> abre una vía sin el bloqueo. Quien añada un envío o bien llama al
/// bloqueo o bien se declara aquí, con cuántas llamadas son y por qué ese texto no puede ser una sugerencia.
/// </summary>
public partial class EnviosDeComunicacionesPasanPorElBloqueoDeSugerenciaTests
{
    // Lista cerrada: un método Enviar* nuevo en estos clientes rompe la prueba hasta que alguien decida si lleva el bloqueo.
    private static readonly string[] MetodosDeEnvio = ["EnviarNuevoMensajeAsync", "EnviarRespuestaAsync", "EnviarTextoAsync"];

    private static readonly Dictionary<string, int> LlamadasSinTextoDeUnaPersona = new()
    {
        // El acuse automático de la ingesta: lo escribe la configuración de la línea, y nunca lleva una sugerencia.
        ["CaeManager.Application/Integraciones/IngestaWebhookWhatsAppService.cs"] = 1,

        // El acuse automático por buzón: el texto es la constante PlantillaAcuseBuzon.CuerpoHtml, y sale al entrar el
        // primer correo de la conversación, antes de que exista nada que sugerir.
        ["CaeManager.Application/Integraciones/AcuseAutomaticoBuzonService.cs"] = 1,

        // El decorador que corta los envíos reales fuera de producción: delega en el cliente, no decide qué se envía.
        ["CaeManager.Infrastructure/Integraciones/GuardaEnvioMicrosoft365GraphClient.cs"] = 2,
    };

    [GeneratedRegex(@"\.(ComprobarEnvioAsync|ComprobarEnvioPorBuzonAsync)\(")]
    private static partial Regex LlamadaAlBloqueo();

    private static Regex LlamadaAlCanal() => new($@"\.({string.Join('|', MetodosDeEnvio)})\(");

    [Fact]
    public void Los_metodos_de_envio_de_los_clientes_del_canal_son_los_declarados()
    {
        var declarados = new[] { typeof(IWhatsAppCloudApiClient), typeof(IMicrosoft365GraphClient) }
            .SelectMany(cliente => cliente.GetMethods())
            .Where(metodo => metodo.Name.StartsWith("Enviar", StringComparison.Ordinal))
            .Select(metodo => metodo.Name)
            .Distinct();

        declarados.Should().BeEquivalentTo(MetodosDeEnvio, "un envío nuevo decide primero si pasa por el bloqueo de la sugerencia");
    }

    [Fact]
    public void Todo_envio_al_canal_comprueba_antes_la_respuesta_sugerida()
    {
        var raiz = Path.Combine(BuscarRaizDelRepositorio(), "src");

        var ficherosQueEnvian = new List<string>();
        var sinBloqueo = new List<string>();
        var excepcionesEncontradas = new Dictionary<string, int>();
        foreach (var fichero in FicherosDeCodigo(raiz))
        {
            var ruta = Path.GetRelativePath(raiz, fichero).Replace('\\', '/');
            var texto = File.ReadAllText(fichero);
            var envios = LlamadaAlCanal().Matches(texto);
            if (envios.Count == 0)
                continue;

            if (LlamadasSinTextoDeUnaPersona.ContainsKey(ruta))
            {
                excepcionesEncontradas[ruta] = envios.Count;
                continue;
            }

            ficherosQueEnvian.Add(ruta);
            var bloqueos = LlamadaAlBloqueo().Matches(texto);
            if (envios.Select((envio, indice) => (envio, indice)).Any(e => bloqueos.Count(b => b.Index < e.envio.Index) <= e.indice))
                sinBloqueo.Add(ruta);
        }

        ficherosQueEnvian.Should().HaveCountGreaterThanOrEqualTo(4, "control del instrumento: los cuatro comandos de hoy envían");
        sinBloqueo.Should().BeEmpty("cada llamada al canal lleva su bloqueo antes; después ya salió");
        excepcionesEncontradas.Should().BeEquivalentTo(
            LlamadasSinTextoDeUnaPersona, "una llamada de más en un fichero exceptuado es un envío nuevo sin bloqueo");
    }

    private static IEnumerable<string> FicherosDeCodigo(string raiz) =>
        Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(fichero => !fichero.Split(Path.DirectorySeparatorChar).Any(parte => parte is "bin" or "obj"));

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
