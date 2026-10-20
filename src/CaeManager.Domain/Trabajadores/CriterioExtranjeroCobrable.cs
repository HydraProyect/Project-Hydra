using CaeManager.Domain.Common;

namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Quién cuenta como «Trabajador extranjero (cobrable)» en la exportación mensual del Cuadro de dirección, con la que el
/// Operador CAE externo cobra por cada Trabajador extranjero gestionado (decisión de Chris, 2026-10-20).
///
/// <para>
/// <b>Por documento</b> (<see cref="PorDocumento"/>): solo un documento que no es español. No cuentan el DNI ni el NIE
/// (quien tiene NIE puede ser residente), tampoco el número de soporte de la TIE, que es la tarjeta española del
/// extranjero residente y comparte formato con el pasaporte español. Cuentan el pasaporte y cualquier otro documento que
/// no coincide con un formato español de persona física. Un DNI o un NIE con la letra de control mal sigue siendo DNI o
/// NIE: un error de tecleo no convierte a nadie en extranjero. Sin documento (Trabajador anonimizado) no cuenta.
/// </para>
///
/// <para>
/// <b>Marca a mano</b> (<see cref="Trabajador.MarcaExtranjeroCobrable"/>): si está fijada, manda sobre el documento
/// (<see cref="Efectivo"/>), en los dos sentidos (p. ej. un ciudadano de la UE con pasaporte que no se cobra).
/// </para>
///
/// <para>
/// No es <see cref="ValidadorIdentificacion.EsDocumentoDeExtranjero"/>, que cuenta el NIE y la TIE y sigue sirviendo a
/// la facturación de la visita de Trabajador extranjero, al catálogo de KPI y a la cifra «Extranjeros» del Cuadro de
/// dirección. Son dos criterios distintos a propósito: este no cambia aquellos.
/// </para>
/// </summary>
public static class CriterioExtranjeroCobrable
{
    public static bool PorDocumento(string? documento)
    {
        var normalizado = ValidadorIdentificacion.Normalizar(documento);
        if (normalizado is null)
            return false;

        return ValidadorIdentificacion.Analizar(normalizado).Tipo
            is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.TieSoporte);
    }

    /// <summary>La marca a mano si está fijada; si no, el criterio por documento.</summary>
    public static bool Efectivo(bool? marca, string? documento) => marca ?? PorDocumento(documento);
}
