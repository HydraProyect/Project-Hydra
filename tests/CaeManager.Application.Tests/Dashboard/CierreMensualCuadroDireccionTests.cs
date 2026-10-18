using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

public class CierreMensualCuadroDireccionTests
{
    private static readonly DateTime Ahora = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_se_cierra_un_mes_en_curso()
    {
        var accion = () => CierreMensualTenantBeneficiario.Crear(2026, 11, "{}", false, Ahora);
        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void La_reapertura_conserva_la_foto_y_el_motivo_hasta_el_recierre()
    {
        var cierre = CierreMensualTenantBeneficiario.Crear(2026, 10, "{\"version\":1}", false, Ahora);
        cierre.Reabrir("Corrección del mes", Ahora.AddHours(1));
        cierre.FotoJson.Should().Be("{\"version\":1}");
        cierre.ReabiertoEnUtc.Should().Be(Ahora.AddHours(1));
        cierre.MotivoReapertura.Should().Be("Corrección del mes");
        cierre.Recerrar("{\"version\":2}", Ahora.AddHours(2));
        cierre.ReabiertoEnUtc.Should().BeNull();
        cierre.FotoJson.Should().Be("{\"version\":2}");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Administrador")]
    [InlineData(null)]
    public async Task Solo_la_direccion_puede_reabrir_un_mes(string? rol)
    {
        var handler = new ReabrirMesCuadroDireccionCommandHandler(new CurrentUserServiceFalso(rol: rol), null!, null!, null!, null!, TimeProvider.System);
        var resultado = await handler.Handle(new(Guid.NewGuid(), 2026, 10, "Corrección"), CancellationToken.None);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CuadroDireccion.SinPermiso");
    }

    [Fact]
    public async Task La_direccion_pasa_la_autorizacion_y_debe_indicar_el_motivo()
    {
        var handler = new ReabrirMesCuadroDireccionCommandHandler(new CurrentUserServiceFalso(rol: "DireccionCae"), null!, null!, null!, null!, TimeProvider.System);
        var resultado = await handler.Handle(new(Guid.NewGuid(), 2026, 10, ""), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("CuadroDireccion.MotivoObligatorio");
    }

    [Fact]
    public void La_foto_serializada_preserva_las_cifras_y_los_precios()
    {
        var foto = new FotoCierreMensual(new(4, 2, 1, 3, 1, 8, 5, [], new(0, 0, 0, 0, 0, 0, 0, 0), 6),
            [new(Guid.NewGuid(), CaeManager.Domain.Facturacion.ConceptoFacturable.AltaCentro, 12.5m, "EUR")]);
        var restaurada = FotoCierreMensual.Leer(foto.Serializar());
        restaurada.Should().BeEquivalentTo(foto);
    }
}
