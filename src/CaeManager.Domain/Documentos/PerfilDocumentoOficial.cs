namespace CaeManager.Domain.Documentos;

/// <summary>
/// Qué documento oficial de la Administración es un TipoDocumento, a efectos
/// de la validación automática (verificación de firma + parser determinista
/// del perfil). Un único selector en vez de un booleano "verificación de
/// firma activa" + parser aparte: el pipeline necesita saber qué parser
/// aplicar, y un flag suelto permitiría el estado inconsistente "activo sin
/// parser". <see cref="Ninguno"/> = apagado (el valor de la inmensa mayoría
/// de tipos). El booleano genérico para verificar firmas de cualquier
/// documento sin parser es la épica 2 — YAGNI hoy.
/// </summary>
public enum PerfilDocumentoOficial
{
    Ninguno = 0,

    /// <summary>Certificado de estar al corriente con la Seguridad Social (TGSS), con CEA verificable en el SVID.</summary>
    CorrienteTgss = 1,

    /// <summary>Certificado de estar al corriente de obligaciones tributarias (AEAT), con CSV verificable en su sede.</summary>
    CorrienteAeat = 2,

    /// <summary>Informe de Trabajadores en Alta (TGSS).</summary>
    Ita = 3,

    /// <summary>Relación Nominal de Trabajadores (Sistema RED), con huella electrónica propia.</summary>
    Rnt = 4,

    /// <summary>Recibo de Liquidación de Cotizaciones (Sistema RED).</summary>
    Rlc = 5,
}
