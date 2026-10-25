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
