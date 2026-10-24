using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Frescura del estado materializado en pantalla (diseño § 13.11): Trabajador 360 escucha
/// <see cref="CacheEstadoCircuito.CambioRecibido"/> y, con un aviso del Tenant cuya versión leyó su circuito, relee el estado
/// de la cabecera y repinta. Un aviso de otro Tenant no llega al circuito y no relee nada; una ráfaga relee una vez; al cerrar
/// la página la suscripción se da de baja.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static readonly Guid TenantLeido = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    /// <summary>
    /// Lo que espera una aserción negativa («no releyó»): varias veces la amortiguación, para que una relectura que fuera a
    /// ocurrir haya tenido tiempo de salir.
    /// </summary>
    private static readonly TimeSpan EsperaNegativa = RepintadoPorCambioDeEstado.Amortiguacion * 4;

    private sealed class UsuarioDelCircuito : ICurrentUserService
    {
        private static readonly Guid Usuario = Guid.NewGuid();
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(TenantLeido);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantDelCircuito : ITenantActual
    {
        public Guid? TenantId => TenantLeido;
    }

    private sealed class VersionDelTenantLeido : ILectorVersionEstadoTenant
    {
        public Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VersionEstadoLeida?>(new VersionEstadoLeida(TenantLeido, 1));
    }

    /// <summary>Cuántos oyentes tiene <see cref="CacheEstadoCircuito.CambioRecibido"/> (el campo del evento).</summary>
    private static int OyentesDe(CacheEstadoCircuito cache) =>
        (typeof(CacheEstadoCircuito)
                .GetField(nameof(CacheEstadoCircuito.CambioRecibido),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(cache) as Delegate)?
            .GetInvocationList().Length ?? 0;

    private async Task<(CacheEstadoCircuito Cache, NotificadorEstadoTenant Notificador, MediatorFalso Mediador, IRenderedComponent<CaeManager.Web.Features.Trabajadores.Pages.TrabajadorDetalle> Cut)>
        RenderizarConCacheAsync(Guid id)
    {
        var notificador = new NotificadorEstadoTenant();
        var cache = new CacheEstadoCircuito(new UsuarioDelCircuito(), new TenantDelCircuito(), new VersionDelTenantLeido(), notificador);
        Services.AddScoped(_ => cache);
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Faltante, ["Aptitud médica"]);

        // Lo que hacen los lectores al leer: la caché lee la versión del Tenant y se suscribe a él.
        await cache.ObtenerAsync(new ConsultaEstadoEnCache<Guid>(
            "prueba", f => [f], _ => Task.FromResult<IReadOnlyList<Guid>>([]), (_, _) => Task.FromResult<IReadOnlyList<Guid>>([])),
            CancellationToken.None);

        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad").TextContent.Should().Contain("Falta: Aptitud médica"));
        return (cache, notificador, mediador, cut);
    }

    [Fact]
    public async Task Un_aviso_del_Tenant_leido_repinta_la_cabecera_y_uno_de_otro_Tenant_no()
    {
        var id = Guid.NewGuid();
        var (cache, notificador, mediador, cut) = await RenderizarConCacheAsync(id);
        cut.Find(".trabajador360-indicadores .badge").TextContent.Trim().Should().Be("Pendiente");
        var lecturasDelEstado = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        OyentesDe(cache).Should().Be(1, "la página escucha mientras está abierta");

        // El documento que faltaba llega (en otro circuito): el estado guardado ya no tiene nada pendiente.
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Vigente);

        // Un aviso de otro Tenant no es de este circuito: ni llega a la caché, ni relee, ni repinta (esperando de verdad).
        notificador.Publicar(new AvisoCambioEstado(OtroTenant, 2, [id]));
        await Task.Delay(EsperaNegativa);
        cache.AvisosRecibidos.Should().Be(0, "el circuito no leyó ese Tenant");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(lecturasDelEstado, "el circuito no leyó ese Tenant");
        cut.Find(".cabecera-identidad").TextContent.Should().Contain("Falta: Aptitud médica");

        // El aviso del Tenant leído relee solo el estado de la cabecera, con el Trabajador de la página, y repinta.
        notificador.Publicar(new AvisoCambioEstado(TenantLeido, 2, [id]));
        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad").TextContent.Should().NotContain("Falta:"));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Skip(lecturasDelEstado).Should().ContainSingle()
            .Which.TrabajadorId.Should().Be(id);
        cut.Find(".trabajador360-indicadores .badge").TextContent.Trim()
            .Should().Be(EstadoDocumentoUi.TextoDocumental(EstadoDocumento.Vigente));

        // Cerrada la página, la suscripción se da de baja y un aviso ya no relee nada (esperando de verdad).
        var antesDeCerrar = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        await DisposeComponentsAsync();
        OyentesDe(cache).Should().Be(0, "la página se dio de baja al cerrarse");
        notificador.Publicar(new AvisoCambioEstado(TenantLeido, 3, [id]));
        await Task.Delay(EsperaNegativa);
        cache.AvisosRecibidos.Should().Be(2, "la caché del circuito sigue recibiendo: es la página la que ya no escucha");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(antesDeCerrar);
    }

    [Fact]
    public async Task Una_rafaga_de_avisos_relee_la_cabecera_una_sola_vez()
    {
        var id = Guid.NewGuid();
        var (_, notificador, mediador, cut) = await RenderizarConCacheAsync(id);
        var lecturasDelEstado = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Vigente);

        for (var version = 2; version <= 6; version++)
            notificador.Publicar(new AvisoCambioEstado(TenantLeido, version, [id]));

        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad").TextContent.Should().NotContain("Falta:"));
        await Task.Delay(EsperaNegativa);
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Skip(lecturasDelEstado).Should()
            .ContainSingle("cinco avisos dentro de la amortiguación son un solo repintado");
    }
}
