using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// El reparto de adjuntos <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión RLS y el
/// <see cref="AlcanceDatosService"/> real (mismo arnés que <c>NotaInternaDeEmpresaBajoRlsTests</c>). Dos Tenants
/// propietarios con una conversación y un adjunto cada uno; la sesión es de un Administrador del primero, con acceso
/// total a su Tenant: lo que se mide es la frontera entre Tenants, no la cartera.
/// </summary>
public class RepartoAdjuntosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _conversacionPropia;
    private Guid _conversacionAjena;
    private Guid _adjuntoPropio;
    private Guid _adjuntoAjeno;
    private Guid _empresaPropia;
    private Guid _empresaAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;

        (_conversacionPropia, _adjuntoPropio, _empresaPropia) = await SembrarAsync(tenantSesion.Id, "Montajes Springfield");
        (_conversacionAjena, _adjuntoAjeno, _empresaAjena) = await SembrarAsync(tenantAjeno.Id, "Montajes Shelbyville");

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y el adjunto ajeno existe.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_el_adjunto_ajeno_existe()
    {
        await using var runtime = CrearRuntime();

        (await runtime.AdjuntosMensaje.IgnoreQueryFilters().CountAsync(a => a.Id == _adjuntoAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el adjunto del Tenant ajeno");
        (await runtime.AdjuntosMensaje.IgnoreQueryFilters().CountAsync(a => a.Id == _adjuntoPropio)).Should().Be(1,
            "control positivo: el adjunto del Tenant de la sesión sí se ve");

        await using var propietario = CrearPropietarioLimpio();
        (await propietario.AdjuntosMensaje.IgnoreQueryFilters().CountAsync(a => a.Id == _adjuntoAjeno)).Should().Be(1);
    }

    [Fact]
    public async Task Un_adjunto_de_otro_Tenant_no_se_lee_ni_se_escribe_por_Id()
    {
        await using (var runtime = CrearRuntime())
        {
            (await Fijar(runtime).Handle(new FijarDestinoAdjuntoCommand(_adjuntoAjeno, null, _empresaPropia, null), CancellationToken.None))
                .Error.Codigo.Should().Be("Adjunto.NoEncontrado");
            (await Resolver(runtime).Handle(new ResolverAdjuntoCommand(_adjuntoAjeno, ResolucionAdjunto.Descartar), CancellationToken.None))
                .Error.Codigo.Should().Be("Adjunto.NoEncontrado");
            (await new ObtenerRepartoAdjuntosQueryHandler(runtime, Alcance(runtime), PoliticaNotaInternaPruebas.Con(Roles.Administrador))
                .Handle(new ObtenerRepartoAdjuntosQuery(_conversacionAjena), CancellationToken.None)).Should().BeEmpty();
            (await new AsignarAdjuntosLoteCommandHandler(runtime, Alcance(runtime), new MediadorSinEnvios(), NullLogger<AsignarAdjuntosLoteCommandHandler>.Instance)
                .Handle(new AsignarAdjuntosLoteCommand(_conversacionAjena, [new AdjuntoAAsignar(_adjuntoAjeno, new DateOnly(2026, 10, 1), null, false)]), CancellationToken.None))
                .Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var ajeno = await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoAjeno);
        ajeno.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        ajeno.DestinoEmpresaId.Should().BeNull();
    }

    /// <summary>Con acceso total el alcance responde sí: lo que frena el Id ajeno es la lectura bajo RLS.</summary>
    [Fact]
    public async Task Una_Empresa_de_otro_Tenant_no_es_un_destino_valido_para_un_adjunto_propio()
    {
        await using (var runtime = CrearRuntime())
        {
            var resultado = await Fijar(runtime).Handle(new FijarDestinoAdjuntoCommand(_adjuntoPropio, null, _empresaAjena, null), CancellationToken.None);

            resultado.Error.Codigo.Should().Be("Adjunto.DestinoNoValido");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().Where(a => a.Id == _adjuntoPropio).Select(a => a.DestinoEmpresaId).SingleAsync())
            .Should().BeNull();
    }

    /// <summary>Control positivo: las mismas órdenes sobre lo propio persisten bajo RLS, evento incluido.</summary>
    [Fact]
    public async Task Fijar_destino_y_rechazar_un_adjunto_propio_persisten_y_la_consulta_los_devuelve()
    {
        await using (var runtime = CrearRuntime())
            (await Fijar(runtime).Handle(new FijarDestinoAdjuntoCommand(_adjuntoPropio, null, _empresaPropia, null), CancellationToken.None))
                .EsExitoso.Should().BeTrue();

        await using (var runtime = CrearRuntime())
            (await Resolver(runtime).Handle(new ResolverAdjuntoCommand(_adjuntoPropio, ResolucionAdjunto.Rechazar, "Caducado"), CancellationToken.None))
                .EsExitoso.Should().BeTrue();

        await using var lectura = CrearRuntime();
        var fila = (await new ObtenerRepartoAdjuntosQueryHandler(lectura, Alcance(lectura), PoliticaNotaInternaPruebas.Con(Roles.Administrador))
            .Handle(new ObtenerRepartoAdjuntosQuery(_conversacionPropia), CancellationToken.None)).Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoRepartoAdjunto.Rechazado);
        fila.DestinoEmpresaId.Should().Be(_empresaPropia);
        fila.MotivoRechazo.Should().Be("Caducado");
        (await lectura.EventosConversacion.CountAsync(e => e.ConversacionId == _conversacionPropia && e.Tipo == TipoEventoConversacion.AdjuntoRechazado))
            .Should().Be(1);
    }

    /// <summary>El evento lo publica el alta tras su commit: aquí se mide que el marcado persista bajo RLS, y solo en lo propio.</summary>
    [Fact]
    public async Task El_marcado_de_asignado_persiste_en_lo_propio_y_no_alcanza_lo_ajeno()
    {
        var documentoId = Guid.NewGuid();
        await using (var runtime = CrearRuntime())
        {
            var handler = new MarcarAdjuntoAsignadoHandler(new AdjuntoMensajeRepository(runtime),
                new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion), runtime,
                NullLogger<MarcarAdjuntoAsignadoHandler>.Instance);

            await handler.Handle(new DocumentoActualizadoEvent(_conversacionPropia, documentoId, _adjuntoPropio), CancellationToken.None);
            await handler.Handle(new DocumentoActualizadoEvent(_conversacionAjena, documentoId, _adjuntoAjeno), CancellationToken.None);
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var propio = await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio);
        propio.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Asignado);
        propio.DocumentoId.Should().Be(documentoId);
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().Where(a => a.Id == _adjuntoAjeno).Select(a => a.EstadoReparto).SingleAsync())
            .Should().Be(EstadoRepartoAdjunto.Pendiente);
    }

    [Fact]
    public async Task Dos_transiciones_del_mismo_adjunto_no_se_pisan_y_un_contexto_rancio_se_refresca()
    {
        await using var primero = CrearRuntime();
        await using var segundo = CrearRuntime();
        var enElPrimero = await new AdjuntoMensajeRepository(primero).ObtenerPorIdAsync(_adjuntoPropio);
        var enElSegundo = await new AdjuntoMensajeRepository(segundo).ObtenerPorIdAsync(_adjuntoPropio);

        enElPrimero!.FijarDestino(null, _empresaPropia, null);
        await primero.SaveChangesAsync();

        enElSegundo!.Descartar(Guid.NewGuid(), DateTime.UtcNow);
        await segundo.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();

        var refrescado = await new AdjuntoMensajeRepository(segundo).ObtenerPorIdAsync(_adjuntoPropio);
        refrescado!.DestinoEmpresaId.Should().Be(_empresaPropia);
        refrescado.EstaPendiente.Should().BeTrue();
    }

    [Fact]
    public async Task Olvidar_cambios_saca_el_adjunto_del_contexto()
    {
        await using var runtime = CrearRuntime();
        var repositorio = new AdjuntoMensajeRepository(runtime);
        var adjunto = await repositorio.ObtenerPorIdAsync(_adjuntoPropio);
        adjunto!.Descartar(Guid.NewGuid(), DateTime.UtcNow);

        repositorio.OlvidarCambios(adjunto);

        runtime.Entry(adjunto).State.Should().Be(EntityState.Detached);
        runtime.ChangeTracker.HasChanges().Should().BeFalse();
    }

    private FijarDestinoAdjuntoCommandHandler Fijar(CaeManagerDbContext runtime) => new(
        runtime, runtime, runtime, runtime, new AdjuntoMensajeRepository(runtime), Alcance(runtime), runtime);

    private ResolverAdjuntoCommandHandler Resolver(CaeManagerDbContext runtime) => new(
        runtime, new AdjuntoMensajeRepository(runtime), new EventoConversacionRepository(runtime), Alcance(runtime),
        new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion), runtime);

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto) => new(
        contexto, new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion),
        _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid Conversacion, Guid Adjunto, Guid Empresa)> SembrarAsync(Guid tenantId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = Empresa.CrearComoCliente(razonSocial, "B12345674", false, null, null);
        var conversacion = new Conversacion("Documentación", empresaId: empresa.Id);
        var mensaje = new Mensaje(conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@example.test", "Cuerpo", DateTime.UtcNow);
        var adjunto = mensaje.AgregarAdjunto("aptitud.pdf", "application/pdf", 2, "adjuntos/aptitud.pdf");
        _propietario.Empresas.Add(empresa);
        _propietario.Conversaciones.Add(conversacion);
        _propietario.Mensajes.Add(mensaje);
        await _propietario.SaveChangesAsync();
        return (conversacion.Id, adjunto.Id, empresa.Id);
    }

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime()
    {
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private sealed class MediadorSinEnvios : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
