using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma;

public class AperturaConTotpTests
{
    [Theory]
    [InlineData(false, "SesionPrivilegiada.TotpNoVerificado")]
    [InlineData(true, "SesionPrivilegiada.TenantPropio")]
    public async Task Exige_totp_de_la_sesion_antes_de_consultar_la_concesion(bool totp, string error)
    {
        var tenant = Guid.NewGuid();
        var usuario = new CurrentUserServiceFalso(usuarioId: Guid.NewGuid(), tenantOrigenId: tenant, superoTotp: totp);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(null!, null!, null!, usuario, null!);
        var resultado = await handler.Handle(new(Guid.NewGuid(), tenant, "motivo", 1), CancellationToken.None);
        resultado.Error.Codigo.Should().Be(error);
    }
}
