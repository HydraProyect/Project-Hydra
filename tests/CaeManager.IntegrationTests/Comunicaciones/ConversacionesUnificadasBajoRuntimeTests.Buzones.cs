using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Los buzones de un Tenant beneficiario para la pantalla de Buzones por Empresa gestionada (D-6 b, L-55):
/// <see cref="ObtenerBuzonesVistaUnificadaQuery"/> por el mediador, sobre el mismo escenario y la misma composición que la
/// lista (cae_app_runtime, contexto RLS firmado, <c>CurrentUserService</c> y <c>AlcanceDatosService</c> de producción). Fija
/// que el Tenant pedido es una coordenada que solo estrecha (fuera, con la cartera vencida o el de origen no gestionado
/// devuelven <c>null</c>), que dentro del Tenant se listan sus compartidos y el personal propio y nunca el personal ajeno,
/// y que los nombres se resuelven dentro del ámbito de ese Tenant.
/// </summary>
public partial class ConversacionesUnificadasBajoRuntimeTests
{
    [Fact]
    public async Task Los_buzones_del_Tenant_dentro_son_sus_compartidos_y_el_personal_propio_y_nunca_el_personal_ajeno()
    {
        var responsable = Guid.NewGuid();
        Guid compartidoId, propioId;
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            var compartido = new ConexionIntegracion("obra.dentro@ejemplo.local", "Buzón compartido dentro");
            compartido.FijarResponsable(responsable);
            var propio = new ConexionIntegracion(
                "gestor.propio@ejemplo.local", "Buzón personal propio", gestorPropietarioId: _usuario);
            _propietario.ConexionesIntegracion.AddRange(compartido, propio);
            await _propietario.SaveChangesAsync();
            compartidoId = compartido.Id;
            propioId = propio.Id;
            AgregarConversacion("Hilo del buzón compartido dentro", _clienteDentro, compartidoId);
            await _propietario.SaveChangesAsync();
        }

        var buzones = await EnviarBuzonesAsync(_tenantDentro);

        buzones.Should().NotBeNull("el Tenant dentro está en el conjunto de la vista");
        buzones!.Conexiones.Select(c => c.Id).Should().BeEquivalentTo(new[] { compartidoId, propioId },
            "conjunto exacto: el buzón personal ajeno del fixture no se lista");
        buzones.Actividad.Keys.Should().BeEquivalentTo(new[] { compartidoId, propioId });
        buzones.Actividad[compartidoId].ConversacionesAbiertas.Should().Be(1);
        buzones.Actividad[propioId].ConversacionesAbiertas.Should().Be(0);
        _directorio.Consultas.Should().ContainSingle().Which.Should().Be(_tenantDentro,
            "los nombres se resuelven dentro del ámbito del Tenant beneficiario, no del activo");
        buzones.Nombres.Should().ContainKey(responsable);
    }

    [Fact]
    public async Task Un_Tenant_de_fuera_con_la_cartera_vencida_o_el_de_origen_no_gestionado_no_devuelve_buzones()
    {
        foreach (var tenant in new[] { _tenantFuera, _tenantCaducada, _tenantOrigen, Guid.NewGuid() })
            (await EnviarBuzonesAsync(tenant)).Should().BeNull("la coordenada no autoriza: {0} no está en el conjunto", tenant);
        _directorio.Consultas.Should().BeEmpty("no se lee nada de un Tenant que no está en el conjunto");
    }

    [Fact]
    public async Task Con_Sesion_Privilegiada_seleccionada_los_buzones_no_se_abren()
    {
        var handler = ActivatorUtilities.CreateInstance<ObtenerBuzonesVistaUnificadaQueryHandler>(
            _servicios, new CoordenadaSeleccionada());

        var accion = () => handler.Handle(new ObtenerBuzonesVistaUnificadaQuery(_tenantDentro), CancellationToken.None);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private Task<BuzonesDeTenantBeneficiarioDto?> EnviarBuzonesAsync(Guid tenantId) =>
        _servicios.GetRequiredService<IMediator>().Send(new ObtenerBuzonesVistaUnificadaQuery(tenantId));

    /// <summary>
    /// El directorio de producción no está en esta composición: el doble devuelve un nombre por id y anota en qué Tenant
    /// del ámbito se le preguntó.
    /// </summary>
    private sealed class DirectorioQueAnotaElAmbito : IDirectorioUsuariosService
    {
        public List<Guid?> Consultas { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default)
        {
            Consultas.Add(AmbitoTenantExplicito.TenantIdActual);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(usuarioIds.ToDictionary(id => id, id => $"Persona {id:N}"));
        }

        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> EsCuentaActivaConRolAsync(
            Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(
            Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private readonly DirectorioQueAnotaElAmbito _directorio = new();
}
