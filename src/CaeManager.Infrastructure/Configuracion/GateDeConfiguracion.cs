namespace CaeManager.Infrastructure.Configuracion;

/// <summary>
/// Una sección de configuración que enciende o apaga una pieza según esté
/// completa. Con la configuración a medias el gate se cierra y la pieza
/// simplemente no existe: la aplicación arranca sana, el panel dice «no
/// configurado» y nada dice qué faltaba. Este contrato obliga a que cada gate
/// pueda contarlo, y <see cref="GateDeConfiguracion"/> saca de la MISMA lista de
/// requisitos el veredicto del gate y el motivo del aviso, para que nunca
/// puedan discrepar.
/// </summary>
public interface IOpcionesConGate
{
    /// <summary>Si la pieza se registra. Debe salir de <see cref="GateDeConfiguracion.Evaluar"/>.</summary>
    bool EstaConfigurado { get; }

    /// <summary>
    /// Qué falta cuando alguien ha empezado a configurar la pieza y no la ha
    /// terminado. Vacío si está completa o si no se ha informado NADA (apagado
    /// por defecto: es lo normal y no merece un aviso). Solo nombra opciones,
    /// nunca sus valores.
    /// </summary>
    IReadOnlyList<string> ProblemasDeConfiguracion();
}

/// <param name="Completo">Todos los requisitos cumplidos y el interruptor, si lo hay, encendido.</param>
/// <param name="Problemas">Un «falta X» por cada requisito sin cumplir; vacío si no hay nada que avisar.</param>
public readonly record struct EvaluacionGate(bool Completo, IReadOnlyList<string> Problemas);

public static class GateDeConfiguracion
{
    /// <summary>
    /// <paramref name="interruptor"/> es el <c>Activo</c> de las secciones que lo
    /// tienen (<c>null</c> si no lo tiene). Con el interruptor apagado no hay
    /// aviso aunque falte todo: apagarlo es una decisión, no un descuido. Con el
    /// interruptor encendido, lo que falte se avisa aunque no se haya informado
    /// ninguna otra clave. Sin interruptor, el aviso solo salta si se ha informado
    /// alguna clave y no todas: ninguna informada es «apagado por defecto».
    /// </summary>
    public static EvaluacionGate Evaluar(bool? interruptor, params (string Clave, string? Valor)[] requisitos)
    {
        var faltan = requisitos
            .Where(r => string.IsNullOrWhiteSpace(r.Valor))
            .Select(r => $"falta {r.Clave}")
            .ToList();

        var completo = interruptor != false && faltan.Count == 0;

        if (interruptor == false || faltan.Count == 0)
            return new EvaluacionGate(completo, []);

        var nadaInformado = faltan.Count == requisitos.Length;
        if (interruptor is null && nadaInformado)
            return new EvaluacionGate(completo, []);

        return new EvaluacionGate(completo, faltan);
    }
}
