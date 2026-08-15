using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Common;

/// <summary>Fake en memoria — nunca llama a Sentry de verdad (ver CODING_STANDARDS.md).</summary>
public class AlertaOperativaFalsa : IAlertaOperativa
{
    public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

    public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));
}
