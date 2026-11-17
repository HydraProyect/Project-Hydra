using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.TieneAutorizacionLecturaConcedida;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.AutorizacionesLectura;

/// <summary>
/// <see cref="TieneAutorizacionLecturaConcedidaQuery"/>: el Tenant activo, como Tenant de origen, tiene una autorización de lectura
/// documental concedida solo si alguna suya está sin revocar y dentro de su vigencia hoy (decisión D6 de la demo de Inbound).
/// El contexto de lectura falso devuelve las autorizaciones de todos los Tenants, para que el filtro que se prueba sea el de la
/// Query y no el global de EF ni la RLS; la lectura real bajo <c>cae_app_runtime</c> la prueba la integración, en CI.
/// </summary>
public class TieneAutorizacionLecturaConcedidaQueryTests
{
    // 2026-11-17 09:00 UTC: en Madrid ya es el 17 de noviembre.
    private static readonly DateTime Ahora = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Hoy = new(2026, 11, 17);
    private static readonly Guid TenantActivo = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    private readonly AutorizacionLecturaRepositorioFalso _repositorio = new();

    private Task<bool> LeerAsync(Guid? tenantActivo = null, bool sinTenant = false) =>
        new TieneAutorizacionLecturaConcedidaQueryHandler(
                new AutorizacionesLecturaQueryContextFalso(_repositorio),
                new TenantActualFijo(sinTenant ? null : tenantActivo ?? TenantActivo),
                new RelojFijo(Ahora))
            .Handle(new TieneAutorizacionLecturaConcedidaQuery(), CancellationToken.None);

    private AutorizacionLecturaDocumental Conceder(Guid tenantDeOrigen, DateOnly? desde = null, DateOnly? hasta = null)
    {
        var autorizacion = AutorizacionLecturaDocumental.Conceder(
            Guid.NewGuid(), Guid.NewGuid(), false, desde ?? Hoy.AddDays(-30), hasta, Guid.NewGuid(), Ahora);
        // El TenantId lo sella el interceptor al guardar; el contexto falso no lo hace.
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(autorizacion, tenantDeOrigen);
        _repositorio.Agregar(autorizacion);
        return autorizacion;
    }

    [Fact]
    public async Task Un_Tenant_sin_ninguna_autorizacion_no_la_tiene()
    {
        (await LeerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Una_autorizacion_concedida_y_vigente_la_da()
    {
        Conceder(TenantActivo);

        (await LeerAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Una_autorizacion_revocada_no_cuenta()
    {
        Conceder(TenantActivo).Revocar(Guid.NewGuid(), Ahora);

        (await LeerAsync()).Should().BeFalse("la revocación es definitiva: una autorización revocada no se reactiva");
    }

    [Fact]
    public async Task Basta_una_vigente_aunque_las_otras_esten_revocadas()
    {
        Conceder(TenantActivo).Revocar(Guid.NewGuid(), Ahora);
        Conceder(TenantActivo);

        (await LeerAsync()).Should().BeTrue("volver a autorizar es conceder otra fila");
    }

    [Fact]
    public async Task Una_autorizacion_caducada_no_cuenta()
    {
        Conceder(TenantActivo, desde: Hoy.AddDays(-60), hasta: Hoy.AddDays(-1));

        (await LeerAsync()).Should().BeFalse("su vigencia terminó ayer");
    }

    [Fact]
    public async Task Una_autorizacion_que_todavia_no_ha_empezado_no_cuenta()
    {
        Conceder(TenantActivo, desde: Hoy.AddDays(1));

        (await LeerAsync()).Should().BeFalse("D6 pide concedida y vigente: una programada aún no lo es");
    }

    [Fact]
    public async Task El_primer_y_el_ultimo_dia_de_la_vigencia_cuentan()
    {
        Conceder(TenantActivo, desde: Hoy, hasta: Hoy);

        (await LeerAsync()).Should().BeTrue("mismo criterio que AutorizacionLecturaDocumental.VigenteEn");
    }

    [Fact]
    public async Task La_autorizacion_de_otro_Tenant_no_cuenta_para_el_Tenant_activo()
    {
        Conceder(OtroTenant);

        (await LeerAsync()).Should().BeFalse("cuenta el Tenant de origen, no cualquier Tenant con una autorización");
        (await LeerAsync(tenantActivo: OtroTenant)).Should().BeTrue("control: el otro Tenant sí la tiene");
    }

    [Fact]
    public async Task Sin_Tenant_activo_falla_cerrado()
    {
        Conceder(TenantActivo);

        (await LeerAsync(sinTenant: true)).Should().BeFalse();
    }

    [Fact]
    public async Task Coincide_con_el_criterio_en_memoria_de_la_entidad()
    {
        // La Query traduce la expresión EstaVigenteEn; la entidad la repite en memoria (VigenteEn). Las dos tienen que dar lo mismo.
        var casos = new (DateOnly Desde, DateOnly? Hasta, bool Revocar)[]
        {
            (Hoy.AddDays(-5), null, false), (Hoy.AddDays(-5), Hoy, false), (Hoy, null, false), (Hoy.AddDays(1), null, false),
            (Hoy.AddDays(-5), Hoy.AddDays(-1), false), (Hoy.AddDays(-5), null, true),
        };

        foreach (var (desde, hasta, revocar) in casos)
        {
            var repositorio = new AutorizacionLecturaRepositorioFalso();
            var autorizacion = AutorizacionLecturaDocumental.Conceder(
                Guid.NewGuid(), Guid.NewGuid(), false, desde, hasta, Guid.NewGuid(), Ahora);
            typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(autorizacion, TenantActivo);
            if (revocar)
                autorizacion.Revocar(Guid.NewGuid(), Ahora);
            repositorio.Agregar(autorizacion);

            var respuesta = await new TieneAutorizacionLecturaConcedidaQueryHandler(
                    new AutorizacionesLecturaQueryContextFalso(repositorio), new TenantActualFijo(TenantActivo), new RelojFijo(Ahora))
                .Handle(new TieneAutorizacionLecturaConcedidaQuery(), CancellationToken.None);

            respuesta.Should().Be(autorizacion.VigenteEn(Hoy), $"desde {desde}, hasta {hasta}, revocada {revocar}");
        }
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }
}
