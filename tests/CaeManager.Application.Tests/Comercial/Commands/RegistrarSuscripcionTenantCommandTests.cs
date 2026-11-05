using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Application.Tests.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Commands;

public class RegistrarSuscripcionTenantCommandTests
{
    private static Tenant CrearTenantPlataforma()
    {
        var tenant = new Tenant("TALVEG");
        tenant.MarcarComoPlataforma();
        return tenant;
    }

    /// <summary>
    /// Sustituye a <c>Bloquea_si_quien_llama_no_opera_desde_el_tenant_de_plataforma</c>,
    /// cuya premisa A3 elimina: pertenecer al tenant de plataforma dejó de ser la
    /// autoridad. Lo que bloquea ahora es no tener la capacidad.
    /// </summary>
    [Fact]
    public async Task Sin_AdminPlataforma_no_se_vincula_ninguna_suscripcion()
    {
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(cliente);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.SinNada(),
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(
            new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Comercial.NoAutorizado");
    }

    /// <summary>
    /// La distinción que A1 fijó y que no debe diluirse: esta operación es
    /// <b>acotada</b>. Una concesión sobre otro cliente no vale, aunque exista y
    /// esté vigente.
    /// </summary>
    [Fact]
    public async Task Una_concesion_acotada_a_otro_cliente_no_autoriza_sobre_este()
    {
        var cliente = new Tenant("Ibertec");
        var otroCliente = new Tenant("Arcos");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(cliente);
        dbContext.ListaTenants.Add(otroCliente);

        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(otroCliente.Id);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), autorizacion,
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(
            new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Comercial.NoAutorizado");

        // Y se preguntó por el tenant del COMANDO, no por otro: si el handler
        // consultara el tenant de origen del invocante, el resultado seguiría
        // siendo "denegado" y un test que mirase solo el código pasaría igual.
        autorizacion.UltimoTenantConsultado.Should().Be(cliente.Id);
    }

    [Fact]
    public async Task Bloquea_si_el_tenant_destino_no_existe()
    {
        var plataforma = CrearTenantPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(Guid.NewGuid(), "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.TenantNoEncontrado");
    }

    [Fact]
    public async Task Bloquea_vincular_una_suscripcion_al_propio_tenant_de_plataforma()
    {
        var plataforma = CrearTenantPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(plataforma.Id, "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.TenantPlataforma");
    }

    [Fact]
    public async Task Propaga_el_fallo_de_Stripe_sin_vincular_nada()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        var paymentProvider = new PaymentProviderFalso(
            Result.Fallo<SuscripcionProveedorDto>(Error.Crear("PaymentProvider.ErrorApi", "no encontrada")));

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_inexistente"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.ErrorApi");
        cliente.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Vincula_la_suscripcion_con_el_estado_que_reporta_Stripe_no_uno_asumido()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        // Impagada desde el primer momento: alguien pegó el Id de una
        // suscripción que ya estaba en mal estado en Stripe — el alta manual
        // no debe darla de alta como si estuviera "Activa" a ciegas.
        var paymentProvider = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_123", "cus_999", EstadoSuscripcionProveedor.Impagada)));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork, new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.StripeCustomerId.Should().Be("cus_999");
        cliente.StripeSubscriptionId.Should().Be("sub_123");
        cliente.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Bloquea_si_Stripe_devuelve_un_estado_de_suscripcion_no_reconocido()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        var paymentProvider = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_123", "cus_999", EstadoSuscripcionProveedor.Desconocida)));

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.EstadoNoReconocido");
        cliente.StripeSubscriptionId.Should().BeNull();
    }

    /// <summary>
    /// El webhook resuelve el Tenant beneficiario por el Id de la Subscription: dos Tenants con
    /// la misma harían que un evento cayera en cualquiera de los dos.
    /// </summary>
    [Fact]
    public async Task Rechaza_una_suscripcion_ya_vinculada_a_otro_tenant_sin_consultar_a_Stripe()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var otro = new Tenant("Otro");
        otro.VincularSuscripcionStripe("cus_1", "sub_123", EstadoComercialTenant.Activa);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente, otro]);
        var paymentProvider = new PaymentProviderFalso();
        var transaccion = new TransaccionDeComandoFalsa();

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), transaccion, new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Comercial.SuscripcionYaVinculada");
        cliente.StripeSubscriptionId.Should().BeNull();
        paymentProvider.VecesConsultado.Should().Be(0);
        transaccion.Deshechas.Should().Be(1);
    }

    /// <summary>
    /// Dos altas concurrentes con la misma Subscription para Tenants distintos: la segunda
    /// espera el candado de la Subscription y, al obtenerlo, ve el vínculo que la primera ya
    /// confirmó. La comprobación tiene que ir DESPUÉS del candado.
    /// </summary>
    [Fact]
    public async Task Comprueba_el_duplicado_despues_de_obtener_el_candado_de_la_suscripcion()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var otro = new Tenant("Otro");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente, otro]);
        var bloqueo = new BloqueoSuscripcionStripeFalso
        {
            AlBloquearSuscripciones = () => otro.VincularSuscripcionStripe("cus_1", "sub_123", EstadoComercialTenant.Activa),
        };

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), bloqueo, bloqueo);

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Comercial.SuscripcionYaVinculada");
    }

    /// <summary>
    /// Re-vincular un Tenant a otra Subscription bloquea el Tenant primero y después la nueva
    /// y la sustituida: así un webhook de la vieja no escribe sobre el Tenant ya re-vinculado.
    /// </summary>
    [Fact]
    public async Task Al_revincular_bloquea_el_tenant_y_despues_la_suscripcion_nueva_y_la_sustituida()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        cliente.VincularSuscripcionStripe("cus_1", "sub_vieja", EstadoComercialTenant.Suspendida);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente]);
        var bloqueo = new BloqueoSuscripcionStripeFalso();

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), bloqueo, bloqueo);

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_nueva"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        bloqueo.Eventos.Should().Equal("tenant", "descarte", "suscripcion", "descarte");
        bloqueo.Tenants.Should().Equal(cliente.Id);
        bloqueo.Suscripciones.Should().BeEquivalentTo(["sub_nueva", "sub_vieja"]);
        cliente.StripeSubscriptionId.Should().Be("sub_nueva");
    }

    /// <summary>La concesión revocada mientras se esperaba el candado del Tenant no vincula.</summary>
    [Fact]
    public async Task No_vincula_si_la_concesion_se_revoca_mientras_espera_el_candado()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente]);
        var paymentProvider = new PaymentProviderFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var autorizacion = new AutorizacionRevocadaDuranteLaEsperaFalsa();

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, autorizacion, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork, new TransaccionDeComandoFalsa(), new BloqueoSuscripcionStripeFalso(), new DescarteCambiosPendientesFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_nueva"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.NoAutorizado");
        autorizacion.VecesConsultada.Should().Be(2);
        paymentProvider.UltimoIdConsultado.Should().BeNull();
        cliente.StripeSubscriptionId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Documenta el caso; quien fija que la sustituida se lee tras el candado del Tenant es el
    /// orden de <c>Eventos</c> en <see cref="Al_revincular_bloquea_el_tenant_y_despues_la_suscripcion_nueva_y_la_sustituida"/>
    /// y, contra PostgreSQL, la integración
    /// (<c>BloqueoSuscripcionStripeTests</c>): este doble devuelve la misma instancia que muta
    /// el gancho, así que no distingue leer antes o después del candado.
    /// </summary>
    [Fact]
    public async Task Bloquea_la_suscripcion_que_esta_vinculada_cuando_obtiene_el_candado_del_tenant()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        cliente.VincularSuscripcionStripe("cus_1", "sub_vieja", EstadoComercialTenant.Suspendida);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente]);
        var bloqueo = new BloqueoSuscripcionStripeFalso
        {
            AlBloquearTenant = () => cliente.VincularSuscripcionStripe("cus_1", "sub_otra", EstadoComercialTenant.Activa),
        };

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), bloqueo, bloqueo);

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_nueva"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        bloqueo.Suscripciones.Should().BeEquivalentTo(["sub_nueva", "sub_otra"]);
    }

    /// <summary>
    /// Si el vínculo cambia mientras se esperan los candados de las Subscriptions (no debería: el
    /// del Tenant ya está tomado), se aborta en vez de escribir sobre un estado que no se bloqueó.
    /// </summary>
    [Fact]
    public async Task Aborta_si_el_vinculo_cambia_mientras_espera_los_candados_de_suscripcion()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        cliente.VincularSuscripcionStripe("cus_1", "sub_vieja", EstadoComercialTenant.Suspendida);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.AddRange([plataforma, cliente]);
        var unitOfWork = new UnitOfWorkFalso();
        var bloqueo = new BloqueoSuscripcionStripeFalso
        {
            AlBloquearSuscripciones = () => cliente.VincularSuscripcionStripe("cus_1", "sub_otra", EstadoComercialTenant.Activa),
        };

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork, new TransaccionDeComandoFalsa(), bloqueo, bloqueo);

        var registrar = () => handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_nueva"), CancellationToken.None);

        await registrar.Should().ThrowAsync<InvalidOperationException>();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
