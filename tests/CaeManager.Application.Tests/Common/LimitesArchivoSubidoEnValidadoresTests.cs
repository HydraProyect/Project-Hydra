using CaeManager.Application.Blindaje42.Commands.RegistrarRespuestaCertificacionTgss;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Los validadores de evidencia aplican el mismo tope que la pantalla que la
/// sube (<see cref="LimitesArchivoSubido.TamanoMaximoBytes"/>): hasta el
/// 2026-09-23 cada uno tenía su propia copia de 10 MB y nada comprobaba que
/// coincidieran. La frontera se mide en los dos lados: el tope exacto se
/// acepta, un byte más se rechaza con el tope en el mensaje. La frontera
/// inferior de la misma regla (evidencia vacía) también se fija aquí.
/// </summary>
public class LimitesArchivoSubidoEnValidadoresTests
{
    public static TheoryData<string> Validadores => ["Tgss", "VerificacionExterna"];

    [Theory]
    [MemberData(nameof(Validadores))]
    public void Una_evidencia_del_tamano_maximo_exacto_se_acepta(string validador)
    {
        var errores = Validar(validador, new byte[LimitesArchivoSubido.TamanoMaximoBytes]);

        errores.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Validadores))]
    public void Una_evidencia_de_un_byte_mas_se_rechaza_nombrando_el_tope(string validador)
    {
        var errores = Validar(validador, new byte[LimitesArchivoSubido.TamanoMaximoBytes + 1]);

        errores.Should().ContainSingle()
            .Which.Should().Be($"La evidencia no puede estar vacía ni superar los {LimitesArchivoSubido.TamanoMaximoMb} MB.");
    }

    [Theory]
    [MemberData(nameof(Validadores))]
    public void Una_evidencia_vacia_se_rechaza_con_el_mismo_mensaje(string validador)
    {
        var errores = Validar(validador, []);

        errores.Should().ContainSingle()
            .Which.Should().Be($"La evidencia no puede estar vacía ni superar los {LimitesArchivoSubido.TamanoMaximoMb} MB.");
    }

    [Fact]
    public void El_tope_en_MB_es_el_de_bytes_en_MB_enteros()
    {
        LimitesArchivoSubido.TamanoMaximoMb.Should().Be(10);
        LimitesArchivoSubido.TamanoMaximoBytes.Should().Be(10 * 1024 * 1024);
    }

    private static List<string> Validar(string validador, byte[] evidencia)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var resultado = validador switch
        {
            "Tgss" => new RegistrarRespuestaCertificacionTgssCommandValidator().Validate(
                new RegistrarRespuestaCertificacionTgssCommand(
                    Guid.NewGuid(), ResultadoCertificacionTgss.SinDescubiertos, hoy, evidencia, "evidencia.pdf")),
            _ => new RegistrarVerificacionExternaSubcontrataCommandValidator().Validate(
                new RegistrarVerificacionExternaSubcontrataCommand(
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), hoy, ResultadoVerificacionExterna.Valido,
                    null, null, evidencia, "evidencia.pdf")),
        };
        return resultado.Errors.Select(e => e.ErrorMessage).ToList();
    }
}
