using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La siembra de demo crea usuarios que pueden iniciar sesión y claves de API
/// que autentican contra la API pública v1. Sus valores por defecto son
/// constantes de un repositorio <b>público</b> — algo deliberado y correcto
/// mientras esa siembra solo corría en local y en CI.
///
/// <para>
/// Desde que la demo se hace sobre el portal de producción, esa premisa deja
/// de valer: las mismas credenciales abrirían tenants vivos en un servidor
/// público, con la contraseña legible por cualquiera. Estos tests fijan la
/// regla que lo impide — <b>en Producción no hay valor por defecto</b>— y,
/// con la misma fuerza, que fuera de Producción nada cambia: las suites E2E
/// inician sesión con la contraseña conocida y romperlas aquí sería cambiar
/// un problema por otro.
/// </para>
/// </summary>
public class CredencialesDemoTests
{
    private static IConfiguration Configuracion(params (string Clave, string Valor)[] valores) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Clave, v.Valor)))
            .Build();

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Fuera_de_produccion_sigue_usando_los_valores_publicos(string entorno)
    {
        var credenciales = CredencialesDemo.Resolver(Configuracion(), new EntornoDePrueba(entorno));

        credenciales.Contrasena.Should().Be(CredencialesDemo.ContrasenaPorDefecto,
            "las suites E2E y el arranque local inician sesión con la contraseña conocida");
        credenciales.ClaveApiActiva.Should().Be(CredencialesDemo.ClaveApiActivaPorDefecto);
        credenciales.ClaveApiRevocada.Should().Be(CredencialesDemo.ClaveApiRevocadaPorDefecto);
    }

    [Theory]
    [InlineData("DatosPrueba:Contrasena")]
    [InlineData("DatosPrueba:ClaveApiActiva")]
    [InlineData("DatosPrueba:ClaveApiRevocada")]
    public void En_produccion_falta_cualquiera_de_las_tres_y_lanza(string claveQueFalta)
    {
        var todas = new[]
        {
            ("DatosPrueba:Contrasena", "Una-Contrasena-De-Verdad#2026"),
            ("DatosPrueba:ClaveApiActiva", "hydra_una_clave_de_verdad"),
            ("DatosPrueba:ClaveApiRevocada", "hydra_otra_clave_de_verdad"),
        };

        var configuracion = Configuracion([.. todas.Where(v => v.Item1 != claveQueFalta)]);

        var acto = () => CredencialesDemo.Resolver(configuracion, new EntornoDePrueba("Production"));

        acto.Should().Throw<InvalidOperationException>(
                "sembrar en Producción con el valor por defecto publicaría el acceso a los tenants de demo")
            .WithMessage($"*{claveQueFalta}*",
                "el mensaje tiene que nombrar la clave que falta: quien lo lea está desplegando, no leyendo el código");
    }

    [Fact]
    public void En_produccion_con_las_tres_configuradas_usa_las_configuradas()
    {
        var configuracion = Configuracion(
            ("DatosPrueba:Contrasena", "Una-Contrasena-De-Verdad#2026"),
            ("DatosPrueba:ClaveApiActiva", "hydra_una_clave_de_verdad"),
            ("DatosPrueba:ClaveApiRevocada", "hydra_otra_clave_de_verdad"));

        var credenciales = CredencialesDemo.Resolver(configuracion, new EntornoDePrueba("Production"));

        credenciales.Contrasena.Should().Be("Una-Contrasena-De-Verdad#2026");
        credenciales.ClaveApiActiva.Should().Be("hydra_una_clave_de_verdad");
        credenciales.ClaveApiRevocada.Should().Be("hydra_otra_clave_de_verdad");
    }

    /// <summary>
    /// El guardia no es de un sembrador concreto. <c>SegundoTenantSeeder</c>
    /// tiene su propio interruptor y su propia constante pública, y comparte
    /// el mismo defecto: si alguien lo enciende en Producción, publica una
    /// sesión de administrador. Este test fija que pasa por la misma regla.
    /// </summary>
    [Fact]
    public void El_guardia_cubre_tambien_la_contrasena_del_segundo_tenant()
    {
        var enProduccion = () => CredencialesDemo.ResolverCredencial(
            Configuracion(), new EntornoDePrueba("Production"),
            SegundoTenantSeeder.ClaveContrasenaConfiguracion,
            SegundoTenantSeeder.ContrasenaAdministradorSegundoTenant);

        enProduccion.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{SegundoTenantSeeder.ClaveContrasenaConfiguracion}*");

        CredencialesDemo.ResolverCredencial(
                Configuracion(), new EntornoDePrueba("Development"),
                SegundoTenantSeeder.ClaveContrasenaConfiguracion,
                SegundoTenantSeeder.ContrasenaAdministradorSegundoTenant)
            .Should().Be(SegundoTenantSeeder.ContrasenaAdministradorSegundoTenant,
                "el E2E de aislamiento multi-tenant inicia sesión con la contraseña conocida");
    }

    /// <summary>
    /// Una cadena en blanco no es una credencial configurada. Sin esto,
    /// <c>DatosPrueba__Contrasena=""</c> —el error de despliegue más fácil de
    /// cometer— pasaría el filtro y la siembra caería al valor público
    /// justamente en Producción, que es el único sitio donde no puede.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void En_produccion_una_credencial_en_blanco_no_cuenta_como_configurada(string enBlanco)
    {
        var configuracion = Configuracion(
            ("DatosPrueba:Contrasena", enBlanco),
            ("DatosPrueba:ClaveApiActiva", "hydra_una_clave_de_verdad"),
            ("DatosPrueba:ClaveApiRevocada", "hydra_otra_clave_de_verdad"));

        var acto = () => CredencialesDemo.Resolver(configuracion, new EntornoDePrueba("Production"));

        acto.Should().Throw<InvalidOperationException>();
    }
}
