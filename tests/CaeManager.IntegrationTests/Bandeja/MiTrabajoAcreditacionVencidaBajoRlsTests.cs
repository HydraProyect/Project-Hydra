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
/// Decisión P12 (2026-09-23): una acreditación de Plataforma CAE aceptada con
/// la vigencia vencida en la plataforma aparece en Mi trabajo agregada como
/// bloqueo de prioridad alta, en la cola de su Tenant propietario y asociada a
/// esa acreditación. Antes, la consulta de acreditaciones que alimenta la cola
/// solo traía las pendientes de subir y las rechazadas, y la vencida no llegaba.
///
/// <para>
/// Igual que <see cref="MiTrabajoRechazadaAplicableBajoRlsTests"/>, la lectura va
/// <b>autenticando como <c>cae_app_runtime</c></b>, con los interceptores de
/// sellado y de sesión RLS de producción, y con datos en dos Tenants: el Tenant
/// de origen del Operador CAE externo y el Tenant beneficiario delegante tienen
/// cada uno su propia acreditación vencida, y cada una tiene que salir en la
/// cola de su Tenant propietario y en ninguna otra.
/// </para>
/// </summary>
public class MiTrabajoAcreditacionVencidaBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantDelegante;
    private Guid _centroDelegante;
    private Guid _acreditacionVencidaDelegante;
    private Guid _documentoVencidoDelegante;
    private Guid _documentoVigenteDelegante;
    private Guid _documentoRechazadoDelegante;
    private Guid _acreditacionVencidaOrigen;

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

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        // Delegante: una aceptada vencida en la plataforma, una aceptada aún
        // vigente allí y una rechazada, todas de documentos vigentes en TALVEG.
        var delegante = await SembrarTenantAsync(_tenantDelegante, "Delegante",
            (EstadoAcreditacion.Aceptada, hoy.AddDays(-2)),
            (EstadoAcreditacion.Aceptada, hoy.AddDays(30)),
            (EstadoAcreditacion.Rechazada, null));
        _centroDelegante = delegante.CentroId;
        (_documentoVencidoDelegante, _acreditacionVencidaDelegante) = delegante.Acreditaciones[0];
        _documentoVigenteDelegante = delegante.Acreditaciones[1].DocumentoId;
        _documentoRechazadoDelegante = delegante.Acreditaciones[2].DocumentoId;

        // Origen: su propia aceptada vencida, que no puede aparecer en la cola
        // del delegante, ni la del delegante en la suya.
        var origen = await SembrarTenantAsync(_tenantOrigen, "Origen", (EstadoAcreditacion.Aceptada, hoy.AddDays(-10)));
        _acreditacionVencidaOrigen = origen.Acreditaciones[0].AcreditacionId;

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
    /// Control del instrumento: la conexión de lectura está de verdad bajo RLS.
    /// Sin él, «no aparece en el otro Tenant» podría pasar sobre una conexión
    /// que se salta RLS y no demostraría nada.
    /// </summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroDelegante)).Should().Be(0,
            "fuera del ámbito del delegante, app.tenant_id es el Tenant de origen y RLS oculta el Centro");

        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
            (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroDelegante)).Should().Be(1);
    }

    [Fact]
    public async Task La_vencida_en_plataforma_es_bloqueo_de_su_Tenant_propietario_por_delante_de_la_rechazada()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

        var delegante = resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantDelegante).Subject;
        var cola = Cola(delegante);

        var vencida = cola.Should().ContainSingle(i => i.DocumentoId == _documentoVencidoDelegante).Subject;
        vencida.Tipo.Should().Be(TipoItemBandeja.PlataformaVencida);
        vencida.Id.Should().Be($"plataforma-vencida-{_acreditacionVencidaDelegante}",
            "el ítem va asociado a la acreditación afectada, no solo al documento");
        vencida.CentroId.Should().Be(_centroDelegante);
        ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(vencida).Should().BeTrue();

        cola.Should().NotContain(i => i.DocumentoId == _documentoVigenteDelegante,
            "una aceptada todavía vigente en la plataforma no es trabajo");

        // Control de observación: la rechazada del mismo Centro llega, y la
        // vencida va delante por prioridad (Vencida → alta, D-6).
        var rechazada = cola.Should().ContainSingle(i => i.DocumentoId == _documentoRechazadoDelegante).Subject;
        rechazada.Tipo.Should().Be(TipoItemBandeja.PlataformaRechazada);
        cola.IndexOf(vencida).Should().BeLessThan(cola.IndexOf(rechazada));

        cola.Should().HaveCount(2, "la siembra del delegante no deja más trabajo que la vencida y la rechazada");
        delegante.Resumen.Bloqueos.Should().Be(2, "la vencida siempre, y la rechazada porque es aplicable a su Centro");
        delegante.Resumen.Actuaciones.Should().Be(0);
    }

    [Fact]
    public async Task Cada_vencida_sale_solo_en_la_cola_de_su_Tenant_propietario()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

        var colaOrigen = Cola(resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantOrigen).Subject);
        var colaDelegante = Cola(resultado.Tenants.Should().ContainSingle(t => t.TenantId == _tenantDelegante).Subject);

        colaOrigen.Should().ContainSingle(i => i.Id == $"plataforma-vencida-{_acreditacionVencidaOrigen}")
            .Which.Tipo.Should().Be(TipoItemBandeja.PlataformaVencida);
        colaOrigen.Should().NotContain(i => i.Id == $"plataforma-vencida-{_acreditacionVencidaDelegante}");
        colaDelegante.Should().NotContain(i => i.Id == $"plataforma-vencida-{_acreditacionVencidaOrigen}");
    }

    private static List<ItemBandejaDto> Cola(MiTrabajoTenantDto tenant) =>
        tenant.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Concat(tenant.BloqueoActuacion.SinGrupo).ToList();

    private sealed record Siembra(Guid CentroId, List<(Guid DocumentoId, Guid AcreditacionId)> Acreditaciones);

    /// <summary>
    /// En el Tenant dado: un Centro de Trabajo con canal de plataforma y un
    /// Trabajador asignado, y por cada acreditación pedida un Documento vigente
    /// en TALVEG de un tipo requerido distinto. Una aceptada lleva la fecha de
    /// vigencia en plataforma que se pida; una rechazada, ninguna.
    /// </summary>
    private async Task<Siembra> SembrarTenantAsync(
        Guid tenantId, string etiqueta, params (EstadoAcreditacion Estado, DateOnly? VenceEnPlataforma)[] acreditaciones)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var cliente = Empresa.CrearComoCliente($"Cervezas Duff {etiqueta}", "B12345674", false, null, null);
        var empresa = new Empresa($"Montajes Springfield {etiqueta}", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var tipos = acreditaciones
            .Select((_, i) => new TipoDocumento($"Formación {etiqueta} {i}", null, aplicaVencimientoAutomatico: false, 1,
                AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si))
            .ToList();
        _propietario.TiposDocumento.AddRange(tipos);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], $"Plataforma {etiqueta}");
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, $"Fábrica {etiqueta}");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy));
        var documentos = tipos.Select(t => Documento.DeTrabajador(trabajador.Id, t.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)))).ToList();
        _propietario.Documentos.AddRange(documentos);
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, $"Acceso {etiqueta}", proveedor.Id, null, null, null);
        _propietario.CanalesGestionDocumental.Add(canal);
        await _propietario.SaveChangesAsync();

        var sembradas = new List<(Guid, Guid)>();
        foreach (var (documento, (estado, vence)) in documentos.Zip(acreditaciones))
        {
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            if (estado == EstadoAcreditacion.Rechazada)
                acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
            else
                acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(vence!.Value));
            _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            sembradas.Add((documento.Id, acreditacion.Id));
        }
        await _propietario.SaveChangesAsync();

        return new Siembra(centro.Id, sembradas);
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
