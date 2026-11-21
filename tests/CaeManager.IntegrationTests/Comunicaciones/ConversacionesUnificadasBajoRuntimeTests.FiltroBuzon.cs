using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Desplegable «Buzón» (L-9) en la vista unificada, bajo <c>cae_app_runtime</c> y la RLS de cada Tenant: el id de la conexión
/// solo estrecha lo que la visibilidad del Tenant ya deja pasar. El id de un buzón de otro Tenant (aquí, el de fuera del
/// alcance) o el del buzón personal de otro Gestor CAE dan la página vacía, sin filas de ese Tenant, sin error y sin marcar a
/// nadie como no consultado.
/// </summary>
public partial class ConversacionesUnificadasBajoRuntimeTests
{
    /// <summary>Un buzón compartido en el Tenant de dentro, con un hilo, y otro en el Tenant de fuera, también con un hilo.</summary>
    private async Task<(Guid BuzonDentro, Guid HiloDelBuzon, Guid BuzonFuera)> SembrarBuzonesAsync()
    {
        Guid buzonDentro, hiloDelBuzon, buzonFuera;
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            var buzon = new ConexionIntegracion("cae@dentro.example.invalid", "CAE dentro");
            _propietario.ConexionesIntegracion.Add(buzon);
            await _propietario.SaveChangesAsync();
            buzonDentro = buzon.Id;
            hiloDelBuzon = AgregarConversacion("Hilo del buzón compartido de dentro", _clienteDentro, buzon.Id);
            await _propietario.SaveChangesAsync();
        }

        using (AmbitoTenantExplicito.Establecer(_tenantFuera))
        {
            var buzon = new ConexionIntegracion("cae@fuera.example.invalid", "CAE fuera");
            _propietario.ConexionesIntegracion.Add(buzon);
            await _propietario.SaveChangesAsync();
            buzonFuera = buzon.Id;
            AgregarConversacion("Hilo del buzón de un Tenant ajeno", null, buzon.Id);
            await _propietario.SaveChangesAsync();
        }

        return (buzonDentro, hiloDelBuzon, buzonFuera);
    }

    [Fact]
    public async Task El_filtro_por_buzon_deja_solo_los_hilos_de_esa_conexion()
    {
        var (buzonDentro, hiloDelBuzon, _) = await SembrarBuzonesAsync();

        var sinFiltro = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(TenantIds: [_tenantDentro]));
        sinFiltro.Filas.Select(f => f.Id).Should().Contain([hiloDelBuzon, _conversacionDentro],
            "control del instrumento: sin filtro salen el hilo del buzón y el que no tiene buzón");

        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(
            TenantIds: [_tenantDentro], ConexionIntegracionId: buzonDentro));

        pagina.Filas.Should().ContainSingle().Which.Id.Should().Be(hiloDelBuzon);
        pagina.Filas.Single().ConexionIntegracionId.Should().Be(buzonDentro);
        pagina.NoConsultados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_buzon_de_un_Tenant_ajeno_da_la_pagina_vacia_aunque_se_pidan_todas_las_Empresas_gestionadas()
    {
        var (_, _, buzonFuera) = await SembrarBuzonesAsync();

        var todas = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(ConexionIntegracionId: buzonFuera));
        var pedidaPorSuTenant = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(
            TenantIds: [_tenantFuera], ConexionIntegracionId: buzonFuera));

        todas.Filas.Should().BeEmpty("el id de un buzón de otro Tenant no trae ninguna fila: el filtro no es autoridad");
        todas.NoConsultados.Should().BeEmpty("ningún Tenant del conjunto falla por el filtro");
        pedidaPorSuTenant.Filas.Should().BeEmpty();
        pedidaPorSuTenant.NoConsultados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_buzon_personal_de_otro_Gestor_CAE_da_la_pagina_vacia()
    {
        var buzonAjeno = await LeerComoPropietarioAsync(_tenantDentro, _conversacionBuzonAjeno);

        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(
            TenantIds: [_tenantDentro], ConexionIntegracionId: buzonAjeno));

        pagina.Filas.Should().BeEmpty("el filtro nunca abre un hilo que la visibilidad dejó fuera");
        pagina.NoConsultados.Should().BeEmpty();
    }

    /// <summary>La conexión de una conversación sembrada, leída como propietario dentro del ámbito de su Tenant.</summary>
    private async Task<Guid> LeerComoPropietarioAsync(Guid tenantId, Guid conversacionId)
    {
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var conversacion = await _propietario.Conversaciones.FindAsync(conversacionId);
            return conversacion!.ConexionIntegracionId!.Value;
        }
    }
}
