using CaeManager.Infrastructure.Autenticacion;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Tests;

/// <summary>El ticket queda ligado a usuario, Acreditación, Documento y host, y no se confunde con el token de sesión.</summary>
public class TicketEntregaExtensionTests
{
    private readonly IDataProtectionProvider _proveedor = new EphemeralDataProtectionProvider();

    [Fact]
    public void Devuelve_lo_que_se_emitio()
    {
        var (usuario, tenant, operacion) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (acreditacion, documento) = (Guid.NewGuid(), Guid.NewGuid());

        var carga = TicketEntregaExtension.Leer(
            _proveedor, TicketEntregaExtension.Proteger(_proveedor, usuario, tenant, operacion, acreditacion, documento, "app.dokify.net"));

        carga.Should().Be(new CargaUtilTicketEntrega(usuario, tenant, operacion, acreditacion, documento, "app.dokify.net"));
    }

    [Fact]
    public void En_el_Tenant_de_origen_no_lleva_Asignacion_de_Operacion()
    {
        var tenant = Guid.NewGuid();

        var carga = TicketEntregaExtension.Leer(
            _proveedor, TicketEntregaExtension.Proteger(_proveedor, Guid.NewGuid(), tenant, null, Guid.NewGuid(), Guid.NewGuid(), "app.dokify.net"));

        carga!.TenantId.Should().Be(tenant);
        carga.AsignacionOperacionId.Should().BeNull();
    }

    [Fact]
    public void Dura_un_minuto() => TicketEntregaExtension.Vigencia.Should().Be(TimeSpan.FromSeconds(60));

    [Fact]
    public void La_caducidad_que_lleva_el_ticket_es_la_de_su_vigencia()
    {
        var emitido = DateTimeOffset.UtcNow;
        var ticket = TicketEntregaExtension.Proteger(_proveedor, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "app.dokify.net");

        _proveedor.CreateProtector("CaeManager.Extension.EntregaArchivo.v2").ToTimeLimitedDataProtector()
            .Unprotect(ticket, out var caducidad);

        caducidad.Should().BeCloseTo(emitido.Add(TicketEntregaExtension.Vigencia), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Un_token_de_sesion_no_vale_como_ticket_ni_al_reves()
    {
        var token = TokenSesionExtension.Proteger(_proveedor, Guid.NewGuid(), "SELLO", Guid.NewGuid());
        var ticket = TicketEntregaExtension.Proteger(_proveedor, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "app.dokify.net");

        TicketEntregaExtension.Leer(_proveedor, token).Should().BeNull();
        TokenSesionExtension.Leer(_proveedor, ticket).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-un-ticket")]
    public void Lo_ilegible_no_entrega_nada(string? ticket) =>
        TicketEntregaExtension.Leer(_proveedor, ticket).Should().BeNull();

    [Fact]
    public void Un_ticket_alterado_no_entrega_nada()
    {
        var ticket = TicketEntregaExtension.Proteger(_proveedor, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "app.dokify.net");

        TicketEntregaExtension.Leer(_proveedor, ticket[..^4] + "AAAA").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a|b|c")]
    [InlineData("11111111111111111111111111111111|22222222222222222222222222222222|33333333333333333333333333333333|host")]
    [InlineData("00000000000000000000000000000000|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|11111111111111111111111111111111|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|00000000000000000000000000000000|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|11111111111111111111111111111111|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|no-es-un-guid|11111111111111111111111111111111|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|00000000000000000000000000000000|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|11111111111111111111111111111111|00000000000000000000000000000000|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|11111111111111111111111111111111|22222222222222222222222222222222| ")]
    [InlineData("cccccccccccccccccccccccccccccccc|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|11111111111111111111111111111111|22222222222222222222222222222222|host|extra")]
    public void Una_carga_malformada_se_rechaza(string carga) =>
        TicketEntregaExtension.ParsearCargaUtil(carga).Should().BeNull();

    [Fact]
    public void Un_ticket_del_binario_saliente_se_lee_sin_inventar_Tenant_ni_Asignacion_de_Operacion()
    {
        var (usuario, acreditacion, documento) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var carga = TicketEntregaExtension.Leer(_proveedor, TicketAnterior($"{usuario:N}|{acreditacion:N}|{documento:N}|app.dokify.net"));

        carga.Should().Be(new CargaUtilTicketEntrega(usuario, null, null, acreditacion, documento, "app.dokify.net"));
    }

    [Fact]
    public void Un_ticket_del_binario_saliente_caduca_igual()
    {
        var carga = $"{Guid.NewGuid():N}|{Guid.NewGuid():N}|{Guid.NewGuid():N}|app.dokify.net";

        TicketEntregaExtension.Leer(_proveedor, TicketAnterior(carga, TimeSpan.FromSeconds(-1))).Should().BeNull();
    }

    [Fact]
    public void Una_carga_vigente_bajo_el_proposito_anterior_no_entrega_nada()
    {
        var carga = $"{Guid.NewGuid():N}|{Guid.NewGuid():N}|{Guid.Empty:N}|{Guid.NewGuid():N}|{Guid.NewGuid():N}|app.dokify.net";

        TicketEntregaExtension.Leer(_proveedor, TicketAnterior(carga)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a|b|c")]
    [InlineData("00000000000000000000000000000000|11111111111111111111111111111111|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|00000000000000000000000000000000|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|11111111111111111111111111111111|00000000000000000000000000000000|host")]
    [InlineData("no-es-un-guid|11111111111111111111111111111111|22222222222222222222222222222222|host")]
    [InlineData("cccccccccccccccccccccccccccccccc|11111111111111111111111111111111|22222222222222222222222222222222| ")]
    [InlineData("cccccccccccccccccccccccccccccccc|11111111111111111111111111111111|22222222222222222222222222222222|host|extra")]
    public void Una_carga_anterior_malformada_se_rechaza(string carga) =>
        TicketEntregaExtension.ParsearCargaUtilAnterior(carga).Should().BeNull();

    /// <summary>Lo que emitía el binario anterior a v2, con su propósito literal: una constante compartida no lo probaría.</summary>
    private string TicketAnterior(string carga, TimeSpan? vigencia = null) =>
        _proveedor.CreateProtector("CaeManager.Extension.EntregaArchivo.v1").ToTimeLimitedDataProtector()
            .Protect(carga, vigencia ?? TicketEntregaExtension.Vigencia);
}
