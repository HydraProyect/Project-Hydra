namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Lo mínimo que una lectura debe traer para que alguien pueda decidir con ella.
/// No es una lista nueva: son las tres evidencias sobre las que
/// <c>VerificacionIaDocumentoService.ComputarMotivos</c> decide si falta prueba
/// (tipo detectado, fecha de emisión y firma) y las claves con las que
/// <see cref="RouterExtraccionMetadatosDocumentoIaService"/> las lee. Una diferencia
/// deliberada: ComputarMotivos pide siempre revisión si la firma no es «true»; aquí
/// un «false» explícito basta para no leer más páginas, porque es una respuesta del
/// modelo y la revisión se pide igual. El router documental no conoce el tipo de
/// documento concreto, así que no hay una lista de campos por tipo: esta es la
/// única definición vigente de «lectura con lo mínimo».
///
/// Coste conocido: un documento sin fecha de emisión explícita (la ausencia es un
/// resultado normal del modelo) amplía siempre hasta el tope de 100 páginas.
///
/// La fecha de vencimiento no figura: la verificación no la exige (hay documentos
/// sin caducidad) y un campo que puede no existir nunca debería disparar la lectura
/// de más páginas de pago.
///
/// Se usa para decidir si un PDF escaneado, leído al principio solo por sus primeras
/// páginas y la última, necesita ampliar el OCR al resto.
/// </summary>
internal static class EvidenciaMinimaLectura
{
    public const string ClaveFechaEmision = "fechaEmision";
    public const string ClaveFechaVencimiento = "fechaVencimiento";
    public const string ClaveTieneFirma = "tieneFirma";

    /// <summary>Evidencias que no consta que la lectura haya encontrado. Vacía si la lectura está completa.</summary>
    public static IReadOnlyList<string> Faltantes(ExtraccionEstructuradaDto extraccion)
    {
        var faltantes = new List<string>(3);

        if (string.IsNullOrWhiteSpace(extraccion.TipoDetectado))
            faltantes.Add("tipoDetectado");

        if (!TieneValor(extraccion, ClaveFechaEmision))
            faltantes.Add(ClaveFechaEmision);

        // Un «false» explícito es una respuesta (el modelo vio el documento y no hay
        // firma); lo que falta es que no diga nada.
        if (!TieneValor(extraccion, ClaveTieneFirma))
            faltantes.Add(ClaveTieneFirma);

        return faltantes;
    }

    public static bool Faltan(ExtraccionEstructuradaDto extraccion) => Faltantes(extraccion).Count > 0;

    private static bool TieneValor(ExtraccionEstructuradaDto extraccion, string clave) =>
        extraccion.Campos.TryGetValue(clave, out var valor) && !string.IsNullOrWhiteSpace(valor);
}
