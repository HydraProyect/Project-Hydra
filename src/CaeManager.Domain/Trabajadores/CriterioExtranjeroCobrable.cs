using CaeManager.Domain.Common;

namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Quién cuenta como «Trabajador extranjero (cobrable)» en la exportación mensual del Cuadro de dirección, con la que el
/// Operador CAE externo cobra por cada Trabajador extranjero gestionado (decisiones de Chris, 2026-10-20).
///
/// <para>
/// <b>Por documento</b> (<see cref="PorDocumento"/>): el documento es un Pasaporte según
/// <see cref="CriterioDocumentoIdentidadTrabajador"/>. No cuentan el DNI ni el NIE (quien tiene NIE puede ser residente).
/// Cuenta cualquier otro valor, incluido el número de soporte de la TIE, que ya no es un tipo de documento del
/// Trabajador. Un DNI o un NIE guardado con la letra de control mal (dato anterior a la validación) sigue siendo DNI o
/// NIE: un error de tecleo no convierte a nadie en extranjero. Un valor heredado que hoy no sería un Pasaporte válido
/// (marcadores como «SIN DNI» o «PENDIENTE», anteriores a la validación) tampoco cuenta: no se cobra lo que no es un
/// documento. Sin documento (Trabajador anonimizado) no cuenta.
/// </para>
///
/// <para>
/// <b>Marca a mano</b> (<see cref="Trabajador.MarcaExtranjeroCobrable"/>): si está fijada, manda sobre el documento
/// (<see cref="Efectivo"/>), en los dos sentidos (p. ej. un ciudadano de la UE con pasaporte que no se cobra).
/// </para>
///
/// <para>
/// <b>Histórico congelado</b>: al asignar un Trabajador a una Visita se guarda la foto
/// (<c>VisitaTrabajador.EsExtranjeroCobrable</c>); la exportación y el conteo del mes leen la foto, no este criterio, así
/// que corregir el documento o la casilla no cambia meses ya gestionados.
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
    public static bool PorDocumento(string? documento) =>
        CriterioDocumentoIdentidadTrabajador.TipoGuardado(documento) == TipoDocumentoTrabajador.Pasaporte
        && CriterioDocumentoIdentidadTrabajador.Clasificar(documento).EsValido;

    /// <summary>La marca a mano si está fijada; si no, el criterio por documento.</summary>
    public static bool Efectivo(bool? marca, string? documento) => marca ?? PorDocumento(documento);
}
