using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.DividirAdjuntoPdf;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Application.Comunicaciones.Queries.PrepararDivisionAdjunto;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.HerramientasPdf;
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

    /// <summary>
    /// Con acceso total el alcance responde sí: lo que frena el Id ajeno es la lectura bajo RLS. Si llegara a leerse, el
    /// archivo sembrado se abriría y el alta crearía Documentos; por eso se mide también que no quede ninguno.
    /// </summary>
    [Fact]
    public async Task Un_adjunto_de_otro_Tenant_no_se_divide_ni_se_prepara_por_Id()
    {
        var empresaGestionada = await SembrarEmpresaGestionadaAsync(_tenantSesion, "Montajes Springfield Gestionada");
        var tipo = await SembrarTipoDeEmpresaAsync();

        await using (var runtime = CrearRuntime())
        {
            var almacen = AlmacenConElAdjunto();
            var mediador = new DivisionConCatalogoReal(runtime, Alcance(runtime), _tenantDeLaPeticion, almacen);

            (await Dividir(runtime, almacen, mediador).Handle(new DividirAdjuntoPdfCommand(_adjuntoAjeno, empresaGestionada, DosTramos(empresaGestionada, tipo)), CancellationToken.None))
                .Error.Codigo.Should().Be("Adjunto.NoEncontrado");
            (await Preparar(runtime, almacen, mediador).Handle(new PrepararDivisionAdjuntoQuery(_adjuntoAjeno, empresaGestionada), CancellationToken.None))
                .Error.Codigo.Should().Be("Adjunto.NoEncontrado");

            mediador.Peticiones.Should().BeEmpty("el adjunto ajeno no llega a abrirse");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoAjeno)).EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        (await comprobacion.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    /// <summary>Control positivo de la prueba anterior: la misma orden sobre lo propio llega hasta el final bajo RLS.</summary>
    [Fact]
    public async Task Dividir_un_adjunto_propio_crea_los_Documentos_en_el_Tenant_de_la_sesion_y_lo_deja_dividido()
    {
        var empresaGestionada = await SembrarEmpresaGestionadaAsync(_tenantSesion, "Montajes Springfield Gestionada");
        var tipo = await SembrarTipoDeEmpresaAsync();

        await using (var runtime = CrearRuntime())
        {
            var almacen = AlmacenConElAdjunto();
            var mediador = new DivisionConCatalogoReal(runtime, Alcance(runtime), _tenantDeLaPeticion, almacen);

            var preparacion = await Preparar(runtime, almacen, mediador).Handle(new PrepararDivisionAdjuntoQuery(_adjuntoPropio, empresaGestionada), CancellationToken.None);
            preparacion.Valor.Catalogo.EmpresaGestionada.Id.Should().Be(empresaGestionada);

            var resultado = await Dividir(runtime, almacen, mediador)
                .Handle(new DividirAdjuntoPdfCommand(_adjuntoPropio, empresaGestionada, DosTramos(empresaGestionada, tipo)), CancellationToken.None);

            resultado.Valor.Should().HaveCount(2).And.OnlyContain(t => t.Estado == EstadoTramoCreado.Creado);
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var documentos = await comprobacion.Documentos.IgnoreQueryFilters().Where(d => d.EmpresaId == empresaGestionada).ToListAsync();
        documentos.Should().HaveCount(2).And.OnlyContain(d => d.TenantId == _tenantSesion);
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio)).EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoAjeno)).EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        (await comprobacion.EventosConversacion.IgnoreQueryFilters()
                .CountAsync(e => e.ConversacionId == _conversacionPropia && e.Tipo == TipoEventoConversacion.AdjuntoDividido && e.ReferenciaId == _adjuntoPropio))
            .Should().Be(1);
    }

    /// <summary>
    /// El acceso total deja pasar cualquier Id por el alcance: una Empresa propia de otro Tenant la frena el catálogo, que
    /// no la ve bajo RLS. Por eso el adjunto se reserva y vuelve a pendiente.
    /// </summary>
    [Fact]
    public async Task Una_Empresa_propia_de_otro_Tenant_no_se_gestiona_desde_un_adjunto_propio()
    {
        var tenantAjeno = await _propietario.Empresas.IgnoreQueryFilters().Where(e => e.Id == _empresaAjena).Select(e => e.TenantId).SingleAsync();
        var empresaDeOtroTenant = await SembrarEmpresaGestionadaAsync(tenantAjeno, "Montajes Shelbyville Gestionada");
        var tipo = await SembrarTipoDeEmpresaAsync();

        await using (var runtime = CrearRuntime())
        {
            var almacen = AlmacenConElAdjunto();
            var mediador = new DivisionConCatalogoReal(runtime, Alcance(runtime), _tenantDeLaPeticion, almacen);

            (await Preparar(runtime, almacen, mediador).Handle(new PrepararDivisionAdjuntoQuery(_adjuntoPropio, empresaDeOtroTenant), CancellationToken.None))
                .Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
            (await Dividir(runtime, almacen, mediador)
                    .Handle(new DividirAdjuntoPdfCommand(_adjuntoPropio, empresaDeOtroTenant, DosTramos(empresaDeOtroTenant, tipo)), CancellationToken.None))
                .Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio)).EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        (await comprobacion.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Dos sesiones sobre el mismo adjunto: la perdedora ya lo ha leído cuando la otra termina, y es el token de Postgres
    /// el que la frena antes de crear nada. Solo quedan los Documentos de la ganadora.
    /// </summary>
    [Fact]
    public async Task Dos_divisiones_a_la_vez_solo_crean_los_Documentos_de_una()
    {
        var empresaGestionada = await SembrarEmpresaGestionadaAsync(_tenantSesion, "Montajes Springfield Gestionada");
        var tipo = await SembrarTipoDeEmpresaAsync();
        var orden = new DividirAdjuntoPdfCommand(_adjuntoPropio, empresaGestionada, DosTramos(empresaGestionada, tipo));

        await using (var perdedora = CrearRuntime())
        {
            var almacen = new AlmacenConOtraSesion(AlmacenConElAdjunto(), async almacenReal =>
            {
                await using var ganadora = CrearRuntime();
                var mediadorGanadora = new DivisionConCatalogoReal(ganadora, Alcance(ganadora), _tenantDeLaPeticion, almacenReal);
                var ganada = await Dividir(ganadora, almacenReal, mediadorGanadora).Handle(orden, CancellationToken.None);
                ganada.EsExitoso.Should().BeTrue();
            });
            var mediador = new DivisionConCatalogoReal(perdedora, Alcance(perdedora), _tenantDeLaPeticion, almacen);

            var perdida = await Dividir(perdedora, almacen, mediador).Handle(orden, CancellationToken.None);

            perdida.Error.Codigo.Should().Be(ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto);
            mediador.Peticiones.Should().BeEmpty();
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Documentos.IgnoreQueryFilters().CountAsync(d => d.EmpresaId == empresaGestionada)).Should().Be(2);
        (await comprobacion.AdjuntosMensaje.IgnoreQueryFilters().SingleAsync(a => a.Id == _adjuntoPropio)).EstadoReparto.Should().Be(EstadoRepartoAdjunto.Dividido);
    }

    private static IReadOnlyList<TramoACrear> DosTramos(Guid empresaGestionada, Guid tipoDocumentoId) =>
    [
        new(1, 1, ClaseTitular.EmpresaGestionada, empresaGestionada, tipoDocumentoId, new DateOnly(2020, 1, 1), null, false),
        new(2, 2, ClaseTitular.EmpresaGestionada, empresaGestionada, tipoDocumentoId, new DateOnly(2020, 1, 1), null, false)
    ];

    private DividirAdjuntoPdfCommandHandler Dividir(CaeManagerDbContext runtime, IFileStorageService almacen, IMediator mediador) => new(
        new DisponibilidadHerramientasPdf(), runtime, new AdjuntoMensajeRepository(runtime), new EventoConversacionRepository(runtime), Alcance(runtime),
        new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion), new RegistroAccesoDatoSensibleFalso(), almacen, mediador, runtime, runtime,
        NullLogger<DividirAdjuntoPdfCommandHandler>.Instance);

    private PrepararDivisionAdjuntoQueryHandler Preparar(CaeManagerDbContext runtime, IFileStorageService almacen, IMediator mediador) => new(
        new DisponibilidadHerramientasPdf(), runtime, Alcance(runtime), new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: _tenantSesion),
        PoliticaNotaInternaPruebas.Con(Roles.Administrador), new RegistroAccesoDatoSensibleFalso(), almacen, mediador);

    /// <summary>Una Empresa que el Tenant gestiona, no una contraparte: <c>EsPropia</c> es lo que exige el catálogo.</summary>
    private async Task<Guid> SembrarEmpresaGestionadaAsync(Guid tenantId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = new Empresa(razonSocial);
        _propietario.Empresas.Add(empresa);
        await _propietario.SaveChangesAsync();
        return empresa.Id;
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

    /// <summary>
    /// <see cref="CrearDocumentosDesdeDivisionCommandHandler"/> y el catálogo reales sobre el contexto bajo RLS. El divisor
    /// y el alta se sustituyen: el primero porque aquí el PDF no importa, y el segundo guarda el Documento de verdad con
    /// el sellado de Tenant del contexto, que es lo que se mide.
    /// </summary>
    private sealed class DivisionConCatalogoReal(
        CaeManagerDbContext contexto, AlcanceDatosService alcance, ITenantActual tenantActual, IFileStorageService almacen) : IMediator
    {
        public List<object> Peticiones { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request);

            switch (request)
            {
                case CrearDocumentosDesdeDivisionCommand dividir:
                    return (TResponse)(object)await new CrearDocumentosDesdeDivisionCommandHandler(
                            new DisponibilidadHerramientasPdf(), tenantActual, this, new DivisorSinPdf(), almacen, alcance,
                            NullLogger<CrearDocumentosDesdeDivisionCommandHandler>.Instance)
                        .Handle(dividir, cancellationToken);
                case ObtenerCatalogoDivisionPdfQuery catalogo:
                    return (TResponse)(object)await Catalogo(catalogo, cancellationToken);
                case PrepararDivisionPdfQuery preparar:
                    var leido = await Catalogo(new ObtenerCatalogoDivisionPdfQuery(preparar.EmpresaGestionadaId), cancellationToken);
                    return (TResponse)(object)(leido.EsExitoso
                        ? Result.Exito(new PreparacionDivisionPdf(2, false, true, [], leido.Valor))
                        : Result.Fallo<PreparacionDivisionPdf>(leido.Error));
                case CrearDocumentoCommand crear:
                    var documento = Documento.DeEmpresa(crear.EmpresaId!.Value, crear.TipoDocumentoId, crear.FechaEmision,
                        VigenciaDocumento.VenceEl(new DateOnly(2030, 1, 1)), crear.ArchivoUrl);
                    contexto.Documentos.Add(documento);
                    await contexto.SaveChangesAsync(cancellationToken);
                    return (TResponse)(object)Result.Exito(documento.Id);
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        private Task<Result<CatalogoDivisionPdf>> Catalogo(ObtenerCatalogoDivisionPdfQuery consulta, CancellationToken cancellationToken) =>
            new ObtenerCatalogoDivisionPdfQueryHandler(contexto, contexto, contexto, contexto, alcance).Handle(consulta, cancellationToken);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DivisorSinPdf : IDivisorPdf, IPdfParaDividir
    {
        public int NumeroPaginas => 2;
        public bool EstaFirmado => false;
        public Task<Result<IPdfParaDividir>> AbrirAsync(byte[] contenidoPdf, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito<IPdfParaDividir>(this));
        public Task<Result<byte[]>> ExtraerPaginasAsync(int paginaInicial, int paginaFinal, long tamanoMaximoBytes, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito("%PDF-tramo"u8.ToArray()));
        public void Dispose() { }
    }

    /// <summary>La primera apertura deja pasar a otra sesión: es el momento entre leer el adjunto y reservarlo.</summary>
    private sealed class AlmacenConOtraSesion(IFileStorageService almacen, Func<IFileStorageService, Task> otraSesion) : IFileStorageService
    {
        private bool _yaEntro;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            almacen.GuardarAsync(contenido, nombreArchivoOriginal, cancellationToken);

        public async Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
        {
            if (!_yaEntro)
            {
                _yaEntro = true;
                await otraSesion(almacen);
            }

            return await almacen.AbrirAsync(identificador, cancellationToken);
        }

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            almacen.EliminarAsync(identificador, cancellationToken);
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
