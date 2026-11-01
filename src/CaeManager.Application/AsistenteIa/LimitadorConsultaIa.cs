using CaeManager.Domain.Common;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.AsistenteIa;

/// <summary>
/// Cuotas deslizantes compartidas por todos los circuitos del proceso. La Consulta
/// reserva a la vez la cuota de la persona y la del Tenant efectivo; la propuesta de
/// plan del agente tiene su propia cuota por Tenant efectivo, separada de la de la
/// Consulta, y se reserva una sola vez por propuesta aunque esta llame dos veces al
/// proveedor (clasificar la orden y seleccionar candidatos).
/// </summary>
public sealed class LimitadorConsultaIa(IOptions<AsistenteConsultaOptions> opciones, TimeProvider reloj)
{
    private readonly object _cerrojo = new();
    private readonly Dictionary<(bool EsUsuario, Guid Id), Queue<DateTimeOffset>> _preguntas = [];
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _propuestas = [];

    public Error? IntentarReservarPropuesta(Guid tenantEfectivoId)
    {
        var config = ConfiguracionValida();

        lock (_cerrojo)
        {
            var ahora = reloj.GetUtcNow();
            Purgar(_propuestas, ahora - config.VentanaCuota);

            var tenant = Obtener(_propuestas, tenantEfectivoId);
            if (tenant.Count >= config.MaxPropuestasTenant)
                return Error.Crear("AsistenteIa.CuotaTenant", TextosConsulta.Obtener("CuotaTenant"));

            tenant.Enqueue(ahora);
            return null;
        }
    }

    public Error? IntentarReservar(Guid usuarioRealId, Guid tenantEfectivoId)
    {
        var config = ConfiguracionValida();

        lock (_cerrojo)
        {
            var ahora = reloj.GetUtcNow();
            Purgar(_preguntas, ahora - config.VentanaCuota);

            var usuario = Obtener(_preguntas, (true, usuarioRealId));
            var tenant = Obtener(_preguntas, (false, tenantEfectivoId));
            if (usuario.Count >= config.MaxPreguntasUsuario)
                return Error.Crear("AsistenteIa.CuotaUsuario", TextosConsulta.Obtener("CuotaUsuario"));
            if (tenant.Count >= config.MaxPreguntasTenant)
                return Error.Crear("AsistenteIa.CuotaTenant", TextosConsulta.Obtener("CuotaTenant"));

            usuario.Enqueue(ahora);
            tenant.Enqueue(ahora);
            return null;
        }
    }

    private AsistenteConsultaOptions ConfiguracionValida()
    {
        var config = opciones.Value;
        if (!config.EsValida())
            throw new OptionsValidationException(AsistenteConsultaOptions.SeccionConfiguracion,
                typeof(AsistenteConsultaOptions), ["Los límites de Consulta deben ser positivos y el historial debe admitir la pregunta."]);
        return config;
    }

    private static void Purgar<TClave>(Dictionary<TClave, Queue<DateTimeOffset>> cuotas, DateTimeOffset inicio)
        where TClave : notnull
    {
        foreach (var (clave, reservas) in cuotas.ToArray())
        {
            while (reservas.TryPeek(out var fecha) && fecha <= inicio)
                reservas.Dequeue();
            if (reservas.Count == 0)
                cuotas.Remove(clave);
        }
    }

    private static Queue<DateTimeOffset> Obtener<TClave>(Dictionary<TClave, Queue<DateTimeOffset>> cuotas, TClave clave)
        where TClave : notnull
    {
        if (!cuotas.TryGetValue(clave, out var reservas))
            cuotas[clave] = reservas = new();
        return reservas;
    }
}
