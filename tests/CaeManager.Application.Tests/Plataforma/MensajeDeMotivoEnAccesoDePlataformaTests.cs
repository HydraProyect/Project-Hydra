using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// Decisión del propietario del 2026-10-09: el mensaje que ve un Actor de
/// Plataforma TALVEG al abrir un acceso sin motivo nombra al Tenant propietario
/// como «esta organización», no como «este cliente». Desde ese mismo día la
/// interfaz rotula «Cliente» al Cliente empresarial, así que «cliente» en este
/// mensaje nombraría otra cosa: la Empresa a la que un Tenant presta servicio,
/// no la organización dueña de los datos en los que se va a entrar.
///
/// Las dos vías vivas de apertura (la vigente,
/// <see cref="AbrirSesionPrivilegiadaCommand"/>, y la heredada,
/// <see cref="AbrirAccesoSoporteCommand"/>) dicen lo mismo, con el mismo
/// criterio que <see cref="VentanaDePrivilegioCuatroHorasTests"/>: el texto vive
/// por separado en cada validador y este test es lo que impide que diverjan.
/// </summary>
public class MensajeDeMotivoEnAccesoDePlataformaTests
{
    private const string MensajeEsperado = "Indica por qué necesitas entrar en los datos de esta organización.";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AbrirSesionPrivilegiada_sin_motivo_pide_el_motivo_nombrando_la_organizacion(string motivo)
    {
        var validador = new AbrirSesionPrivilegiadaCommandValidator();

        var resultado = validador.Validate(new AbrirSesionPrivilegiadaCommand(
            Guid.NewGuid(), Guid.NewGuid(), motivo, HorasDeVentana: 1));

        MensajesDelMotivo(resultado).Should().Equal(MensajeEsperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AbrirAccesoSoporte_vía_heredada_sin_motivo_pide_el_motivo_nombrando_la_organizacion(string motivo)
    {
        var validador = new AbrirAccesoSoporteCommandValidator();

        var resultado = validador.Validate(new AbrirAccesoSoporteCommand(
            Guid.NewGuid(), motivo, HorasDeVentana: 1));

        MensajesDelMotivo(resultado).Should().Equal(MensajeEsperado);
    }

    private static IEnumerable<string> MensajesDelMotivo(FluentValidation.Results.ValidationResult resultado) =>
        resultado.Errors
            .Where(e => e.PropertyName == nameof(AbrirSesionPrivilegiadaCommand.Motivo))
            .Select(e => e.ErrorMessage);
}
