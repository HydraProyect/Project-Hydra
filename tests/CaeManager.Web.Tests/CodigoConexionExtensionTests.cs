using System.Text;
using System.Text.Json;
using CaeManager.Web.Features.Extension;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El código que se copia de la pantalla y se pega en la extensión cuando el
/// enlace automático no está disponible.
///
/// <para>
/// Lo que se fija aquí no es «que funcione»: es un <b>contrato entre dos
/// lenguajes</b>. Quien lee este código es <c>leerCodigoConexion</c> de
/// <c>extension/background.js</c>, que vive en el paquete de la extensión y se
/// actualiza en la tienda, no con el despliegue. Si alguien renombra un campo
/// aquí, las extensiones ya instaladas dejan de poder conectarse a mano y
/// nada en esta solución se pone en rojo — salvo estas pruebas.
/// </para>
/// </summary>
public class CodigoConexionExtensionTests
{
    private const string UrlTalveg = "https://staging.talveg.es/";
    private const string Token = "CfDJ8ejemplo-inventado.no-es-un-token-real";
    private static readonly DateTime Expira = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Decodificar(string codigo) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(codigo))).RootElement;

    [Fact]
    public void El_codigo_lleva_los_tres_datos_con_los_nombres_que_espera_la_extension()
    {
        var raiz = Decodificar(CodigoConexionExtension.Crear(UrlTalveg, Token, Expira));

        // "u", "t" y "e": los mismos nombres que desestructura conectarManual.
        raiz.GetProperty("u").GetString().Should().Be(UrlTalveg);
        raiz.GetProperty("t").GetString().Should().Be(Token);
        raiz.GetProperty("e").GetString().Should().Be("2026-09-21T12:00:00.0000000Z");
    }

    [Fact]
    public void La_caducidad_viaja_en_UTC_explicito()
    {
        // Sin la Z, Date.parse del navegador la interpreta como hora LOCAL y
        // la extensión se creería conectada dos horas de más o de menos. Es un
        // fallo que solo se ve cuando el token ya no vale.
        var raiz = Decodificar(CodigoConexionExtension.Crear(UrlTalveg, Token, Expira));

        raiz.GetProperty("e").GetString().Should().EndWith("Z");
        DateTime.Parse(raiz.GetProperty("e").GetString()!).ToUniversalTime().Should().Be(Expira);
    }

    [Fact]
    public void El_token_viaja_intacto_aunque_lleve_caracteres_de_base64url()
    {
        // Los tokens de Data Protection llevan '-' y '_'. Si alguien cambiara
        // el empaquetado a base64url sin escapar, el token saldría mutilado y
        // el fallo aparecería como un 401 en otra pantalla.
        var conSimbolos = "aA0-_.~+/=";

        Decodificar(CodigoConexionExtension.Crear(UrlTalveg, conSimbolos, Expira))
            .GetProperty("t").GetString().Should().Be(conSimbolos);
    }

    [Fact]
    public void El_codigo_es_base64_estandar_que_atob_sabe_leer()
    {
        // La extensión lo descodifica con atob, que NO acepta el alfabeto
        // base64url. Esto lo deja fijado por si alguien "moderniza" el
        // empaquetado sin tocar el otro lado.
        var codigo = CodigoConexionExtension.Crear(UrlTalveg, Token, Expira);

        codigo.Should().MatchRegex("^[A-Za-z0-9+/]+={0,2}$");
    }
}
