using System.Net;
using CaeManager.Application.Comercial.Common;
using CaeManager.Infrastructure.Comercial;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;
using Xunit;

namespace CaeManager.Web.Tests.Comercial;

/// <summary>
/// Salto de <see cref="StripePaymentProvider"/> contra la API REAL de Stripe en
/// MODO PRUEBA (P18 de las decisiones del 2026-09-19: en GitHub solo viven
/// claves de prueba, sin cobro real). El resto de la suite de Stripe firma sus
/// webhooks a mano y no toca la red (<see cref="StripePaymentProviderTests"/>):
/// esa parte no necesita clave y no la usa. Lo que ninguna prueba con doble
/// observa es que el SDK <c>Stripe.net</c> que hay instalado hable de verdad
/// con la API — que es lo que rompe una subida de versión de Dependabot.
///
/// Se salta a sí misma, DECLARÁNDOLO, si <c>Stripe__ApiKey</c> no está en el
/// entorno (mismo mecanismo que <c>TheorySiHayClaveIaAttribute</c>). La clave
/// solo la aporta el workflow integraciones-con-clave.yml, disparado por la
/// cola de fusión o a mano — nunca por un PR (ver scripts/verificar-secretos-de-ci.sh).
///
/// Ninguna prueba de aquí escribe en Stripe ni crea objetos: solo lecturas de
/// una suscripción que no existe.
/// </summary>
public class StripeModoPruebaTests
{
    private const string VariableClave = "Stripe__ApiKey";
    private const string SuscripcionInexistente = "sub_inexistente_ci_talveg";

    private static string? ClaveDelEntorno() => Environment.GetEnvironmentVariable(VariableClave);

    private static bool EsClaveDeProduccion(string? clave) =>
        clave is not null &&
        (clave.StartsWith("sk_live_", StringComparison.Ordinal) || clave.StartsWith("rk_live_", StringComparison.Ordinal));

    private static bool EsClaveDePrueba(string? clave) =>
        clave is not null &&
        (clave.StartsWith("sk_test_", StringComparison.Ordinal) || clave.StartsWith("rk_test_", StringComparison.Ordinal));

    /// <summary>
    /// Esta prueba NUNCA se salta: corre en cada job de CI que ejecute Web.Tests.
    /// Si algún día una clave de producción llega al entorno de un test —por un
    /// secreto mal cargado o un workflow copiado—, el rojo salta aquí, antes de
    /// que ninguna otra prueba la use.
    /// </summary>
    [Fact]
    public void Ninguna_clave_de_produccion_de_Stripe_llega_al_entorno_de_los_tests()
    {
        var clave = ClaveDelEntorno();

        EsClaveDeProduccion(clave).Should().BeFalse(
            $"la variable {VariableClave} de un entorno de tests solo admite claves de modo prueba (sk_test_/rk_test_); " +
            "una clave de producción en CI cobraría o expondría datos reales — decisión P18 de 2026-09-19");
    }

    /// <summary>
    /// Contraprueba del instrumento anterior: la comprobación distingue de
    /// verdad una clave de producción de una de prueba y de la ausencia de clave.
    /// Sin esto, una regresión que hiciera <c>EsClaveDeProduccion</c> devolver
    /// siempre false dejaría verde la prueba anterior para siempre.
    /// </summary>
    [Theory]
    [InlineData("sk_live_abc", true)]
    [InlineData("rk_live_abc", true)]
    [InlineData("sk_test_abc", false)]
    [InlineData("rk_test_abc", false)]
    [InlineData("whsec_abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void El_detector_de_clave_de_produccion_distingue_produccion_de_prueba_y_de_ausencia(string? clave, bool esProduccion)
    {
        EsClaveDeProduccion(clave).Should().Be(esProduccion);
    }

    [FactSiHayClaveStripe]
    public async Task La_clave_de_prueba_autentica_y_una_suscripcion_inexistente_es_un_404_no_un_401()
    {
        var clave = ClaveDelEntorno();

        // Antes de crear ningún cliente HTTP: una clave que no sea de prueba no
        // sale de este proceso.
        EsClaveDePrueba(clave).Should().BeTrue(
            $"{VariableClave} debe empezar por sk_test_ o rk_test_ — modo prueba de Stripe, nunca producción");

        var servicio = new SubscriptionService(new StripeClient(clave));

        var accion = async () => await servicio.GetAsync(SuscripcionInexistente);

        var excepcion = (await accion.Should().ThrowAsync<StripeException>()).Which;

        // 401 = la clave no autentica (revocada, mal copiada, de otra cuenta);
        // 404 resource_missing = autenticó y la API contestó como debe.
        excepcion.HttpStatusCode.Should().Be(HttpStatusCode.NotFound,
            "una clave válida en modo prueba autentica y Stripe contesta 404 a una suscripción que no existe");
        excepcion.StripeError.Code.Should().Be("resource_missing");
    }

    [FactSiHayClaveStripe]
    public async Task El_proveedor_real_devuelve_un_fallo_controlado_de_API_y_no_de_configuracion()
    {
        var clave = ClaveDelEntorno();
        EsClaveDePrueba(clave).Should().BeTrue(
            $"{VariableClave} debe empezar por sk_test_ o rk_test_ — modo prueba de Stripe, nunca producción");

        var proveedor = new StripePaymentProvider(
            Options.Create(new StripeOptions { ApiKey = clave }), NullLogger<StripePaymentProvider>.Instance);

        var resultado = await proveedor.ObtenerSuscripcionAsync(SuscripcionInexistente);

        resultado.EsFallido.Should().BeTrue();
        // ErrorApi = la petición SÍ salió hacia Stripe (con clave configurada) y
        // Stripe la rechazó de forma controlada. NoConfigurado significaría que
        // la clave no llegó al proveedor y esta prueba no habría ejercitado nada.
        resultado.Error.Codigo.Should().Be("PaymentProvider.ErrorApi");
    }
}

/// <summary>
/// <see cref="FactAttribute"/> que se salta a sí mismo, DECLARÁNDOLO, cuando no
/// hay ninguna clave de Stripe en el entorno. Si la clave existe pero es de
/// producción NO se salta: la prueba debe correr y fallar en su primera
/// aserción, sin haber hecho ninguna llamada de red.
/// </summary>
public sealed class FactSiHayClaveStripeAttribute : FactAttribute
{
    public FactSiHayClaveStripeAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Stripe__ApiKey")))
        {
            Skip = "Requiere una clave de Stripe en MODO PRUEBA en la variable de entorno \"Stripe__ApiKey\" — no configurada " +
                   "en este entorno. Solo la aporta el workflow integraciones-con-clave.yml (cola de fusión o a mano).";
        }
    }
}
