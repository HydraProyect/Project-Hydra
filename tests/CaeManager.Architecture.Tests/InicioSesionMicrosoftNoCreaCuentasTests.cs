using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El inicio de sesión con Microsoft no da de alta cuentas</b> (invitación previa,
/// decisión D-3 de 2026-10-25). Solo entra quien ya tiene una cuenta de TALVEG en el
/// Tenant destino; la cuenta la crea antes un Administrador o el Actor de Plataforma
/// TALVEG. Antes el callback hacía <c>UserManager.CreateAsync</c> con el correo de
/// Entra, que no está verificado, y abría una cuenta a cualquiera del directorio.
///
/// <para>
/// Las pruebas de <c>DecidirCuenta</c> son de una función pura y no verían un alta
/// reintroducida en el propio callback; esto sí. <b>No observa</b>: un alta hecha por
/// otro servicio al que el callback llame, ni la que se haga por <c>IUserStore</c>
/// directo. Se descartan comentarios, no literales de texto.
/// </para>
/// </summary>
public class InicioSesionMicrosoftNoCreaCuentasTests
{
    private static readonly string[] CaminoDelInicioConMicrosoft =
    [
        "src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs",
        "src/CaeManager.Web/Components/Account/InicioSesionMicrosoft.cs",
    ];

    [Fact]
    public void Ni_el_callback_ni_la_decision_de_cuenta_crean_cuentas()
    {
        foreach (var relativa in CaminoDelInicioConMicrosoft)
        {
            var codigo = LimpiadorDeComentarios.Quitar(File.ReadAllText(Absoluta(relativa)), razor: false);

            codigo.Should().NotContain("CreateAsync(", $"{relativa} no debe dar de alta cuentas (D-3)");
            codigo.Should().NotContain("new ApplicationUser", $"{relativa} no debe construir cuentas nuevas (D-3)");
        }
    }

    /// <summary>
    /// Vínculo tras el segundo factor (D-2): el callback guarda el vínculo directo una
    /// sola vez y después de desviar al TOTP, y <c>LoginCon2fa</c> guarda el pendiente
    /// en sus dos caminos de éxito (código de la aplicación y de recuperación). Las
    /// pruebas de <c>DecidirSegundoFactor</c> no verían un vínculo movido antes del
    /// desvío ni una página que dejara de completarlo.
    /// </summary>
    [Fact]
    public void El_vinculo_se_guarda_solo_tras_el_segundo_factor()
    {
        var callback = LimpiadorDeComentarios.Quitar(
            File.ReadAllText(Absoluta("src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs")), razor: false);

        var sinEspacios = SinEspacios(callback);
        sinEspacios.Should().Contain("varvinculoNuevo=decision==DecisionCuentaMicrosoft.VincularCuentaExistente;",
            "el vínculo nuevo es exactamente el de la decisión de vincular por correo");
        sinEspacios.Should().MatchRegex(@"DecidirSegundoFactor\([^;]*,vinculoNuevo\);",
            "la decisión del segundo factor recibe si el vínculo es nuevo");
        callback.Should().Contain("PrincipalSegundoFactorPendiente(usuario.Id, vinculoNuevo ? infoExterna : null)",
            "el desvío al TOTP de un vínculo nuevo deja la identidad pendiente en la cookie de dos pasos");

        sinEspacios.Split("AddLoginAsync(").Length.Should().Be(2, "el callback guarda un único vínculo directo");
        sinEspacios.Should().NotContain("CompletarVinculoPendienteAsync(",
            "el vínculo que espera al TOTP lo guarda solo LoginCon2fa, nunca el callback");
        callback.IndexOf("AddLoginAsync(", StringComparison.Ordinal).Should().BeGreaterThan(
            callback.IndexOf("return Results.LocalRedirect(verificacion);", StringComparison.Ordinal),
            "el vínculo directo va después de salir hacia el TOTP, nunca dentro de ese desvío");
        callback.IndexOf("return Results.LocalRedirect(verificacion);", StringComparison.Ordinal).Should().BeGreaterThan(0);

        var pagina = LimpiadorDeComentarios.Quitar(
            File.ReadAllText(Absoluta("src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor")), razor: true);

        var llamadas = Posiciones(pagina, "await CompletarVinculoPendienteAsync(");
        llamadas.Should().HaveCount(2,
            "LoginCon2fa completa el vínculo pendiente tras el código de la aplicación y tras el de recuperación");
        foreach (var llamada in llamadas)
        {
            // Directamente dentro del bloque de éxito, después de verificar el código.
            var exito = pagina.LastIndexOf("if (resultado.Succeeded)", llamada, StringComparison.Ordinal);
            exito.Should().BeGreaterThan(0, "el vínculo pendiente se completa solo en el camino de éxito");
            var tramo = System.Text.RegularExpressions.Regex.Replace(pagina[exito..llamada], "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
            tramo.Count(c => c == '{').Should().Be(1, "la llamada está en el propio bloque de éxito");
            tramo.Should().NotContain("}", "la llamada está en el propio bloque de éxito");
            pagina.LastIndexOf("SignInAsync(", exito, StringComparison.Ordinal).Should().BeGreaterThan(0,
                "el éxito es el de la verificación del código");
        }
    }

    private static string SinEspacios(string texto) => string.Concat(texto.Where(c => !char.IsWhiteSpace(c)));

    private static List<int> Posiciones(string texto, string patron)
    {
        var posiciones = new List<int>();
        for (var i = texto.IndexOf(patron, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(patron, i + 1, StringComparison.Ordinal))
            posiciones.Add(i);
        return posiciones;
    }

    private static string Absoluta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
