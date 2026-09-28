using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Todo camino que termina el inicio de sesión aterriza con la marca de un
/// solo uso</b> (D-2: Mi trabajo es el aterrizaje TRAS INICIAR SESIÓN, no lo que
/// responde cada clic en «Inicio»).
///
/// <para>
/// Inicio solo redirige a Mi trabajo con <c>?desde=login</c>; si un paso final del
/// login (contraseña, 2FA, cambio obligatorio de contraseña, callback externo)
/// navega a «/» sin la marca, el Gestor CAE de un Operador CAE externo aterriza en
/// un estado vacío en vez de en su bandeja.
/// </para>
///
/// <para>
/// <b>Lo que SÍ observa:</b> que cada fichero del flujo llame a
/// <c>RedireccionLocal.DestinoTrasLogin</c> el número de veces esperado y que
/// ninguno vuelva a usar <c>RedireccionLocal.Sanear</c> como destino final.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): la granularidad es la cuenta de
/// llamadas por fichero (no comprueba que la llamada esté en la rama de éxito);
/// un paso final nuevo en otro fichero no entra hasta añadirlo a
/// <see cref="PasosFinalesDelLogin"/>; el comportamiento HTTP real (la cabecera
/// <c>Location</c>) lo cubre E2E, no esto.
/// </para>
/// </summary>
public class AterrizajeTrasLoginLlevaLaMarcaTests
{
    private static readonly (string Fichero, int Llamadas)[] PasosFinalesDelLogin =
    [
        ("src/CaeManager.Web/Components/Account/Pages/Login.razor", 1),
        ("src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor", 2),
        ("src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor", 1),
        ("src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs", 1),
    ];

    private static readonly Regex LlamaADestinoTrasLogin = new(
        @"RedireccionLocal\s*\.\s*DestinoTrasLogin\s*\(", RegexOptions.Compiled);

    private static readonly Regex UsaSanearComoDestino = new(
        @"RedireccionLocal\s*\.\s*Sanear\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Cada_paso_final_del_login_aterriza_con_la_marca_de_un_solo_uso()
    {
        foreach (var (fichero, llamadas) in PasosFinalesDelLogin)
        {
            var texto = SinComentarios(fichero);

            LlamaADestinoTrasLogin.Matches(texto).Count.Should().Be(llamadas,
                $"{fichero} termina el inicio de sesión y debe aterrizar con ?desde=login " +
                "(RedireccionLocal.DestinoTrasLogin): sin la marca, Inicio no lleva a Mi trabajo (D-2)");
            UsaSanearComoDestino.IsMatch(texto).Should().BeFalse(
                $"{fichero} no puede volver a RedireccionLocal.Sanear como destino: pierde la marca de aterrizaje");
        }
    }

    private static string SinComentarios(string relativo)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), relativo.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"control: {relativo} sigue donde este test lo busca");
        return LimpiadorDeComentarios.Quitar(File.ReadAllText(ruta), razor: relativo.EndsWith(".razor", StringComparison.Ordinal));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
