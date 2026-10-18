using Bunit;
using CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Commands.DescartarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El recordatorio propuesto a los dos días laborables de espera de respuesta externa: se ve solo si hay propuesta, el
/// borrador es editable y nada sale hasta que el Gestor CAE pulsa «Aprobar y enviar».
/// </summary>
public class RecordatorioPropuestoTests : BunitContext
{
    private readonly MediatorDeRecordatorio _mediator = new();
    private readonly Guid _conversacionId = Guid.NewGuid();

    public RecordatorioPropuestoTests()
    {
        Services.AddLogging().AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private IRenderedComponent<RecordatorioPropuesto> Renderizar(Action? alEnviar = null) =>
        Render<RecordatorioPropuesto>(p => p
            .Add(c => c.ConversacionId, _conversacionId)
            .Add(c => c.Asunto, "Documentación de octubre")
            .Add(c => c.Estado, EstadoConversacion.EsperandoRespuestaExterna)
            .Add(c => c.OnEnviado, () => alEnviar?.Invoke()));

    [Fact]
    public void Sin_propuesta_no_pinta_nada()
    {
        var componente = Renderizar();

        componente.Markup.Trim().Should().BeEmpty();
        _mediator.Enviados.Should().BeEmpty("sin propuesta no se aprueba ni se descarta nada");
    }

    [Fact]
    public void Con_propuesta_muestra_el_borrador_con_el_asunto_y_no_envia_nada_hasta_aprobar()
    {
        _mediator.Propuesta = new RecordatorioPropuestoDto(Guid.NewGuid(), DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);

        var componente = Renderizar();

        componente.Markup.Should().Contain("Recordatorio propuesto");
        componente.Find("textarea").TextContent.Should().Contain("Documentación de octubre");
        componente.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Aprobar y enviar", "Descartar"]);
        _mediator.Enviados.Should().BeEmpty("nada se envía solo");
    }

    [Fact]
    public void Aprobar_envia_el_borrador_editado_y_avisa_a_la_pagina()
    {
        var propuestaId = Guid.NewGuid();
        _mediator.Propuesta = new RecordatorioPropuestoDto(propuestaId, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);
        var avisada = false;
        var componente = Renderizar(() => avisada = true);

        componente.Find("textarea").Input("Hola, ¿pudisteis revisarlo?");
        componente.FindAll("button").Single(b => b.TextContent.Trim() == "Aprobar y enviar").Click();

        var aprobacion = _mediator.Enviados.OfType<AprobarRecordatorioConversacionCommand>().Should().ContainSingle().Subject;
        aprobacion.ConversacionId.Should().Be(_conversacionId);
        aprobacion.PropuestaId.Should().Be(propuestaId);
        aprobacion.CuerpoHtml.Should().Be("Hola, ¿pudisteis revisarlo?");
        avisada.Should().BeTrue("la página recarga el hilo con el recordatorio ya enviado");
        componente.Markup.Should().NotContain("Recordatorio propuesto");
    }

    [Fact]
    public void Descartar_no_envia_el_recordatorio_y_lo_retira()
    {
        var propuestaId = Guid.NewGuid();
        _mediator.Propuesta = new RecordatorioPropuestoDto(propuestaId, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);
        var componente = Renderizar();

        componente.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar").Click();

        _mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<DescartarRecordatorioConversacionCommand>()
            .Which.PropuestaId.Should().Be(propuestaId);
        componente.Markup.Should().NotContain("Recordatorio propuesto");
    }

    private sealed class MediatorDeRecordatorio : IMediator
    {
        public RecordatorioPropuestoDto? Propuesta { get; set; }
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerRecordatorioPropuestoConversacionQuery)
                return Task.FromResult((TResponse)(object?)Propuesta!);

            Enviados.Add(request);
            return Task.FromResult((TResponse)(object)Result.Exito());
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
