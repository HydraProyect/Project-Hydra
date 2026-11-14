using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using CaeManager.Domain.Common;
using FluentAssertions;
using FluentValidation;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

/// <summary>
/// Tope de 200 Trabajadores por lote de generación de Plantillas (decisión de Chris, 2026-11-14). El único punto de
/// entrada que crea un lote es <see cref="IniciarLoteGeneracionDocumentosCommand"/>; se prueba su validador y, por
/// el pipeline real (<see cref="ValidationBehavior{TRequest,TResponse}"/>), que a 201 el handler ni se invoca.
/// </summary>
public class TopeLoteGeneracionDocumentosTests
{
    private static IniciarLoteGeneracionDocumentosCommand Comando(IReadOnlyList<Guid> trabajadorIds) =>
        new(Guid.NewGuid(), trabajadorIds);

    private static List<Guid> Trabajadores(int cuantos) =>
        Enumerable.Range(0, cuantos).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void El_tope_es_200()
    {
        IniciarLoteGeneracionDocumentosCommandValidator.MaximoTrabajadoresPorLote.Should().Be(200);
    }

    [Fact]
    public void Un_lote_de_exactamente_200_trabajadores_es_valido()
    {
        var resultado = new IniciarLoteGeneracionDocumentosCommandValidator().Validate(Comando(Trabajadores(200)));

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Un_lote_de_201_trabajadores_se_rechaza_entero_con_el_mensaje_del_tope()
    {
        var resultado = new IniciarLoteGeneracionDocumentosCommandValidator().Validate(Comando(Trabajadores(201)));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("200").And.Contain("201");
    }

    [Fact]
    public void Los_repetidos_no_cuentan_para_el_tope_porque_el_handler_los_deduplica()
    {
        var unico = Guid.NewGuid();
        var conRepetidos = Enumerable.Repeat(unico, 500).ToList();

        new IniciarLoteGeneracionDocumentosCommandValidator().Validate(Comando(conRepetidos)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Doscientos_uno_distintos_con_repetidos_de_relleno_siguen_rechazados()
    {
        var ids = Trabajadores(201);
        ids.AddRange(ids.Take(50));

        new IniciarLoteGeneracionDocumentosCommandValidator().Validate(Comando(ids)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Por_el_pipeline_201_lanza_ValidationException_con_el_mensaje_y_el_handler_no_se_invoca()
    {
        var handlerInvocado = false;
        var pipeline = new ValidationBehavior<IniciarLoteGeneracionDocumentosCommand, Result<IniciarLoteGeneracionDocumentosResultadoDto>>(
            [new IniciarLoteGeneracionDocumentosCommandValidator()]);

        var accion = () => pipeline.Handle(Comando(Trabajadores(201)), _ =>
        {
            handlerInvocado = true;
            return Task.FromResult(Result.Exito(new IniciarLoteGeneracionDocumentosResultadoDto(Guid.NewGuid(), [])));
        }, CancellationToken.None);

        var excepcion = await accion.Should().ThrowAsync<ValidationException>();
        excepcion.Which.Errors.Should().Contain(e => e.ErrorMessage.Contains("200"));
        handlerInvocado.Should().BeFalse("un lote que supera el tope no nace, ni a medias");
    }

    [Fact]
    public async Task Por_el_pipeline_200_llega_al_handler()
    {
        var handlerInvocado = false;
        var pipeline = new ValidationBehavior<IniciarLoteGeneracionDocumentosCommand, Result<IniciarLoteGeneracionDocumentosResultadoDto>>(
            [new IniciarLoteGeneracionDocumentosCommandValidator()]);

        await pipeline.Handle(Comando(Trabajadores(200)), _ =>
        {
            handlerInvocado = true;
            return Task.FromResult(Result.Exito(new IniciarLoteGeneracionDocumentosResultadoDto(Guid.NewGuid(), [])));
        }, CancellationToken.None);

        handlerInvocado.Should().BeTrue();
    }
}
