using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.VistaDemo;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.VistaUnificada;

/// <summary>
/// Los Tenants beneficiarios que la vista unificada de Comunicaciones puede leer para el usuario actual, con el recuento
/// de conversaciones abiertas de cada uno (D-6 b, R-4; § 4.3 «Contadores de "Mi cartera"» del diseño en
/// Project-Hydra-Negocio/tecnico/DISENO-D6B-COMUNICACIONES-MULTITENANT-2026-11-17.md). Alimenta el selector rotulado
/// «Empresa gestionada» (DN-8; estado L-1 del lienzo). Solo lectura.
///
/// <para>
/// Mismo recorrido que la lista (<see cref="ObtenerConversacionesUnificadasQuery"/>), por la misma entrada
/// (<see cref="EntradaVistaUnificada.AbrirAsync"/>: exclusiones de plano, conjunto de la vista y lente de demo) y la
/// misma lectura por Tenant (<see cref="EntradaVistaUnificada.ConversacionesDelTenantAsync"/>), dentro del
/// <see cref="AmbitoTenantAutorizado"/> de cada Tenant y bajo su RLS. Así, ningún Tenant aparece aquí si la lista no lo
/// leería, y el recuento de un Tenant es el número de filas que la lista devuelve con <c>TenantIds = [ese Tenant]</c> y
/// <c>GrupoEstado = Abiertas</c>.
/// </para>
///
/// <para>
/// «Abiertas» es el grupo de DN-O (<see cref="GruposEstadoConversacion.Abiertas"/>), el mismo que la vista Abiertas de
/// la lista. Un Tenant autorizado sin conversaciones abiertas sale con recuento cero; uno que falla o que no pasa la
/// segunda línea sale en <see cref="TenantsBeneficiariosVistaUnificadaDto.NoConsultados"/>, nunca con cero.
/// </para>
/// </summary>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerTenantsBeneficiariosVistaUnificadaQuery : IRequest<TenantsBeneficiariosVistaUnificadaDto>;

/// <param name="TenantId">El Tenant beneficiario, que es el valor que la lista acepta en su filtro.</param>
/// <param name="TenantNombre"><c>Tenants.Nombre</c>, rótulo «Empresa gestionada» (DN-8).</param>
/// <param name="EsOrigen">Es el Tenant de origen del usuario, que entra en la vista solo si lo gestiona.</param>
/// <param name="Abiertas">Conversaciones del grupo Abiertas (DN-O) que la vista lee de ese Tenant.</param>
public record TenantBeneficiarioVistaUnificadaDto(Guid TenantId, string TenantNombre, bool EsOrigen, int Abiertas);

/// <param name="Tenants">Por nombre; el total de «Todas las Empresas gestionadas» es la suma de sus recuentos.</param>
/// <param name="NoConsultados">Tenants del conjunto que no se pudieron contar (§ 4.3): nunca son «cero».</param>
public record TenantsBeneficiariosVistaUnificadaDto(
    IReadOnlyList<TenantBeneficiarioVistaUnificadaDto> Tenants,
    IReadOnlyList<TenantNoConsultadoDto> NoConsultados);

public class ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IIntegracionesQueryContext integracionesContext,
    IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual,
    ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada,
    ILogger<ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler> logger,
    IVistaDemoActual? vistaDemo = null)
    : IRequestHandler<ObtenerTenantsBeneficiariosVistaUnificadaQuery, TenantsBeneficiariosVistaUnificadaDto>
{
    public async Task<TenantsBeneficiariosVistaUnificadaDto> Handle(
        ObtenerTenantsBeneficiariosVistaUnificadaQuery request, CancellationToken cancellationToken)
    {
        var (identidad, conjunto) = await EntradaVistaUnificada.AbrirAsync(
            null, currentUserService, operaciones, tenants, coordenadaSesionPrivilegiada, vistaDemo, cancellationToken);
        var cota = OrdenConversacionesUnificadas.TruncarAMicrosegundos(DateTime.UtcNow);
        var nombres = await EntradaVistaUnificada.NombresAsync(tenants, conjunto, cancellationToken);

        var contados = new List<TenantBeneficiarioVistaUnificadaDto>();
        var noConsultados = new List<TenantNoConsultadoDto>();
        foreach (var tenantId in conjunto)
        {
            var nombre = nombres.GetValueOrDefault(tenantId, string.Empty);
            var esOrigen = tenantId == identidad.TenantOrigenId;
            try
            {
                var (autorizado, abiertas) = await AmbitoTenantAutorizado.EjecutarAsync(
                    identidad, operaciones, tenants, tenantId,
                    ambito => ContarAbiertasAsync(ambito, esOrigen, cota, cancellationToken),
                    cancellationToken);
                if (autorizado && abiertas is { } recuento)
                    contados.Add(new TenantBeneficiarioVistaUnificadaDto(tenantId, nombre, esOrigen, recuento));
                else
                    noConsultados.Add(new TenantNoConsultadoDto(tenantId, nombre, esOrigen));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Vista unificada de Comunicaciones: no se pudo contar el Tenant {TenantId}; se devuelve el resto.", tenantId);
                noConsultados.Add(new TenantNoConsultadoDto(tenantId, nombre, esOrigen));
            }
        }

        var ordenados = contados
            .OrderBy(t => t.TenantNombre, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(t => t.TenantId, Comparer<Guid>.Create(OrdenConversacionesUnificadas.CompararComoUuid))
            .ToList();
        return new TenantsBeneficiariosVistaUnificadaDto(ordenados, noConsultados);
    }

    /// <summary>El recuento de un Tenant, dentro de su ámbito; <c>null</c> si no pasa la segunda línea.</summary>
    private async Task<int?> ContarAbiertasAsync(
        AmbitoTenantAutorizado ambito, bool esOrigen, DateTime cota, CancellationToken cancellationToken)
    {
        var consulta = await EntradaVistaUnificada.ConversacionesDelTenantAsync(
            ambito, esOrigen, cota, GrupoEstadoConversacion.Abiertas, comunicacionesContext, integracionesContext,
            alcanceDatos, currentUserService, tenantActual, cancellationToken);
        return consulta is null ? null : await consulta.CountAsync(cancellationToken);
    }
}
