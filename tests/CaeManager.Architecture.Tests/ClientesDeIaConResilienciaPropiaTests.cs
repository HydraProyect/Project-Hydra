using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cada cliente HTTP que habla con un proveedor de IA de pago tiene que usar
/// <c>AplicarResilienciaHttpIa</c>, no el <c>AplicarResilienciaHttp</c> genérico.
///
/// La diferencia entre los dos es cuándo se reintenta. El genérico trata igual
/// un 429 que un 500 que un timeout, que es correcto para un servicio
/// idempotente; ninguno de estos endpoints lo es, así que ahí un reintento
/// puede duplicar el cobro y volver a transmitir el documento. Con hasta tres
/// intentos HTTP multiplicados por los tres del trabajo durable, un solo
/// encargo podía llegar a nueve ejecuciones facturables.
///
/// Ese es exactamente el tipo de propiedad que se pierde sin ruido: quien añada
/// mañana un cuarto proveedor copiará la línea de al lado, y si copia la
/// equivocada nada falla, nada avisa, y la factura sube. El ratchet convierte
/// ese descuido en un build rojo.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="ConexionesFueraDelInterceptorTests"/>.
/// </summary>
public class ClientesDeIaConResilienciaPropiaTests
{
    private const string ArchivoDeRegistro =
        "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs";

    /// <summary>
    /// Los tipos cuyo <c>AddHttpClient</c> tiene que llevar el pipeline de IA.
    /// Se listan por el nombre de la implementación, que es lo que aparece
    /// siempre en el registro (algunos van cualificados con su espacio de
    /// nombres en la interfaz, otros no llevan interfaz).
    /// </summary>
    private static readonly string[] ImplementacionesDeIa =
    [
        "AnthropicAsistenteIaService",
        "AnthropicExtraccionTrabajadoresIaService",
        "AnthropicDeteccionVisitaCorreoService",
        "AnthropicDeteccionGestionCorreoService",
        "AnthropicDeteccionRelevanciaCaeService",
        "MistralOcrDocumentAIProvider",
        "AnthropicDocumentAIProvider",
        "GeminiDocumentAIProvider",
    ];

    [Fact]
    public void Todo_cliente_de_un_proveedor_de_ia_usa_el_pipeline_de_reintento_restringido()
    {
        var contenido = LeerRegistro();
        var infractores = new List<string>();

        foreach (var implementacion in ImplementacionesDeIa)
        {
            var registro = ExtraerRegistroDe(contenido, implementacion);

            registro.Should().NotBeNull(
                $"{implementacion} tiene que seguir registrándose con AddHttpClient; si cambió de mecanismo, " +
                "actualiza este ratchet en el mismo commit");

            if (!registro!.Contains("AplicarResilienciaHttpIa(", StringComparison.Ordinal))
                infractores.Add(implementacion);
        }

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "estos clientes llaman a un proveedor de IA de pago sobre endpoints que no garantizan idempotencia: " +
            "con AplicarResilienciaHttp, un reintento tras un timeout o un 5xx puede pagar dos veces la misma " +
            "petición y volver a enviar el documento. Usa AplicarResilienciaHttpIa");
    }

    /// <summary>
    /// Guarda del propio instrumento: si el patrón no encontrara ningún
    /// registro, el test principal daría verde por no tener nada que mirar. Se
    /// comprueba contra el pipeline genérico, que tiene que seguir existiendo y
    /// usándose — si un día no quedara ninguno, este ratchet estaría comparando
    /// contra un fantasma.
    /// </summary>
    [Fact]
    public void El_pipeline_generico_sigue_existiendo_y_con_clientes_que_lo_usan()
    {
        var contenido = LeerRegistro();

        contenido.Should().Contain("AplicarResilienciaHttp(this IHttpClientBuilder",
            "el pipeline genérico es la mitad de la comparación que este ratchet vigila");

        var usosGenericos = Regex.Matches(contenido, @"\.AplicarResilienciaHttp\(").Count;
        usosGenericos.Should().BeGreaterThan(0,
            "clientes como Graph, WhatsApp o el BOE siguen usándolo con razón: no son proveedores de IA de pago");
    }

    /// <summary>
    /// Recorta el registro de un cliente: desde su <c>AddHttpClient</c> hasta
    /// el punto y coma que cierra la sentencia encadenada. Sin acotarlo, buscar
    /// "AplicarResilienciaHttpIa" en el fichero entero daría verde para un
    /// cliente aunque quien lo use fuera otro.
    /// </summary>
    private static string? ExtraerRegistroDe(string contenido, string implementacion)
    {
        var inicio = contenido.IndexOf($"AddHttpClient<", StringComparison.Ordinal);

        while (inicio >= 0)
        {
            var fin = contenido.IndexOf(';', inicio);
            if (fin < 0) return null;

            var registro = contenido[inicio..fin];
            if (registro.Contains($", {implementacion}>", StringComparison.Ordinal)
                || registro.Contains($"<{implementacion}>", StringComparison.Ordinal))
            {
                return registro;
            }

            inicio = contenido.IndexOf("AddHttpClient<", fin, StringComparison.Ordinal);
        }

        return null;
    }

    private static string LeerRegistro()
    {
        var raiz = RaizDelRepositorio();
        var archivo = Path.Combine(raiz, ArchivoDeRegistro.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(archivo).Should().BeTrue(
            "si el registro de servicios cambia de sitio, este ratchet deja de vigilar nada y hay que reapuntarlo");

        return File.ReadAllText(archivo);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
