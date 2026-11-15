using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        public bool Cancelar { get; init; }

        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
        {
            if (Lanzar) throw new InvalidOperationException("fallo del evaluador");
            if (Cancelar) throw new OperationCanceledException("cancelada");
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
        DocumentosQueryContextFalso documentos, IEvaluadorExpedienteVisitaService evaluador) where TRequest : notnull
    {
        var servicios = new ServiceCollection()
            .AddSingleton<IDocumentosQueryContext>(documentos)
            .AddSingleton(evaluador)
            .BuildServiceProvider();
        return new(servicios, NullLogger<ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, Result>>.Instance);
    }

    [Fact]
    public async Task Una_peticion_sin_el_marcador_no_exige_la_persistencia_de_visitas()
    {
        // MediatR construye todos los behaviors en cada envío: una consulta en un contenedor que no registra ni
        // Documentos ni el evaluador (como las pruebas de integración que montan AddApplication a mano) tiene que pasar.
        var vacio = new ServiceCollection().BuildServiceProvider();
        var behavior = new ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<OrdenCualquiera, Result>(
            vacio, NullLogger<ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<OrdenCualquiera, Result>>.Instance);

        var respuesta = await behavior.Handle(new OrdenCualquiera(Guid.NewGuid()), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        respuesta.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public void El_behavior_se_construye_sin_la_persistencia_de_visitas()
    {
        // El contenedor de AddApplication sin IVisitaRepository: construir el behavior no puede exigirlo.
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        var proveedor = servicios.BuildServiceProvider();

        var accion = () => ActivatorUtilities.CreateInstance<ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<OrdenCualquiera, Result>>(proveedor);

        accion.Should().NotThrow();
    }

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
    public async Task Una_cancelacion_al_reevaluar_no_devuelve_error_de_una_orden_ya_guardada()
    {
        var (documentos, acreditacion) = Mundo();
        var evaluador = new EvaluadorQueAnota { Cancelar = true };

        var respuesta = await Behavior<OrdenDeAcreditacion>(documentos, evaluador)
            .Handle(new OrdenDeAcreditacion(acreditacion.Id), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        respuesta.EsExitoso.Should().BeTrue("la orden ya guardó: cancelar la reevaluación del sello no la convierte en fallida");
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
