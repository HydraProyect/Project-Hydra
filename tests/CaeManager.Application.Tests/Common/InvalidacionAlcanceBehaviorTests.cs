using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class InvalidacionAlcanceBehaviorTests
{
    private record FalsoCommand : ICommand;
    private record FalsoConValorCommand : ICommand<Guid>;
    private record FalsaQuery : IRequest<string>;

    private sealed class InvalidadorContador : IInvalidadorAlcance
    {
        public int Llamadas { get; private set; }
        public void Invalidar() => Llamadas++;
    }

    [Fact]
    public async Task Un_Command_invalida_el_alcance_despues_de_ejecutarse()
    {
        var invalidador = new InvalidadorContador();
        var llamadasAlEjecutar = -1;
        var behavior = new InvalidacionAlcanceBehavior<FalsoCommand, Result>(invalidador);

        await behavior.Handle(new FalsoCommand(), _ =>
        {
            llamadasAlEjecutar = invalidador.Llamadas;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        llamadasAlEjecutar.Should().Be(0, "se invalida DESPUÉS del handler, no antes");
        invalidador.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_Command_con_valor_tambien_invalida()
    {
        var invalidador = new InvalidadorContador();
        var behavior = new InvalidacionAlcanceBehavior<FalsoConValorCommand, Result<Guid>>(invalidador);

        await behavior.Handle(new FalsoConValorCommand(), _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        invalidador.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_Command_que_falla_con_excepcion_invalida_igualmente()
    {
        var invalidador = new InvalidadorContador();
        var behavior = new InvalidacionAlcanceBehavior<FalsoCommand, Result>(invalidador);

        var accion = () => behavior.Handle(new FalsoCommand(), _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>();
        invalidador.Llamadas.Should().Be(1, "un guardado parcial tampoco debe dejar una visión de la que no se sabe si es cierta");
    }

    [Fact]
    public async Task Una_Query_no_invalida()
    {
        var invalidador = new InvalidadorContador();
        var behavior = new InvalidacionAlcanceBehavior<FalsaQuery, string>(invalidador);

        await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        invalidador.Llamadas.Should().Be(0);
    }
}
