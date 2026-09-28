using CaeManager.Application.Blindaje42.Commands.RegistrarRespuestaCertificacionTgss;
using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Las reglas «no puede ser futura» de los validadores juzgan con el día de
/// negocio (Europe/Madrid). A las 22:30 UTC de un día de verano en Madrid ya es
/// mañana: la fecha de hoy en Madrid se admite (con el día UTC se rechazaba como
/// futura) y la de mañana en Madrid se rechaza.
/// </summary>
public class NoFuturaConDiaDeNegocioValidatorTests
{
    private static readonly DateTimeOffset MediaHoraTrasLaMedianocheDeMadrid = new(2026, 7, 15, 22, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly HoyEnMadrid = new(2026, 7, 16);

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    public static TheoryData<string> Reglas => new()
    {
        "CrearDocumento", "RenovarDocumento", "SolicitarCertificacionTgss",
        "RegistrarRespuestaCertificacionTgss", "RegistrarVerificacionExterna",
    };

    [Theory]
    [MemberData(nameof(Reglas))]
    public void La_fecha_de_hoy_en_Madrid_se_admite(string regla)
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadrid));

        ErroresDeFecha(regla, HoyEnMadrid).Should().BeEmpty("en Madrid ya es 16; el día UTC (15) la tomaría por futura");
    }

    [Theory]
    [MemberData(nameof(Reglas))]
    public void La_fecha_de_manana_en_Madrid_se_rechaza_como_futura(string regla)
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadrid));

        ErroresDeFecha(regla, HoyEnMadrid.AddDays(1)).Should().ContainSingle()
            .Which.Should().Contain("futura");
    }

    [Fact]
    public void El_validador_lee_el_dia_al_validar_y_no_al_construirse()
    {
        // Un validador construido antes de medianoche y usado después (p. ej. si
        // su registro pasara a singleton) no puede quedarse con el día viejo.
        var validador = new CrearDocumentoCommandValidator();

        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadrid.AddYears(5)));

        validador.Validate(CrearDocumento(HoyEnMadrid.AddYears(5))).Errors
            .Where(e => e.PropertyName == nameof(CrearDocumentoCommand.FechaEmision)).Should().BeEmpty();
    }

    private static CrearDocumentoCommand CrearDocumento(DateOnly fecha) =>
        new(Guid.NewGuid(), null, null, null, null, Guid.NewGuid(), fecha, null, null, null, false);

    private static List<string> ErroresDeFecha(string regla, DateOnly fecha)
    {
        var (resultado, propiedad) = regla switch
        {
            "CrearDocumento" => (new CrearDocumentoCommandValidator().Validate(CrearDocumento(fecha)),
                nameof(CrearDocumentoCommand.FechaEmision)),
            "RenovarDocumento" => (new RenovarDocumentoCommandValidator().Validate(
                    new RenovarDocumentoCommand(Guid.NewGuid(), fecha, null, null, null)),
                nameof(RenovarDocumentoCommand.FechaEmision)),
            "SolicitarCertificacionTgss" => (new SolicitarCertificacionTgssCommandValidator().Validate(
                    new SolicitarCertificacionTgssCommand(Guid.NewGuid(), Guid.NewGuid(), fecha, null)),
                nameof(SolicitarCertificacionTgssCommand.FechaSolicitud)),
            "RegistrarRespuestaCertificacionTgss" => (new RegistrarRespuestaCertificacionTgssCommandValidator().Validate(
                    new RegistrarRespuestaCertificacionTgssCommand(Guid.NewGuid(), ResultadoCertificacionTgss.SinDescubiertos, fecha)),
                nameof(RegistrarRespuestaCertificacionTgssCommand.FechaRespuesta)),
            "RegistrarVerificacionExterna" => (new RegistrarVerificacionExternaSubcontrataCommandValidator().Validate(
                    new RegistrarVerificacionExternaSubcontrataCommand(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fecha, ResultadoVerificacionExterna.Valido, null, null)),
                nameof(RegistrarVerificacionExternaSubcontrataCommand.FechaVerificacion)),
            _ => throw new ArgumentOutOfRangeException(nameof(regla)),
        };

        return resultado.Errors.Where(e => e.PropertyName == propiedad).Select(e => e.ErrorMessage).ToList();
    }
}
