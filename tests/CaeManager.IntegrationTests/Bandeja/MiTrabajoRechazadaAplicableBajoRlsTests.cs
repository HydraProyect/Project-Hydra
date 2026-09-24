using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// P2.7, residual de #809 en /mi-trabajo (D-7 del piloto Outbound): la
/// severidad «Bloqueo» de Mi trabajo agregada cuenta una acreditación
/// Rechazada solo si el cálculo de estado de su Centro de Trabajo la cuenta
/// como causa bloqueante — el mismo criterio que la cola agrupada de /bandeja
/// e Inicio (<c>BandejaRechazadaBloqueaAccesoTests</c>).
///
/// <para>
/// Mi trabajo es multi-Tenant: el Gestor CAE del Operador CAE externo ve la
/// cola del Tenant beneficiario delegante, y el cálculo de estado del Centro
/// tiene que correr en el contexto del Tenant propietario de esa cola. Por
/// eso la lectura va <b>autenticando como <c>cae_app_runtime</c></b>, con los
/// interceptores de sellado y de sesión RLS de producción: si el cálculo
/// corriera fuera del <c>AmbitoTenantExplicito</c> del delegante (en el Tenant
/// de origen de la sesión), RLS no le dejaría ver el Centro, la Rechazada
/// aplicable saldría sin marcar y el recuento de bloqueos daría 0. La siembra
/// va como propietario de la base, igual que la de los demás tests del
/// fan-out, porque no es lo que se mide.
/// </para>
/// </summary>
public class MiTrabajoRechazadaAplicableBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantDelegante;
    private Guid _centroId;
    private Guid _documentoAplicable;
    private Guid _documentoNoAplicable;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantOrigen = new Tenant("Operador CAE externo de prueba");
        var tenantDelegante = new Tenant("Tenant beneficiario de prueba");
        _propietario.Tenants.AddRange(tenantOrigen, tenantDelegante);
        _tenantOrigen = tenantOrigen.Id;
        _tenantDelegante = tenantDelegante.Id;
        var delegacion = new DelegacionTenant(_tenantOrigen, _tenantDelegante);
        _propietario.DelegacionesTenant.Add(delegacion);
        _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));
        await _propietario.SaveChangesAsync();

        // Mi trabajo también recorre el Tenant de origen del Operador CAE
        // (ObtenerClientesAutorizadosQuery lo devuelve primero), y cada vuelta
        // lee su ParametroSistema: sin él, la consulta falla antes de llegar
        // al delegante.
        using (AmbitoTenantExplicito.Establecer(_tenantOrigen))
        {
            _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _propietario.SaveChangesAsync();
        }

        await SembrarDeleganteAsync();

        // Lectura como en producción: identidad de tráfico restringida, el
        // ámbito explícito manda y, fuera de él, el Tenant de origen de la sesión.
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(_runtime);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(usuario);
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: la conexión de lectura está de verdad bajo
    /// RLS. Sin filtro global de EF, en el Tenant de origen no se ve el Centro
    /// de Trabajo del delegante; en el ámbito del delegante, sí. Sin este
    /// control, el test principal podría pasar sobre una conexión que se salta
    /// RLS y no demostraría nada sobre el contexto de Tenant del cálculo.
    /// </summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroId)).Should().Be(0,
            "fuera del ámbito del delegante, app.tenant_id es el Tenant de origen y RLS oculta el Centro");

        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
            (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroId)).Should().Be(1);
    }

    [Fact]
    public async Task Solo_la_Rechazada_aplicable_a_su_Centro_cuenta_como_bloqueo()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

        var delegante = resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantDelegante).Subject;
        var cola = delegante.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Concat(delegante.BloqueoActuacion.SinGrupo).ToList();

        // Control de observación: las dos Rechazadas llegan a la cola. Sin él,
        // «no cuenta como bloqueo» pasaría también si la siembra no llegara.
        var aplicable = cola.Should().ContainSingle(i => i.DocumentoId == _documentoAplicable).Subject;
        var noAplicable = cola.Should().ContainSingle(i => i.DocumentoId == _documentoNoAplicable).Subject;
        aplicable.Tipo.Should().Be(TipoItemBandeja.PlataformaRechazada);
        noAplicable.Tipo.Should().Be(TipoItemBandeja.PlataformaRechazada);

        aplicable.RechazoBloqueaCentro.Should().BeTrue(
            "el cálculo de estado del Centro, corrido en el Tenant propietario de la cola, la cuenta como causa bloqueante (D-7)");
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(aplicable).Should().BeTrue();

        noAplicable.RechazoBloqueaCentro.Should().BeFalse();
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(noAplicable).Should().BeFalse(
            "un tipo no requerido y sin fila del Centro no aplica: su rechazo es trabajo, no un bloqueo");

        cola.Should().HaveCount(2, "la siembra no deja más trabajo que las dos Rechazadas");
        delegante.Resumen.Bloqueos.Should().Be(1);
        delegante.Resumen.Actuaciones.Should().Be(1);
    }

    /// <summary>
    /// En el delegante: un Centro de Trabajo con un Trabajador asignado y dos
    /// Documentos vigentes en TALVEG, cada uno con su acreditación rechazada en
    /// el canal de plataforma del Centro. Uno es de un tipo requerido (aplica
    /// al Centro); el otro, de un tipo no requerido y sin fila del Centro (no
    /// aplica).
    /// </summary>
    private async Task SembrarDeleganteAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantDelegante);

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var requerido = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var noRequerido = new TipoDocumento("Curso voluntario", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
        _propietario.TiposDocumento.AddRange(requerido, noRequerido);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy));
        var documentoAplicable = Documento.DeTrabajador(trabajador.Id, requerido.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var documentoNoAplicable = Documento.DeTrabajador(trabajador.Id, noRequerido.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.Documentos.AddRange(documentoAplicable, documentoNoAplicable);
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        _propietario.CanalesGestionDocumental.Add(canal);
        await _propietario.SaveChangesAsync();

        foreach (var documento in new[] { documentoAplicable, documentoNoAplicable })
        {
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
            _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        }
        await _propietario.SaveChangesAsync();

        _centroId = centro.Id;
        _documentoAplicable = documentoAplicable.Id;
        _documentoNoAplicable = documentoNoAplicable.Id;
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
}
