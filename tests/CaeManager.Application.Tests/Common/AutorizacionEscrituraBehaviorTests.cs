using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class AutorizacionEscrituraBehaviorTests
{
    // Nombres terminados en "Command"/"Query" a propósito — el behavior
    // distingue por convención de nombre (ver AutorizacionEscrituraBehavior).
    private record FalsoCommand : IRequest<Result>;
    private record FalsoConValorCommand : IRequest<Result<Guid>>;
    private record FalsaQuery : IRequest<string>;

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Bloquea_un_command_con_resultado_simple_para_roles_de_solo_lectura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(new CurrentUserServiceFalso(Guid.NewGuid(), rol));
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoCommand(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Bloquea_un_command_con_resultado_generico_para_roles_de_solo_lectura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoConValorCommand, Result<Guid>>(new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        var resultado = await behavior.Handle(
            new FalsoConValorCommand(), _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    public async Task Deja_pasar_un_command_para_roles_con_permiso_de_escritura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task No_bloquea_una_query_ni_siquiera_para_roles_de_solo_lectura()
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsaQuery, string>(new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"));

        var resultado = await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
    }
}
