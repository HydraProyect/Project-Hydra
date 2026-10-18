using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

namespace CaeManager.Web.Features.Bandeja.Components;

public partial class PropagacionesPendientes
{
    private Task<TResultado> EnTenantAsync<TResultado>(Fila fila, Func<Task<TResultado>> accion) => Puerta.EjecutarAsync(async () =>
    {
        // La coordenada de la fila no es autoridad. La cartera se vuelve a consultar en cada acción,
        // incluido el envío diferido; los behaviors se ejecutan después dentro del Tenant propietario.
        var autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery(), _ciclo.Token);
        if (!autorizados.Any(t => t.TenantId == fila.TenantId))
            throw new UnauthorizedAccessException();
        using (AmbitoTenantExplicito.Establecer(fila.TenantId)) return await accion();
    }, _ciclo.Token);
}
