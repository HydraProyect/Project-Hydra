namespace CaeManager.Domain.Documentos;

/// <summary>
/// En qué situación está un requisito bloqueante (un <see cref="TipoDocumentoCentro"/> con
/// <see cref="TipoDocumentoCentro.BloqueaAcceso"/>) para su sujeto, hoy.
/// </summary>
public enum SituacionDeRequisitoBloqueante
{
    /// <summary>Hay al menos un Documento del tipo válido hoy: no bloquea.</summary>
    Cumplido = 0,

    /// <summary>No existe ningún Documento del tipo para ese sujeto: bloquea.</summary>
    Ausente = 1,

    /// <summary>Existen Documentos del tipo pero ninguno es válido hoy (todos vencidos): bloquea igual que el ausente.</summary>
    Vencido = 2
}

/// <summary>
/// <b>Punto único</b> de la regla «un documento bloqueante ausente o vencido bloquea el acceso»
/// (decisión del propietario del producto, 2026-10-03). Ninguna superficie decide por su cuenta si un documento
/// bloqueante «cuenta»: llaman aquí.
///
/// <list type="number">
/// <item><b>Sujeto Trabajador.</b> Un Documento bloqueante de ámbito Trabajador ausente o no válido hoy
/// (vencido) bloquea a ESE Trabajador. Un vencido bloquea igual que un ausente.</item>
/// <item><b>Sujeto Empresa.</b> Un Documento bloqueante de ámbito Empresa ausente o vencido bloquea a TODOS
/// los Trabajadores de esa Empresa en TODOS los Centros del Tenant propietario, aunque su documentación
/// personal esté completa. El modelo es egocéntrico por Tenant: nunca se cruza de un Tenant a otro.</item>
/// <item><b>El Centro no es el sujeto.</b> Un documento bloqueante de Trabajador o de Empresa bloquea a
/// personas, no convierte al Centro en «bloqueado». Qué debe enseñar el Centro cuando tiene Trabajadores o
/// Empresas bloqueados es una decisión de producto pendiente: esta regla no la toma.</item>
/// </list>
///
/// <para>
/// «Válido hoy» es <b>no vencido</b>: un documento con fecha de vencimiento anterior a hoy no vale; uno que
/// vence hoy, o más adelante (Próximo o Urgente), vale. «Sin confirmar» y «No caduca» no tienen fecha y por
/// tanto no están vencidos: no bloquean. Eso es lo que ya hacía Mi trabajo y no se decidió cambiarlo para el
/// bloqueo (el propietario del producto fijó «Sin confirmar» solo para paneles e incidencias, no para el acceso).
/// </para>
///
/// <para>
/// Es una función pura sobre <see cref="VigenciaDocumento"/>, no un predicado SQL: EF no puede llamarla
/// dentro de una consulta, así que quien la use trae el estado y la fecha de vigencia y la evalúa en memoria
/// (copiar <c>FechaVencimiento == null || FechaVencimiento &gt;= hoy</c> a una consulta es justo la copia que
/// provocó D-13/D-17/D-22; la vigila <c>ReglasDeNegocioSinCopiasTests</c>).
/// </para>
/// </summary>
public static class ReglaBloqueoDeAcceso
{
    /// <summary>¿Vale este documento hoy para cumplir un requisito bloqueante? Todo lo que no está vencido.</summary>
    public static bool ValidoHoy(VigenciaDocumento vigencia, DateOnly hoy) =>
        CalculadoraEstadoDocumento.Calcular(vigencia, hoy, umbralAmbarDias: 0, umbralRojoDias: 0) != EstadoDocumento.Vencido;

    /// <summary>
    /// Situación del requisito dados TODOS los Documentos de ese tipo que tiene el sujeto (puede haber un
    /// vencido y su renovación: basta uno válido hoy para cumplir).
    /// </summary>
    public static SituacionDeRequisitoBloqueante Evaluar(IEnumerable<VigenciaDocumento> documentosDelTipo, DateOnly hoy)
    {
        var existe = false;
        foreach (var vigencia in documentosDelTipo)
        {
            if (ValidoHoy(vigencia, hoy))
                return SituacionDeRequisitoBloqueante.Cumplido;
            existe = true;
        }

        return existe ? SituacionDeRequisitoBloqueante.Vencido : SituacionDeRequisitoBloqueante.Ausente;
    }

    /// <summary>Ausente y vencido bloquean por igual; solo cumplido no bloquea.</summary>
    public static bool Bloquea(SituacionDeRequisitoBloqueante situacion) =>
        situacion != SituacionDeRequisitoBloqueante.Cumplido;

    /// <summary>
    /// Qué ámbitos de tipo pueden ser requisito bloqueante, porque tienen un sujeto del bloqueo: el Trabajador
    /// o la Empresa. Un tipo de Cliente, Vehículo o Proyecto marcado como bloqueante no tiene sujeto definido y
    /// no bloquea a nadie (no se inventa una regla para ellos).
    /// </summary>
    public static bool AmbitoPuedeBloquear(AmbitoAplicacion ambito) =>
        ambito is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa;
}
