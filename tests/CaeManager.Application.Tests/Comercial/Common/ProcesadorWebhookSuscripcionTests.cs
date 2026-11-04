using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Common;

public class ProcesadorWebhookSuscripcionTests
{
    private static EventoWebhookProveedorDto Evento(string idSuscripcion, EstadoSuscripcionProveedor estadoEnElEvento) =>
        new("customer.subscription.updated", new SuscripcionProveedorDto(idSuscripcion, "cus_1", estadoEnElEvento));

    private static (Tenant Tenant, TenantsQueryContextFalso Db) TenantVinculado(EstadoComercialTenant estado)
    {
        var tenant = new Tenant("Ibertec");
        tenant.VincularSuscripcionStripe("cus_1", "sub_1", estado);
        var db = new TenantsQueryContextFalso();
        db.ListaTenants.Add(tenant);
        return (tenant, db);
    }

    /// <summary>
    /// Stripe no garantiza el orden de entrega: un <c>past_due</c> atrasado que llega después
    /// de que la Subscription volviera a <c>active</c> no debe devolver al Tenant beneficiario a
    /// «aviso de impago». Se aplica lo que Stripe reporta ahora, no la copia del evento.
    /// </summary>
    [Fact]
    public async Task Un_evento_atrasado_no_regresa_el_estado_aplica_el_estado_actual_de_Stripe()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_1", "cus_1", EstadoSuscripcionProveedor.Activa)));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.EnPeriodoGracia), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.Aplicado);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        proveedor.UltimoIdConsultado.Should().Be("sub_1");
    }

    [Fact]
    public async Task Aplica_el_impago_cuando_Stripe_lo_confirma_al_consultar()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_1", "cus_1", EstadoSuscripcionProveedor.Impagada)));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.Aplicado);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Si_Stripe_no_responde_no_escribe_nada_y_pide_reintento()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso(
            Result.Fallo<SuscripcionProveedorDto>(Error.Crear("PaymentProvider.ErrorApi", "caído")));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Cancelada), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.ProveedorNoDisponible);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_suscripcion_sin_tenant_vinculado_no_consulta_a_Stripe()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_otra", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.SinTenantVinculado);
        proveedor.VecesConsultado.Should().Be(0);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_tenant_de_plataforma_se_ignora_sin_consultar_ni_mutar()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        plataforma.VincularSuscripcionStripe("cus_1", "sub_1", EstadoComercialTenant.Activa);
        var db = new TenantsQueryContextFalso();
        db.ListaTenants.Add(plataforma);
        var proveedor = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_1", "cus_1", EstadoSuscripcionProveedor.Cancelada)));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Cancelada), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.TenantNoSuscribible);
        plataforma.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        proveedor.VecesConsultado.Should().Be(0);
    }

    [Fact]
    public async Task Un_evento_sin_suscripcion_no_toca_nada()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(new EventoWebhookProveedorDto("invoice.paid", null), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.SinSuscripcion);
        proveedor.VecesConsultado.Should().Be(0);
    }

    [Fact]
    public async Task Un_estado_actual_no_reconocido_no_muta()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.AvisoImpago);
        var proveedor = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_1", "cus_1", EstadoSuscripcionProveedor.Desconocida)));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ProcesadorWebhookSuscripcion(db, proveedor, unitOfWork)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Should().Be(ResultadoWebhookSuscripcion.EstadoNoReconocido);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.AvisoImpago);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
