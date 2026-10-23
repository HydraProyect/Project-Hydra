using CaeManager.Domain.Common;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.AsistenteIa;

/// <summary>Cuotas deslizantes compartidas por todos los circuitos del proceso; reserva ambas de forma atómica.</summary>
public sealed class LimitadorConsultaIa(IOptions<AsistenteConsultaOptions> opciones, TimeProvider reloj)
{
    private readonly object _cerrojo = new();
    private readonly Dictionary<(bool EsUsuario, Guid Id), Queue<DateTimeOffset>> _preguntas = [];

    public Error? IntentarReservar(Guid usuarioRealId, Guid tenantEfectivoId)
    {
        var config = opciones.Value;
        if (!config.EsValida())
            throw new OptionsValidationException(AsistenteConsultaOptions.SeccionConfiguracion,
                typeof(AsistenteConsultaOptions), ["Los límites de Consulta deben ser positivos y el historial debe admitir la pregunta."]);

        lock (_cerrojo)
        {
            var ahora = reloj.GetUtcNow();
            var inicio = ahora - config.VentanaCuota;
            foreach (var (clave, preguntas) in _preguntas.ToArray())
            {
                while (preguntas.TryPeek(out var fecha) && fecha <= inicio)
                    preguntas.Dequeue();
                if (preguntas.Count == 0)
                    _preguntas.Remove(clave);
            }

            var usuario = Obtener((true, usuarioRealId));
            var tenant = Obtener((false, tenantEfectivoId));
            if (usuario.Count >= config.MaxPreguntasUsuario)
                return Error.Crear("AsistenteIa.CuotaUsuario", TextosConsulta.Obtener("CuotaUsuario"));
            if (tenant.Count >= config.MaxPreguntasTenant)
                return Error.Crear("AsistenteIa.CuotaTenant", TextosConsulta.Obtener("CuotaTenant"));

            usuario.Enqueue(ahora);
            tenant.Enqueue(ahora);
            return null;
        }
    }

    private Queue<DateTimeOffset> Obtener((bool EsUsuario, Guid Id) clave)
    {
        if (!_preguntas.TryGetValue(clave, out var preguntas))
            _preguntas[clave] = preguntas = new();
        return preguntas;
    }
}
