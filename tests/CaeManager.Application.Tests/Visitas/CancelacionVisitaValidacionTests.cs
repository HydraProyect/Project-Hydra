using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.CancelarVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using FluentValidation;

namespace CaeManager.Application.Tests.Visitas;

public class CancelacionVisitaValidacionTests
{
    public static TheoryData<string?> MotivosInvalidos => new()
    {
        null, "", "   ", "\t\r\n", new string('a', Visita.LongitudMaximaMotivo + 1)
    };

    [Theory]
    [MemberData(nameof(MotivosInvalidos))]
    public async Task El_servidor_rechaza_el_motivo_invalido_antes_de_cancelar(string? motivo)
    {
        var individual = new ValidationBehavior<CancelarVisitaCommand, Result<VisitaCanceladaDto>>(
            [new CancelarVisitaCommandValidator()]);
        var lote = new ValidationBehavior<CancelarVisitasCommand, Result<ResultadoCancelacionLoteDto>>(
            [new CancelarVisitasCommandValidator()]);
        var ejecuciones = 0;

        var cancelar = async () => await individual.Handle(new(Guid.NewGuid(), motivo), _ =>
        {
            ejecuciones++;
            return Task.FromResult(Result.Exito(new VisitaCanceladaDto(Guid.NewGuid(), Guid.NewGuid())));
        }, default);
        var cancelarLote = async () => await lote.Handle(new([Guid.NewGuid()], motivo), _ =>
        {
            ejecuciones++;
            return Task.FromResult(Result.Exito(new ResultadoCancelacionLoteDto(0, [], [])));
        }, default);

        var mensaje = string.IsNullOrWhiteSpace(motivo)
            ? "Indica el motivo de la cancelación."
            : $"El motivo no puede superar los {Visita.LongitudMaximaMotivo} caracteres.";
        var rechazo = (await cancelar.Should().ThrowAsync<ValidationException>()).Which;
        var rechazoLote = (await cancelarLote.Should().ThrowAsync<ValidationException>()).Which;
        rechazo.Errors.Should().ContainSingle(e => e.PropertyName == "Motivo" && e.ErrorMessage == mensaje);
        rechazoLote.Errors.Should().ContainSingle(e => e.PropertyName == "Motivo" && e.ErrorMessage == mensaje);
        ejecuciones.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Visita.LongitudMaximaMotivo)]
    public async Task Acepta_un_motivo_hasta_el_limite_de_la_columna(int longitud)
    {
        var motivo = new string('a', longitud);

        (await new CancelarVisitaCommandValidator().ValidateAsync(new CancelarVisitaCommand(Guid.NewGuid(), motivo)))
            .IsValid.Should().BeTrue();
        (await new CancelarVisitasCommandValidator().ValidateAsync(new CancelarVisitasCommand([Guid.NewGuid()], motivo)))
            .IsValid.Should().BeTrue();
    }
}
