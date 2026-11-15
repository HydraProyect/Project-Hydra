using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// D-4 (#2005): con la regla de bloqueo común un Pendiente en plataforma impide el sello del expediente, así que una orden
/// que cambia una Acreditación tiene que reevaluar las Visitas de su Documento cuando termina bien, y solo entonces.
/// </summary>
public class ReevaluarExpedientesTrasCambioDeAcreditacionBehaviorTests
{
    private sealed record OrdenDeAcreditacion(Guid AcreditacionId) : ICambiaEstadoDeAcreditacion;
    private sealed record OrdenCualquiera(Guid AcreditacionId);

    private sealed class EvaluadorQueAnota : IEvaluadorExpedienteVisitaService
    {
        public List<Guid> DocumentosEvaluados { get; } = [];
        public bool Lanzar { get; init; }

        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
        {
            if (Lanzar) throw new InvalidOperationException("fallo del evaluador");
            DocumentosEvaluados.Add(documentoId);
            return Task.CompletedTask;
        }
    }

    private static (DocumentosQueryContextFalso Documentos, AcreditacionDocumentoPlataforma Acreditacion) Mundo()
    {
        var documentos = new DocumentosQueryContextFalso();
        var acreditacion = new AcreditacionDocumentoPlataforma(Guid.NewGuid(), Guid.NewGuid());
        documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        return (documentos, acreditacion);
    }

    private static ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, Result> Behavior<TRequest>(
        DocumentosQueryContextFalso documentos, IEvaluadorExpedienteVisitaService evaluador) where TRequest : notnull =>
        new(documentos, evaluador, NullLogger<ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, Result>>.Instance);

    [Fact]
    public async Task Tras_una_orden_de_acreditacion_con_exito_reevalua_las_visitas_de_su_documento()
    {
        var (documentos, acreditacion) = Mundo();
        var evaluador = new EvaluadorQueAnota();

        var respuesta = await Behavior<OrdenDeAcreditacion>(documentos, evaluador)
            .Handle(new OrdenDeAcreditacion(acreditacion.Id), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        respuesta.EsExitoso.Should().BeTrue();
        evaluador.DocumentosEvaluados.Should().Equal(acreditacion.DocumentoId);
    }

    [Fact]
    public async Task Una_orden_fallida_no_reevalua_nada()
    {
        var (documentos, acreditacion) = Mundo();
        var evaluador = new EvaluadorQueAnota();

        await Behavior<OrdenDeAcreditacion>(documentos, evaluador)
            .Handle(new OrdenDeAcreditacion(acreditacion.Id),
                _ => Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "fallo"))), CancellationToken.None);

        evaluador.DocumentosEvaluados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_orden_sin_el_marcador_no_reevalua_nada()
    {
        var (documentos, acreditacion) = Mundo();
        var evaluador = new EvaluadorQueAnota();

        await Behavior<OrdenCualquiera>(documentos, evaluador)
            .Handle(new OrdenCualquiera(acreditacion.Id), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        evaluador.DocumentosEvaluados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_al_reevaluar_no_tumba_la_orden_ya_guardada()
    {
        var (documentos, acreditacion) = Mundo();
        var evaluador = new EvaluadorQueAnota { Lanzar = true };

        var respuesta = await Behavior<OrdenDeAcreditacion>(documentos, evaluador)
            .Handle(new OrdenDeAcreditacion(acreditacion.Id), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        respuesta.EsExitoso.Should().BeTrue();
    }
}
