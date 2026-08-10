using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Clasificación de si una Conversacion con Cliente resuelto ya tiene alguna gestión CAE real que
/// hacer, o si todavía es solo contexto de negocio (el cliente copia al gestor desde el inicio de
/// una negociación comercial con un Centro/Empresa, antes de que exista ninguna gestión CAE — ronda
/// de reducción de ruido en Comunicaciones, patrón "conversación pre-CAE"). Satélite 1:1 con
/// Conversacion que se actualiza en vez de acumular histórico — a diferencia de
/// <see cref="ClasificacionRuidoMensaje"/> (uno por Mensaje, fijo desde la ingesta), aquí solo
/// importa el estado más reciente: se re-evalúa en cada mensaje entrante nuevo mientras
/// <see cref="EsAccionableCae"/> siga siendo false. En cuanto un mensaje hace que la IA devuelva
/// true, la clasificación se congela ahí — <see cref="RelevanciaCaeService"/> nunca vuelve a
/// llamar al clasificador para esa conversación (ver <see cref="RelevanciaCaeService.DebeReevaluar"/>).
/// </summary>
public class ClasificacionRelevanciaCae : EntidadConTenant
{
    public const int LongitudMaximaResumen = 500;

    public Guid ConversacionId { get; private set; }
    public bool EsAccionableCae { get; private set; }
    public string Resumen { get; private set; } = string.Empty;
    public int Confianza { get; private set; }
    public DateTime ActualizadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ClasificacionRelevanciaCae()
    {
    }

    public ClasificacionRelevanciaCae(Guid conversacionId, bool esAccionableCae, string resumen, int confianza)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("La clasificación debe estar ligada a una conversación.", nameof(conversacionId));

        ConversacionId = conversacionId;
        Actualizar(esAccionableCae, resumen, confianza);
    }

    public void Actualizar(bool esAccionableCae, string resumen, int confianza)
    {
        if (string.IsNullOrWhiteSpace(resumen))
            throw new ArgumentException("La clasificación debe explicar qué se detectó.", nameof(resumen));
        if (confianza is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(confianza), confianza, "La confianza debe estar entre 0 y 100.");

        EsAccionableCae = esAccionableCae;

        var normalizado = resumen.Trim();
        Resumen = normalizado.Length > LongitudMaximaResumen ? normalizado[..LongitudMaximaResumen] : normalizado;

        Confianza = confianza;
        ActualizadaEnUtc = DateTime.UtcNow;
    }
}
