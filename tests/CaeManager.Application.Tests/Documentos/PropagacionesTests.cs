using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.GestionarPropagacion;
using CaeManager.Application.Documentos.Queries.ObtenerPropagacionesPendientes;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using MediatR;

namespace CaeManager.Application.Tests.Documentos;

public class PropagacionesTests
{
    private sealed class Disponibilidad(bool activa) : IDisponibilidadComunicacionesPropagacion
    {
        public bool Activa => activa;
    }

    public sealed class Repositorio : IPropagacionDocumentoRepository, IPropagacionesDocumentoQueryContext
    {
        public List<PropagacionDocumento> Filas { get; } = [];
        public IQueryable<PropagacionDocumento> Propagaciones => new TestAsyncQueryable<PropagacionDocumento>(Filas.AsQueryable());
        public Task<PropagacionDocumento?> ObtenerAsync(Guid documentoId, Guid canalId, CancellationToken ct) =>
            Task.FromResult(Filas.SingleOrDefault(p => p.DocumentoId == documentoId && p.CanalGestionDocumentalId == canalId));
        public void Agregar(PropagacionDocumento propagacion) => Filas.Add(propagacion);
    }

    private sealed class Mediador(Func<object, object?> resolver) : IMediator
    {
        public List<object> Enviados { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult((TResponse)resolver(request)!);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    [Fact]
    public async Task Sin_Comunicaciones_no_consulta_buzones_ni_envia()
    {
        var mediator = new Mediador(_ => throw new InvalidOperationException("No debe consultar nada"));
        var handler = new ComprobarBuzonAvisoPropagacionQueryHandler(new Disponibilidad(false), mediator);
        (await handler.Handle(new(Guid.NewGuid()), CancellationToken.None)).Should().BeNull();
        mediator.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_buzon_habilitado_de_esta_Empresa_no_hay_envio()
    {
        var otroClienteEmpresarial = Guid.NewGuid();
        var conexiones = new List<ConexionIntegracionListaDto>
        {
            new(Guid.NewGuid(), "otro@example.test", "Otro", otroClienteEmpresarial, "Otro", EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)
        };
        var mediator = new Mediador(_ => conexiones);
        var handler = new ComprobarBuzonAvisoPropagacionQueryHandler(new Disponibilidad(true), mediator);
        (await handler.Handle(new(Guid.NewGuid()), CancellationToken.None)).Should().BeNull();
        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<ObtenerConexionesIntegracionQuery>()
            .Which.SoloPropiasYGenerales.Should().BeTrue();
    }

    [Fact]
    public async Task Prefiere_el_buzon_dedicado_visible_al_buzon_general()
    {
        var clienteEmpresarialId = Guid.NewGuid();
        var dedicadoId = Guid.NewGuid();
        var conexiones = new List<ConexionIntegracionListaDto>
        {
            new(Guid.NewGuid(), "general@example.test", "General", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null),
            new(dedicadoId, "dedicado@example.test", "Dedicado", clienteEmpresarialId, "Empresa", EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)
        };
        var handler = new ComprobarBuzonAvisoPropagacionQueryHandler(new Disponibilidad(true), new Mediador(_ => conexiones));
        (await handler.Handle(new(clienteEmpresarialId), CancellationToken.None)).Should().Be(dedicadoId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Solo_registra_hecho_si_el_envio_por_Comunicaciones_ha_tenido_exito(bool buzonConectado, bool envioCorrecto)
    {
        var canal = CanalGestionDocumental.PorEmail(Guid.NewGuid(), "Avisos", "centro@example.test", null);
        var destino = new PropagacionPendienteDto(Guid.NewGuid(), canal.Id, canal.CentroId, Guid.NewGuid(),
            "RNT <septiembre>", "Centro Norte", TipoCanalGestion.Email, null, false, null);
        var buzonId = Guid.NewGuid();
        var conversacionId = Guid.NewGuid();
        var mediator = new Mediador(r => r switch
        {
            ObtenerPropagacionesPendientesQuery => new List<PropagacionPendienteDto> { destino },
            ComprobarBuzonAvisoPropagacionQuery => buzonConectado ? (Guid?)buzonId : null,
            EnviarMensajeNuevoCommand => envioCorrecto ? Result.Exito(conversacionId) : Result.Fallo<Guid>(Error.Crear("Correo.Fallo", "No se pudo enviar.")),
            _ => throw new NotSupportedException()
        });
        var centros = new CentrosQueryContextFalso();
        centros.ListaCanalesGestionDocumental.Add(canal);
        var repositorio = new Repositorio();
        var unidad = new UnitOfWorkFalso();
        var handler = new GestionarPropagacionCommandHandler(mediator, centros, repositorio, new CurrentUserServiceFalso(Guid.NewGuid()), unidad);
        var resultado = await handler.Handle(new(destino.DocumentoId, destino.CanalId, EnviarAviso: true), CancellationToken.None);
        resultado.EsExitoso.Should().Be(envioCorrecto);
        repositorio.Filas.Count.Should().Be(envioCorrecto ? 1 : 0);
        unidad.VecesGuardado.Should().Be(envioCorrecto ? 1 : 0);
        var envios = mediator.Enviados.OfType<EnviarMensajeNuevoCommand>().ToList();
        envios.Count.Should().Be(buzonConectado ? 1 : 0);
        if (buzonConectado)
        {
            envios[0].ConexionIntegracionId.Should().Be(buzonId);
            envios[0].ClienteId.Should().Be(destino.ClienteEmpresarialId);
            envios[0].CuerpoHtml.Should().Contain("&lt;septiembre&gt;");
            envios[0].Destinatarios.Should().Equal("centro@example.test");
        }
        if (envioCorrecto) repositorio.Filas.Single().ConversacionId.Should().Be(conversacionId);
    }

    [Fact]
    public async Task Un_destino_que_ya_no_es_visible_no_envia_ni_escribe()
    {
        var mediator = new Mediador(_ => new List<PropagacionPendienteDto>());
        var repositorio = new Repositorio();
        var unidad = new UnitOfWorkFalso();
        var handler = new GestionarPropagacionCommandHandler(mediator, new CentrosQueryContextFalso(), repositorio,
            new CurrentUserServiceFalso(Guid.NewGuid()), unidad);
        var resultado = await handler.Handle(new(Guid.NewGuid(), Guid.NewGuid(), true), CancellationToken.None);
        resultado.EsFallido.Should().BeTrue();
        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<ObtenerPropagacionesPendientesQuery>();
        unidad.VecesGuardado.Should().Be(0);
    }
}
