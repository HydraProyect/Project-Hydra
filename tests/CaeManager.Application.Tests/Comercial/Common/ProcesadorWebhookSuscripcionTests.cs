using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Common;

public class ProcesadorWebhookSuscripcionTests
{
    private readonly UnitOfWorkFalso unitOfWork = new();
    private readonly TransaccionDeComandoFalsa transaccion = new();

    private static EventoWebhookProveedorDto Evento(string idSuscripcion, EstadoSuscripcionProveedor estadoEnElEvento) =>
        new("customer.subscription.updated", new SuscripcionProveedorDto(idSuscripcion, "cus_1", estadoEnElEvento));

    private static PaymentProviderFalso StripeReporta(EstadoSuscripcionProveedor estado) =>
        new(Result.Exito(new SuscripcionProveedorDto("sub_1", "cus_1", estado)));

    private static (Tenant Tenant, TenantsQueryContextFalso Db) TenantVinculado(EstadoComercialTenant estado)
    {
        var tenant = new Tenant("Ibertec");
        tenant.VincularSuscripcionStripe("cus_1", "sub_1", estado);
        var db = new TenantsQueryContextFalso();
        db.ListaTenants.Add(tenant);
        return (tenant, db);
    }

    private ProcesadorWebhookSuscripcion Procesador(
        TenantsQueryContextFalso db, PaymentProviderFalso proveedor, BloqueoSuscripcionStripeFalso? bloqueo = null) =>
        new(db, proveedor, transaccion, bloqueo ??= new BloqueoSuscripcionStripeFalso(), bloqueo, unitOfWork);

    /// <summary>
    /// Stripe no garantiza el orden de entrega: un <c>past_due</c> atrasado que llega después
    /// de que la Subscription volviera a <c>active</c> no debe devolver al Tenant beneficiario a
    /// «aviso de impago». Se aplica lo que Stripe reporta ahora, no la copia del evento; y como
    /// eso coincide con lo guardado, no se escribe nada.
    /// </summary>
    [Fact]
    public async Task Un_evento_atrasado_no_regresa_el_estado_ni_escribe()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Activa);

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.EnPeriodoGracia), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.SinCambios);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        proveedor.UltimoIdConsultado.Should().Be("sub_1");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Un <c>updated</c> atrasado con <c>active</c> que llega cuando la Subscription ya está
    /// cancelada en Stripe no reactiva al Tenant beneficiario suspendido.
    /// </summary>
    [Fact]
    public async Task Un_evento_activo_atrasado_no_reactiva_una_suscripcion_cancelada()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Suspendida);
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Cancelada);

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.SinCambios);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Suspendida);
    }

    [Fact]
    public async Task Aplica_el_impago_cuando_Stripe_lo_confirma_al_consultar()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Impagada);

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.Aplicado);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
        unitOfWork.VecesGuardado.Should().Be(1);
        transaccion.Confirmadas.Should().Be(1);
    }

    /// <summary>
    /// La carrera del alta a medias: el webhook llega mientras el alta manual tiene el
    /// candado y todavía no ha confirmado el vínculo. El webhook debe buscar el Tenant
    /// DESPUÉS de obtener el candado, cuando el alta ya confirmó; si lo buscara antes,
    /// respondería «sin Tenant» con 200 y el cambio se perdería.
    /// </summary>
    [Fact]
    public async Task Busca_el_tenant_despues_de_obtener_el_candado_de_la_suscripcion()
    {
        var tenant = new Tenant("Ibertec");
        var db = new TenantsQueryContextFalso();
        db.ListaTenants.Add(tenant);
        var bloqueo = new BloqueoSuscripcionStripeFalso
        {
            AlBloquearSuscripciones = () => tenant.VincularSuscripcionStripe("cus_1", "sub_1", EstadoComercialTenant.Activa),
        };
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Impagada);

        var resultado = await Procesador(db, proveedor, bloqueo)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Impagada), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.Aplicado);
        bloqueo.Suscripciones.Should().Equal("sub_1");
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
    }

    [Fact]
    public async Task Si_Stripe_no_responde_no_escribe_nada_y_pide_reintento_con_el_codigo()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso(
            Result.Fallo<SuscripcionProveedorDto>(Error.Crear("PaymentProvider.ErrorApi", "caído")));

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Cancelada), CancellationToken.None);

        resultado.Should().Be(new ProcesamientoWebhookSuscripcion(
            ResultadoWebhookSuscripcion.ProveedorNoDisponible, "PaymentProvider.ErrorApi"));
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_suscripcion_sin_tenant_vinculado_no_consulta_a_Stripe()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso();

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_otra", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.SinTenantVinculado);
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
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Cancelada);

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Cancelada), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.TenantNoSuscribible);
        plataforma.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        proveedor.VecesConsultado.Should().Be(0);
    }

    [Fact]
    public async Task Un_evento_sin_suscripcion_no_toca_nada()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = new PaymentProviderFalso();
        var bloqueo = new BloqueoSuscripcionStripeFalso();

        var resultado = await Procesador(db, proveedor, bloqueo)
            .ProcesarAsync(new EventoWebhookProveedorDto("invoice.paid", null), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.SinSuscripcion);
        proveedor.VecesConsultado.Should().Be(0);
        bloqueo.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_estado_actual_no_reconocido_no_muta()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.AvisoImpago);
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Desconocida);

        var resultado = await Procesador(db, proveedor)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.EstadoNoReconocido);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.AvisoImpago);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Intenta el candado, descarta el contexto y solo entonces lee el Tenant.</summary>
    [Fact]
    public async Task Descarta_el_contexto_entre_el_candado_y_la_lectura()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var bloqueo = new BloqueoSuscripcionStripeFalso();

        await Procesador(db, StripeReporta(EstadoSuscripcionProveedor.Activa), bloqueo)
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), CancellationToken.None);

        bloqueo.Eventos.Should().Equal("intento", "descarte");
    }

    /// <summary>
    /// Con el candado ocupado (otra entrega o un alta a medias) no se espera ocupando una
    /// conexión: se pide reintento sin leer, consultar ni escribir.
    /// </summary>
    [Fact]
    public async Task Con_el_candado_ocupado_pide_reintento_sin_consultar_ni_escribir()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var proveedor = StripeReporta(EstadoSuscripcionProveedor.Impagada);

        var resultado = await Procesador(db, proveedor, new BloqueoSuscripcionStripeFalso { Ocupada = true })
            .ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Impagada), CancellationToken.None);

        resultado.Resultado.Should().Be(ResultadoWebhookSuscripcion.SuscripcionOcupada);
        proveedor.VecesConsultado.Should().Be(0);
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Stripe que no contesta: se agota el límite y se pide reintento con su código, sin escribir.</summary>
    [Fact]
    public async Task Si_Stripe_se_cuelga_agota_el_limite_y_pide_reintento_sin_escribir()
    {
        var (tenant, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var procesador = new ProcesadorWebhookSuscripcion(
            db, new StripeQueNoContesta(), transaccion, new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso(), unitOfWork)
        {
            LimiteConsulta = TimeSpan.FromMilliseconds(50),
        };

        var resultado = await procesador.ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Cancelada), CancellationToken.None);

        resultado.Should().Be(new ProcesamientoWebhookSuscripcion(
            ResultadoWebhookSuscripcion.ProveedorNoDisponible, "PaymentProvider.TiempoAgotado"));
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>La cancelación de quien llama (petición abortada) no se disfraza de tiempo agotado: se propaga.</summary>
    [Fact]
    public async Task La_cancelacion_de_quien_llama_se_propaga()
    {
        var (_, db) = TenantVinculado(EstadoComercialTenant.Activa);
        var procesador = new ProcesadorWebhookSuscripcion(
            db, new StripeQueNoContesta(), transaccion, new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso(), unitOfWork);
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var procesar = () => procesador.ProcesarAsync(Evento("sub_1", EstadoSuscripcionProveedor.Activa), cancelacion.Token);

        await procesar.Should().ThrowAsync<OperationCanceledException>();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private sealed class StripeQueNoContesta : IPaymentProvider
    {
        public string Codigo => "cuelga";

        public Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma) =>
            throw new NotSupportedException();

        public async Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("inalcanzable");
        }
    }
}
