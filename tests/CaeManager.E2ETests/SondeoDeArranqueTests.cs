using System.Net;
using System.Net.Sockets;

namespace CaeManager.E2ETests;

/// <summary>
/// El sondeo con que <see cref="WebAppFixture"/> espera el arranque de la
/// aplicación, probado sin aplicación: la petición, el proceso, el reloj y la
/// pausa son dobles, así que ningún caso espera de verdad.
///
/// <para>
/// Sin <c>[Collection]</c> a propósito: una colección de aplicación arrancaría
/// la fixture cuyo sondeo se prueba aquí, y el test dependería de lo que
/// comprueba. Por eso figura en <c>ClasesSinColeccionCongeladas</c>
/// (<c>ColeccionesDeE2ECongeladasTests</c>).
/// </para>
///
/// <para>
/// El defecto que fija (2026-10-10): con el servidor aceptando la conexión y
/// tardando más de 5 s en contestar, <c>HttpClient</c> lanza
/// <see cref="TaskCanceledException"/>; el bucle solo capturaba
/// <see cref="HttpRequestException"/> y la fixture moría en
/// <c>InitializeAsync</c>. El último caso comprueba contra un
/// <c>HttpClient</c> real que ese es de verdad el tipo que llega: los demás
/// lo dan por sabido.
/// </para>
/// </summary>
public class SondeoDeArranqueTests
{
    private const string Url = "http://127.0.0.1:1/salud";
    private static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(250);

    /// <summary>La forma exacta en que <c>HttpClient</c> informa de que se agotó su <c>Timeout</c>.</summary>
    private static TaskCanceledException TimeoutDelCliente() =>
        new("The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

    private static Func<Task<HttpResponseMessage>> Responde(HttpStatusCode codigo) =>
        () => Task.FromResult(new HttpResponseMessage(codigo));

    private static Func<Task<HttpResponseMessage>> Lanza(Exception excepcion) =>
        () => Task.FromException<HttpResponseMessage>(excepcion);

    /// <summary>
    /// Ejecuta el sondeo con un reloj que solo avanza en la pausa (250 ms por intento) y con las respuestas dadas, una por
    /// intento; agotadas, repite la última. Devuelve cuántas peticiones se hicieron.
    /// </summary>
    private static async Task<int> SondearAsync(
        TimeSpan plazo,
        Func<int?>? codigoDeSalida,
        params Func<Task<HttpResponseMessage>>[] respuestas)
    {
        var ahora = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var peticiones = 0;

        try
        {
            await SondeoDeArranque.EsperarAsync(
                pedirSalud: () => respuestas[Math.Min(peticiones++, respuestas.Length - 1)](),
                codigoDeSalidaSiElProcesoTermino: codigoDeSalida ?? (() => null),
                plazo: plazo,
                ahora: () => ahora,
                pausa: () =>
                {
                    ahora += Pausa;
                    return Task.CompletedTask;
                },
                url: Url);
        }
        catch (Exception ex)
        {
            ex.Data["peticiones"] = peticiones;
            throw;
        }

        return peticiones;
    }

    [Fact]
    public async Task Dos_peticiones_que_agotan_su_tiempo_y_despues_200_vuelve_al_tercer_intento()
    {
        var peticiones = await SondearAsync(
            TimeSpan.FromSeconds(240),
            codigoDeSalida: null,
            Lanza(TimeoutDelCliente()),
            Lanza(TimeoutDelCliente()),
            Responde(HttpStatusCode.OK));

        Assert.Equal(3, peticiones);
    }

    [Fact]
    public async Task Conexion_rechazada_y_despues_200_vuelve()
    {
        var peticiones = await SondearAsync(
            TimeSpan.FromSeconds(240),
            codigoDeSalida: null,
            Lanza(new HttpRequestException("Connection refused")),
            Responde(HttpStatusCode.OK));

        Assert.Equal(2, peticiones);
    }

    [Fact]
    public async Task Respuestas_503_y_despues_200_vuelve()
    {
        var peticiones = await SondearAsync(
            TimeSpan.FromSeconds(240),
            codigoDeSalida: null,
            Responde(HttpStatusCode.ServiceUnavailable),
            Responde(HttpStatusCode.ServiceUnavailable),
            Responde(HttpStatusCode.OK));

        Assert.Equal(3, peticiones);
    }

    [Fact]
    public async Task Si_la_peticion_siempre_agota_su_tiempo_el_plazo_vence_y_el_mensaje_dice_la_causa_y_los_intentos()
    {
        // Plazo de 1 s y 250 ms por intento: cuatro intentos justos (0, 250, 500 y 750 ms).
        var timeout = TimeoutDelCliente();

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => SondearAsync(TimeSpan.FromSeconds(1), codigoDeSalida: null, Lanza(timeout)));

        Assert.Equal(4, ex.Data["peticiones"]);
        Assert.Contains("(4 intentos)", ex.Message);
        Assert.Contains("Última causa: timeout de la petición", ex.Message);
        Assert.Contains("HttpClient.Timeout of 5 seconds", ex.Message);
        Assert.Contains(Url, ex.Message);
        Assert.Same(timeout, ex.InnerException);
    }

    [Fact]
    public async Task Si_el_plazo_vence_sin_conexion_el_mensaje_nombra_la_conexion_rechazada()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => SondearAsync(TimeSpan.FromSeconds(1), codigoDeSalida: null, Lanza(new HttpRequestException("Connection refused"))));

        Assert.Contains("(4 intentos)", ex.Message);
        Assert.Contains("Última causa: conexión rechazada o fallida (Connection refused)", ex.Message);
    }

    [Fact]
    public async Task Si_el_plazo_vence_con_respuestas_503_el_mensaje_nombra_el_codigo_HTTP()
    {
        // La última causa es la del último intento, no la del primero: empieza agotando el tiempo y acaba en 503.
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => SondearAsync(
                TimeSpan.FromSeconds(1),
                codigoDeSalida: null,
                Lanza(TimeoutDelCliente()),
                Responde(HttpStatusCode.ServiceUnavailable)));

        Assert.Contains("(4 intentos)", ex.Message);
        Assert.Contains("Última causa: código HTTP 503.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Si_el_proceso_termino_falla_con_su_codigo_de_salida_sin_pedir_nada()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SondearAsync(TimeSpan.FromSeconds(240), codigoDeSalida: () => 134, Responde(HttpStatusCode.OK)));

        Assert.Equal(0, ex.Data["peticiones"]);
        Assert.Contains("(código 134)", ex.Message);
    }

    [Fact]
    public async Task Si_el_proceso_termina_a_mitad_del_sondeo_deja_de_reintentar()
    {
        var consultas = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SondearAsync(
                TimeSpan.FromSeconds(240),
                codigoDeSalida: () => ++consultas > 2 ? 1 : null,
                Lanza(TimeoutDelCliente())));

        Assert.Equal(2, ex.Data["peticiones"]);
        Assert.Contains("(código 1)", ex.Message);
    }

    /// <summary>
    /// El único caso con red, y solo de bucle local: un puerto que acepta la conexión (la cola de pendientes del sistema
    /// operativo basta, nadie llama a <c>Accept</c>) y nunca contesta, sondeado con un <c>HttpClient</c> real de 100 ms.
    /// Comprueba que la excepción que los demás casos simulan es la que <c>HttpClient</c> lanza de verdad, y que el
    /// sondeo la reintenta. No puede acertar por carrera: nadie escribe una respuesta, así que la petición solo puede
    /// agotar su tiempo.
    /// </summary>
    [Fact]
    public async Task Un_servidor_que_acepta_la_conexion_y_no_contesta_agota_el_HttpClient_real_y_se_reintenta()
    {
        using var mudo = new TcpListener(IPAddress.Loopback, 0);
        mudo.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)mudo.LocalEndpoint).Port}/salud";

        using var cliente = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var ahora = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var lanzadas = new List<Exception>();

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => SondeoDeArranque.EsperarAsync(
                pedirSalud: async () =>
                {
                    try
                    {
                        return await cliente.GetAsync(url);
                    }
                    catch (Exception lanzada)
                    {
                        lanzadas.Add(lanzada);
                        throw;
                    }
                },
                codigoDeSalidaSiElProcesoTermino: () => null,
                plazo: Pausa * 2,
                ahora: () => ahora,
                pausa: () =>
                {
                    ahora += Pausa;
                    return Task.CompletedTask;
                },
                url: url));

        Assert.Equal(2, lanzadas.Count);
        Assert.All(lanzadas, lanzada =>
        {
            Assert.IsType<TaskCanceledException>(lanzada);
            Assert.IsType<TimeoutException>(lanzada.InnerException);
        });
        Assert.Contains("(2 intentos)", ex.Message);
        Assert.Contains("Última causa: timeout de la petición", ex.Message);
    }
}
