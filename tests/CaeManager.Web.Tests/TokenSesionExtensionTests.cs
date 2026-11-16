using CaeManager.Infrastructure.Autenticacion;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El credencial con el que la extensión de navegador se autentica.
///
/// <para>
/// Las ramas de parseo se prueban aparte del ciclo criptográfico porque
/// fallan hacia lados distintos: un token manipulado lo rechaza Data
/// Protection y se nota, pero una carga útil malformada que el parseo
/// ACEPTARA concedería acceso sin ningún síntoma. Un Guid vacío colándose
/// como <c>UsuarioId</c> es el caso que más caro sale y el que ningún test de
/// extremo a extremo llegaría a producir.
/// </para>
/// </summary>
public class TokenSesionExtensionTests
{
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();

    private static readonly MarcasSesionOrigen Marcas = new(
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), DateTimeOffset.FromUnixTimeSeconds(1_800_000_300));

    private static readonly long Segundos = Marcas.Inicio.ToUnixTimeSeconds();

    [Fact]
    public void Un_token_emitido_se_lee_con_el_mismo_usuario_y_stamp()
    {
        var usuarioId = Guid.NewGuid();
        var sid = Guid.NewGuid();
        var token = TokenSesionExtension.Proteger(_protector, usuarioId, "STAMPBASE32", sid, Marcas);

        var carga = TokenSesionExtension.Leer(_protector, token);

        carga.Should().NotBeNull();
        carga!.UsuarioId.Should().Be(usuarioId);
        carga.SecurityStamp.Should().Be("STAMPBASE32");
        carga.Sid.Should().Be(sid);
        carga.Marcas.Should().Be(Marcas, "las marcas de la sesión de origen viajan tal cual, al segundo");
    }

    [Fact]
    public void Un_token_de_otro_proveedor_de_claves_no_se_lee()
    {
        var token = TokenSesionExtension.Proteger(_protector, Guid.NewGuid(), "STAMP", Guid.NewGuid(), Marcas);

        // Otro proveedor efímero = otras claves, como tras rotarlas.
        TokenSesionExtension.Leer(new EphemeralDataProtectionProvider(), token).Should().BeNull();
    }

    [Fact]
    public void Un_texto_que_no_es_un_token_no_se_lee()
    {
        TokenSesionExtension.Leer(_protector, "esto-no-es-un-token").Should().BeNull();
    }

    [Fact]
    public void Emitir_sin_security_stamp_no_esta_permitido()
    {
        // Sin stamp no habría con qué revocar el token después.
        var emitir = () => TokenSesionExtension.Proteger(_protector, Guid.NewGuid(), "   ", Guid.NewGuid(), Marcas);

        emitir.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("solo-una-parte")]
    [InlineData("a|b|c")]
    [InlineData("no-es-un-guid|STAMP")]
    [InlineData("00000000000000000000000000000000|STAMP")]
    [InlineData("|STAMP")]
    public void Una_carga_util_malformada_se_rechaza(string cargaUtil)
    {
        TokenSesionExtension.ParsearCargaUtil(cargaUtil).Should().BeNull();
    }

    [Fact]
    public void Un_token_anterior_sin_sid_se_rechaza()
    {
        var protector = _protector.CreateProtector("CaeManager.Extension.Sesion.v1").ToTimeLimitedDataProtector();
        var token = protector.Protect($"{Guid.NewGuid():N}|STAMP", TimeSpan.FromHours(8));
        TokenSesionExtension.Leer(_protector, token).Should().BeNull();
        TokenSesionExtension.ParsearCargaUtil($"{Guid.NewGuid():N}|STAMP|{Guid.Empty:N}|{Segundos}|{Segundos}").Should().BeNull();
    }

    [Fact]
    public void Una_carga_util_sin_stamp_se_rechaza()
    {
        TokenSesionExtension.ParsearCargaUtil($"{Guid.NewGuid():N}|   |{Guid.NewGuid():N}|{Segundos}|{Segundos}").Should().BeNull();
    }

    [Fact]
    public void Una_carga_util_valida_se_acepta()
    {
        // Discrimina: si el parseo rechazara todo, los casos de arriba
        // pasarían igual y no probarían nada.
        var usuarioId = Guid.NewGuid();
        var sid = Guid.NewGuid();

        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}|{Segundos}|{Segundos + 60}")
            .Should().BeEquivalentTo(new CargaUtilTokenExtension(
                usuarioId, "STAMP", sid, new MarcasSesionOrigen(Marcas.Inicio, Marcas.Inicio.AddSeconds(60))));
    }

    /// <summary>
    /// Issue #1891: el token emitido antes de llevar las marcas de la sesión de origen no se admite, y una marca
    /// ilegible tampoco. Sin ellas no habría con qué aplicar el tope ni la inactividad de esa sesión.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData(" 12")]
    [InlineData("999999999999999999")]
    public void Una_carga_util_sin_marcas_legibles_se_rechaza(string marca)
    {
        var usuarioId = Guid.NewGuid();
        var sid = Guid.NewGuid();

        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}").Should().BeNull("el formato anterior, sin marcas");
        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}|{Segundos}").Should().BeNull("una sola marca");
        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}|{marca}|{Segundos}").Should().BeNull("inicio ilegible");
        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}|{Segundos}|{marca}").Should().BeNull("interacción ilegible");
        TokenSesionExtension.ParsearCargaUtil($"{usuarioId:N}|STAMP|{sid:N}|{Segundos}|{Segundos}|{Segundos}").Should().BeNull("una parte de más");
    }
}
