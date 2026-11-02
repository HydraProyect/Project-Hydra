using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
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
/// total a su Tenant: lo que se mide es la frontera entre Tenants, no la cartera. Las carreras del alta usan dos
/// contextos reales: el guardado del Documento y del adjunto es uno, y solo Postgres puede demostrar que el perdedor no
/// deja nada.
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
            (await Consulta(runtime).Handle(new ObtenerRepartoAdjuntosQuery(_conversacionAjena), CancellationToken.None)).Should().BeEmpty();
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
        var fila = (await Consulta(lectura).Handle(new ObtenerRepartoAdjuntosQuery(_conversacionPropia), CancellationToken.None)).Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoRepartoAdjunto.Rechazado);
        fila.DestinoEmpresaId.Should().Be(_empresaPropia);
        fila.MotivoRechazo.Should().Be("Caducado");
        (await lectura.EventosConversacion.CountAsync(e => e.ConversacionId == _conversacionPropia && e.Tipo == TipoEventoConversacion.AdjuntoRechazado))
            .Should().Be(1);
    }

    /// <summary>El Documento y la transición salen del mismo guardado: se ven juntos bajo RLS, y solo en lo propio.</summary>
    [Fact]
    public async Task El_alta_deja_Documento_y_adjunto_asignado_en_lo_propio_y_no_alcanza_lo_ajeno()
    {
        var tipoDocumentoId = await SembrarTipoDeEmpresaAsync();
        var almacen = AlmacenConElAdjunto();
        Guid documentoId;
        await using (var runtime = CrearRuntime())
        {
            var propio = await Alta(runtime, almacen, new AltaGuardadaDeVerdad(runtime)).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None);
            var ajeno = await Alta(runtime, almacen, new AltaGuardadaDeVerdad(runtime)).Handle(ComandoDeAlta(_adjuntoAjeno, tipoDocumentoId), CancellationToken.None);

            propio.EsExitoso.Should().BeTrue();
            documentoId = propio.Valor;
            ajeno.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var adjunto = await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio);
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Asignado);
        adjunto.DocumentoId.Should().Be(documentoId);
        adjunto.DestinoEmpresaId.Should().Be(_empresaPropia);
        adjunto.TipoDocumentoPropuestoId.Should().Be(tipoDocumentoId);
        (await comprobacion.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == documentoId)).Should().Be(1);
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().Where(a => a.Id == _adjuntoAjeno).Select(a => a.EstadoReparto).SingleAsync())
            .Should().Be(EstadoRepartoAdjunto.Pendiente);
    }

    [Fact]
    public async Task De_dos_altas_simultaneas_gana_una_y_la_perdedora_no_deja_Documento()
    {
        var tipoDocumentoId = await SembrarTipoDeEmpresaAsync();
        var almacen = AlmacenConElAdjunto();
        await using var perdedora = CrearRuntime();
        await using var ganadora = CrearRuntime();
        Result<Guid>? alta = null;
        var mediador = new AltaGuardadaDeVerdad(perdedora, async () =>
            alta = await Alta(ganadora, almacen, new AltaGuardadaDeVerdad(ganadora)).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None));

        var perdedor = await Alta(perdedora, almacen, mediador).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None);

        alta!.EsExitoso.Should().BeTrue();
        perdedor.Error.Codigo.Should().Be(ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto);
        perdedora.ChangeTracker.HasChanges().Should().BeFalse("nada del alta perdida puede salir en el siguiente guardado");
        almacen.Identificadores.Should().HaveCount(2, "el original y la copia de quien ganó");

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Documentos.IgnoreQueryFilters().Where(d => d.EmpresaId == _empresaPropia).Select(d => d.Id).ToListAsync())
            .Should().Equal(alta.Valor);
        var adjunto = await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio);
        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Asignado);
        adjunto.DocumentoId.Should().Be(alta.Valor);
    }

    /// <summary>
    /// El filtro global ya no ve el Documento, pero el adjunto guardado prueba el commit: su archivo se queda, porque la
    /// fila eliminada es restaurable.
    /// </summary>
    [Fact]
    public async Task Un_Documento_eliminado_tras_el_commit_da_el_alta_por_hecha_y_conserva_su_archivo()
    {
        var tipoDocumentoId = await SembrarTipoDeEmpresaAsync();
        var almacen = AlmacenConElAdjunto();
        await using var alta = CrearRuntime();
        await using var otraSesion = CrearRuntime();
        var mediador = new AltaGuardadaDeVerdad(alta, despuesDeGuardar: async documentoId =>
        {
            var documento = await otraSesion.Documentos.SingleAsync(d => d.Id == documentoId);
            documento.MarcarComoEliminado(Guid.NewGuid());
            await otraSesion.SaveChangesAsync();
            throw new InvalidOperationException("Un paso posterior al commit.");
        });

        var resultado = await Alta(alta, almacen, mediador).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        almacen.Identificadores.Should().HaveCount(2, "el original y el archivo del Documento eliminado");

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == resultado.Valor)).EstaEliminado.Should().BeTrue();
    }

    /// <summary>
    /// Bajo el rol de runtime: el filtro ignorado sigue limitado por RLS y por el Tenant explícito, y aun así ve la fila
    /// eliminada, que basta para no borrar el archivo. El adjunto no la respalda, así que tampoco es éxito.
    /// </summary>
    [Fact]
    public async Task Una_fila_eliminada_con_el_Id_reservado_que_el_adjunto_no_respalda_deja_el_estado_indeterminado()
    {
        var tipoDocumentoId = await SembrarTipoDeEmpresaAsync();
        var almacen = AlmacenConElAdjunto();
        await using var alta = CrearRuntime();
        var mediador = new AltaGuardadaDeVerdad(alta, enLugarDeGuardar: async documentoId =>
        {
            using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
            var eliminado = Documento.DeEmpresa(_empresaPropia, tipoDocumentoId, new DateOnly(2026, 10, 1),
                VigenciaDocumento.VenceEl(new DateOnly(2027, 10, 1)), "documentos/eliminado.pdf", idReservado: documentoId);
            eliminado.MarcarComoEliminado(Guid.NewGuid());
            _propietario.Documentos.Add(eliminado);
            await _propietario.SaveChangesAsync();
            return Result.Fallo<Guid>(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona lo cambió."));
        });

        var resultado = await Alta(alta, almacen, mediador).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoEstadoIndeterminado);
        almacen.Identificadores.Should().HaveCount(2, "en duda, la copia se conserva");
        alta.ChangeTracker.HasChanges().Should().BeFalse();
    }

    /// <summary>La propuesta cambia con el alta ya en marcha: el token tumba el Documento, y queda lo que fijó la otra sesión.</summary>
    [Fact]
    public async Task Si_otra_sesion_cambia_la_propuesta_durante_el_alta_no_queda_Documento()
    {
        var tipoDocumentoId = await SembrarTipoDeEmpresaAsync();
        var almacen = AlmacenConElAdjunto();
        await using var alta = CrearRuntime();
        await using var otraSesion = CrearRuntime();
        var mediador = new AltaGuardadaDeVerdad(alta, async () =>
            (await Fijar(otraSesion).Handle(new FijarDestinoAdjuntoCommand(_adjuntoPropio, null, _empresaPropia, null), CancellationToken.None))
                .EsExitoso.Should().BeTrue());

        var resultado = await Alta(alta, almacen, mediador).Handle(ComandoDeAlta(_adjuntoPropio, tipoDocumentoId), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto);
        almacen.Identificadores.Should().BeEquivalentTo([ArchivoDelAdjunto]);

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == _empresaPropia)).Should().Be(0);
        var adjunto = await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio);
        adjunto.EstaPendiente.Should().BeTrue();
        adjunto.DocumentoId.Should().BeNull();
        adjunto.TipoDocumentoPropuestoId.Should().BeNull();
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

    private const string ArchivoDelAdjunto = "adjuntos/aptitud.pdf";

    private static ActualizarDocumentoDesdeAdjuntoCommandTests.AlmacenamientoEnMemoriaFalso AlmacenConElAdjunto()
    {
        var almacen = new ActualizarDocumentoDesdeAdjuntoCommandTests.AlmacenamientoEnMemoriaFalso();
        almacen.Sembrar(ArchivoDelAdjunto, "%PDF-aptitud"u8.ToArray());
        return almacen;
    }

    private ActualizarDocumentoDesdeAdjuntoCommand ComandoDeAlta(Guid adjuntoId, Guid tipoDocumentoId) =>
        new(adjuntoId, tipoDocumentoId, null, _empresaPropia, new DateOnly(2026, 10, 1), null, null);

    private ActualizarDocumentoDesdeAdjuntoCommandHandler Alta(
        CaeManagerDbContext runtime, IFileStorageService almacen, AltaGuardadaDeVerdad mediador) => new(
        runtime, new AdjuntoMensajeRepository(runtime), Alcance(runtime), runtime, mediador, mediador, almacen,
        new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion), _tenantDeLaPeticion,
        NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

    private async Task<Guid> SembrarTipoDeEmpresaAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var tipo = new TipoDocumento("Seguro de responsabilidad civil", 12, true, 1, AmbitoAplicacion.Empresa);
        _propietario.TiposDocumento.Add(tipo);
        await _propietario.SaveChangesAsync();
        return tipo.Id;
    }

    private ObtenerRepartoAdjuntosQueryHandler Consulta(CaeManagerDbContext runtime) => new(
        runtime, Alcance(runtime), PoliticaNotaInternaPruebas.Con(Roles.Administrador), runtime, runtime);

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

    /// <summary>
    /// Lo que importa de <see cref="CrearDocumentoCommand"/> para la carrera: el Documento con el Id reservado, guardado
    /// en el contexto del alta bajo <see cref="ConcurrenciaBehavior{TRequest,TResponse}"/>, como en el pipeline real.
    /// <paramref name="antesDeGuardar"/> es la otra sesión colándose entre la lectura y el guardado;
    /// <paramref name="despuesDeGuardar"/>, lo que pasa tras el commit; <paramref name="enLugarDeGuardar"/>, un alta que
    /// no llega a guardar y responde lo que le digan.
    /// </summary>
    private sealed class AltaGuardadaDeVerdad(
        CaeManagerDbContext contexto, Func<Task>? antesDeGuardar = null, Func<Guid, Task>? despuesDeGuardar = null,
        Func<Guid, Task<Result<Guid>>>? enLugarDeGuardar = null) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var crear = (CrearDocumentoCommand)request;
            if (enLugarDeGuardar is not null)
                return (TResponse)(object)await enLugarDeGuardar(crear.DocumentoIdReservado!.Value);

            var documento = Documento.DeEmpresa(crear.EmpresaId!.Value, crear.TipoDocumentoId, crear.FechaEmision,
                VigenciaDocumento.VenceEl(new DateOnly(2027, 10, 1)), crear.ArchivoUrl, idReservado: crear.DocumentoIdReservado);
            contexto.Documentos.Add(documento);

            if (antesDeGuardar is not null)
                await antesDeGuardar();

            var resultado = await new ConcurrenciaBehavior<CrearDocumentoCommand, Result<Guid>>().Handle(crear, async _ =>
            {
                await contexto.SaveChangesAsync(cancellationToken);
                return Result.Exito(documento.Id);
            }, cancellationToken);

            if (despuesDeGuardar is not null)
                await despuesDeGuardar(documento.Id);

            return (TResponse)(object)resultado;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
