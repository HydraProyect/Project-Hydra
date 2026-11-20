using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// El selector de la vista unificada (D-6 b, R-4; rótulo «Empresa gestionada», estado L-1 del lienzo):
/// <see cref="ObtenerTenantsBeneficiariosVistaUnificadaQuery"/> sobre el mismo escenario y la misma composición que la
/// lista (cae_app_runtime, contexto RLS firmado, <c>CurrentUserService</c> y <c>AlcanceDatosService</c> de producción).
/// Fija que el selector solo ofrece Tenants que la lista lee, que su recuento cuadra con la lista filtrada por ese
/// Tenant y que el filtro de la lista es una coordenada intersecada, nunca autoridad.
/// </summary>
public partial class ConversacionesUnificadasBajoRuntimeTests
{
    [Fact]
    public async Task El_selector_solo_ofrece_los_Tenants_que_la_lista_lee()
    {
        var selector = await EnviarSelectorAsync();

        var dentro = selector.Tenants.Should().ContainSingle(t => t.TenantId == _tenantDentro).Subject;
        dentro.TenantNombre.Should().Be("Tenant beneficiario dentro");
        dentro.EsOrigen.Should().BeFalse();
        dentro.Abiertas.Should().Be(1, "cuenta la conversación del Cliente empresarial y no la del buzón personal ajeno");
        selector.Tenants.Should().NotContain(t => t.TenantId == _tenantFuera || t.TenantId == _tenantCaducada,
            "ni el Tenant sin vínculo ni el de cartera vencida, aunque conserve la delegación heredada");
        selector.NoConsultados.Should().BeEmpty("un Tenant de fuera no se distingue de uno inexistente");

        var lista = await EnviarAsync(new ObtenerConversacionesUnificadasQuery());
        lista.Filas.Select(f => f.TenantId).Distinct()
            .Should().BeSubsetOf(selector.Tenants.Select(t => t.TenantId), "la lista no lee ningún Tenant que el selector no ofrezca");
    }

    [Fact]
    public async Task El_recuento_de_cada_Tenant_cuadra_con_la_lista_filtrada_por_el()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            AgregarConversacionEnEstado("Dentro, esperando respuesta", EstadoConversacion.EsperandoRespuestaExterna);
            AgregarConversacionEnEstado("Dentro, resuelta", EstadoConversacion.Resuelta);
            await _propietario.SaveChangesAsync();
        }
        var (otro, clienteOtro) = await CrearTenantDentroAsync("Segundo Tenant beneficiario dentro", "B10380210");
        using (AmbitoTenantExplicito.Establecer(otro))
        {
            AgregarConversacion("Otro, primera", clienteOtro, null);
            AgregarConversacion("Otro, segunda", clienteOtro, null);
            AgregarConversacionEnEstado("Otro, resuelta", EstadoConversacion.Resuelta, clienteOtro);
            await _propietario.SaveChangesAsync();
        }
        var (vacio, _) = await CrearTenantDentroAsync("Tenant beneficiario sin conversaciones", "B10380228");

        var selector = await EnviarSelectorAsync();

        selector.NoConsultados.Should().BeEmpty();
        selector.Tenants.Where(t => t.TenantId != _tenantOrigen)
            .Select(t => (t.TenantId, t.Abiertas))
            .Should().BeEquivalentTo(new[] { (_tenantDentro, 2), (otro, 2), (vacio, 0) },
                "Abiertas es el grupo de DN-O, y un Tenant autorizado sin conversaciones sale con cero, no se omite");
        foreach (var tenant in selector.Tenants)
        {
            var filas = await RecorrerAsync([tenant.TenantId], GrupoEstadoConversacion.Abiertas);
            filas.Should().HaveCount(tenant.Abiertas, $"el recuento de {tenant.TenantNombre} es lo que la lista devuelve para él");
            filas.Should().OnlyContain(f => f.TenantId == tenant.TenantId);
        }

        (await RecorrerAsync(null, GrupoEstadoConversacion.Abiertas)).Should().HaveCount(selector.Tenants.Sum(t => t.Abiertas),
            "el total de «Todas las Empresas gestionadas» es la suma de los recuentos");
        (await RecorrerAsync([_tenantDentro], null)).Should().HaveCount(3,
            "control: sin el grupo, la resuelta también sale, así que el filtro por grupo filtra de verdad");
    }

    [Fact]
    public async Task Un_Tenant_no_autorizado_no_pasa_como_filtro_aunque_vaya_con_uno_autorizado()
    {
        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(
            TenantIds: [_tenantFuera, _tenantCaducada, _tenantDentro, _tenantFuera]));

        pagina.Filas.Should().Contain(f => f.Id == _conversacionDentro);
        pagina.Filas.Should().OnlyContain(f => f.TenantId == _tenantDentro,
            "la coordenada solo estrecha el conjunto: los Tenants de fuera se descartan aunque vayan acompañados");
        pagina.NoConsultados.Should().BeEmpty("los descartados no se nombran: no se distinguen de uno inexistente");
    }

    [Fact]
    public async Task Con_Sesion_Privilegiada_seleccionada_el_selector_no_se_abre()
    {
        var handler = ActivatorUtilities.CreateInstance<ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler>(
            _servicios, new CoordenadaSeleccionada());

        var accion = () => handler.Handle(new ObtenerTenantsBeneficiariosVistaUnificadaQuery(), CancellationToken.None);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private Task<TenantsBeneficiariosVistaUnificadaDto> EnviarSelectorAsync() =>
        _servicios.GetRequiredService<IMediator>().Send(new ObtenerTenantsBeneficiariosVistaUnificadaQuery());

    /// <summary>Todas las páginas de la lista, de una en una fila, para que el cursor también entre en la cuenta.</summary>
    private async Task<List<ConversacionUnificadaDto>> RecorrerAsync(
        IReadOnlyList<Guid>? tenantIds, GrupoEstadoConversacion? grupoEstado)
    {
        var filas = new List<ConversacionUnificadaDto>();
        CursorConversacionesUnificadas? cursor = null;
        for (var vuelta = 0; vuelta < 50; vuelta++)
        {
            var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(
                TenantIds: tenantIds, Cursor: cursor, TamanoPagina: 1, GrupoEstado: grupoEstado));
            pagina.NoConsultados.Should().BeEmpty();
            filas.AddRange(pagina.Filas);
            cursor = pagina.Siguiente;
            if (cursor is null) return filas;
        }
        throw new InvalidOperationException("El recorrido no terminó en 50 páginas.");
    }

    private void AgregarConversacionEnEstado(string asunto, EstadoConversacion estado, Guid? clienteId = null)
    {
        var conversacion = new Conversacion(asunto, clienteId: clienteId ?? _clienteDentro);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "alguien@externo.test", "<p>Hola</p>");
        conversacion.CambiarEstado(estado).EsExitoso.Should().BeTrue("control del fixture: la transición desde Abierta existe");
        _propietario.Conversaciones.Add(conversacion);
    }
}
