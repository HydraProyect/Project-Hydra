using System.Text.RegularExpressions;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Una respuesta sugerida no debe poder enviarse por error, y esa garantía vive en servidor: un comando nuevo que llame al
/// canal sin pasar por <c>BloqueoRespuestaSugerida</c> abre una vía sin el bloqueo. Quien añada un envío o bien llama al
/// bloqueo o bien se declara aquí, con cuántas llamadas son y por qué ese texto no puede ser una sugerencia.
///
/// <para>
/// El mismo bloqueo fija el origen del texto del Mensaje saliente (issue 2007): un Mensaje saliente nuevo con texto de una
/// persona lleva origenTexto: tomado del resultado del bloqueo, o se declara aquí como automático.
/// </para>
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

    // Mensajes salientes que crea el sistema sin texto de una persona, y por tanto sin origen que fijar (quedan como Persona,
    // el valor por defecto de las filas sin sugerencia). Cuántas llamadas son por fichero: una de más es un saliente nuevo.
    private static readonly Dictionary<string, int> SalientesAutomaticosSinOrigenDeTexto = new()
    {
        // El acuse automático por canal de WhatsApp: lo escribe la configuración de la línea.
        ["CaeManager.Application/Integraciones/IngestaWebhookWhatsAppService.cs"] = 1,

        // El acuse automático por buzón: el texto es la constante PlantillaAcuseBuzon.CuerpoHtml.
        ["CaeManager.Application/Integraciones/AcuseAutomaticoBuzonService.cs"] = 1,

        // El paquete documental de la visita: lo redacta el sistema con los datos de la visita.
        ["CaeManager.Application/Visitas/PaqueteDocumental/PaqueteDocumentalVisitaService.cs"] = 1,
    };

    [GeneratedRegex(@"\.AgregarMensaje\(")]
    private static partial Regex LlamadaAAgregarMensaje();

    [GeneratedRegex(@"origenTexto:\s*OrigenTextoMensaje\.")]
    private static partial Regex OrigenCodificadoAMano();

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

    [Fact]
    public void Todo_mensaje_saliente_de_Application_fija_el_origen_del_texto_desde_el_bloqueo()
    {
        var raiz = Path.Combine(BuscarRaizDelRepositorio(), "src", "CaeManager.Application");

        var automaticos = new Dictionary<string, int>();
        var sinOrigen = new List<string>();
        var fijanElOrigen = new List<string>();
        var origenSinBloqueo = new List<string>();
        var origenCodificadoAMano = new List<string>();
        foreach (var fichero in FicherosDeCodigo(raiz))
        {
            var ruta = "CaeManager.Application/" + Path.GetRelativePath(raiz, fichero).Replace('\\', '/');
            var texto = File.ReadAllText(fichero);
            var salientes = LlamadaAAgregarMensaje().Matches(texto)
                .Select(llamada => texto[llamada.Index..(texto.IndexOf(';', llamada.Index) + 1)])
                .Where(sentencia => sentencia.Contains("DireccionMensaje.Saliente", StringComparison.Ordinal))
                .ToList();
            if (salientes.Count == 0)
                continue;

            if (OrigenCodificadoAMano().IsMatch(texto))
                origenCodificadoAMano.Add(ruta);

            var conOrigen = salientes.Count(sentencia => sentencia.Contains("origenTexto:", StringComparison.Ordinal));
            if (conOrigen > 0)
            {
                fijanElOrigen.Add(ruta);
                if (!LlamadaAlBloqueo().IsMatch(texto))
                    origenSinBloqueo.Add(ruta);
            }

            if (conOrigen == salientes.Count)
                continue;

            if (SalientesAutomaticosSinOrigenDeTexto.ContainsKey(ruta))
                automaticos[ruta] = salientes.Count - conOrigen;
            else
                sinOrigen.Add(ruta);
        }

        fijanElOrigen.Should().HaveCountGreaterThanOrEqualTo(4, "control del instrumento: los cuatro comandos de hoy fijan el origen");
        sinOrigen.Should().BeEmpty("un Mensaje saliente con texto de una persona lleva el origen que dio el bloqueo; si es automático, se declara");
        automaticos.Should().BeEquivalentTo(
            SalientesAutomaticosSinOrigenDeTexto, "una llamada de más en un fichero exceptuado es un saliente nuevo sin origen");
        origenSinBloqueo.Should().BeEmpty("el origen sale del resultado del bloqueo, no de otra fuente");
        origenCodificadoAMano.Should().BeEmpty("el origen nunca se escribe a mano: lo decide la sugerencia revisada que encontró el bloqueo");
    }

    [Fact]
    public void Ningun_comando_recibe_el_origen_del_texto_del_cliente()
    {
        var comandos = typeof(OrigenTextoEnvio).Assembly.GetTypes()
            .Where(tipo => typeof(ICommandBase).IsAssignableFrom(tipo) && !tipo.IsInterface)
            .ToList();

        comandos.Should().NotBeEmpty("control del instrumento: la reflexión ve los comandos");
        comandos.Where(tipo => tipo.GetProperties().Any(p => p.PropertyType == typeof(OrigenTextoMensaje) || p.PropertyType == typeof(OrigenTextoMensaje?)))
            .Select(tipo => tipo.FullName)
            .Should().BeEmpty("el origen del texto lo fija el servidor desde la sugerencia persistida");
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
