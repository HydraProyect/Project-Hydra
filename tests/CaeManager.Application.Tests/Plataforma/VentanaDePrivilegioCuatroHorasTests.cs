using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// DEC-43 (2026-09-02): ninguna vía de apertura de un acceso privilegiado
/// puede fijar una ventana de más de 4 horas. Hay <b>dos</b> vías vivas en
/// producción que abren ese tipo de acceso — la vigente
/// (<see cref="AbrirSesionPrivilegiadaCommand"/>, plano 3) y la heredada
/// (<see cref="AbrirAccesoSoporteCommand"/>, que activa una
/// <c>DelegacionTenant</c> en vez de una <c>SesionPrivilegiada</c>) — y las dos
/// tienen que respetar el mismo techo.
///
/// El techo vive por separado en cada validador a propósito (ver el comentario
/// de <see cref="AbrirAccesoSoporteCommandValidator.MaximoHorasDeVentana"/>): no
/// se acopla la feature <c>Tenants</c> al dominio de <c>Plataforma</c> solo para
/// compartir una constante. El último test de esta clase es lo que cierra el
/// hueco que abre esa decisión: si algún día divergen, falla aquí y no en
/// producción.
/// </summary>
public class VentanaDePrivilegioCuatroHorasTests
{
    [Fact]
    public void AbrirSesionPrivilegiada_acepta_cuatro_horas_y_rechaza_cinco()
    {
        var validador = new AbrirSesionPrivilegiadaCommandValidator();

        var cuatro = new AbrirSesionPrivilegiadaCommand(
            Guid.NewGuid(), Guid.NewGuid(), "Reproducir la incidencia", HorasDeVentana: 4);
        var cinco = cuatro with { HorasDeVentana = 5 };

        validador.Validate(cuatro).IsValid.Should().BeTrue("cuatro horas es el techo, inclusive");
        validador.Validate(cinco).IsValid.Should().BeFalse("cinco horas ya supera el techo de DEC-43");
    }

    [Fact]
    public void AbrirAccesoSoporte_vía_heredada_acepta_cuatro_horas_y_rechaza_cinco()
    {
        var validador = new AbrirAccesoSoporteCommandValidator();

        var cuatro = new AbrirAccesoSoporteCommand(
            Guid.NewGuid(), "Reproducir la incidencia", HorasDeVentana: 4);
        var cinco = cuatro with { HorasDeVentana = 5 };

        validador.Validate(cuatro).IsValid.Should().BeTrue("la vía heredada queda bajo el mismo techo de 4 horas");
        validador.Validate(cinco).IsValid.Should().BeFalse(
            "si esta vía admitiera más de 4 horas, DEC-43 se cumpliría en una ceremonia y se saltaría en la otra");
    }

    [Fact]
    public void Los_dos_techos_son_el_mismo_valor_aunque_vivan_en_features_distintas()
    {
        // Comparación por TimeSpan exacto, no por (int)TotalHours: truncar
        // horas fraccionarias dejaría pasar una divergencia real (p. ej. si
        // VentanaMaxima pasara a 4.5 horas, (int)TotalHours seguiría dando 4 y
        // este test callaría el hueco en vez de encontrarlo).
        TimeSpan.FromHours(AbrirAccesoSoporteCommandValidator.MaximoHorasDeVentana).Should().Be(
            SesionPrivilegiada.VentanaMaxima,
            "las dos vías abren, en la práctica, el mismo tipo de acceso privilegiado, y DEC-43 no admite " +
            "un valor distinto de 4 horas para ninguna de las dos");
    }
}
