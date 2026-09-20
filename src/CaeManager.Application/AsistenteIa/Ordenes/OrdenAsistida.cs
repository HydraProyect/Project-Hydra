namespace CaeManager.Application.AsistenteIa.Ordenes;

/// <summary>
/// Cómo se saca del texto el valor de un campo. Son tres y no más, porque son
/// las tres formas que se midieron contra un modelo real antes de escribir esto
/// (medición del 2026-09-20; el informe vive en el repositorio de negocio).
/// </summary>
public enum FormaDeExtraccion
{
    /// <summary>
    /// El valor sale de una lista de candidatos que TALVEG ya ha filtrado por lo
    /// que la persona puede ver. Exige siempre una opción de abstención
    /// explícita: sin ella, el modelo medido inventó un valor inexistente con
    /// confianza 0,89 en lugar de decir que no lo sabía.
    /// </summary>
    SeleccionDeCatalogo,

    /// <summary>
    /// Fechas. No se extraen como texto: se preguntan por componentes (modo,
    /// día, mes, día de la semana, semana) y se resuelven en código. Es lo que
    /// acertó donde la extracción por candidatos fallaba.
    /// </summary>
    PartesCerradas,

    /// <summary>
    /// Se selecciona un tramo literal del texto original —nombre, documento,
    /// motivo—. No se genera texto nuevo en ningún caso.
    /// </summary>
    TextoLiteral,
}

/// <summary>Un dato que una orden necesita para poder ejecutarse.</summary>
/// <param name="Nombre">Identificador estable del campo dentro de la orden.</param>
/// <param name="Forma">Cómo se obtiene del texto.</param>
/// <param name="Obligatorio">
/// Si es obligatorio y falta, la orden no se ejecuta: queda en borrador y el
/// asistente pregunta. Nunca se rellena con un valor inventado.
/// </param>
/// <param name="Descripcion">Qué es, en lenguaje de negocio.</param>
public record CampoDeOrden(
    string Nombre,
    FormaDeExtraccion Forma,
    bool Obligatorio,
    string Descripcion);

/// <summary>
/// Qué separa esta orden de la vecina con la que se confunde.
/// <para>
/// No es documentación: es una regla que hay que declarar para que la
/// clasificación sea determinable. Medido: ante «toda la semana del 5 de
/// octubre», sin regla declarada, el modelo eligió Asignación con vigencia con
/// probabilidad 0,86 sin que nadie se lo hubiera pedido. La ambigüedad no la
/// resuelve el modelo: la resuelve el negocio, o no se resuelve.
/// </para>
/// </summary>
/// <param name="ConLaOrden">Identificador de la orden vecina.</param>
/// <param name="Regla">La regla que decide entre las dos.</param>
/// <param name="ConfirmadaPorNegocio">
/// Falso mientras la regla sea una propuesta de quien escribió el catálogo y no
/// una decisión tomada. Una frontera sin confirmar no impide clasificar, pero
/// obliga a que la orden lo diga en su limitación.
/// </param>
public record FronteraDeOrden(
    string ConLaOrden,
    string Regla,
    bool ConfirmadaPorNegocio);

/// <summary>
/// Una clase de orden escrita que el asistente sabe reconocer.
/// <para>
/// Se llama «orden» y no «flujo» a propósito: en este código «flujo» ya
/// significa <see cref="System.IO.Stream"/> por traducción literal
/// (<c>flujoZip</c>, <c>flujoFirmado</c>, <c>_flujoDentro</c>), y un concepto de
/// dominio con ese nombre sería ambiguo desde el primer día.
/// </para>
/// </summary>
/// <param name="Id">Identificador estable. Es lo que se audita.</param>
/// <param name="Criterio">
/// La frase con la que se clasifica una orden entrante. Se manda al modelo tal
/// cual, así que está escrita para ser leída por él y por una persona.
/// </param>
/// <param name="Fronteras">Qué la separa de sus vecinas.</param>
/// <param name="Campos">Los datos que necesita.</param>
/// <param name="Commands">
/// Los Commands existentes que la ejecutan, en orden. Son tipos y no nombres
/// para que el compilador sostenga la relación: si alguien renombra o borra un
/// Command, esto deja de compilar en vez de degradarse en silencio.
/// </param>
/// <param name="EsSoloLectura">
/// La orden no escribe nada. Entonces no declara Commands y se ejecuta con una
/// Query.
/// </param>
/// <param name="Ejecutable">
/// Si es falso, el asistente puede entender la orden y preparar el borrador,
/// pero no completarla. Declararlo evita que prometa algo que nadie sabe hacer.
/// </param>
/// <param name="Limitacion">
/// Por qué no es ejecutable, o qué se hereda al ejecutarla. Vacío solo si no hay
/// ninguna.
/// </param>
/// <param name="EnviaComunicacionExterna">
/// La orden manda algo fuera de TALVEG —un correo a un tercero— y por tanto no
/// es reversible deshaciendo un registro.
/// </param>
/// <param name="RequiereConfirmacion">
/// Una persona confirma el plan antes de ejecutar.
/// <para>
/// Decisión del propietario del 2026-09-20: un Enter sobre el plan, sin
/// ejecución directa, tampoco para los pasos sin ambigüedad. Es coherente con
/// «confirmación humana siempre» (P31/F-04). Se aplica a todas las órdenes,
/// incluidas las de solo lectura, para que la regla sea una sola y la primera
/// excepción tenga que verse en un diff.
/// </para>
/// </param>
public record OrdenAsistida(
    string Id,
    string Criterio,
    IReadOnlyList<FronteraDeOrden> Fronteras,
    IReadOnlyList<CampoDeOrden> Campos,
    IReadOnlyList<Type> Commands,
    bool EsSoloLectura,
    bool Ejecutable,
    string Limitacion,
    bool EnviaComunicacionExterna,
    bool RequiereConfirmacion)
{
    /// <summary>Los campos sin los que la orden no puede ejecutarse.</summary>
    public IEnumerable<CampoDeOrden> CamposObligatorios => Campos.Where(c => c.Obligatorio);

    /// <summary>Alguna de sus fronteras es todavía una propuesta, no una decisión.</summary>
    public bool TieneFronteraSinConfirmar => Fronteras.Any(f => !f.ConfirmadaPorNegocio);
}
