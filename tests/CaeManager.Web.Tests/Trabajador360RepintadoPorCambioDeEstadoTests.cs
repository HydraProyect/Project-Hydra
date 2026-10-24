using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using Bunit;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Frescura del estado materializado en pantalla (diseño § 13.11): Trabajador 360 escucha
/// <see cref="CacheEstadoCircuito.CambioRecibido"/> y, con un aviso del Tenant cuya versión leyó su circuito, relee el estado
/// de la cabecera y repinta. Un aviso de otro Tenant no llega al circuito y no relee nada.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static readonly Guid TenantLeido = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

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

    [Fact]
    public async Task Un_aviso_del_Tenant_leido_repinta_la_cabecera_y_uno_de_otro_Tenant_no()
    {
        var id = Guid.NewGuid();
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
        var lecturasDelEstado = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();

        // El documento que faltaba llega (en otro circuito): el estado guardado ya no tiene nada pendiente.
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Vigente);

        // Un aviso de otro Tenant no es de este circuito: ni relee ni repinta.
        notificador.Publicar(new AvisoCambioEstado(OtroTenant, 2, [id]));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(lecturasDelEstado, "el circuito no leyó ese Tenant");
        cut.Find(".cabecera-identidad").TextContent.Should().Contain("Falta: Aptitud médica");

        // El aviso del Tenant leído relee solo el estado de la cabecera, con el Trabajador de la página, y repinta.
        notificador.Publicar(new AvisoCambioEstado(TenantLeido, 2, [id]));
        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad").TextContent.Should().NotContain("Falta:"));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Skip(lecturasDelEstado).Should().ContainSingle()
            .Which.TrabajadorId.Should().Be(id);
        cut.Find(".trabajador360-indicadores .badge").TextContent.Trim().Should().NotBe("Pendiente");

        // Cerrada la página, un aviso ya no relee nada.
        var antesDeCerrar = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        await DisposeComponentsAsync();
        notificador.Publicar(new AvisoCambioEstado(TenantLeido, 3, [id]));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(antesDeCerrar);
    }
}
