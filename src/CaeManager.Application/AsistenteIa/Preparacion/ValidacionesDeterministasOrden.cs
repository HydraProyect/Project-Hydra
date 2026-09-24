using CaeManager.Domain.Common;

namespace CaeManager.Application.AsistenteIa.Preparacion;

/// <summary>Gravedad de un aviso sobre la orden interpretada.</summary>
public enum GravedadAvisoOrden
{
    /// <summary>
    /// Se puede seguir, pero la persona tiene que verlo antes del Enter: una fecha que
    /// ya ha pasado puede ser una regularización legítima o un error de tecleo.
    /// </summary>
    Advertencia,

    /// <summary>
    /// El paso no puede ejecutarse así: queda en borrador hasta que se corrija.
    /// </summary>
    Bloqueante,
}

/// <summary>
/// Un aviso sobre un dato de la orden, con la corrección propuesta si hay una sola
/// razonable. <see cref="Propuesta"/> es una sugerencia para el plan: nunca se aplica
/// sin la confirmación de la persona.
/// </summary>
public sealed record AvisoOrden(string Campo, GravedadAvisoOrden Gravedad, string Mensaje, string? Propuesta = null);

/// <summary>
/// Paso 4 del asistente de flujos: lo que se puede comprobar sin modelo, en
/// milisegundos, antes de enseñar el plan. Todo lo que devuelve son avisos para el
/// plan, no decisiones: la persona confirma con un Enter y puede corregir.
/// <para>
/// Ejemplo del propietario (propuesta, § 3): «juan perez DNI 1233443F … el 18 de 09
/// hasta el 30 del 09», escrito el 19/09. Salen dos avisos: la letra del DNI debería
/// ser E, no F, y el 18/09 ya ha pasado. La propuesta de la § 3 sugería además
/// «¿el 18/10?»; aquí no se sugiere, porque con el fin en el 30/09 ese inicio
/// quedaría después del fin. Se avisa y decide la persona.
/// </para>
/// </summary>
public static class ValidacionesDeterministasOrden
{
    /// <summary>
    /// Comprueba un documento de persona ya normalizado por
    /// <see cref="EnmascaradorIdentificadores"/>. Un DNI o NIE con la letra mal es
    /// bloqueante —daría de alta a una persona con un documento que no existe— y se
    /// propone el mismo número con la letra que le corresponde. Un pasaporte o
    /// cualquier otro formato no lleva dígito de control calculable: no se avisa de
    /// nada, que no es lo mismo que darlo por bueno.
    /// </summary>
    public static IReadOnlyList<AvisoOrden> ValidarDocumentoPersona(string campo, string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento))
            return [];

        var analisis = ValidadorIdentificacion.Analizar(documento);
        if (analisis.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie))
        {
            // Un DNI sin letra no casa con el formato de Analizar, pero su letra se
            // puede calcular: faltar la letra es un error que sí sabemos corregir.
            var esperadaSinLetra = ValidadorIdentificacion.LetraControlEsperada(documento);
            if (esperadaSinLetra is { } letraQueFalta && documento.All(char.IsDigit))
            {
                return
                [
                    new AvisoOrden(campo, GravedadAvisoOrden.Bloqueante,
                        $"Al documento {documento} le falta la letra: debería ser {letraQueFalta}.",
                        documento + letraQueFalta),
                ];
            }

            return [];
        }

        if (analisis.EsValido)
            return [];

        var esperada = ValidadorIdentificacion.LetraControlEsperada(documento)!.Value;
        var corregido = documento[..^1] + esperada;
        return
        [
            new AvisoOrden(campo, GravedadAvisoOrden.Bloqueante,
                $"La letra del documento {documento} debería ser {esperada}, no {documento[^1]}.",
                corregido),
        ];
    }

    /// <summary>
    /// Comprueba un periodo <c>desde</c>–<c>hasta</c> frente a hoy. Un fin anterior al
    /// inicio es bloqueante. Un inicio que ya ha pasado es una advertencia —puede ser
    /// una regularización— y, si el mismo día del mes siguiente no ha pasado y no queda
    /// después del fin, se propone ese día, que es el error de tecleo más probable («18 de
    /// 09» por «18 de 10»). Si el fin también ha pasado, se avisa del fin.
    /// </summary>
    public static IReadOnlyList<AvisoOrden> ValidarPeriodo(DateOnly? desde, DateOnly? hasta, DateOnly hoy)
    {
        var avisos = new List<AvisoOrden>();

        if (desde is { } inicio && hasta is { } fin && fin < inicio)
        {
            avisos.Add(new AvisoOrden("hasta", GravedadAvisoOrden.Bloqueante,
                $"La fecha de fin ({fin:dd/MM/yyyy}) es anterior a la de inicio ({inicio:dd/MM/yyyy})."));
            return avisos;
        }

        if (desde is { } d && d < hoy)
        {
            var mesSiguiente = d.AddMonths(1);
            // Solo se propone si deja un periodo coherente: con «del 18/09 al 30/09»
            // escrito el 19/09, el 18/10 quedaría después del fin, y proponerlo sería
            // proponer otro error.
            var propuesta = mesSiguiente >= hoy && (hasta is not { } h || mesSiguiente <= h)
                ? mesSiguiente.ToString("dd/MM/yyyy")
                : null;
            avisos.Add(new AvisoOrden("desde", GravedadAvisoOrden.Advertencia,
                propuesta is null
                    ? $"El {d:dd/MM/yyyy} ya ha pasado."
                    : $"El {d:dd/MM/yyyy} ya ha pasado: ¿quieres decir el {propuesta}?",
                propuesta));
        }

        if (hasta is { } f && f < hoy)
        {
            avisos.Add(new AvisoOrden("hasta", GravedadAvisoOrden.Advertencia,
                $"La fecha de fin ({f:dd/MM/yyyy}) ya ha pasado."));
        }

        return avisos;
    }
}
