using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class ConcurrenciaBehaviorTests
{
    private record FalsoCommand : IRequest<Result>;
    private record FalsoConValorCommand : IRequest<Result<Guid>>;
    private record FalsaQuery : IRequest<string>;

    [Fact]
    public async Task Convierte_el_choque_en_un_fallo_con_mensaje_para_la_persona()
    {
        var behavior = new ConcurrenciaBehavior<FalsoCommand, Result>();

        var resultado = await behavior.Handle(
            new FalsoCommand(),
            _ => throw new DbUpdateConcurrencyException("simulado"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
        resultado.Error.Mensaje.Should().Contain("Otra persona modificó este registro");
    }

    [Fact]
    public async Task Tambien_para_los_command_que_devuelven_un_valor()
    {
        var behavior = new ConcurrenciaBehavior<FalsoConValorCommand, Result<Guid>>();

        var resultado = await behavior.Handle(
            new FalsoConValorCommand(),
            _ => throw new DbUpdateConcurrencyException("simulado"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
    }

    [Fact]
    public async Task No_se_traga_nada_cuando_no_hay_conflicto()
    {
        var behavior = new ConcurrenciaBehavior<FalsoCommand, Result>();

        var resultado = await behavior.Handle(
            new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Una_query_propaga_la_excepcion_en_vez_de_inventarse_un_Result()
    {
        // Una Query no guarda nada, así que si el choque llega desde una es
        // un caso que este behavior no sabe representar — devolver un Result
        // que el llamador no espera sería peor que propagar.
        var behavior = new ConcurrenciaBehavior<FalsaQuery, string>();

        var ejecutar = async () => await behavior.Handle(
            new FalsaQuery(),
            _ => throw new DbUpdateConcurrencyException("simulado"),
            CancellationToken.None);

        await ejecutar.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
