using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class AutorizacionEscrituraBehaviorTests
{
    // Lo que decide es la interfaz ICommand, no el sufijo del nombre — ver
    // AutorizacionEscrituraBehavior y el test de al lado que lo demuestra.
    private record FalsoCommand : ICommand;
    private record FalsoConValorCommand : ICommand<Guid>;
    private record FalsaQuery : IRequest<string>;

    // Se llama "Command" pero no implementa ICommand: el behavior lo deja
    // pasar. Es exactamente el agujero que antes abría un typo en el nombre,
    // solo que ahora al revés — y quien lo cierra es ArquitecturaCommandsTests,
    // que falla si un tipo *Command del ensamblado de Application no
    // implementa ICommand. Este test existe para dejar constancia de que la
    // red de seguridad es ese test de arquitectura, no el behavior.
    private record FalsoSinInterfazCommand : IRequest<Result>;

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RolInventado")]
    public async Task Bloquea_un_command_cuando_no_hay_un_rol_de_escritura_reconocible(string? rol)
    {
        // Lista blanca, no lista negra (hallazgo N-5 de INFORME-AUDITORIA-2.md).
        // "Sin rol" ocurre de verdad en dos casos: un usuario que aún no lo
        // tiene asignado, y un Operador Delegado cuya delegación se revocó
        // mientras su token de selección seguía vigente — ahí
        // ObtenerRolActualAsync devuelve null a propósito.
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

    [Fact]
    public async Task No_bloquea_una_query_ni_siquiera_para_roles_de_solo_lectura()
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsaQuery, string>(new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"));

        var resultado = await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
    }

    [Fact]
    public async Task Un_tipo_llamado_Command_sin_ICommand_no_lo_autoriza_el_behavior()
    {
        // No es el comportamiento deseable, es el comportamiento real: el
        // behavior ya no mira nombres. Quien impide que esto exista en el
        // código de producción es ArquitecturaCommandsTests.
        var behavior = new AutorizacionEscrituraBehavior<FalsoSinInterfazCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"));

        var resultado = await behavior.Handle(
            new FalsoSinInterfazCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
