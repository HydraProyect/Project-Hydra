using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Commands.PedirPrioridadValidacion;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Fase G: <c>PedirPrioridadValidacionCommand</c> resuelve la conexión de
/// correo en el servidor (nunca confía en un Id que llegara del cliente,
/// misma regla que otros Commands con Ids de otras entidades) y solo deja
/// rastro en <see cref="SolicitudPrioridadDocumento"/> si el envío tuvo
/// éxito. El envío en sí ya está cubierto por
/// <c>EnviarMensajeNuevoCommandHandlerTests</c> — aquí se sustituye por un
/// <see cref="IMediator"/> mínimo que solo resuelve ese único Command, para
/// aislar la lógica propia de este handler (resolución de conexión,
/// registro de la solicitud, propagación de fallos).
/// </summary>
public class PedirPrioridadValidacionCommandTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _centroId;
    private Guid _centroAjenoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Pedir Prioridad Envío S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Envío S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con envío");
        var centroAjeno = new Centro(cliente.Id, empresa.Id, "Centro ajeno al envío");
        contexto.Centros.AddRange(centro, centroAjeno);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _centroAjenoId = centroAjeno.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_conexion_disponible_devuelve_fallo_y_no_registra_la_solicitud()
    {
        await using var contexto = CrearContexto();
        var handler = CrearHandler(contexto, new MediatorDeUnSoloComandoFalso(Result.Exito(Guid.NewGuid())));

        var resultado = await handler.Handle(
            new PedirPrioridadValidacionCommand(_centroId, ["prl@centro.com"], "Asunto", "<p>Cuerpo</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SolicitudPrioridad.SinBuzonConectado");
        (await contexto.SolicitudesPrioridadDocumento.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Con_conexion_disponible_envia_y_registra_la_solicitud()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("cae@tenant.com", "Buzón del tenant"));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var mediatorFalso = new MediatorDeUnSoloComandoFalso(Result.Exito(Guid.NewGuid()));
        var handler = CrearHandler(contexto2, mediatorFalso);

        var resultado = await handler.Handle(
            new PedirPrioridadValidacionCommand(_centroId, ["prl@centro.com"], "Asunto", "<p>Cuerpo</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediatorFalso.ComandoRecibido.Should().NotBeNull();
        mediatorFalso.ComandoRecibido!.Destinatarios.Should().Equal("prl@centro.com");
        mediatorFalso.ComandoRecibido.ClienteId.Should().Be(_clienteId);

        var solicitud = await contexto2.SolicitudesPrioridadDocumento.SingleAsync();
        solicitud.CentroId.Should().Be(_centroId);
        solicitud.EnviadaPorUsuarioId.Should().Be(_usuarioId);
    }

    [Fact]
    public async Task Si_el_envio_falla_no_registra_la_solicitud_y_propaga_el_error()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("cae@tenant.com", "Buzón del tenant"));
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto();
        var fallo = Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));
        var handler = CrearHandler(contexto2, new MediatorDeUnSoloComandoFalso(fallo));

        var resultado = await handler.Handle(
            new PedirPrioridadValidacionCommand(_centroId, ["prl@centro.com"], "Asunto", "<p>Cuerpo</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoDisponible");
        (await contexto2.SolicitudesPrioridadDocumento.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Fuera_de_la_cartera_visible_devuelve_fallo()
    {
        await using var contexto = CrearContexto();
        var handler = new PedirPrioridadValidacionCommandHandler(
            contexto, contexto, new SolicitudPrioridadDocumentoRepository(contexto),
            new AlcanceDatosServiceFalso(centroIds: [_centroAjenoId]),
            new CurrentUserServiceFalso(_usuarioId), new MediatorDeUnSoloComandoFalso(Result.Exito(Guid.NewGuid())), contexto);

        var resultado = await handler.Handle(
            new PedirPrioridadValidacionCommand(_centroId, ["prl@centro.com"], "Asunto", "<p>Cuerpo</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
    }

    private PedirPrioridadValidacionCommandHandler CrearHandler(CaeManagerDbContext contexto, IMediator mediator) =>
        new(contexto, contexto, new SolicitudPrioridadDocumentoRepository(contexto), new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(_usuarioId), mediator, contexto);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Resuelve únicamente <see cref="EnviarMensajeNuevoCommand"/> con el Result preconfigurado — todo lo demás no debería llegar a pedirse desde este handler.</summary>
    private class MediatorDeUnSoloComandoFalso(Result<Guid> resultadoEnvio) : IMediator
    {
        public EnviarMensajeNuevoCommand? ComandoRecibido { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is EnviarMensajeNuevoCommand comando)
            {
                ComandoRecibido = comando;
                return Task.FromResult((TResponse)(object)resultadoEnvio);
            }

            throw new NotSupportedException($"Este fake solo resuelve {nameof(EnviarMensajeNuevoCommand)}.");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification =>
            throw new NotSupportedException();
    }
}
